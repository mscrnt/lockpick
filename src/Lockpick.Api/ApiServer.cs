using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lockpick.Core.Events;
using Lockpick.Core.Monitoring;
using Lockpick.Core.State;
using Lockpick.Core.Trainer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lockpick.Api;

/// <summary>
/// Lockpick's API for other tools (scripts, OBS, LiveSplit, Stream Deck and other macro tools). JSON over HTTP under
/// <c>/api/v1</c>, a token on every request, events as a server-sent-event stream. It listens on this PC only unless
/// the player allows other devices, and trainer commands work only when the player allowed them here and has Trainer
/// Mode on; they go through <see cref="TrainerService"/> like the app's own buttons (checked, read back, logged with
/// source "api"). See docs/API.md.
/// </summary>
public sealed class ApiServer : IAsyncDisposable
{
    public const string Prefix = "/api/v1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly string[] Endpoints =
    [
        "GET /status", "GET /state", "GET /events?after=N", "GET /events/stream?after=N",
        "POST /capture/start", "POST /capture/stop", "POST /capture/marker {note}",
        "GET /trainer", "POST /trainer/mode {mode}", "POST /trainer/health {value|full, lock}",
        "POST /trainer/health/unlock", "POST /trainer/ammo {enabled}", "GET /trainer/items",
        "POST /trainer/inventory/quantity {slot, quantity}", "POST /trainer/itembox {itemId, quantity}",
        "GET /challenges", "GET /challenge", "POST /challenge/start {id, rules}", "POST /challenge/retry", "POST /challenge/stop",
        "GET /run", "GET /livesplit.png?width&height", "POST /run/split", "POST /run/undo", "POST /run/skip", "POST /run/reset",
        "GET /widget/{id}.png?width&height|scale", "GET /stream-layout"
    ];

    private readonly ApiHost _host;
    private readonly ILogger _logger;

    // Each widget picture's PNG, kept while the app hands out the same picture (its look hasn't changed).
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<WidgetPicture, byte[]> _encoded = [];
    private WebApplication? _app;
    private ApiOptions? _options;

    public ApiServer(ApiHost host, ILogger logger)
    {
        _host = host;
        _logger = logger;
        host.Source.EventRaised += (_, e) => Events.Add(e);
        if (host.Trainer is not null)
        {
            host.Trainer.EventRaised += (_, e) => Events.Add(e);
        }

        if (host.Challenges is not null)
        {
            host.Challenges.EventRaised += (_, e) => Events.Add(e);
        }

        if (host.Runs is not null)
        {
            host.Runs.EventRaised += (_, e) => Events.Add(e);
        }

        if (host.Feed is not null)
        {
            host.Feed.EventRaised += (_, e) => Events.Add(e);
        }
    }

    public EventFeed Events { get; } = new();

    /// <summary>Where it listens while running (e.g. http://127.0.0.1:1996), else null.</summary>
    public Uri? Address { get; private set; }

    public bool IsRunning => _app is not null;

    /// <summary>Starts (or restarts) with <paramref name="options"/>; returns null, or why it couldn't listen.</summary>
    public async Task<string?> StartAsync(ApiOptions options)
    {
        await StopAsync().ConfigureAwait(false);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            if (options.AllowNetwork)
            {
                k.ListenAnyIP(options.Port);
            }
            else
            {
                k.Listen(IPAddress.Loopback, options.Port);
            }
        });

        var app = builder.Build();
        _options = options;
        app.Use(Authorize);
        Map(app);
        try
        {
            await app.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            await app.DisposeAsync().ConfigureAwait(false);
            _logger.LogWarning("API couldn't listen on port {Port}: {Error}", options.Port, ex.Message);
            return $"Couldn't listen on port {options.Port}: {ex.Message}";
        }

        _app = app;
        var bound = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        Address = bound is null ? null : new Uri(bound.Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal).Replace("[::]", "127.0.0.1", StringComparison.Ordinal));
        _logger.LogInformation("API listening on {Address} ({Scope}, trainer commands {Trainer})", Address,
            options.AllowNetwork ? "network" : "this PC only", options.AllowTrainer ? "allowed" : "off");
        return null;
    }

    public async Task StopAsync()
    {
        if (_app is { } app)
        {
            _app = null;
            Address = null;
            await app.StopAsync().ConfigureAwait(false);
            await app.DisposeAsync().ConfigureAwait(false);
            _logger.LogInformation("API stopped");
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task Authorize(HttpContext context, RequestDelegate next)
    {
        var header = context.Request.Headers.Authorization.ToString();
        var supplied = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim()
            : context.Request.Headers["X-Lockpick-Token"].ToString() is { Length: > 0 } custom ? custom
            : context.Request.Query["token"].ToString();
        var expected = _options?.Token ?? string.Empty;
        if (expected.Length == 0 || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected)))
        {
            await Reply(context, 401, new { error = "missing or wrong token (Lockpick → Settings → API and profiles)" }).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    private void Map(WebApplication app)
    {
        app.MapGet(Prefix, () => Ok(new
        {
            name = "Lockpick",
            version = _host.AppVersion,
            api = 1,
            endpoints = Endpoints,
        }));

        app.MapGet(Prefix + "/status", () => Ok(Status()));
        app.MapGet("/overlay/{name}", (string name) => Page(name) is { } page
            ? Results.Content(page, "text/html; charset=utf-8")
            : Error(404, $"no overlay page '{name}' (try /overlay/hud)"));
        app.MapGet(Prefix + "/state", () => Ok(State()));
        app.MapGet(Prefix + "/events", (long? after) =>
        {
            var events = Events.Since(after ?? 0);
            return Ok(new { events, last = events.Count > 0 ? events[^1].Sequence : after ?? 0 });
        });
        app.MapGet(Prefix + "/events/stream", Stream);

        app.MapPost(Prefix + "/capture/start", async () => _host.Capture is not { } capture
            ? Error(404, "captures aren't available")
            : await capture.StartAsync().ConfigureAwait(false) is { } problem ? Error(409, problem) : Ok(new { capturing = true }));
        app.MapPost(Prefix + "/capture/stop", async () => _host.Capture is not { } capture
            ? Error(404, "captures aren't available")
            : await capture.StopAsync().ConfigureAwait(false) is { } file ? Ok(new { capturing = false, file }) : Error(409, "no capture is running"));
        app.MapPost(Prefix + "/capture/marker", async (MarkerRequest? request) => _host.Capture is not { } capture
            ? Error(404, "captures aren't available")
            : await capture.MarkAsync(request?.Note).ConfigureAwait(false) is { } number ? Ok(new { marker = number }) : Error(409, "no capture is running"));

        app.MapGet(Prefix + "/challenges", () => Ok(new
        {
            challenges = Lockpick.Core.Challenges.ChallengeDefinition.Presets,
            rules = Enum.GetValues<Lockpick.Core.Challenges.ChallengeRule>(),
        }));
        app.MapGet(Prefix + "/challenge", () => _host.Challenges is { } challenges ? Ok(challenges.Status) : Error(404, "challenges aren't available"));
        app.MapPost(Prefix + "/challenge/start", (ChallengeRequest request) =>
        {
            if (_host.Challenges is not { } challenges)
            {
                return Error(404, "challenges aren't available");
            }

            if (Lockpick.Core.Challenges.ChallengeDefinition.Presets.FirstOrDefault(p => p.Id == request.Id) is not { } preset)
            {
                return Error(400, "unknown challenge id (GET /challenges lists them)");
            }

            challenges.Start(preset, request.Rules);
            return Ok(challenges.Status);
        });
        app.MapGet(Prefix + "/run", () => _host.Runs is { } runs ? Ok(Run(runs)) : Error(404, "runs aren't available"));
        app.MapGet(Prefix + "/livesplit.png", (int? width, int? height) =>
        {
            // LiveSplit's layout drawn by livesplit-core (the OBS page /overlay/livesplit shows it).
            var w = Math.Clamp(width ?? 300, 100, 1200);
            var h = Math.Clamp(height ?? 500, 100, 2000);
            return _host.LiveSplitPicture?.Invoke(w, h) is { } pixels
                ? Results.Bytes(Png.FromPremultipliedBgra(pixels, w, h), "image/png")
                : Error(404, "no LiveSplit timer: choose splits on the Runs page");
        });
        app.MapGet(Prefix + "/widget/{id}.png", async (string id, int? width, int? height, double? scale) =>
        {
            // An overlay widget, drawn as large as fits (the OBS page /overlay/widget shows it), or with scale at its
            // own size times that (the stream layout, /overlay/stream); 204 while it has nothing to show (e.g. no
            // enemies in the room).
            WidgetPicture? picture;
            if (scale is { } factor && _host.WidgetAtScale is { } atScale)
            {
                picture = await atScale(id, Math.Clamp(double.IsFinite(factor) ? factor : 1, 0.25, 6)).ConfigureAwait(false);
            }
            else if (_host.Widget is { } widget)
            {
                picture = await widget(id, Math.Clamp(width ?? 400, 64, 2000), Math.Clamp(height ?? 400, 64, 2000)).ConfigureAwait(false);
            }
            else
            {
                return Error(404, "widgets aren't available");
            }

            return picture is not null ? Results.Bytes(Encoded(picture), "image/png") : Results.NoContent();
        });
        app.MapGet(Prefix + "/stream-layout", () => _host.StreamLayout is { } layout
            ? Ok(new { widgets = layout() })
            : Error(404, "the stream layout isn't available"));
        app.MapPost(Prefix + "/run/split", () => RunCommand(r => r.Split()));
        app.MapPost(Prefix + "/run/undo", () => RunCommand(r => r.Undo()));
        app.MapPost(Prefix + "/run/skip", () => RunCommand(r => r.Skip()));
        app.MapPost(Prefix + "/run/reset", () => RunCommand(r => r.Reset()));
        app.MapPost(Prefix + "/challenge/retry", () =>
        {
            if (_host.Challenges is not { } challenges)
            {
                return Error(404, "challenges aren't available");
            }

            challenges.Retry();
            return Ok(challenges.Status);
        });
        app.MapPost(Prefix + "/challenge/stop", () =>
        {
            if (_host.Challenges is not { } challenges)
            {
                return Error(404, "challenges aren't available");
            }

            challenges.Stop();
            return Ok(challenges.Status);
        });

        app.MapGet(Prefix + "/trainer", () => _host.Trainer is null ? Error(404, "the trainer isn't available") : Ok(Trainer(detail: true)));
        app.MapPost(Prefix + "/trainer/mode", (ModeRequest request) => SetMode(request.Mode));
        app.MapPost(Prefix + "/trainer/health", (HealthRequest request) => Health(request));
        app.MapPost(Prefix + "/trainer/health/unlock", () => TrainerCommand((trainer, game) =>
        {
            trainer.Unlock(game.HealthField, TrainerSource.Api);
            return null;
        }));
        app.MapGet(Prefix + "/trainer/items", () =>
            _host.Source.Status.Profile is { } profile && _host.Adapter.CreateTrainer(profile) is { } game
                ? Ok(new { items = game.GivableItems })
                : Error(409, "not attached to a supported game"));
        app.MapPost(Prefix + "/trainer/inventory/quantity", (QuantityRequest request) => TrainerCommand((trainer, game) =>
        {
            if (_host.Source.Latest is not { } snapshot)
            {
                return Refused("Count", "the game state isn't known yet");
            }

            return game.SetQuantity(snapshot, request.Slot, request.Quantity, out var refusal) is { } write
                ? trainer.Write(write.Field, write.Value, TrainerSource.Api, $"Count of slot {request.Slot + 1}")
                : Refused("Count", refusal ?? "refused");
        }));
        app.MapPost(Prefix + "/trainer/itembox", (ItemBoxRequest request) => TrainerCommand((trainer, game) =>
        {
            if (_host.Source.Latest is not { } snapshot)
            {
                return Refused("Item box", "the game state isn't known yet");
            }

            return game.AddToStorage(snapshot, request.ItemId, request.Quantity ?? 1, out var refusal) is { } write
                ? trainer.Write(write.Field, write.Value, TrainerSource.Api, $"Item box + {_host.Adapter.ItemName(request.ItemId) ?? $"item {request.ItemId}"}")
                : Refused("Item box", refusal ?? "refused");
        }));
        app.MapPost(Prefix + "/trainer/ammo", (ToggleRequest request) => TrainerCommand((trainer, game) =>
        {
            if (request.Enabled)
            {
                return trainer.SetRule(TrainerRules.InfiniteAmmo, "Infinite ammo", game.CreateInfiniteAmmoRule(), TrainerSource.Api);
            }

            trainer.ClearRule(TrainerRules.InfiniteAmmo, TrainerSource.Api);
            return null;
        }));
    }

    private async Task Stream(HttpContext context, long? after)
    {
        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        var reader = Events.Listen(out var stop);
        try
        {
            var ct = context.RequestAborted;
            foreach (var missed in Events.Since(after ?? long.MaxValue))
            {
                await Send(context, missed, ct).ConfigureAwait(false);
            }

            await context.Response.WriteAsync(": connected\n\n", ct).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    var item = await reader.ReadAsync(wait.Token).ConfigureAwait(false);
                    await Send(context, item, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await context.Response.WriteAsync(": keep-alive\n\n", ct).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The client went away.
        }
        finally
        {
            stop();
        }
    }

    private static async Task Send(HttpContext context, ApiEvent item, CancellationToken ct)
    {
        await context.Response.WriteAsync($"id: {item.Sequence}\nevent: {item.Type}\ndata: {JsonSerializer.Serialize(item, Json)}\n\n", ct).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
    }

    private object Status()
    {
        var status = _host.Source.Status;
        return new
        {
            connection = status.State,
            message = status.Message,
            processId = status.Process?.ProcessId,
            profile = status.Profile?.Id,
            game = status.Profile?.DisplayName,
            trainer = _host.Trainer is null ? null : Trainer(detail: false),
            capture = new { available = _host.Capture is not null, active = _host.Capture?.IsCapturing ?? false },
            challenge = _host.Challenges?.Status,
            api = new { version = _host.AppVersion, network = _options?.AllowNetwork ?? false, trainerCommands = _options?.AllowTrainer ?? false },
        };
    }

    private object State()
    {
        var s = _host.Source.Latest;
        var status = _host.Source.Status;
        return new
        {
            connection = status.State,
            profile = status.Profile?.Id,
            sequence = s?.Sequence,
            capturedAt = s?.CapturedAt,
            inGame = Value(s?.InGameplay),
            character = s is { Character.HasValue: true } ? s.Character.Value.ToString() : null,
            health = s is { Health.HasValue: true }
                ? new
                {
                    current = s.Health.Value.Current,
                    max = s.Health.Value.Maximum,
                    condition = s.Condition.HasValue ? s.Condition.Value.ToString().ToLowerInvariant() : null,
                }
                : null,
            room = s is { Location.HasValue: true }
                ? new { id = s.Location.Value.Id, stage = s.Location.Value.Stage, name = _host.Adapter.RoomName(s.Location.Value.Id) }
                : null,
            camera = Value(s?.Camera),
            position = s is { Position.HasValue: true } ? new { x = s.Position.Value.X, y = s.Position.Value.Y, z = s.Position.Value.Z } : null,
            gameTimeSeconds = s is { GameTime.HasValue: true } ? Math.Round(s.GameTime.Value.TotalSeconds, 2) : (double?)null,
            inventory = Items(s?.Inventory),
            itemBox = Items(s?.ItemBox),
            trainer = _host.Trainer is null ? null : Trainer(detail: false),
        };
    }

    private object[]? Items(Field<IReadOnlyList<HeldItem>>? items) =>
        items is { HasValue: true, Value: { } list }
            ? [.. list.Select(h => new { slot = h.Slot, itemId = h.ItemId, name = _host.Adapter.ItemName(h.ItemId), quantity = h.Quantity })]
            : null;

    private object Trainer(bool detail)
    {
        var trainer = _host.Trainer!;
        return new
        {
            mode = trainer.Mode,
            writeAccess = trainer.HasWriteAccess,
            writeAccessError = trainer.WriteAccessError,
            runModified = trainer.RunModified,
            runModifiedAt = trainer.RunModifiedAt,
            locks = trainer.Locks,
            infiniteAmmo = trainer.HasRule(TrainerRules.InfiniteAmmo),
            commandsAllowed = _options?.AllowTrainer ?? false,
            recent = detail ? trainer.Log.Recent.Take(50) : null,
        };
    }

    private IResult SetMode(string? mode)
    {
        if (_host.Trainer is not { } trainer)
        {
            return Error(404, "the trainer isn't available");
        }

        if (!Enum.TryParse<TrainerMode>(mode, ignoreCase: true, out var wanted) || !Enum.IsDefined(wanted))
        {
            return Error(400, "mode must be readOnly, trainer or challenge");
        }

        // Making the game safer (read-only, Challenge) is always allowed; giving the trainer write access needs the opt-in.
        if (wanted == TrainerMode.Trainer && !(_options?.AllowTrainer ?? false))
        {
            return Error(403, "trainer commands are off (Lockpick → Settings → API and profiles)");
        }

        trainer.SetMode(wanted);
        return Ok(Trainer(detail: false));
    }

    private IResult Health(HealthRequest request) => TrainerCommand((trainer, game) =>
    {
        long? target = request.Full == true
            ? _host.Source.Latest is { Health.HasValue: true } s && s.Health.Value.Maximum is { } max ? max : null
            : request.Value;
        if (target is not { } value)
        {
            return new TrainerAction(DateTimeOffset.UtcNow, "Set", game.HealthField, null, null, TrainerSource.Api, TrainerResult.Refused,
                request.Full == true ? "max HP isn't known yet" : "give \"value\" or \"full\": true");
        }

        return request.Lock == true
            ? trainer.Lock(game.HealthField, value, TrainerSource.Api)
            : trainer.Write(game.HealthField, value, TrainerSource.Api, "Set");
    });

    private IResult TrainerCommand(Func<TrainerService, IGameTrainer, TrainerAction?> command)
    {
        if (_host.Trainer is not { } trainer)
        {
            return Error(404, "the trainer isn't available");
        }

        if (!(_options?.AllowTrainer ?? false))
        {
            return Error(403, "trainer commands are off (Lockpick → Settings → API and profiles)");
        }

        if (_host.Source.Status is not { State: ConnectionState.Attached, Profile: { } profile } || _host.Adapter.CreateTrainer(profile) is not { } game)
        {
            return Error(409, "not attached to a supported game");
        }

        var action = command(trainer, game);
        return action is null || action.Result == TrainerResult.Applied
            ? Ok(new { result = action?.Result ?? TrainerResult.Applied, action = action?.Describe(), trainer = Trainer(detail: false) })
            : Results.Json(new { result = action.Result, error = action.Describe(), action }, Json, statusCode: 409);
    }

    private static TrainerAction Refused(string action, string why) =>
        new(DateTimeOffset.UtcNow, action, null, null, null, TrainerSource.Api, TrainerResult.Refused, why);

    private static T? Value<T>(Field<T>? field)
        where T : struct => field is { HasValue: true } f ? f.Value : null;

    private static IResult Ok(object value) => Results.Json(value, Json);

    /// <summary>An embedded page for OBS browser sources (src/Lockpick.Api/Pages/<name>.html), or null.</summary>
    private byte[] Encoded(WidgetPicture picture) =>
        _encoded.GetValue(picture, p => Png.FromPremultipliedBgra(p.Pixels, p.Width, p.Height));

    internal static string? Page(string name)
    {
        if (name.Length == 0 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            return null;
        }

        using var stream = typeof(ApiServer).Assembly.GetManifestResourceStream($"Lockpick.Api.Pages.{name}.html");
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Run keys from other tools (a Stream Deck, a chat bot): they change only Lockpick's timing, never the game.</summary>
    private IResult RunCommand(Action<Lockpick.Core.Runs.RunService> command)
    {
        if (_host.Runs is not { } runs)
        {
            return Error(404, "runs aren't available");
        }

        command(runs);
        return Ok(Run(runs));
    }

    /// <summary>The run as numbers (seconds): every split with its time, the personal best's and the difference.</summary>
    private object Run(Lockpick.Core.Runs.RunService runs)
    {
        var status = runs.Status;
        var set = status.Set;
        var comparison = set is null ? null : _host.Comparison?.Invoke(set)
            ?? Lockpick.Core.Runs.RunComparison.From(runs.History?.Load(set.Id) ?? [], set.Splits.Count);
        var timed = status.State is Lockpick.Core.Runs.RunState.Running or Lockpick.Core.Runs.RunState.Finished or Lockpick.Core.Runs.RunState.Ended;
        static double? Seconds(TimeSpan? t) => t is { } v ? Math.Round(v.TotalSeconds, 2) : null;
        return new
        {
            state = status.State,
            set = set is null ? null : new { id = set.Id, name = set.Name, category = set.Category, verified = set.Verified, importedFrom = set.ImportedFrom is null ? null : Path.GetFileName(set.ImportedFrom) },
            attempt = status.Attempt,
            current = status.Current,
            gameTimeSeconds = Seconds(status.GameTime(_host.Source.Latest)),
            realTimeSeconds = Seconds(status.RealTime(DateTimeOffset.UtcNow)),
            fromSave = status.FromSave,
            cheatsUsed = status.CheatsUsed,
            result = status.Result,
            personalBestSeconds = Seconds(comparison is { PersonalBest.Count: > 0 } c ? c.PersonalBest[^1] : null),
            sumOfBestSeconds = Seconds(comparison?.SumOfBest),
            splits = set?.Splits.Select((split, i) =>
            {
                var time = timed ? status.Times.ElementAtOrDefault(i) : null;
                var best = comparison?.PersonalBest.ElementAtOrDefault(i);
                var previous = i == 0 ? TimeSpan.Zero : (timed ? status.Times.ElementAtOrDefault(i - 1)?.GameTime : null);
                var gold = time is not null && previous is { } p && comparison?.BestSegments.ElementAtOrDefault(i) is { } segment && time.GameTime - p < segment;
                return new
                {
                    name = split.Name,
                    trigger = split.Trigger.Describe(),
                    timeSeconds = Seconds(time?.GameTime),
                    personalBestSeconds = Seconds(best),
                    deltaSeconds = time is not null && best is { } b ? Seconds(time.GameTime - b) : null,
                    gold,
                };
            }).ToList(),
        };
    }

    private static IResult Error(int status, string message) => Results.Json(new { error = message }, Json, statusCode: status);

    private static Task Reply(HttpContext context, int status, object body)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(body, Json);
    }

    private sealed record MarkerRequest(string? Note);

    private sealed record ModeRequest(string? Mode);

    private sealed record HealthRequest(long? Value, bool? Full, bool? Lock);

    private sealed record ToggleRequest(bool Enabled);

    private sealed record QuantityRequest(int Slot, int Quantity);

    private sealed record ChallengeRequest(string? Id, IReadOnlyList<Lockpick.Core.Challenges.ChallengeRule>? Rules);

    private sealed record ItemBoxRequest(int ItemId, int? Quantity);
}
