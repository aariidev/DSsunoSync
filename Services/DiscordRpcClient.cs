using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ArideevSunoPresence;

/// <summary>Discord Rich Presence IPC with handshake, command acknowledgements and reconnect support.</summary>
public sealed class DiscordRpcClient : IAsyncDisposable
{
    private const int MaxFrameBytes = 1024 * 1024;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private NamedPipeClientStream? _pipe;
    private CancellationTokenSource? _readerStop;
    private Task? _readerTask;
    private string? _connectedClientId;
    private volatile bool _ready;

    public bool IsConnected => _ready && _pipe?.IsConnected == true;
    public event Action<string>? ConnectionChanged;

    public async Task SetActivityAsync(string appId, TrackInfo track, DateTimeOffset? playbackStartedAt, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(appId, cancellationToken).ConfigureAwait(false);

        var activity = new Dictionary<string, object?>
        {
            ["name"] = "Suno",
            ["type"] = 2,
            ["details"] = Limit(track.Title, 128),
            ["state"] = track.IsPlaying
                ? Limit(track.Artist, 128)
                : Limit($"En pausa · {track.Artist}", 128),
            ["assets"] = BuildAssets(track)
        };

        if (track.IsPlaying && playbackStartedAt.HasValue)
        {
            var startUnixSeconds = playbackStartedAt.Value.ToUnixTimeSeconds();
            var timestamps = new Dictionary<string, long> { ["start"] = startUnixSeconds };
            if (track.DurationSeconds > 0)
                timestamps["end"] = startUnixSeconds + track.DurationSeconds;
            activity["timestamps"] = timestamps;
        }

        var targetUrl = track.PageUrl is { Length: > 0 } ? track.PageUrl : "https://suno.com";
        if (targetUrl.Length <= 512)
            activity["buttons"] = new[] { new Dictionary<string, string> { ["label"] = "Abrir canción", ["url"] = targetUrl } };

        await SendCommandAndWaitAsync("SET_ACTIVITY",
            new Dictionary<string, object?> { ["pid"] = Environment.ProcessId, ["activity"] = activity },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearActivityAsync(string appId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(appId)) return;
        await EnsureConnectedAsync(appId, cancellationToken).ConfigureAwait(false);
        await SendCommandAndWaitAsync("SET_ACTIVITY",
            new Dictionary<string, object?> { ["pid"] = Environment.ProcessId, ["activity"] = null },
            cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, object?> BuildAssets(TrackInfo track)
    {
        var assets = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(track.Album)
            && !string.Equals(track.Album, track.Title, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(track.Album, track.Artist, StringComparison.OrdinalIgnoreCase))
            assets["large_text"] = Limit(track.Album, 128);
        // Discord accepts publicly reachable external HTTPS images for large_image.
        // Keep within a conservative URL length; an overlong CDN signature should not
        // prevent the text activity itself from being published.
        if (!string.IsNullOrWhiteSpace(track.CoverUrl)
            && track.CoverUrl.Length <= 256
            && Uri.TryCreate(track.CoverUrl, UriKind.Absolute, out var imageUri)
            && imageUri.Scheme == Uri.UriSchemeHttps)
            assets["large_image"] = imageUri.ToString();
        if (!string.IsNullOrWhiteSpace(track.PageUrl) && track.PageUrl.Length <= 512)
            assets["large_url"] = track.PageUrl;
        return assets;
    }

    private async Task EnsureConnectedAsync(string appId, CancellationToken cancellationToken)
    {
        if (!IsValidApplicationId(appId))
            throw new ArgumentException("El Application ID debe contener entre 17 y 20 dígitos.", nameof(appId));
        if (IsConnected && _connectedClientId == appId) return;

        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected && _connectedClientId == appId) return;
            await DisconnectAsync().ConfigureAwait(false);

            Exception? lastError = null;
            for (var index = 0; index < 10; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = new NamedPipeClientStream(".", $"discord-ipc-{index}", PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await candidate.ConnectAsync(350, cancellationToken).ConfigureAwait(false);
                    _pipe = candidate;

                    var handshake = JsonSerializer.SerializeToUtf8Bytes(new { v = 1, client_id = appId });
                    await SendFrameAsync(0, handshake, cancellationToken).ConfigureAwait(false);

                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(6));
                    var ready = false;
                    while (!ready)
                    {
                        var frame = await ReadFrameAsync(candidate, timeout.Token).ConfigureAwait(false);
                        if (frame.Opcode == 3)
                        {
                            await SendFrameAsync(4, frame.Payload, cancellationToken).ConfigureAwait(false);
                            continue;
                        }
                        if (frame.Opcode == 2)
                            throw new IOException("Discord cerró el canal IPC durante el handshake.");
                        if (frame.Opcode != 1) continue;

                        using var document = JsonDocument.Parse(frame.Payload);
                        var root = document.RootElement;
                        var evt = root.TryGetProperty("evt", out var evtElement) ? evtElement.GetString() : null;
                        if (evt == "READY") ready = true;
                        else if (evt == "ERROR")
                        {
                            var message = GetRpcError(root);
                            throw new IOException($"Discord rechazó el handshake: {message}");
                        }
                    }

                    _connectedClientId = appId;
                    _ready = true;
                    _readerStop = new CancellationTokenSource();
                    var readerToken = _readerStop.Token;
                    _readerTask = Task.Run(() => ReadLoopAsync(candidate, readerToken));
                    ConnectionChanged?.Invoke("Conectado a Discord (handshake READY)");
                    return;
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or JsonException)
                {
                    lastError = ex;
                    candidate.Dispose();
                    if (ReferenceEquals(_pipe, candidate)) _pipe = null;
                    _connectedClientId = null;
                    _ready = false;
                    if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
                }
            }
            throw new IOException("No se pudo completar el handshake con Discord. Comprueba que Discord de escritorio esté abierto y que no sea la versión web.", lastError);
        }
        finally { _connectLock.Release(); }
    }

    private async Task SendCommandAndWaitAsync(string command, object args, CancellationToken cancellationToken)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(nonce, completion))
            throw new InvalidOperationException("No se pudo registrar la respuesta RPC.");

        try
        {
            var request = new Dictionary<string, object?>
            {
                ["cmd"] = command,
                ["args"] = args,
                ["nonce"] = nonce
            };
            await SendFrameAsync(1, JsonSerializer.SerializeToUtf8Bytes(request), cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            JsonElement response = await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);

            if (response.TryGetProperty("evt", out var evt) && evt.GetString() == "ERROR")
                throw new IOException($"Discord rechazó {command}: {GetRpcError(response)}");

            if (response.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("error", out var error))
                throw new IOException($"Discord rechazó {command}: {error}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Discord no confirmó el comando {command} en 5 segundos.");
        }
        finally
        {
            _pending.TryRemove(nonce, out _);
        }
    }

    private async Task ReadLoopAsync(NamedPipeClientStream pipe, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
            {
                var frame = await ReadFrameAsync(pipe, cancellationToken).ConfigureAwait(false);
                if (frame.Opcode == 3)
                {
                    await SendFrameAsync(4, frame.Payload, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (frame.Opcode == 2) break;
                if (frame.Opcode != 1) continue;

                using var document = JsonDocument.Parse(frame.Payload);
                var root = document.RootElement;
                if (root.TryGetProperty("nonce", out var nonceElement))
                {
                    var nonce = nonceElement.GetString();
                    if (!string.IsNullOrWhiteSpace(nonce)
                        && _pending.TryRemove(nonce, out var pending))
                        pending.TrySetResult(root.Clone());
                }

                if (root.TryGetProperty("evt", out var evt) && evt.GetString() == "ERROR")
                    ConnectionChanged?.Invoke($"Discord RPC: {GetRpcError(root)}");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ConnectionChanged?.Invoke($"Discord desconectado: {ex.Message}"); }
        finally
        {
            _ready = false;
            if (ReferenceEquals(_pipe, pipe))
            {
                _connectedClientId = null;
                try { pipe.Dispose(); } catch { }
                _pipe = null;
            }
            foreach (var pending in _pending.Values)
                pending.TrySetException(new IOException("El canal IPC de Discord se ha cerrado."));
            _pending.Clear();
            ConnectionChanged?.Invoke("Discord desconectado; reintentando...");
        }
    }

    private static string GetRpcError(JsonElement root)
    {
        if (root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object)
        {
            var message = data.TryGetProperty("message", out var msg) ? msg.GetString() : null;
            var code = data.TryGetProperty("code", out var codeElement) ? codeElement.ToString() : null;
            if (!string.IsNullOrWhiteSpace(message))
                return string.IsNullOrWhiteSpace(code) ? message : $"{message} (código {code})";
        }
        return "error no especificado";
    }

    private async Task SendFrameAsync(int opcode, byte[] payload, CancellationToken cancellationToken)
    {
        if (_pipe is null || !_pipe.IsConnected) throw new IOException("El canal RPC de Discord no está conectado.");
        if (payload.Length > MaxFrameBytes) throw new InvalidDataException("El mensaje RPC supera el tamaño permitido.");
        var header = new byte[8];
        BitConverter.GetBytes(opcode).CopyTo(header, 0);
        BitConverter.GetBytes(payload.Length).CopyTo(header, 4);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pipe = _pipe;
            if (pipe is null || !pipe.IsConnected) throw new IOException("Discord IPC se ha cerrado.");
            await pipe.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await pipe.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    private static async Task<(int Opcode, byte[] Payload)> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[8];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        var opcode = BitConverter.ToInt32(header, 0);
        var length = BitConverter.ToInt32(header, 4);
        if (length < 0 || length > MaxFrameBytes) throw new InvalidDataException($"Longitud RPC no válida: {length}");
        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return (opcode, payload);
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Discord cerró el canal IPC.");
            offset += read;
        }
    }

    private async Task DisconnectAsync()
    {
        _ready = false;
        _readerStop?.Cancel();
        try { if (_readerTask is not null) await _readerTask.ConfigureAwait(false); } catch { }
        _readerStop?.Dispose();
        _readerStop = null;
        _readerTask = null;
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
        _connectedClientId = null;
        foreach (var pending in _pending.Values)
            pending.TrySetException(new IOException("Discord IPC se ha desconectado."));
        _pending.Clear();
    }

    private static bool IsValidApplicationId(string value) => value.Length is >= 17 and <= 20 && value.All(char.IsDigit);
    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max];

    public async ValueTask DisposeAsync()
    {
        await _connectLock.WaitAsync().ConfigureAwait(false);
        try { await DisconnectAsync().ConfigureAwait(false); }
        finally { _connectLock.Release(); _connectLock.Dispose(); _writeLock.Dispose(); }
    }
}
