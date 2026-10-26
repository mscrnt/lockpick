using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lockpick.Tests.Fakes;

/// <summary>
/// A stand-in for OBS's WebSocket server (obs-websocket 5: Hello with a challenge, Identify, Identified, then requests
/// and their responses), keeping just enough of OBS's state — recording, replay buffer, scenes, inputs — for the
/// requests Lockpick sends, and answering failures with OBS's own status codes.
/// </summary>
public sealed class FakeObs : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<WebSocket> _sockets = [];
    private readonly object _gate = new();
    private int _nextItem = 1;

    public FakeObs(string? password = "secret")
    {
        Password = password;
        _listener.Start();
        _ = Task.Run(AcceptAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string? Password { get; set; }

    /// <summary>Every request, in order: its type and data.</summary>
    public ConcurrentQueue<(string Type, JsonNode? Data)> Requests { get; } = new();

    private int _connections;

    /// <summary>How many times Lockpick has connected.</summary>
    public int Connections => Volatile.Read(ref _connections);

    private bool _recording;
    private DateTime? _startsAt;

    /// <summary>Recording (set by a test: at once). Started by a request, it runs <see cref="StartDelay"/> later, as in OBS.</summary>
    public bool Recording
    {
        get => _recording || (_startsAt is { } at && DateTime.UtcNow >= at);
        set => (_recording, _startsAt) = (value, null);
    }

    /// <summary>How long after StartRecord the recording runs (OBS answers first; chapters before then are refused).</summary>
    public TimeSpan StartDelay { get; set; } = TimeSpan.FromMilliseconds(300);

    public bool ReplayBuffer { get; set; }

    /// <summary>The replay buffer turned off in OBS's output settings (OBS answers "not available", not "not running").</summary>
    public bool ReplayBufferUnavailable { get; set; }

    public string CurrentScene { get; set; } = "Game";

    /// <summary>Scene names, top to bottom as OBS's Scenes dock shows them.</summary>
    public List<string> Scenes { get; } = ["Game", "Paused"];

    /// <summary>Inputs by name: their kind and settings.</summary>
    public Dictionary<string, (string Kind, JsonObject Settings)> Inputs { get; } = [];

    /// <summary>Scene items: (scene, source).</summary>
    public HashSet<(string Scene, string Source)> Items { get; } = [];

    public List<string> Chapters { get; } = [];

    public bool Has(string type) => Requests.Any(r => r.Type == type);

    public int Count(string type) => Requests.Count(r => r.Type == type);

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        foreach (var socket in _sockets)
        {
            socket.Abort();
            socket.Dispose();
        }

        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        try
        {
            // The HTTP upgrade (RFC 6455): the key's SHA-1 with the protocol's GUID.
            var request = await ReadHeadersAsync(stream);
            var key = request.Split("\r\n").First(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))[18..].Trim();
#pragma warning disable CA5350 // SHA-1 is what the WebSocket handshake is defined with, not a security choice
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
#pragma warning restore CA5350
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\nSec-WebSocket-Protocol: obswebsocket.json\r\n\r\n"));
            using var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, SubProtocol = "obswebsocket.json" });
            _sockets.Add(socket);
            Interlocked.Increment(ref _connections);

            var (challenge, salt) = ("challenge-" + Guid.NewGuid().ToString("N"), "salt-" + Guid.NewGuid().ToString("N"));
            var hello = new JsonObject { ["obsWebSocketVersion"] = "5.7.4", ["rpcVersion"] = 1 };
            if (Password is not null)
            {
                hello["authentication"] = new JsonObject { ["challenge"] = challenge, ["salt"] = salt };
            }

            await SendAsync(socket, 0, hello);
            var identify = await ReceiveAsync(socket);
            if (identify?["op"]?.GetValue<int>() != 1)
            {
                return;
            }

            if (Password is not null)
            {
                var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(Password + salt)));
                var expected = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
                if (identify["d"]?["authentication"]?.GetValue<string>() != expected)
                {
                    await socket.CloseAsync((WebSocketCloseStatus)4009, "Authentication failed.", _stop.Token);
                    return;
                }
            }

            await SendAsync(socket, 2, new JsonObject { ["negotiatedRpcVersion"] = 1 });
            while (await ReceiveAsync(socket) is { } message)
            {
                if (message["op"]?.GetValue<int>() != 6)
                {
                    continue;
                }

                var d = message["d"]!;
                var type = d["requestType"]!.GetValue<string>();
                var data = d["requestData"]?.DeepClone();
                Requests.Enqueue((type, data));
                var (code, response) = Handle(type, data as JsonObject ?? []);
                var status = new JsonObject { ["result"] = code == 100, ["code"] = code };
                if (code != 100)
                {
                    status["comment"] = $"fake OBS: {type} failed ({code})";
                }

                var reply = new JsonObject { ["requestType"] = type, ["requestId"] = d["requestId"]!.GetValue<string>(), ["requestStatus"] = status };
                if (response is not null)
                {
                    reply["responseData"] = response;
                }

                await SendAsync(socket, 7, reply);
            }
        }
        catch (Exception ex) when (ex is IOException or WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private (int Code, JsonObject? Response) Handle(string type, JsonObject data)
    {
        lock (_gate)
        {
            string Text(string name) => data[name]!.GetValue<string>();
            switch (type)
            {
                case "GetVersion":
                    return (100, new JsonObject { ["obsVersion"] = "32.2.2", ["obsWebSocketVersion"] = "5.7.4" });
                case "GetRecordStatus":
                    return (100, new JsonObject { ["outputActive"] = Recording });
                case "StartRecord":
                    if (Recording || _startsAt is not null)
                    {
                        return (500, null);
                    }

                    _startsAt = DateTime.UtcNow + StartDelay;
                    return (100, null);
                case "StopRecord":
                    if (!Recording)
                    {
                        return (501, null);
                    }

                    Recording = false;
                    return (100, new JsonObject { ["outputPath"] = "C:/fake.mp4" });
                case "CreateRecordChapter":
                    if (!Recording)
                    {
                        return (501, null);
                    }

                    Chapters.Add(Text("chapterName"));
                    return (100, null);
                case "SaveReplayBuffer":
                    return (ReplayBufferUnavailable ? 604 : ReplayBuffer ? 100 : 501, null);
                case "GetCurrentProgramScene":
                    return (100, new JsonObject { ["sceneName"] = CurrentScene, ["currentProgramSceneName"] = CurrentScene });
                case "SetCurrentProgramScene":
                    if (!Scenes.Contains(Text("sceneName")))
                    {
                        return (600, null);
                    }

                    CurrentScene = Text("sceneName");
                    return (100, null);
                case "GetSceneList":
                    // As OBS sends them: bottom of the dock first.
                    return (100, new JsonObject { ["scenes"] = new JsonArray([.. Enumerable.Reverse(Scenes).Select((s, i) => (JsonNode)new JsonObject { ["sceneName"] = s, ["sceneIndex"] = i })]) });
                case "GetVideoSettings":
                    return (100, new JsonObject { ["baseWidth"] = 1920, ["baseHeight"] = 1080 });
                case "CreateScene":
                    if (Scenes.Contains(Text("sceneName")))
                    {
                        return (601, null);
                    }

                    Scenes.Insert(0, Text("sceneName"));
                    return (100, null);
                case "CreateInput":
                    if (Inputs.ContainsKey(Text("inputName")))
                    {
                        return (601, null);
                    }

                    Inputs[Text("inputName")] = (Text("inputKind"), (JsonObject)data["inputSettings"]!.DeepClone());
                    Items.Add((Text("sceneName"), Text("inputName")));
                    return (100, new JsonObject { ["sceneItemId"] = _nextItem++ });
                case "SetInputSettings":
                    if (!Inputs.TryGetValue(Text("inputName"), out var input))
                    {
                        return (600, null);
                    }

                    foreach (var (name, value) in (JsonObject)data["inputSettings"]!)
                    {
                        input.Settings[name] = value?.DeepClone();
                    }

                    return (100, null);
                case "GetSceneItemId":
                    return Items.Contains((Text("sceneName"), Text("sourceName"))) ? (100, new JsonObject { ["sceneItemId"] = 99 }) : (600, null);
                case "CreateSceneItem":
                    Items.Add((Text("sceneName"), Text("sourceName")));
                    return (100, new JsonObject { ["sceneItemId"] = _nextItem++ });
                case "SetSceneItemTransform":
                    return (100, null);
                default:
                    return (204, null);
            }
        }
    }

    private async Task SendAsync(WebSocket socket, int op, JsonObject d) =>
        await socket.SendAsync(Encoding.UTF8.GetBytes(new JsonObject { ["op"] = op, ["d"] = d }.ToJsonString()), WebSocketMessageType.Text, true, _stop.Token);

    private async Task<JsonNode?> ReceiveAsync(WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, _stop.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return JsonNode.Parse(message.ToArray());
            }
        }
    }

    private static async Task<string> ReadHeadersAsync(NetworkStream stream)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (!(bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n'))
        {
            if (await stream.ReadAsync(one) == 0)
            {
                throw new IOException("closed");
            }

            bytes.Add(one[0]);
        }

        return Encoding.ASCII.GetString([.. bytes]);
    }
}
