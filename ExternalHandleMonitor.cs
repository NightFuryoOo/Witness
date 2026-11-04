using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TheWitness;

internal sealed record ExternalHandleResult(
    DateTimeOffset TimestampUtc,
    string OwnerProcessName,
    int OwnerProcessId,
    uint GrantedAccess,
    bool OwnerLaunchedGame = false,
    string? OwnerImagePath = null,
    string? OwnerImageSha256 = null);

internal static class ExternalHandleMonitor
{
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint DuplicateSameAccess = 0x00000002;

    [Flags]
    private enum ProcessAccess : uint
    {
        DupHandle = 0x0040,
        QueryInformation = 0x0400,
        QueryLimitedInformation = 0x1000
    }

    private const uint TokenQuery = 0x0008;
    private const int TokenUser = 1;

    private static byte? cachedProcessTypeIndex;

    internal static IReadOnlyList<ExternalHandleResult> CheckOnce(Process gameProcess)
    {
        List<ExternalHandleResult> results = new();
        if (gameProcess.HasExited)
        {
            return results;
        }

        int targetPid = gameProcess.Id;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (!TryGetProcessSidBytes(gameProcess.Handle, out byte[] targetSid))
        {
            return results;
        }

        int? launcherPid = TryGetVerifiedParentProcessId(gameProcess);

        try
        {
            List<HandleEntry> entries = SnapshotHandles();
            byte processTypeIndex = cachedProcessTypeIndex!.Value;
            Dictionary<int, string> nameCache = new();

            foreach (HandleEntry entry in entries)
            {
                if (entry.ObjectTypeIndex != processTypeIndex)
                {
                    continue;
                }

                if (entry.OwnerPid == targetPid || entry.OwnerPid == Environment.ProcessId)
                {
                    continue;
                }

                if (!IsSuspiciousAccess(entry.GrantedAccess))
                {
                    continue;
                }

                if (!TryConfirmHandleMatchesTarget(entry, targetPid, targetSid))
                {
                    continue;
                }

                bool ownerLaunchedGame = launcherPid is int launcher && launcher == entry.OwnerPid;
                string? imagePath = TryGetImagePath(entry.OwnerPid);
                results.Add(new ExternalHandleResult(
                    now,
                    ResolveProcessName(entry.OwnerPid, nameCache),
                    entry.OwnerPid,
                    entry.GrantedAccess,
                    ownerLaunchedGame,
                    imagePath,
                    ownerLaunchedGame ? TryComputeImageSha256(imagePath) : null));
            }
        }
        catch (Exception)
        {
        }

        return results;
    }

    private static bool IsSuspiciousAccess(uint access) =>
        (access & ProcessVmWrite) != 0 || ((access & ProcessVmOperation) != 0 && (access & ProcessVmRead) != 0);

    private static bool TryConfirmHandleMatchesTarget(HandleEntry entry, int targetPid, byte[] targetSid)
    {
        IntPtr ownerHandle = OpenProcess(ProcessAccess.DupHandle | ProcessAccess.QueryLimitedInformation | ProcessAccess.QueryInformation, false, entry.OwnerPid);
        if (ownerHandle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (!TryGetProcessSidBytes(ownerHandle, out byte[] ownerSid) || !ownerSid.AsSpan().SequenceEqual(targetSid))
            {
                return false;
            }

            if (!DuplicateHandle(ownerHandle, (IntPtr)entry.HandleValue, GetCurrentProcess(), out IntPtr dupTarget, 0, false, DuplicateSameAccess))
            {
                return false;
            }

            try
            {
                return GetProcessId(dupTarget) == (uint)targetPid;
            }
            finally
            {
                CloseHandle(dupTarget);
            }
        }
        finally
        {
            CloseHandle(ownerHandle);
        }
    }

    private static bool TryGetProcessSidBytes(IntPtr processHandle, out byte[] sid)
    {
        sid = Array.Empty<byte>();
        if (!OpenProcessToken(processHandle, TokenQuery, out IntPtr tokenHandle))
        {
            return false;
        }

        try
        {
            GetTokenInformation(tokenHandle, TokenUser, IntPtr.Zero, 0, out int neededSize);
            if (neededSize <= 0)
            {
                return false;
            }

            IntPtr tokenInfo = Marshal.AllocHGlobal(neededSize);
            try
            {
                if (!GetTokenInformation(tokenHandle, TokenUser, tokenInfo, neededSize, out _))
                {
                    return false;
                }

                IntPtr sidPtr = Marshal.ReadIntPtr(tokenInfo);
                int sidLength = GetLengthSid(sidPtr);
                if (sidLength <= 0)
                {
                    return false;
                }

                sid = new byte[sidLength];
                Marshal.Copy(sidPtr, sid, 0, sidLength);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(tokenInfo);
            }
        }
        finally
        {
            CloseHandle(tokenHandle);
        }
    }

    private static int? TryGetVerifiedParentProcessId(Process gameProcess)
    {
        try
        {
            ProcessBasicInformation info = default;
            if (NtQueryInformationProcess(gameProcess.Handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0)
            {
                return null;
            }

            long parentId = info.InheritedFromUniqueProcessId.ToInt64();
            if (parentId <= 0 || parentId > int.MaxValue)
            {
                return null;
            }

            using Process parent = Process.GetProcessById((int)parentId);
            return parent.StartTime <= gameProcess.StartTime ? (int)parentId : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static readonly Dictionary<(string Path, long Length, long LastWriteTicks), string?> imageHashCache = new();

    internal static string? TryComputeImageSha256(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return null;
        }

        try
        {
            FileInfo info = new(imagePath);
            if (!info.Exists)
            {
                return null;
            }

            (string, long, long) key = (info.FullName, info.Length, info.LastWriteTimeUtc.Ticks);
            lock (imageHashCache)
            {
                if (imageHashCache.TryGetValue(key, out string? cached))
                {
                    return cached;
                }
            }

            string? hash;
            using (FileStream stream = new(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
            }

            lock (imageHashCache)
            {
                imageHashCache[key] = hash;
            }

            return hash;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? TryGetImagePath(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            return p.MainModule?.FileName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string ResolveProcessName(int pid, Dictionary<int, string> cache)
    {
        if (cache.TryGetValue(pid, out string? cached))
        {
            return cached;
        }

        string name;
        try
        {
            using Process p = Process.GetProcessById(pid);
            name = p.ProcessName;
        }
        catch (Exception)
        {
            name = "(unresolvable)";
        }

        cache[pid] = name;
        return name;
    }

    private static List<HandleEntry> SnapshotHandles()
    {
        if (!DuplicateHandle(GetCurrentProcess(), GetCurrentProcess(), GetCurrentProcess(), out IntPtr selfProbe, 0, false, DuplicateSameAccess))
        {
            throw new InvalidOperationException("DuplicateHandle failed.");
        }

        try
        {
            List<HandleEntry> entries = QuerySystemHandleInformation();

            if (cachedProcessTypeIndex is null)
            {
                int myPid = Environment.ProcessId;
                ushort selfHandleValue = (ushort)(long)selfProbe;
                HandleEntry? match = entries.Find(e => e.OwnerPid == myPid && e.HandleValue == selfHandleValue);
                if (match is null)
                {
                    throw new InvalidOperationException("Own handle not found.");
                }

                cachedProcessTypeIndex = match.ObjectTypeIndex;
            }

            return entries;
        }
        finally
        {
            CloseHandle(selfProbe);
        }
    }

    private static List<HandleEntry> QuerySystemHandleInformation()
    {
        const int SystemHandleInformation = 16;
        const uint StatusInfoLengthMismatch = 0xC0000004;

        int length = 1 << 20;
        while (true)
        {
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                uint status = NtQuerySystemInformation(SystemHandleInformation, buffer, length, out int returnLength);
                if (status == 0)
                {
                    return ParseHandles(buffer);
                }

                if (status != StatusInfoLengthMismatch)
                {
                    throw new Win32Exception((int)status);
                }

                length = returnLength > length ? returnLength + (1 << 16) : length * 2;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static List<HandleEntry> ParseHandles(IntPtr buffer)
    {
        List<HandleEntry> entries = new();

        uint count = unchecked((uint)Marshal.ReadInt32(buffer, 0));
        int arrayOffset = IntPtr.Size;
        int entrySize = IntPtr.Size == 8 ? 24 : 16;

        for (uint i = 0; i < count; i++)
        {
            int entryOffset = arrayOffset + (int)(i * entrySize);
            ushort ownerPid = unchecked((ushort)Marshal.ReadInt16(buffer, entryOffset));
            byte objectTypeIndex = Marshal.ReadByte(buffer, entryOffset + 4);
            ushort handleValue = unchecked((ushort)Marshal.ReadInt16(buffer, entryOffset + 6));
            uint grantedAccess = unchecked((uint)Marshal.ReadInt32(buffer, entryOffset + 8 + IntPtr.Size));

            entries.Add(new HandleEntry(ownerPid, objectTypeIndex, handleValue, grantedAccess));
        }

        return entries;
    }

    private sealed record HandleEntry(int OwnerPid, byte ObjectTypeIndex, ushort HandleValue, uint GrantedAccess);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass, ref ProcessBasicInformation processInformation, int processInformationLength, out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern uint NtQuerySystemInformation(int systemInformationClass, IntPtr systemInformation, int systemInformationLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(ProcessAccess desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr hSourceProcessHandle, IntPtr hSourceHandle, IntPtr hTargetProcessHandle, out IntPtr lpTargetHandle, uint dwDesiredAccess, bool bInheritHandle, uint dwOptions);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessId(IntPtr process);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("advapi32.dll")]
    private static extern int GetLengthSid(IntPtr sid);
}
