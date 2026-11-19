namespace ArideevSunoPresence;

public sealed class TrackInfo
{
    public bool HasTrack { get; set; }
    public string Title { get; set; } = "Canción desconocida";
    public string Artist { get; set; } = "Artista desconocido";
    public string? CoverUrl { get; set; }
    public string? TrackId { get; set; }
    public string? SourceId { get; set; }
    public string? PageUrl { get; set; }
    public int DurationSeconds { get; set; }
    public double PositionSeconds { get; set; }
    public bool IsPlaying { get; set; }
    public string Source { get; set; } = "browser";

    // Browser diagnostics. These fields are informational and never contain page content
    // beyond the current track's visible metadata.
    public bool MediaSessionAvailable { get; set; }
    public bool MediaSessionHasMetadata { get; set; }
    public string MediaSessionState { get; set; } = "unknown";
    public int AudioElementCount { get; set; }
    public int VideoElementCount { get; set; }
    public int PlayingMediaCount { get; set; }
    public string DiagnosticMessage { get; set; } = "";

    public string Fingerprint => string.Join("|", TrackId ?? "", Title, Artist, CoverUrl ?? "", IsPlaying);

    public TrackInfo Sanitized()
    {
        static string Clean(string? value, int max, string fallback)
        {
            var result = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            return string.IsNullOrWhiteSpace(result) ? fallback : result[..Math.Min(result.Length, max)];
        }

        var cover = SafeHttps(CoverUrl, 2048);
        var page = SafeSunoPage(PageUrl);
        return new TrackInfo
        {
            HasTrack = HasTrack,
            Title = Clean(Title, 120, "Canción desconocida"),
            Artist = Clean(Artist, 100, "Artista desconocido"),
            CoverUrl = cover,
            TrackId = Clean(TrackId, 300, ""),
            SourceId = Clean(SourceId, 64, "unknown"),
            PageUrl = page ?? "https://suno.com",
            DurationSeconds = Math.Clamp(DurationSeconds, 0, 86400),
            PositionSeconds = double.IsFinite(PositionSeconds) ? Math.Clamp(PositionSeconds, 0, 86400) : 0,
            IsPlaying = HasTrack && IsPlaying,
            Source = Clean(Source, 20, "Suno"),
            MediaSessionAvailable = MediaSessionAvailable,
            MediaSessionHasMetadata = MediaSessionHasMetadata,
            MediaSessionState = Clean(MediaSessionState, 24, "unknown"),
            AudioElementCount = Math.Clamp(AudioElementCount, 0, 200),
            VideoElementCount = Math.Clamp(VideoElementCount, 0, 200),
            PlayingMediaCount = Math.Clamp(PlayingMediaCount, 0, 200),
            DiagnosticMessage = Clean(DiagnosticMessage, 240, "")
        };
    }

    private static string? SafeHttps(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        return uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo) ? uri.ToString() : null;
    }

    private static string? SafeSunoPage(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && value.Length <= 512
            && Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo)
            && (uri.Host.Equals("suno.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".suno.com", StringComparison.OrdinalIgnoreCase)))
            return uri.ToString();
        return null;
    }
}
