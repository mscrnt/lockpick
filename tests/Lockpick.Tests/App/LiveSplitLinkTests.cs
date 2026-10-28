using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Lockpick.App.Services;
using Lockpick.Core.Events;
using Lockpick.Core.Runs;
using Lockpick.Core.State;
using Lockpick.Tests.Core;
using Lockpick.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lockpick.Tests.App;

/// <summary>The LiveSplit link against a stand-in for LiveSplit's TCP server (its text protocol, one command a line).</summary>
public sealed class LiveSplitLinkTests : IDisposable
{
    private readonly FakeStateSource _source = new();
    private readonly RunService _runs;
    private readonly FakeLiveSplit _server = new();

    private static readonly SplitSet Set = new("t", "Test", "Jill - Test", PlayableCharacter.Jill,
        [new("One", new ManualSplit()), new("Two", new ManualSplit()), new("End", new RunFinished())], false, "test");

    public LiveSplitLinkTests()
    {
        _runs = new RunService(_source, null, _ => null);
        _source.Attach(1, TrainerServiceTests.Profile);
        _runs.Choose(Set);
    }

    public void Dispose()
    {
        _runs.Dispose();
        _server.Dispose();
    }

    private static void WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(8);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "timed out");
            Thread.Sleep(50);
        }
    }

    private void NewGame()
    {
        _source.Poll(new GameSnapshot { InGameplay = Field<bool>.Valid(true), GameTime = Field<TimeSpan>.Valid(TimeSpan.FromSeconds(1)) });
        _source.Raise(new GameLoaded(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), IsNewGame: true));
    }

    [Fact]
    public void Driving_starts_splits_and_resets_LiveSplit_on_the_game_s_clock()
    {
        using var link = new LiveSplitLink(_runs, _source, LiveSplitModes.Drive, _server.Port, NullLogger.Instance);
        WaitFor(() => link.Status.StartsWith("Connected", StringComparison.Ordinal));
        Assert.True(_runs.AutoSplit);

        NewGame();
        WaitFor(() => _server.Received.Contains("starttimer"));
        Assert.Contains("alwayspausegametime", _server.Received); // LiveSplit's game time is set by Lockpick, not counted by it
        Assert.DoesNotContain("initgametime", _server.Received);   // not a command of LiveSplit's own server

        _runs.Split();
        WaitFor(() => _server.Received.Contains("split"));
        Assert.Contains(_server.Received, c => c.StartsWith("setgametime 0:00:01", StringComparison.Ordinal));

        _runs.Undo();
        WaitFor(() => _server.Received.Contains("unsplit"));
        _runs.Reset();
        WaitFor(() => _server.Received.Count(c => c == "reset") >= 2); // once at the start, once now
    }

    [Fact]
    public void Following_moves_the_run_with_LiveSplit_s_splits_and_doesn_t_split_by_itself()
    {
        using var link = new LiveSplitLink(_runs, _source, LiveSplitModes.Follow, _server.Port, NullLogger.Instance);
        WaitFor(() => link.Status.StartsWith("Connected", StringComparison.Ordinal));
        Assert.False(_runs.AutoSplit);

        NewGame();
        _server.Phase = "Running";
        _server.Index = 0;
        // Lockpick reads LiveSplit at the first split twice before it moves on (not a fixed wait: CI machines are slow).
        var asked = _server.Received.Count(c => c == "getsplitindex");
        WaitFor(() => _server.Received.Count(c => c == "getsplitindex") >= asked + 2);
        _server.Index = 2; // two splits in LiveSplit
        WaitFor(() => _runs.Status.Current == 2);
        Assert.NotNull(_runs.Status.Times[1]);

        _server.Index = 1; // undo in LiveSplit
        WaitFor(() => _runs.Status.Current == 1);

        _server.Phase = "NotRunning"; // reset in LiveSplit
        _server.Index = -1;
        WaitFor(() => _runs.Status.State == RunState.Ended);
        Assert.Equal(RunResult.Reset, _runs.Status.Result);
    }

    [Fact]
    public void Following_with_no_LiveSplit_leaves_Lockpick_splitting_by_itself()
    {
        var port = _server.Port;
        _server.Dispose(); // nothing listening
        using var link = new LiveSplitLink(_runs, _source, LiveSplitModes.Follow, port, NullLogger.Instance);
        WaitFor(() => link.Status.StartsWith("Waiting", StringComparison.Ordinal));
        Assert.True(_runs.AutoSplit);
    }

    /// <summary>Answers the get-commands LiveSplit answers and records every command.</summary>
    private sealed class FakeLiveSplit : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public FakeLiveSplit()
        {
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public ConcurrentQueue<string> Commands { get; } = new();

        public IReadOnlyList<string> Received => [.. Commands];

        public volatile string Phase = "NotRunning";

        public volatile int Index = -1;

        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream);
                    using var writer = new StreamWriter(stream) { NewLine = "\r\n", AutoFlush = true };
                    while (await reader.ReadLineAsync(_stop.Token) is { } line)
                    {
                        Commands.Enqueue(line);
                        if (line == "getcurrenttimerphase")
                        {
                            await writer.WriteLineAsync(Phase);
                        }
                        else if (line == "getsplitindex")
                        {
                            await writer.WriteLineAsync(Index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException)
            {
            }
        }
    }
}
