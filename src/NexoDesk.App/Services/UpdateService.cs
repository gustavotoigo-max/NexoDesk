using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using NexoDesk.App.ViewModels;

namespace NexoDesk.App.Services;

public sealed record UpdateInfo(Version Version, string Tag, string DownloadUrl, string Notes);

/// <summary>
/// Atualização pela interface: consulta a última versão publicada em Releases no GitHub e, se for mais nova,
/// baixa o instalador e o executa. O instalador fecha o NexoDesk aberto e instala por cima.
/// </summary>
public sealed class UpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/gustavotoigo-max/NexoDesk/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NexoDesk-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>Retorna a atualização disponível, ou null se já está na última versão (ou sem internet).</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        GitHubRelease? release;
        try
        {
            release = await Http.GetFromJsonAsync(LatestReleaseUrl, UpdateJson.Default.GitHubRelease, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return null;
        }

        if (release?.TagName is null || release.Draft || release.Prerelease)
            return null;
        if (!TryParseTag(release.TagName, out Version? latest))
            return null;
        if (latest <= Current)
            return null;

        GitHubAsset? setup = release.Assets?.FirstOrDefault(a =>
            a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && a.BrowserDownloadUrl is { Length: > 0 });
        if (setup?.BrowserDownloadUrl is null)
            return null;

        return new UpdateInfo(latest, release.TagName, setup.BrowserDownloadUrl, release.Body ?? "");
    }

    /// <summary>Baixa o instalador em %TEMP% e o executa. Devolve false se o download falhar.</summary>
    public static async Task<bool> DownloadAndRunAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
    {
        string path = Path.Combine(Path.GetTempPath(), $"NexoDesk-Setup-{info.Version}.exe");
        try
        {
            using (HttpResponseMessage response = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                long? total = response.Content.Headers.ContentLength;
                await using Stream input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                byte[] buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    read += n;
                    if (total is > 0)
                        progress?.Report((double)read / total.Value);
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            return false;
        }

        // Executa o instalador; ele fecha o NexoDesk aberto (CloseApplications=force) e instala por cima.
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return true;
    }

    private static Version Current => Version.TryParse(AppInfo.Version, out Version? v) ? v : new Version(0, 0, 0);

    private static bool TryParseTag(string tag, out Version? version) =>
        Version.TryParse(tag.TrimStart('v', 'V'), out version);
}

internal sealed record GitHubRelease(
    [property: JsonPropertyName("tag_name")] string? TagName,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("draft")] bool Draft,
    [property: JsonPropertyName("prerelease")] bool Prerelease,
    [property: JsonPropertyName("assets")] List<GitHubAsset>? Assets);

internal sealed record GitHubAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string? BrowserDownloadUrl);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(GitHubRelease))]
internal sealed partial class UpdateJson : JsonSerializerContext;
