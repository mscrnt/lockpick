using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lockpick.Api;
using Lockpick.Core.Events;
using Lockpick.Core.State;
using Lockpick.Core.Trainer;
using Lockpick.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lockpick.Tests.Core;

/// <summary>The real server on a free local port, against fake game state and fake memory.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1001", Justification = "Disposed by xUnit through IAsyncLifetime.DisposeAsync.")]
public sealed class ApiServerTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private const ulong Hp = 0x401000;
    private const ulong MaxHp = 0x401002;

    private readonly FakeStateSource _source = new();
    private readonly FakeWriteHost _writeHost = new();
    private readonly MockGameAdapter _adapter = new() { Trainer = new StubTrainer() };
    private readonly FakeCapture _capture = new();
    private readonly TrainerService _trainer;
    private readonly Lockpick.Core.Runs.RunService _runs;
    private readonly ApiServer _server;
    private HttpClient _client = new();

    public ApiServerTests()
    {
        _writeHost.Memory.WriteI16((ulong)Hp, 50).WriteU8(MaxHp, 96);
        _trainer = new TrainerService(_source, _writeHost, new TrainerActionLog(null), NullLogger.Instance);
        _runs = new Lockpick.Core.Runs.RunService(_source, _trainer, _ => null);
        _server = new ApiServer(new ApiHost(_source, _adapter, _trainer, _capture, "9.9.9", Runs: _runs), NullLogger.Instance);
        _source.Attach(1234, TrainerServiceTests.Profile);
        _source.Poll(new GameSnapshot
        {
            InGameplay = Field<bool>.Valid(true),
            Health = Field<Health>.Valid(new Health(50, 96)),
            Location = Field<RoomLocation>.Valid(new RoomLocation(1, 6)),
            Inventory = Field<IReadOnlyList<HeldItem>>.Valid([new HeldItem(0, 2, 15)]),
            RawFields = new Dictionary<string, Field<long>> { ["hp"] = Field<long>.Valid(50), ["maxHp"] = Field<long>.Valid(96) },
        });
    }

    public async Task InitializeAsync() => await Start(allowTrainer: false);

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _server.DisposeAsync();
        _runs.Dispose();
        _trainer.Dispose();
        await _source.DisposeAsync();
    }

    private async Task Start(bool allowTrainer)
    {
        Assert.Null(await _server.StartAsync(new ApiOptions { Port = 0, Token = Token, AllowTrainer = allowTrainer }));
        _client.Dispose();
        _client = new HttpClient { BaseAddress = new Uri(_server.Address!, ApiServer.Prefix + "/") };
        _client.DefaultRequestHeaders.Authorization = new("Bearer", Token);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task Every_request_needs_the_token()
    {
        using var anonymous = new HttpClient { BaseAddress = _client.BaseAddress };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("status")).StatusCode);
        anonymous.DefaultRequestHeaders.Add("X-Lockpick-Token", "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("status")).StatusCode);

        using var plain = new HttpClient { BaseAddress = _client.BaseAddress };
        Assert.Equal(HttpStatusCode.OK, (await plain.GetAsync($"status?token={Token}")).StatusCode); // browser sources
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("status")).StatusCode);
    }

    [Fact]
    public async Task The_OBS_hud_page_is_served_with_the_token()
    {
        using var obs = new HttpClient { BaseAddress = _server.Address };
        Assert.Equal(HttpStatusCode.Unauthorized, (await obs.GetAsync("overlay/hud")).StatusCode);
        var page = await obs.GetAsync($"overlay/hud?token={Token}&corner=bottom-left");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.Contains("/api/v1/state", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await obs.GetAsync($"overlay/nope?token={Token}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await obs.GetAsync($"overlay/..%2Fsecret?token={Token}")).StatusCode);
    }

    [Fact]
    public async Task The_run_is_served_and_the_split_key_works_from_other_tools()
    {
        var set = new Lockpick.Core.Runs.SplitSet("t", "Test", "Jill - Test", PlayableCharacter.Jill,
            [new("First", new Lockpick.Core.Runs.ManualSplit()), new("End", new Lockpick.Core.Runs.RunFinished())], false, "test");
        _runs.Choose(set);
        _source.Raise(new GameLoaded(DateTimeOffset.UtcNow, TimeSpan.Zero, IsNewGame: true));

        var run = await Json(await _client.GetAsync("run"));
        Assert.Equal("running", run.GetProperty("state").GetString());
        Assert.Equal("Jill - Test", run.GetProperty("set").GetProperty("category").GetString());
        Assert.Equal(2, run.GetProperty("splits").GetArrayLength());

        var after = await Json(await _client.PostAsync("run/split", null));
        Assert.Equal(1, after.GetProperty("current").GetInt32());
        Assert.Equal(JsonValueKind.Number, after.GetProperty("splits")[0].GetProperty("timeSeconds").ValueKind);

        using var obs = new HttpClient { BaseAddress = _server.Address };
        var page = await obs.GetAsync($"overlay/splits?token={Token}");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("/api/v1/run", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var feed = await obs.GetAsync($"overlay/feed?token={Token}");
        Assert.Contains("/api/v1/events", await feed.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Widgets_are_served_as_pictures_for_obs()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("widget/minimap.png")).StatusCode); // none in this host

        string? asked = null;
        await using var server = new ApiServer(new ApiHost(_source, _adapter, _trainer, _capture, "9.9.9",
            Widget: (id, width, height) =>
            {
                asked = $"{id} {width}x{height}";
                return Task.FromResult(id == "minimap" ? new WidgetPicture(new byte[2 * 2 * 4], 2, 2) : null);
            }), NullLogger.Instance);
        Assert.Null(await server.StartAsync(new ApiOptions { Port = 0, Token = Token }));
        using var client = new HttpClient { BaseAddress = new Uri(server.Address!, ApiServer.Prefix + "/") };
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token);

        var map = await client.GetAsync("widget/minimap.png?width=50&height=9000");
        Assert.Equal("image/png", map.Content.Headers.ContentType?.MediaType);
        Assert.Equal("minimap 64x2000", asked); // sizes kept in bounds
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("widget/enemies.png")).StatusCode); // nothing to show
        using var obs = new HttpClient { BaseAddress = server.Address };
        var page = await obs.GetAsync($"overlay/widget?id=minimap&token={Token}");
        Assert.Contains("/api/v1/widget/", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task It_listens_on_this_PC_only_unless_allowed()
    {
        Assert.True(IPAddress.IsLoopback(IPAddress.Parse(_server.Address!.Host)));
    }

    [Fact]
    public async Task State_describes_the_game()
    {
        var state = await Json(await _client.GetAsync("state"));
        Assert.True(state.GetProperty("inGame").GetBoolean());
        Assert.Equal(50, state.GetProperty("health").GetProperty("current").GetInt32());
        Assert.Equal("106", state.GetProperty("room").GetProperty("id").GetString());
        Assert.Equal(15, state.GetProperty("inventory")[0].GetProperty("quantity").GetInt32());
        Assert.Equal("readOnly", state.GetProperty("trainer").GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Events_can_be_polled_from_a_sequence_number_and_streamed()
    {
        _source.Raise(new RoomChanged(DateTimeOffset.UtcNow, new RoomLocation(1, 6), new RoomLocation(1, 5)));
        var events = await Json(await _client.GetAsync("events?after=0"));
        var last = events.GetProperty("last").GetInt64();
        Assert.Equal("RoomChanged", events.GetProperty("events")[0].GetProperty("type").GetString());
        Assert.Equal(0, (await Json(await _client.GetAsync($"events?after={last}"))).GetProperty("events").GetArrayLength());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var stream = await _client.GetStreamAsync($"events/stream?after={last}", cts.Token);
        using var reader = new StreamReader(stream);
        Assert.Equal(": connected", await reader.ReadLineAsync(cts.Token));
        _source.Raise(new PlayerDamaged(DateTimeOffset.UtcNow, 10, 40));
        string? line;
        do
        {
            line = await reader.ReadLineAsync(cts.Token);
        }
        while (line is not null && !line.StartsWith("event:", StringComparison.Ordinal));

        Assert.Equal("event: PlayerDamaged", line);
    }

    [Fact]
    public async Task Markers_and_captures_go_to_the_app()
    {
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsJsonAsync("capture/marker", new { note = "split" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync("capture/start", null)).StatusCode);
        var marker = await Json(await _client.PostAsJsonAsync("capture/marker", new { note = "split 1" }));
        Assert.Equal(1, marker.GetProperty("marker").GetInt32());
        Assert.Equal("split 1", _capture.Notes.Single());
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync("capture/stop", null)).StatusCode);
    }

    [Fact]
    public async Task Trainer_commands_are_refused_until_the_player_allows_them()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsJsonAsync("trainer/mode", new { mode = "trainer" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsJsonAsync("trainer/health", new { value = 90 })).StatusCode);

        // Making the game safer is always allowed.
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync("trainer/mode", new { mode = "challenge" })).StatusCode);
        Assert.Equal(TrainerMode.Challenge, _trainer.Mode);
    }

    [Fact]
    public async Task With_trainer_commands_allowed_a_write_goes_through_the_trainer()
    {
        await Start(allowTrainer: true);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsJsonAsync("trainer/health", new { value = 90 })).StatusCode); // mode is read-only

        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync("trainer/mode", new { mode = "trainer" })).StatusCode);
        var set = await _client.PostAsJsonAsync("trainer/health", new { value = 90 });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Contains(_trainer.Log.Recent, a => a.Source == TrainerSource.Api && a.NewValue == 90 && a.Result == TrainerResult.Applied);

        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsJsonAsync("trainer/health", new { value = 500 })).StatusCode); // over max HP
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync("trainer/health", new { full = true, @lock = true })).StatusCode);
        Assert.Equal(96, _trainer.Locks["hp"]);
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync("trainer/health/unlock", null)).StatusCode);
        Assert.Empty(_trainer.Locks);
    }

    private sealed class StubTrainer : IGameTrainer
    {
        public string HealthField => "hp";

        public string MaxHealthField => "maxHp";

        public Func<GameSnapshot, IReadOnlyList<FieldWrite>> CreateInfiniteAmmoRule() => _ => [];

        public IReadOnlyList<string> KeepRunningFields => [];

        public TrainerRule CreateKeepRunningRule(Func<bool> inFront) => new("Keep running", _ => []);

        public IReadOnlyList<GivableItem> GivableItems => [];

        public bool HasQuantity(int itemId) => false;

        public FieldWrite? SetQuantity(GameSnapshot snapshot, int slot, int quantity, out string? refusal)
        {
            refusal = "not in this test";
            return null;
        }

        public FieldWrite? AddToStorage(GameSnapshot snapshot, int itemId, int quantity, out string? refusal)
        {
            refusal = "not in this test";
            return null;
        }
    }

    private sealed class FakeCapture : ICaptureControl
    {
        public List<string?> Notes { get; } = [];

        public bool IsCapturing { get; private set; }

        public Task<string?> StartAsync()
        {
            IsCapturing = true;
            return Task.FromResult<string?>(null);
        }

        public Task<string?> StopAsync()
        {
            var was = IsCapturing;
            IsCapturing = false;
            return Task.FromResult<string?>(was ? "capture.zip" : null);
        }

        public Task<int?> MarkAsync(string? note)
        {
            if (!IsCapturing)
            {
                return Task.FromResult<int?>(null);
            }

            Notes.Add(note);
            return Task.FromResult<int?>(Notes.Count);
        }
    }
}
