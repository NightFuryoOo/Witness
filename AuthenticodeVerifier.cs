using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace TheWitness;

internal static class AuthenticodeVerifier
{
    private static readonly string[] TrustedPublishers =
    {
        "Microsoft Corporation",
        "Microsoft Windows",
        "Valve Corp.",
        "GOG sp. z o.o.",
        "Discord Inc.",
        "NVIDIA Corporation",
        "OBS Project, LLC"
    };

    private static readonly ConcurrentDictionary<string, bool> VerdictCache =
        new(StringComparer.OrdinalIgnoreCase);

    internal static bool IsSignedByTrustedPublisher(string filePath)
    {
        try
        {
            FileInfo info = new(filePath);
            if (!info.Exists)
            {
                return false;
            }

            string cacheKey = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            return VerdictCache.GetOrAdd(cacheKey, _ => Verify(info.FullName));
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static string? TryGetSignerName(string filePath)
    {
        try
        {
            using X509Certificate2 certificate = new(X509Certificate.CreateFromSignedFile(filePath));
            return certificate.GetNameInfo(X509NameType.SimpleName, false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool Verify(string filePath)
    {
        if (!HasValidSignature(filePath))
        {
            return false;
        }

        string? signer = TryGetSignerName(filePath);
        return signer is not null &&
               TrustedPublishers.Any(name => string.Equals(name, signer, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasValidSignature(string filePath)
    {
        WintrustFileInfo fileInfo = new()
        {
            StructSize = (uint)Marshal.SizeOf<WintrustFileInfo>(),
            FilePath = filePath
        };
        IntPtr fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
            WintrustData data = new()
            {
                StructSize = (uint)Marshal.SizeOf<WintrustData>(),
                UiChoice = WtdUiNone,
                RevocationChecks = WtdRevokeNone,
                UnionChoice = WtdChoiceFile,
                File = fileInfoPtr,
                StateAction = WtdStateActionVerify,
                ProvFlags = WtdRevocationCheckNone | WtdCacheOnlyUrlRetrieval
            };

            Guid action = GenericVerifyV2;
            int result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            data.StateAction = WtdStateActionClose;
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return result == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(fileInfoPtr);
        }
    }

    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdRevocationCheckNone = 0x10;
    private const uint WtdCacheOnlyUrlRetrieval = 0x1000;

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WintrustFileInfo
    {
        public uint StructSize;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WintrustData data);
}
