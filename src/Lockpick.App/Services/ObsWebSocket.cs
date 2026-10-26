using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lockpick.App.Services;

/// <summary>Something OBS refused or couldn't do, in words for the player; <see cref="Code"/> is OBS's request status or close code.</summary>
public sealed class ObsException(string message, int code = 0) : Exception(message)
{
    /// <summary>The request status OBS sent (600 not found, 601 already exists, 500 output running, 501 not running…), the
    /// close code it ended the connection with (4009 wrong password), or 0.</summary>
    public int Code { get; } = code;
}

/// <summary>
/// A small client for the WebSocket server built into OBS 28+ (obs-websocket 5, checked against OBS 32.2.2 /
/// WebSocket 5.7.4; protocol: github.com/obsproject/obs-websocket, docs/generated/protocol.md). OBS says Hello (with
/// a challenge when it has a password), Lockpick answers Identify, OBS says Identified; then requests go out with an id
/// each and their responses come back by it. No events are subscribed to: Lockpick only asks.
/// </summary>
public sealed class ObsWebSocket : IAsyncDisposable
{
    /// <summary>OBS's request status for "already exists" (a scene or source of that name).</summary>
    public const int AlreadyExists = 601;

    /// <summary>OBS's request status for "not found".</summary>
    public const int NotFound = 600;

    /// <summary>An output (recording, replay buffer) that is already running.</summary>
    public const int OutputRunning = 500;

    /// <summary>An output that isn't running.</summary>
    public const int OutputNotRunning = 501;

    /// <summary>OBS's close code for a wrong (or missing) password: trying again won't help until it changes.</summary>
    public const int PasswordRefused = 4009;

    /// <summary>OBS's close code for a protocol version it doesn't speak.</summary>
    public const int TooOld = 4010;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>Fields left out rather than sent as null (OBS checks that requestData and authentication are an object and a string).</summary>
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private readonly ClientWebSocket _socket;
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _stop = new();
    private Task _receive = Task.CompletedTask;
    private long _nextId;
    private int _disposed;

    private ObsWebSocket(ClientWebSocket socket) => _socket = socket;

    /// <summary>OBS's own version and its WebSocket server's (from Hello and GetVersion).</summary>
    public string ObsVersion { get; private set; } = string.Empty;

    public string WebSocketVersion { get; private set; } = string.Empty;

    /// <summary>Done when the connection has closed (OBS quit, the network went, or <see cref="DisposeAsync"/>).</summary>
    public Task Closed => _receive;

    /// <summary>Connects and identifies; throws <see cref="ObsException"/> with the reason when OBS isn't there or refuses.</summary>
    public static async Task<ObsWebSocket> ConnectAsync(string host, int port, string? password, CancellationToken cancel = default)
    {
        var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol("obswebsocket.json");
        var obs = new ObsWebSocket(socket);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(Timeout);
            try
            {
                await socket.ConnectAsync(new Uri($"ws://{Bracketed(host)}:{port.ToString(CultureInfo.InvariantCulture)}"), timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException && !cancel.IsCancellationRequested)
            {
                throw new ObsException($"No OBS WebSocket server at {host}:{port}. In OBS: Tools → WebSocket Server Settings → Enable WebSocket server.");
            }

            var hello = await obs.ReadAsync(timeout.Token).ConfigureAwait(false) ?? throw obs.ClosedError();
            if (Op(hello) != 0)
            {
                throw new ObsException("That isn't an OBS WebSocket server (it didn't say hello).");
            }

            var d = hello.GetProperty("d");
            obs.WebSocketVersion = d.TryGetProperty("obsWebSocketVersion", out var version) ? version.GetString() ?? string.Empty : string.Empty;
            string? answer = null;
            if (d.TryGetProperty("authentication", out var auth))
            {
                if (string.IsNullOrEmpty(password))
                {
                    throw new ObsException("OBS asks for its WebSocket password (OBS: Tools → WebSocket Server Settings → Show Connect Info).", PasswordRefused);
                }

                answer = Answer(password, auth.GetProperty("salt").GetString()!, auth.GetProperty("challenge").GetString()!);
            }

            await obs.SendAsync(new { op = 1, d = new { rpcVersion = 1, authentication = answer, eventSubscriptions = 0 } }, timeout.Token).ConfigureAwait(false);
            var identified = await obs.ReadAsync(timeout.Token).ConfigureAwait(false) ?? throw obs.ClosedError();
            if (Op(identified) != 2)
            {
                throw new ObsException("OBS didn't accept Lockpick's hello.");
            }

            obs._receive = Task.Run(() => obs.ReceiveLoopAsync(obs._stop.Token), CancellationToken.None);
            var info = await obs.RequestAsync("GetVersion", cancel: cancel).ConfigureAwait(false);
            obs.ObsVersion = info.TryGetProperty("obsVersion", out var obsVersion) ? obsVersion.GetString() ?? string.Empty : string.Empty;
            return obs;
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            await obs.DisposeAsync().ConfigureAwait(false);
            throw new ObsException($"OBS at {host}:{port} didn't answer.");
        }
        catch
        {
            await obs.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The answer to OBS's challenge: base64(sha256(base64(sha256(password + salt)) + challenge)).</summary>
    internal static string Answer(string password, string salt, string challenge)
    {
        var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    /// <summary>Sends one request and returns its response data (an empty object when there is none).</summary>
    public async Task<JsonElement> RequestAsync(string type, object? data = null, CancellationToken cancel = default)
    {
        var id = Interlocked.Increment(ref _nextId).ToString(CultureInfo.InvariantCulture);
        var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(Timeout);
            await SendAsync(new { op = 6, d = new { requestType = type, requestId = id, requestData = data } }, timeout.Token).ConfigureAwait(false);
            var response = await answer.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            var status = response.GetProperty("requestStatus");
            if (!status.GetProperty("result").GetBoolean())
            {
                var code = status.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
                var comment = status.TryGetProperty("comment", out var m) ? m.GetString() : null;
                throw new ObsException(comment is { Length: > 0 } ? $"OBS: {comment}" : $"OBS refused {type} (code {code}).", code);
            }

            return response.TryGetProperty("responseData", out var result) ? result : default;
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new ObsException($"OBS didn't answer {type}.");
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
            throw new ObsException("The connection to OBS closed.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
            }
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _receive.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObsException)
        {
        }

        _socket.Dispose();
        _send.Dispose();
        _stop.Dispose();
    }

    private async Task ReceiveLoopAsync(CancellationToken stop)
    {
        try
        {
            while (await ReadAsync(stop).ConfigureAwait(false) is { } message)
            {
                // op 7: a request's response; events (op 5) aren't subscribed to, anything else is ignored.
                if (Op(message) == 7 && message.GetProperty("d") is var d
                    && d.TryGetProperty("requestId", out var id) && _pending.TryGetValue(id.GetString() ?? string.Empty, out var answer))
                {
                    answer.TrySetResult(d.Clone());
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or JsonException or InvalidOperationException)
        {
        }
        finally
        {
            var closed = ClosedError();
            foreach (var answer in _pending.Values)
            {
                answer.TrySetException(closed);
            }
        }
    }

    /// <summary>The next whole message, or null once OBS has closed the connection.</summary>
    private async Task<JsonElement?> ReadAsync(CancellationToken cancel)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer, cancel).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        using var document = JsonDocument.Parse(message.ToArray());
        return document.RootElement.Clone();
    }

    private async Task SendAsync(object message, CancellationToken cancel)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        await _send.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancel).ConfigureAwait(false);
        }
        finally
        {
            _send.Release();
        }
    }

    /// <summary>Why OBS closed the connection, from its close code (protocol: WebSocketCloseCode).</summary>
    private ObsException ClosedError() => (int?)_socket.CloseStatus switch
    {
        PasswordRefused => new ObsException("OBS refused the password (OBS: Tools → WebSocket Server Settings → Show Connect Info).", PasswordRefused),
        TooOld => new ObsException("This OBS's WebSocket server is too old for Lockpick: update OBS (28 or later).", TooOld),
        4011 => new ObsException("OBS closed the connection (another app asked it to)."),
        _ => new ObsException(_socket.CloseStatusDescription is { Length: > 0 } why ? $"OBS closed the connection: {why}" : "The connection to OBS closed."),
    };

    private static int Op(JsonElement message) => message.TryGetProperty("op", out var op) ? op.GetInt32() : -1;

    private static string Bracketed(string host) => host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[') ? $"[{host}]" : host;
}

/// <summary>
/// The WebSocket server settings of OBS on this PC (%APPDATA%\obs-studio\plugin_config\obs-websocket\config.json), read
/// only when the player asks Lockpick to fill them in.
/// </summary>
public sealed record ObsServerConfig(bool Enabled, int Port, string? Password)
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio", "plugin_config", "obs-websocket", "config.json");

    /// <summary>The settings, or null when OBS (28+) hasn't been set up on this PC for this Windows user.</summary>
    public static ObsServerConfig? Read(string? path = null)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path ?? DefaultPath));
            var root = document.RootElement;
            var enabled = root.TryGetProperty("server_enabled", out var e) && e.ValueKind == JsonValueKind.True;
            var port = root.TryGetProperty("server_port", out var p) && p.TryGetInt32(out var n) ? n : 4455;
            var auth = !root.TryGetProperty("auth_required", out var a) || a.ValueKind != JsonValueKind.False;
            var password = auth && root.TryGetProperty("server_password", out var s) ? s.GetString() : null;
            return new ObsServerConfig(enabled, port, string.IsNullOrEmpty(password) ? null : password);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
