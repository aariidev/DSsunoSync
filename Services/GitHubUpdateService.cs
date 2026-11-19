using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace ArideevSunoPresence;

public enum UpdateCheckStatus
{
    NoRelease,
    UpToDate,
    Downloaded
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, Version? Version, string? InstallerPath);

public sealed class GitHubUpdateService
{
    private const long MaxInstallerBytes = 300L * 1024 * 1024;
    private const string InstallerFileName = "ArideevSunoPresence-Update.exe";
    private static readonly Uri LatestReleaseUri = new("https://api.github.com/repos/aariidev/Sndspresence/releases/latest");
    private static readonly Uri LatestReleasePageUri = new("https://github.com/aariidev/Sndspresence/releases/latest");
    private static readonly HttpClient Http = CreateHttpClient();

    public async Task<UpdateCheckResult> CheckAndDownloadAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        using var releaseResponse = await Http.GetAsync(LatestReleaseUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (releaseResponse.StatusCode == HttpStatusCode.NotFound)
            return new UpdateCheckResult(UpdateCheckStatus.NoRelease, null, null);
        if (await IsRateLimitResponseAsync(releaseResponse, cancellationToken))
            return await CheckAndDownloadFromReleasePageAsync(currentVersion, cancellationToken);
        releaseResponse.EnsureSuccessStatusCode();

        await using var releaseStream = await releaseResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var release = await JsonDocument.ParseAsync(releaseStream, cancellationToken: cancellationToken);
        var root = release.RootElement;
        var tag = root.GetProperty("tag_name").GetString()
            ?? throw new InvalidDataException("La versión publicada no contiene una etiqueta.");
        var latestVersion = ParseReleaseVersion(tag);
        if (IsUpToDate(currentVersion, latestVersion))
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate, latestVersion, null);

        var installerName = $"ArideevSunoPresence-Setup-x64-{latestVersion}.exe";
        var installerAsset = FindAsset(root, installerName)
            ?? throw new InvalidDataException($"La versión {latestVersion} no contiene {installerName}.");
        if (installerAsset.TryGetProperty("size", out var installerSize)
            && installerSize.TryGetInt64(out var installerBytes)
            && installerBytes > MaxInstallerBytes)
            throw new InvalidDataException("El instalador supera el tamaño máximo permitido.");
        var installerUri = GetDownloadUri(installerAsset);
        var expectedHash = GetAssetDigest(installerAsset);
        Uri? checksumUri = null;
        if (expectedHash is null)
        {
            var checksumAsset = FindAsset(root, installerName + ".sha256")
                ?? throw new InvalidDataException("La publicación no incluye el SHA-256 del instalador.");
            if (checksumAsset.TryGetProperty("size", out var checksumSize)
                && checksumSize.TryGetInt64(out var checksumBytes)
                && checksumBytes > 1024)
                throw new InvalidDataException("El archivo SHA-256 publicado es demasiado grande.");
            checksumUri = GetDownloadUri(checksumAsset);
        }
        return await DownloadAndVerifyAsync(latestVersion, installerUri, expectedHash, checksumUri, cancellationToken);
    }

    private static async Task<UpdateCheckResult> CheckAndDownloadFromReleasePageAsync(
        Version currentVersion, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleasePageUri);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new UpdateCheckResult(UpdateCheckStatus.NoRelease, null, null);
        response.EnsureSuccessStatusCode();

        var finalUri = response.RequestMessage?.RequestUri
            ?? throw new InvalidDataException("GitHub no indicó la URL de la última publicación.");
        if (finalUri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(finalUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GitHub redirigió a una URL de publicación no válida.");
        var tag = GetReleaseTagFromUri(finalUri)
            ?? throw new InvalidDataException("No se pudo leer la etiqueta desde la página de Releases de GitHub.");
        var latestVersion = ParseReleaseVersion(tag);
        if (IsUpToDate(currentVersion, latestVersion))
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate, latestVersion, null);

        var installerName = $"ArideevSunoPresence-Setup-x64-{latestVersion}.exe";
        return await DownloadAndVerifyAsync(
            latestVersion,
            GetReleaseAssetUri(tag, installerName),
            expectedHash: null,
            GetReleaseAssetUri(tag, installerName + ".sha256"),
            cancellationToken);
    }

    private static bool IsUpToDate(Version currentVersion, Version latestVersion)
    {
        var normalizedCurrent = new Version(currentVersion.Major, currentVersion.Minor, Math.Max(0, currentVersion.Build));
        var normalizedLatest = new Version(latestVersion.Major, latestVersion.Minor, latestVersion.Build);
        return normalizedLatest <= normalizedCurrent;
    }

    private static Version ParseReleaseVersion(string tag)
    {
        var versionText = tag.StartsWith('v') ? tag[1..] : tag;
        if (!Version.TryParse(versionText, out var version) || version.Build < 0)
            throw new InvalidDataException($"La etiqueta de versión de GitHub no es válida: {tag}");
        return new Version(version.Major, version.Minor, version.Build);
    }

    private static async Task<UpdateCheckResult> DownloadAndVerifyAsync(
        Version version, Uri installerUri, string? expectedHash, Uri? checksumUri,
        CancellationToken cancellationToken)
    {
        if (expectedHash is null)
        {
            if (checksumUri is null)
                throw new InvalidDataException("La publicación no incluye el SHA-256 del instalador.");
            expectedHash = await DownloadChecksumAsync(checksumUri, cancellationToken);
        }
        if (expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit))
            throw new InvalidDataException("El SHA-256 del instalador no es válido.");

        var updateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Arideev", "SunoPresence", "Updates");
        Directory.CreateDirectory(updateDirectory);
        var installerPath = Path.Combine(updateDirectory, InstallerFileName);
        var temporaryPath = installerPath + ".download";
        try
        {
            await DownloadInstallerAsync(installerUri, temporaryPath, cancellationToken);
            string actualHash;
            await using (var file = File.OpenRead(temporaryPath))
                actualHash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("La verificación SHA-256 del instalador ha fallado.");
            File.Move(temporaryPath, installerPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }

        return new UpdateCheckResult(UpdateCheckStatus.Downloaded, version, installerPath);
    }

    private static async Task<string> DownloadChecksumAsync(Uri checksumUri, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(checksumUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 1024)
            throw new InvalidDataException("El archivo SHA-256 publicado es demasiado grande.");

        var checksumText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (checksumText.Length > 1024)
            throw new InvalidDataException("El archivo SHA-256 publicado es demasiado grande.");
        return checksumText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            ?? throw new InvalidDataException("El archivo SHA-256 publicado está vacío.");
    }

    private static async Task<bool> IsRateLimitResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests))
            return false;
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining)
            && remaining.FirstOrDefault() == "0")
            return true;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return body.Contains("rate limit", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetReleaseTagFromUri(Uri uri)
    {
        const string releaseTagPath = "/aariidev/Sndspresence/releases/tag/";
        var path = uri.AbsolutePath;
        var marker = path.LastIndexOf(releaseTagPath, StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return null;
        var tag = Uri.UnescapeDataString(path[(marker + releaseTagPath.Length)..].Trim('/'));
        return string.IsNullOrWhiteSpace(tag) ? null : tag;
    }

    private static Uri GetReleaseAssetUri(string tag, string assetName)
    {
        return new Uri(
            $"https://github.com/aariidev/Sndspresence/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(assetName)}");
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var version = typeof(GitHubUpdateService).Assembly.GetName().Version;
        var versionText = version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ArideevSunoPresence", versionText));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static JsonElement? FindAsset(JsonElement release, string name)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var asset in assets.EnumerateArray())
            if (asset.TryGetProperty("name", out var assetName)
                && string.Equals(assetName.GetString(), name, StringComparison.Ordinal))
                return asset;
        return null;
    }

    private static Uri GetDownloadUri(JsonElement asset)
    {
        var urlText = asset.GetProperty("browser_download_url").GetString();
        if (!Uri.TryCreate(urlText, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("La URL del instalador publicada no es válida.");
        return uri;
    }

    private static string? GetAssetDigest(JsonElement asset)
    {
        if (!asset.TryGetProperty("digest", out var digestElement) || digestElement.ValueKind != JsonValueKind.String)
            return null;
        var digest = digestElement.GetString();
        return digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? digest["sha256:".Length..]
            : null;
    }

    private static async Task DownloadInstallerAsync(Uri uri, string destination, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxInstallerBytes)
            throw new InvalidDataException("El instalador supera el tamaño máximo permitido.");

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];
        long totalBytes = 0;
        int bytesRead;
        while ((bytesRead = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            totalBytes += bytesRead;
            if (totalBytes > MaxInstallerBytes)
                throw new InvalidDataException("El instalador supera el tamaño máximo permitido.");
            await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
        }
        if (totalBytes == 0) throw new InvalidDataException("GitHub entregó un instalador vacío.");
    }
}
