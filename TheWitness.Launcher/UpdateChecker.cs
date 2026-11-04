using System.Net.Http.Headers;
using System.Text.Json;

namespace TheWitness.Launcher;

internal static class UpdateChecker
{
    private static readonly HttpClient Http = CreateClient();

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    internal static async Task<LatestReleaseInfo?> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await Http.GetAsync(ReleaseSource.MetadataUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);
            LauncherMetadata? metadata = await JsonSerializer.DeserializeAsync<LauncherMetadata>(body, JsonOptions, cancellationToken);
            if (metadata is null || string.IsNullOrWhiteSpace(metadata.Version) || string.IsNullOrWhiteSpace(metadata.Sha256))
            {
                return null;
            }

            return new LatestReleaseInfo(metadata.Version, metadata.Sha256.ToUpperInvariant());
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return null;
        }
    }

    private static HttpClient CreateClient()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TheWitnessLauncher", "1.0"));
        return client;
    }

    private sealed class LauncherMetadata
    {
        public string? Version { get; set; }
        public string? Sha256 { get; set; }
    }
}
