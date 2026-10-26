using Lockpick.App.Services;
using Lockpick.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lockpick.Tests.App;

/// <summary>Lockpick's connection to OBS's WebSocket server (#65) against a stand-in for it: the OBS script's automation.</summary>
public sealed class ObsLinkTests : IDisposable
{
    private readonly FakeObs _obs = new();
    private ObsSettings _settings;
    private string? _password = "secret";

    public ObsLinkTests() => _settings = new ObsSettings { Enabled = true, Port = _obs.Port, StopDelaySeconds = 0, ReplayOnDeath = true };

    public void Dispose() => _obs.Dispose();

    private ObsLink Link()
    {
        var link = new ObsLink(() => _settings, () => _password, NullLogger.Instance);
        WaitFor(() => link.Connected);
        return link;
    }

    private static void WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(8);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "timed out");
            Thread.Sleep(20);
        }
    }

    /// <summary>Everything posted so far has been handled: a death (replay buffer on death) is asked for after it.</summary>
    private void Settle(ObsLink link)
    {
        Assert.True(_settings.ReplayOnDeath);
        var before = _obs.Count("SaveReplayBuffer");
        link.Post(new ObsMoment("death", "settle"));
        WaitFor(() => _obs.Count("SaveReplayBuffer") > before);
    }

    [Fact]
    public async Task OBS_is_answered_with_its_challenge_and_a_wrong_password_is_named()
    {
        await using (var obs = await ObsWebSocket.ConnectAsync("127.0.0.1", _obs.Port, "secret"))
        {
            Assert.Equal("32.2.2", obs.ObsVersion);
            Assert.Equal("5.7.4", obs.WebSocketVersion);
            var status = await obs.RequestAsync("GetRecordStatus");
            Assert.False(status.GetProperty("outputActive").GetBoolean());
            var refused = await Assert.ThrowsAsync<ObsException>(() => obs.RequestAsync("StopRecord"));
            Assert.Equal(ObsWebSocket.OutputNotRunning, refused.Code);
        }

        var wrong = await Assert.ThrowsAsync<ObsException>(() => ObsWebSocket.ConnectAsync("127.0.0.1", _obs.Port, "guess"));
        Assert.Equal(ObsWebSocket.PasswordRefused, wrong.Code);
        Assert.Contains("refused the password", wrong.Message, StringComparison.Ordinal);
        var none = await Assert.ThrowsAsync<ObsException>(() => ObsWebSocket.ConnectAsync("127.0.0.1", _obs.Port, null));
        Assert.Contains("asks for its WebSocket password", none.Message, StringComparison.Ordinal);
        var nobody = await Assert.ThrowsAsync<ObsException>(() => ObsWebSocket.ConnectAsync("127.0.0.1", 1, null));
        Assert.Contains("No OBS WebSocket server", nobody.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_is_recorded_with_its_chapters_and_the_recording_stops_when_it_ends()
    {
        _settings = _settings with { RecordWithRuns = true };
        using var link = Link();
        Assert.Contains("Connected to OBS 32.2.2 (WebSocket 5.7.4) on this PC", link.Status, StringComparison.Ordinal);
        Assert.True(link.ThisPc);

        link.Post(new ObsMoment("run-start", "Jill - Any%, attempt 3"));
        WaitFor(() => _obs.Recording);
        link.Post(new ObsMoment("split", "Lockpick at 0:12:34.5"));
        link.Post(new ObsMoment("run-end", "Reset at Lockpick"));
        WaitFor(() => _obs.Has("StopRecord"));

        Assert.False(_obs.Recording);
        Assert.Equal(["Run: Jill - Any%, attempt 3", "Lockpick at 0:12:34.5", "Reset at Lockpick"], _obs.Chapters);
    }

    [Fact]
    public void A_recording_the_player_started_is_left_running()
    {
        _settings = _settings with { RecordWithRuns = true };
        _obs.Recording = true;
        using var link = Link();

        link.Post(new ObsMoment("run-start", "Jill - Any%, attempt 1"));
        link.Post(new ObsMoment("run-end", "Reset"));
        Settle(link);

        Assert.True(_obs.Recording);
        Assert.False(_obs.Has("StartRecord"));
        Assert.False(_obs.Has("StopRecord"));
        Assert.Equal(["Run: Jill - Any%, attempt 1", "Reset", "Death"], _obs.Chapters); // chapters still mark the player's recording
    }

    [Fact]
    public void Trying_again_before_the_stop_keeps_the_recording_going()
    {
        _settings = _settings with { RecordWithRuns = true, StopDelaySeconds = 1 };
        using var link = Link();

        link.Post(new ObsMoment("run-start", "attempt 1"));
        WaitFor(() => _obs.Recording);
        link.Post(new ObsMoment("run-end", "Challenge failed"));
        link.Post(new ObsMoment("run-start", "attempt 2"));
        Thread.Sleep(1500);
        Settle(link);

        Assert.True(_obs.Recording);
        Assert.False(_obs.Has("StopRecord"));
        Assert.Equal(1, _obs.Count("StartRecord"));

        link.Post(new ObsMoment("run-end", "Finished"));
        WaitFor(() => _obs.Has("StopRecord"));
    }

    [Fact]
    public void The_replay_buffer_is_saved_on_the_moments_chosen()
    {
        _settings = _settings with { ReplayOnDeath = false, ReplayOnFail = true, ReplayOnBest = true };
        _obs.ReplayBuffer = true;
        using var link = Link();

        link.Post(new ObsMoment("death", "death 1 this session"));
        link.Post(new ObsMoment("challenge-fail", "No damage: hit by a zombie"));
        link.Post(new ObsMoment("new-best", "Jill - Any% in 1:02:03.4"));
        WaitFor(() => _obs.Count("SaveReplayBuffer") == 2);

        _obs.ReplayBuffer = false; // not started in OBS: nothing saved, and the player is told (once)
        _settings = _settings with { ReplayOnDeath = true };
        Settle(link);
        Settle(link);
        Assert.True(link.Connected);
        Assert.Equal("OBS's replay buffer isn't running, so nothing was saved: press Start Replay Buffer in OBS (Controls).", link.Note);
        _obs.ReplayBuffer = true; // started: the note goes
        Settle(link);
        Assert.Empty(link.Note);

        _obs.ReplayBufferUnavailable = true; // turned off in OBS's settings: said once, the rest goes on
        Settle(link);
        Settle(link);
        Assert.True(link.Connected);
        Assert.Equal("OBS's replay buffer is off: OBS → Settings → Output → Replay Buffer → Enable, then Start Replay Buffer.", link.Note);
    }

    [Fact]
    public void The_paused_scene_shows_while_the_game_is_away_and_the_scene_before_comes_back()
    {
        _settings = _settings with { PauseScene = "Paused" };
        using var link = Link();
        Assert.Equal(["Game", "Paused"], link.Scenes); // top to bottom, as in OBS's Scenes dock

        link.Post(new ObsMoment("game-inactive", string.Empty));
        WaitFor(() => _obs.CurrentScene == "Paused");
        link.Post(new ObsMoment("game-active", string.Empty));
        WaitFor(() => _obs.CurrentScene == "Game");

        _settings = _settings with { PauseScene = null };
        link.Post(new ObsMoment("game-inactive", string.Empty));
        Settle(link);
        Assert.Equal("Game", _obs.CurrentScene);
    }

    [Fact]
    public async Task Setting_up_makes_the_Lockpick_scene_and_keeps_what_is_already_there()
    {
        using var link = Link();
        var app = new AppSettings { ApiEnabled = true, ApiPort = 52341, ApiToken = "tok" };

        var first = await link.SetUpSceneAsync(c => ObsScenePlan.Sources(c, app, "Biohazard.exe", liveSplit: true));

        Assert.Contains("\"Lockpick\" scene in OBS has its 7 sources (7 new)", first, StringComparison.Ordinal);
        Assert.Contains(ObsLink.SceneName, _obs.Scenes);
        Assert.Contains(ObsLink.SceneName, link.Scenes);
        Assert.Equal("game_capture", _obs.Inputs["Resident Evil (game)"].Kind);
        Assert.Equal("Resident Evil:RESIDENT EVIL:Biohazard.exe", _obs.Inputs["Resident Evil (game)"].Settings["window"]!.GetValue<string>());
        Assert.StartsWith("http://127.0.0.1:52341/overlay/livesplit?token=tok", _obs.Inputs["Lockpick: splits"].Settings["url"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(7, _obs.Count("SetSceneItemTransform"));

        // Again with a new token, the player having resized the minimap's browser and taken it out of the scene.
        _obs.Inputs["Lockpick: minimap"].Settings["width"] = 500;
        _obs.Items.Remove((ObsLink.SceneName, "Lockpick: minimap"));
        var again = await link.SetUpSceneAsync(c => ObsScenePlan.Sources(c, app with { ApiToken = "new" }, "Biohazard.exe", liveSplit: true));

        Assert.Contains("(1 from your other scenes, 6 already in it, left where you put them)", again, StringComparison.Ordinal);
        Assert.Equal(1, _obs.Count("CreateScene"));
        Assert.Contains("token=new", _obs.Inputs["Lockpick: minimap"].Settings["url"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(500, _obs.Inputs["Lockpick: minimap"].Settings["width"]!.GetValue<int>()); // only the address changes
        Assert.Contains((ObsLink.SceneName, "Lockpick: minimap"), _obs.Items);                  // put back in the scene
        Assert.Equal(8, _obs.Count("SetSceneItemTransform"));                                    // placed once, the others left where they are
    }

    [Fact]
    public void OBS_on_another_PC_gets_the_widgets_from_this_PC_s_address_and_no_game_capture()
    {
        var app = new AppSettings { ApiEnabled = true, ApiPort = 52341, ApiToken = "tok" };
        var away = new ObsCanvas(1920, 1080, ThisPc: false, LocalAddress: "192.168.1.20");

        var closed = Assert.Throws<ObsException>(() => ObsScenePlan.Sources(away, app, "ResidentEvil.exe", liveSplit: false));
        Assert.Contains("allow other devices on your network", closed.Message, StringComparison.Ordinal);

        var sources = ObsScenePlan.Sources(away, app with { ApiAllowNetwork = true }, "ResidentEvil.exe", liveSplit: false);
        Assert.DoesNotContain(sources, s => s.Kind == "game_capture");
        Assert.All(sources, s => Assert.StartsWith("http://192.168.1.20:52341/overlay/", (string)s.Settings["url"], StringComparison.Ordinal));

        var off = Assert.Throws<ObsException>(() => ObsScenePlan.Sources(away with { ThisPc = true }, new AppSettings(), "ResidentEvil.exe", liveSplit: false));
        Assert.Contains("Turn on Lockpick's API", off.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refused_password_waits_for_a_new_one()
    {
        _password = "guess";
        using var link = new ObsLink(() => _settings, () => _password, NullLogger.Instance);
        WaitFor(() => link.Status.Contains("refused the password", StringComparison.Ordinal));
        Thread.Sleep(600);
        Assert.Equal(1, _obs.Connections); // not asked again and again

        _password = "secret";
        link.Configure();
        WaitFor(() => link.Connected);
    }

    [Fact]
    public void Moments_wait_for_nothing_while_OBS_is_away_and_turning_it_off_disconnects()
    {
        _settings = _settings with { Enabled = false };
        using var link = new ObsLink(() => _settings, () => _password, NullLogger.Instance);
        link.Post(new ObsMoment("run-start", "dropped"));
        Thread.Sleep(300);
        Assert.Equal("Off", link.Status);
        Assert.Equal(0, _obs.Connections);

        _settings = _settings with { Enabled = true, Chapters = true };
        link.Configure();
        WaitFor(() => link.Connected);
        _obs.Recording = true;
        Settle(link);
        Assert.Equal(["Death"], _obs.Chapters); // the run-start from before the connection isn't sent late

        _settings = _settings with { Enabled = false };
        link.Configure();
        WaitFor(() => !link.Connected && link.Status == "Off");
    }

    [Fact]
    public void OBS_s_own_settings_on_this_PC_fill_in_the_port_and_password()
    {
        var path = Path.Combine(Path.GetTempPath(), "lockpick-obs-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """{"alerts_enabled":false,"auth_required":true,"first_load":false,"server_enabled":true,"server_password":"pw","server_port":4466}""");
            Assert.Equal(new ObsServerConfig(true, 4466, "pw"), ObsServerConfig.Read(path));
            File.WriteAllText(path, """{"auth_required":false,"server_enabled":false,"server_password":"unused","server_port":4455}""");
            Assert.Equal(new ObsServerConfig(false, 4455, null), ObsServerConfig.Read(path));
            Assert.Null(ObsServerConfig.Read(path + ".missing"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
