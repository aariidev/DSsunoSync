namespace ArideevSunoPresence;

/// <summary>Retains the latest browser state and retries publishing it if Discord is restarted.</summary>
public sealed class PresenceCoordinator : IAsyncDisposable
{
    private readonly DiscordRpcClient _rpc = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TrackInfo? _latest;
    private string _appId = "";
    private DateTimeOffset? _startedAt;
    private DateTimeOffset _lastSentAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPositionAt = DateTimeOffset.MinValue;
    private string _lastFingerprint = "";

    public bool IsConnected => _rpc.IsConnected;
    public event Action<string>? StatusChanged;

    public PresenceCoordinator() => _rpc.ConnectionChanged += message => StatusChanged?.Invoke(message);

    public async Task ConfigureAsync(string appId, bool enabled, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previousAppId = _appId;
            var requestedAppId = appId.Trim();
            if (IsValidId(previousAppId) && !string.Equals(previousAppId, requestedAppId, StringComparison.Ordinal))
            {
                try { await _rpc.ClearActivityAsync(previousAppId, cancellationToken).ConfigureAwait(false); } catch { }
            }
            _appId = requestedAppId;
            if (!enabled || !IsValidId(_appId))
            {
                var clearId = IsValidId(_appId) ? _appId : previousAppId;
                if (IsValidId(clearId))
                {
                    try { await _rpc.ClearActivityAsync(clearId, cancellationToken).ConfigureAwait(false); } catch { }
                }
                StatusChanged?.Invoke(enabled ? "Falta un Application ID válido" : "Rich Presence desactivado");
                return;
            }
            if (_latest is { HasTrack: true })
                await PublishLockedAsync(force: true, cancellationToken).ConfigureAwait(false);
            else
                StatusChanged?.Invoke("Application ID configurado; esperando una canción de Suno");
        }
        finally { _gate.Release(); }
    }

    public async Task UpdateTrackAsync(TrackInfo incoming, string appId, bool enabled, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _appId = appId.Trim();
            var track = incoming.Sanitized();
            if (!track.HasTrack)
            {
                _latest = null;
                _lastFingerprint = "";
                _startedAt = null;
                _lastPositionAt = DateTimeOffset.MinValue;
                if (enabled && IsValidId(_appId) && _rpc.IsConnected)
                {
                    try { await _rpc.ClearActivityAsync(_appId, cancellationToken).ConfigureAwait(false); }
                    catch (Exception ex) { StatusChanged?.Invoke(ex.Message); }
                }
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var changed = _latest is null || !string.Equals(_latest.TrackId ?? _latest.Title, track.TrackId ?? track.Title, StringComparison.Ordinal)
                || !string.Equals(_latest.Title, track.Title, StringComparison.Ordinal)
                || !string.Equals(_latest.Artist, track.Artist, StringComparison.Ordinal)
                || !string.Equals(_latest.CoverUrl, track.CoverUrl, StringComparison.Ordinal)
                || !string.Equals(_latest.PageUrl, track.PageUrl, StringComparison.Ordinal);
            var seeked = false;
            if (_latest is { IsPlaying: true } previous && track.IsPlaying && _lastPositionAt != DateTimeOffset.MinValue)
            {
                var expected = previous.PositionSeconds + Math.Max(0, (now - _lastPositionAt).TotalSeconds);
                seeked = Math.Abs(track.PositionSeconds - expected) > 3.0;
            }
            if (changed || seeked) _startedAt = null;
            _latest = track;
            if (track.IsPlaying)
                _startedAt = now - TimeSpan.FromSeconds(Math.Max(0, track.PositionSeconds));
            else
                _startedAt = null;
            _lastPositionAt = now;

            // Remember the last state while disabled or while the ID is not configured.
            // Enabling later immediately publishes the current track, not a stale one.
            if (!enabled || !IsValidId(_appId)) return;
            await PublishLockedAsync(force: changed || seeked || _lastFingerprint != track.Fingerprint, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task TestDiscordAsync(string appId, bool enabled, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _appId = appId.Trim();
            if (!IsValidId(_appId))
                throw new ArgumentException("Introduce un Application ID válido de 17 a 20 dígitos.");
            if (!enabled)
                throw new InvalidOperationException("Activa Rich Presence antes de probar la conexión.");

            StatusChanged?.Invoke("Probando Discord: conectando y enviando actividad temporal...");
            var testTrack = new TrackInfo
            {
                HasTrack = true,
                Title = "Prueba de Rich Presence",
                Artist = "aarideev | SunoSync",
                PageUrl = "https://suno.com",
                IsPlaying = true,
                PositionSeconds = 0,
                DurationSeconds = 0,
                Source = "test"
            };

            await _rpc.SetActivityAsync(_appId, testTrack, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            StatusChanged?.Invoke("Discord aceptó la prueba RPC. Deberías verla durante unos segundos.");

            await Task.Delay(TimeSpan.FromSeconds(6), cancellationToken).ConfigureAwait(false);
            if (_latest is { HasTrack: true })
            {
                await PublishLockedAsync(force: true, cancellationToken).ConfigureAwait(false);
                StatusChanged?.Invoke("Prueba completada; restaurada la canción de Suno.");
            }
            else
            {
                await _rpc.ClearActivityAsync(_appId, cancellationToken).ConfigureAwait(false);
                StatusChanged?.Invoke("Prueba completada: conexión RPC y SET_ACTIVITY funcionan.");
            }
        }
        finally { _gate.Release(); }
    }

    public async Task RetryAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!enabled || _latest is not { HasTrack: true } || !IsValidId(_appId)) return;
            if (!IsConnected || DateTimeOffset.UtcNow - _lastSentAt > TimeSpan.FromSeconds(25))
                await PublishLockedAsync(force: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) { StatusChanged?.Invoke(ex.Message); }
        finally { _gate.Release(); }
    }

    private async Task PublishLockedAsync(bool force, CancellationToken cancellationToken)
    {
        if (_latest is null || !IsValidId(_appId)) return;
        if (!force && DateTimeOffset.UtcNow - _lastSentAt < TimeSpan.FromSeconds(20)) return;
        try
        {
            await _rpc.SetActivityAsync(_appId, _latest, _startedAt, cancellationToken).ConfigureAwait(false);
            _lastSentAt = DateTimeOffset.UtcNow;
            _lastFingerprint = _latest.Fingerprint;
            StatusChanged?.Invoke("Rich Presence actualizado");
        }
        catch (Exception ex) { StatusChanged?.Invoke(ex.Message); }
    }

    private static bool IsValidId(string value) => value.Length is >= 17 and <= 20 && value.All(char.IsDigit);

    public async ValueTask DisposeAsync()
    {
        await _rpc.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
