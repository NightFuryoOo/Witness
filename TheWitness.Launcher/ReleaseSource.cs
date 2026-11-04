namespace TheWitness.Launcher;

internal static class ReleaseSource
{
    private const string Owner = "NightFuryoOo";
    private const string Repo = "Witness";

    internal const string MetadataAssetName = "latest.json";

    internal const string PackageAssetName = "TheWitness-win-x64.zip";

    private const string BaseUrl = $"https://github.com/{Owner}/{Repo}/releases/latest/download/";

    internal static string MetadataUrl => BaseUrl + MetadataAssetName;

    internal static string PackageUrl => BaseUrl + PackageAssetName;
}
