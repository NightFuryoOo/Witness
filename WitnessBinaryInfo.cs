using System.Reflection;
using System.Security.Cryptography;

namespace TheWitness;

internal static class WitnessBinaryInfo
{
    private static string? cachedSha256;
    private static bool resolved;

    internal static string? ComputeSelfSha256()
    {
        if (resolved)
        {
            return cachedSha256;
        }

        try
        {
            string? path = Assembly.GetExecutingAssembly().Location;
            if (!string.IsNullOrEmpty(path))
            {
                using FileStream stream = File.OpenRead(path);
                cachedSha256 = Convert.ToHexString(SHA256.HashData(stream));
            }
        }
        catch (Exception)
        {
        }

        resolved = true;
        return cachedSha256;
    }
}
