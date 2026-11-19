using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ArideevSunoPresence;

/// <summary>
/// Small loopback-only HTTP endpoint for the browser extension.
/// TcpListener avoids Http.sys URL-ACL/admin requirements on Windows.
/// </summary>
public sealed class LocalBridge : IDisposable
{
    private const int MaxBodyBytes = 32 * 1024;
    private const int MaxHeaderBytes = 16 * 1024;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Action<TrackInfo> _onTrack;
    private readonly Action<string>? _onError;

    public LocalBridge(int port, Action<TrackInfo> onTrack, Action<string>? onError = null)
    {
        _onTrack = onTrack;
        _onError = onError;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public Task StartAsync()
    {
        _listener.Start(16);
        _ = Task.Run(ListenLoopAsync);
        return Task.CompletedTask;
    }

    private async Task ListenLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) when (_stop.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) { break; }
            _ = Task.Run(() => HandleClientAsync(client, _stop.Token));
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                await using var stream = client.GetStream();
                var requestHead = await ReadHeaderAsync(stream, cancellationToken).ConfigureAwait(false);
                if (requestHead is null) return;
                var headerText = Encoding.ASCII.GetString(requestHead.Value.Header);
                var lines = headerText.Split("\r\n", StringSplitOptions.None);
                var requestParts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (requestParts.Length < 2)
                {
                    await WriteResponseAsync(stream, 400, "Bad Request", "", null, cancellationToken).ConfigureAwait(false);
                    return;
                }

                var method = requestParts[0];
                var path = requestParts[1].Split('?', 2)[0];
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1))
                {
                    var colon = line.IndexOf(':');
                    if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }

                headers.TryGetValue("Origin", out var origin);

                // A request without Origin is allowed only for local diagnostics (PowerShell).
                // Browser extension requests must present the real chrome-extension:// origin.
                var hasBrowserOrigin = !string.IsNullOrWhiteSpace(origin);
                var allowedOrigin = !hasBrowserOrigin || IsAllowedExtensionOrigin(origin);
                if (!allowedOrigin)
                {
                    _onError?.Invoke("Puente local: bloqueada una solicitud con origen de extensión no válido.");
                    await WriteResponseAsync(stream, 403, "Forbidden", "", null, cancellationToken).ConfigureAwait(false);
                    return;
                }

                var corsHeaders = new Dictionary<string, string>
                {
                    ["Vary"] = "Origin",
                    ["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS",
                    ["Access-Control-Allow-Headers"] = "Content-Type",
                    ["Access-Control-Max-Age"] = "600"
                };
                if (hasBrowserOrigin)
                    corsHeaders["Access-Control-Allow-Origin"] = origin!;

                // Standard CORS is required for the extension popup to read the response.
                // Chromium's current Local Network Access model uses a user permission
                // (loopback-network), not the older Private Network Access preflight header.
                if (method == "OPTIONS")
                {
                    await WriteResponseAsync(stream, 204, "No Content", "", corsHeaders, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (method == "GET" && path == "/health")
                {
                    var healthJson = JsonSerializer.Serialize(new
                    {
                        ok = true,
                        service = "aarideev | SunoSync",
                        version = "0.3.11"
                    });
                    await WriteResponseAsync(stream, 200, "OK", healthJson,
                        corsHeaders, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (!hasBrowserOrigin || !IsAllowedExtensionOrigin(origin))
                {
                    _onError?.Invoke("Puente local: el envío de pistas requiere el origen válido de la extensión.");
                    await WriteResponseAsync(stream, 403, "Forbidden", "", null, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (method != "POST" || path != "/track")
                {
                    _onError?.Invoke($"Puente local: ruta no reconocida ({method} {path}).");
                    await WriteResponseAsync(stream, 404, "Not Found", "", corsHeaders, cancellationToken).ConfigureAwait(false);
                    return;
                }
                if (!headers.TryGetValue("Content-Type", out var contentType)
                    || !contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                {
                    _onError?.Invoke("Puente local: la extensión no envió application/json.");
                    await WriteResponseAsync(stream, 415, "Unsupported Media Type", "", corsHeaders, cancellationToken).ConfigureAwait(false);
                    return;
                }
                if (!headers.TryGetValue("Content-Length", out var contentLengthText)
                    || !int.TryParse(contentLengthText, out var contentLength)
                    || contentLength < 0 || contentLength > MaxBodyBytes)
                {
                    _onError?.Invoke("Puente local: tamaño del mensaje no válido.");
                    await WriteResponseAsync(stream, 413, "Payload Too Large", "", corsHeaders, cancellationToken).ConfigureAwait(false);
                    return;
                }

                var body = await ReadBodyAsync(stream, contentLength, requestHead.Value.InitialBody, cancellationToken).ConfigureAwait(false);
                var track = JsonSerializer.Deserialize<TrackInfo>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (track is null)
                {
                    await WriteResponseAsync(stream, 400, "Bad Request", "", corsHeaders, cancellationToken).ConfigureAwait(false);
                    return;
                }

                _onTrack(track.Sanitized());
                await WriteResponseAsync(stream, 200, "OK", "{\"ok\":true}", corsHeaders, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _onError?.Invoke(ex.Message); }
        }
    }

    private static bool IsAllowedExtensionOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin) || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, "chrome-extension", StringComparison.OrdinalIgnoreCase)) return false;
        // Chromium extension IDs are 32 letters from a-p (Chrome and Edge).
        return uri.Host.Length == 32 && uri.Host.All(ch => ch is >= 'a' and <= 'p');
    }

    private static async Task<(byte[] Header, byte[] InitialBody)?> ReadHeaderAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        var chunk = new byte[1024];
        while (memory.Length < MaxHeaderBytes)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) return null;
            memory.Write(chunk, 0, read);
            var data = memory.GetBuffer();
            var length = checked((int)memory.Length);
            for (var i = 3; i < length; i++)
            {
                if (data[i - 3] == 13 && data[i - 2] == 10 && data[i - 1] == 13 && data[i] == 10)
                {
                    var headerEnd = i + 1;
                    return (data[..headerEnd], data[headerEnd..length]);
                }
            }
        }
        throw new InvalidDataException("HTTP header demasiado grande.");
    }

    private static async Task<byte[]> ReadBodyAsync(NetworkStream stream, int contentLength, byte[] initialBody, CancellationToken cancellationToken)
    {
        if (initialBody.Length > contentLength) throw new InvalidDataException("El cuerpo HTTP contiene bytes inesperados.");
        var body = new byte[contentLength];
        initialBody.CopyTo(body, 0);
        var offset = initialBody.Length;
        while (offset < contentLength)
        {
            var read = await stream.ReadAsync(body.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("El navegador cerró la conexión antes de enviar el JSON completo.");
            offset += read;
        }
        return body;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int status, string reason, string body,
        Dictionary<string, string>? headers, CancellationToken cancellationToken)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var builder = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n")
            .Append("Connection: close\r\n")
            .Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n")
            .Append("Content-Type: application/json; charset=utf-8\r\n");
        if (headers is not null)
            foreach (var header in headers) builder.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
        builder.Append("\r\n");
        var headerBytes = Encoding.ASCII.GetBytes(builder.ToString());
        await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        if (bodyBytes.Length > 0) await stream.WriteAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }
}
