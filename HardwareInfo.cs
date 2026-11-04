using System.Management;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TheWitness;

internal sealed class HardwareInfo
{
    private const string HashSalt = "TheWitness/HW/v1/6f2a9c41-7d38-4be0-9a1c-2e5b8f0d34a7";

    private const string Unavailable = "N/A";

    internal string CpuHash { get; }
    internal string GpuHash { get; }
    internal string RamHash { get; }
    internal string MotherboardHash { get; }
    internal string AllHash { get; }

    private static readonly object CacheGate = new();
    private static HardwareInfo? cached;

    private HardwareInfo(string cpuCanonical, string gpuCanonical, string ramCanonical, string motherboardCanonical)
    {
        CpuHash = Hash(cpuCanonical);
        GpuHash = Hash(gpuCanonical);
        RamHash = Hash(ramCanonical);
        MotherboardHash = Hash(motherboardCanonical);

        AllHash = Hash(string.Join("\n", cpuCanonical, gpuCanonical, ramCanonical, motherboardCanonical));
    }

    internal static HardwareInfo Capture()
    {
        if (cached is { } ready)
        {
            return ready;
        }

        lock (CacheGate)
        {
            cached ??= new HardwareInfo(
                BuildCpuCanonical(),
                BuildGpuCanonical(),
                BuildRamCanonical(),
                BuildMotherboardCanonical());
            return cached;
        }
    }

    private static string BuildCpuCanonical()
    {
        List<string> rows = QueryWmi(
            "SELECT Name,Manufacturer,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed,SocketDesignation,ProcessorId FROM Win32_Processor",
            mo => Row(
                ("NAME", mo["Name"]),
                ("MFR", mo["Manufacturer"]),
                ("CORES", mo["NumberOfCores"]),
                ("THREADS", mo["NumberOfLogicalProcessors"]),
                ("MHZ", mo["MaxClockSpeed"]),
                ("SOCKET", mo["SocketDesignation"]),
                ("ID", mo["ProcessorId"])));

        return JoinRows(rows);
    }

    private static string BuildGpuCanonical()
    {
        List<string> rows = QueryWmi(
            "SELECT Name,PNPDeviceID,VideoProcessor FROM Win32_VideoController",
            mo => Row(
                ("NAME", mo["Name"]),
                ("PNP", mo["PNPDeviceID"]),
                ("VPROC", mo["VideoProcessor"])));

        return JoinRows(rows);
    }

    private static string BuildRamCanonical()
    {
        List<string> rows = QueryWmi(
            "SELECT DeviceLocator,Capacity,Manufacturer,PartNumber,SerialNumber,Speed FROM Win32_PhysicalMemory",
            mo => Row(
                ("SLOT", mo["DeviceLocator"]),
                ("CAP", mo["Capacity"]),
                ("MFR", mo["Manufacturer"]),
                ("PN", mo["PartNumber"]),
                ("SN", mo["SerialNumber"]),
                ("SPD", mo["Speed"])));

        return JoinRows(rows);
    }

    private static string BuildMotherboardCanonical()
    {
        string board = FirstRow(QueryWmi(
            "SELECT Manufacturer,Product,Version,SerialNumber FROM Win32_BaseBoard",
            mo => Row(
                ("MFR", mo["Manufacturer"]),
                ("PRODUCT", mo["Product"]),
                ("VER", mo["Version"]),
                ("SN", mo["SerialNumber"]))));

        string system = FirstRow(QueryWmi(
            "SELECT UUID,Vendor,Name,IdentifyingNumber FROM Win32_ComputerSystemProduct",
            mo => Row(
                ("UUID", mo["UUID"]),
                ("VENDOR", mo["Vendor"]),
                ("NAME", mo["Name"]),
                ("IDNUM", mo["IdentifyingNumber"]))));

        return $"BOARD[{board}];SYSTEM[{system}]";
    }

    private static List<string> QueryWmi(string wql, Func<ManagementBaseObject, string> project)
    {
        List<string> results = new();
        try
        {
            using ManagementObjectSearcher searcher = new(wql);
            using ManagementObjectCollection collection = searcher.Get();
            foreach (ManagementBaseObject mo in collection)
            {
                try
                {
                    results.Add(project(mo));
                }
                catch
                {
                }
                finally
                {
                    mo.Dispose();
                }
            }
        }
        catch
        {
        }

        return results;
    }

    private static string Row(params (string Key, object? Value)[] fields)
    {
        StringBuilder sb = new();
        foreach ((string key, object? value) in fields)
        {
            if (sb.Length > 0)
            {
                sb.Append('|');
            }

            string normalized = Normalize(value);
            sb.Append(key).Append('=').Append(normalized.Length == 0 ? Unavailable : normalized);
        }

        return sb.ToString();
    }

    private static string JoinRows(List<string> rows)
    {
        if (rows.Count == 0)
        {
            return Unavailable;
        }

        rows.Sort(StringComparer.Ordinal);
        return string.Join(";", rows);
    }

    private static string FirstRow(List<string> rows)
    {
        rows.Sort(StringComparer.Ordinal);
        return rows.Count > 0 ? rows[0] : Unavailable;
    }

    private static string Normalize(object? value)
    {
        if (value == null)
        {
            return string.Empty;
        }

        string text = value.ToString() ?? string.Empty;
        text = text.Replace("\0", string.Empty).Trim();
        text = Regex.Replace(text, @"\s+", " ");

        return IsPlaceholder(text) ? string.Empty : text.ToUpperInvariant();
    }

    private static bool IsPlaceholder(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        return text.Equals("To be filled by O.E.M.", StringComparison.OrdinalIgnoreCase)
            || text.Equals("Default string", StringComparison.OrdinalIgnoreCase)
            || text.Equals("None", StringComparison.OrdinalIgnoreCase)
            || text.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            || text.Equals("Not Specified", StringComparison.OrdinalIgnoreCase)
            || text.Equals("Not Applicable", StringComparison.OrdinalIgnoreCase)
            || text.Equals("System Serial Number", StringComparison.OrdinalIgnoreCase)
            || text.Equals("Default", StringComparison.OrdinalIgnoreCase)
            || text.Equals("O.E.M.", StringComparison.OrdinalIgnoreCase);
    }

    private static string Hash(string canonical)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(HashSalt + "|" + canonical);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
