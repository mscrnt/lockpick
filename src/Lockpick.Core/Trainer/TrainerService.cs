using Lockpick.Core.Events;
using Lockpick.Core.Memory;
using Lockpick.Core.Monitoring;
using Lockpick.Core.Processes;
using Lockpick.Core.Profiles;
using Lockpick.Core.State;
using Microsoft.Extensions.Logging;

namespace Lockpick.Core.Trainer;

/// <summary>
/// The only way Lockpick changes the game. Game-agnostic: it writes profile fields, never raw addresses.
/// <list type="bullet">
/// <item>A write handle exists only in <see cref="TrainerMode.Trainer"/> while attached; Challenge and read-only modes
/// never open one.</item>
/// <item>Every write names a profile field that is runtime-verified and declares bounds (<see cref="WriteSpec"/>), is
/// checked against them (and against another field's live value, e.g. HP ≤ max HP), is read back, and is logged with
/// the old and new value.</item>
/// <item>Locks and rules (infinite ammo) are re-applied on each poll while the player is in a game; a rule may also
/// apply outside it (keep-running) and put the game back when it ends.</item>
/// <item>A run (from a new game or a loaded save) that the trainer changed raises <see cref="RunInvalidated"/> once.</item>
/// </list>
/// </summary>
public sealed class TrainerService : IDisposable
{
    private readonly IGameStateSource _source;
    private readonly IProcessWriteHost _host;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TrainerRule> _rules = new(StringComparer.Ordinal);
    private IProcessWriter? _writer;
    private VersionProfile? _profile;
    private int? _processId;
    private GameSnapshot? _latest;
    private bool _disposed;

    public TrainerService(IGameStateSource source, IProcessWriteHost host, TrainerActionLog log, ILogger logger, TimeProvider? time = null)
    {
        _source = source;
        _host = host;
        Log = log;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _source.StatusChanged += OnStatus;
        _source.Polled += OnPolled;
        _source.EventRaised += OnEvent;
        OnStatus(this, _source.Status);
    }

    public TrainerActionLog Log { get; }

    public TrainerMode Mode { get; private set; } = TrainerMode.ReadOnly;

    /// <summary>True while a write handle is open (Trainer Mode and attached).</summary>
    public bool HasWriteAccess
    {
        get
        {
            lock (_gate)
            {
                return _writer is not null;
            }
        }
    }

    /// <summary>Why Trainer Mode has no write handle although it is on (e.g. Windows refused it), or null.</summary>
    public string? WriteAccessError { get; private set; }

    /// <summary>The trainer changed the game since the current run started.</summary>
    public bool RunModified { get; private set; }

    public DateTimeOffset? RunModifiedAt { get; private set; }

    public IReadOnlyDictionary<string, long> Locks
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, long>(_locks, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public bool HasRule(string id)
    {
        lock (_gate)
        {
            return _rules.ContainsKey(id);
        }
    }

    /// <summary>Raised for trainer actions that changed the game and for <see cref="RunInvalidated"/>.</summary>
    public event EventHandler<GameEvent>? EventRaised;

    /// <summary>Mode, write access, locks, rules or the run's state changed.</summary>
    public event EventHandler? StateChanged;

    public void SetMode(TrainerMode mode)
    {
        lock (_gate)
        {
            if (mode == Mode)
            {
                return;
            }

            if (mode != TrainerMode.Trainer)
            {
                // Leaving Trainer Mode stops everything that writes; nothing is carried over. Rules put the game back
                // first, while the write handle is still open.
                RestoreRules(TrainerSource.User);
                Mode = mode;
                _locks.Clear();
                _rules.Clear();
                CloseWriter();
            }
            else
            {
                Mode = mode;
                OpenWriter();
            }

            Record(new TrainerAction(_time.GetUtcNow(), mode switch
            {
                TrainerMode.Trainer => "Trainer Mode on",
                TrainerMode.Challenge => "Challenge Mode on",
                _ => "Read-only",
            }, null, null, null, TrainerSource.User, TrainerResult.Applied, mode == TrainerMode.Trainer ? WriteAccessError : null));
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Writes one field (validated, read back, logged).</summary>
    public TrainerAction Write(string field, long value, TrainerSource source = TrainerSource.User, string action = "Set")
    {
        TrainerAction result;
        lock (_gate)
        {
            result = WriteCore(field, value, source, action);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    /// <summary>Writes <paramref name="value"/> now and keeps writing it whenever the game changes it.</summary>
    public TrainerAction Lock(string field, long value, TrainerSource source = TrainerSource.User)
    {
        TrainerAction result;
        lock (_gate)
        {
            result = WriteCore(field, value, source, "Lock");
            if (result.Result == TrainerResult.Applied)
            {
                _locks[field] = value;
            }
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public void Unlock(string field, TrainerSource source = TrainerSource.User)
    {
        lock (_gate)
        {
            if (_locks.Remove(field))
            {
                Record(new TrainerAction(_time.GetUtcNow(), "Unlock", field, null, null, source, TrainerResult.Applied));
            }
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Turns on a rule (e.g. infinite ammo): on every poll in a game, <paramref name="rule"/> says which fields to write.
    /// Refused unless Trainer Mode is on with write access.
    /// </summary>
    public TrainerAction SetRule(string id, string name, Func<GameSnapshot, IReadOnlyList<FieldWrite>> rule, TrainerSource source = TrainerSource.User) =>
        SetRule(id, new TrainerRule(name, rule), source);

    /// <summary>Turns on a rule; one already on under <paramref name="id"/> puts the game back first and is replaced.</summary>
    public TrainerAction SetRule(string id, TrainerRule rule, TrainerSource source = TrainerSource.User)
    {
        TrainerAction result;
        lock (_gate)
        {
            var refusal = WriteRefusal();
            if (refusal is null && _rules.TryGetValue(id, out var before))
            {
                Restore(before, source);
                _rules.Remove(id);
            }

            refusal ??= ApplyPatches(rule, source);
            result = new TrainerAction(_time.GetUtcNow(), $"{rule.Name} on", null, null, null, source,
                refusal is null ? TrainerResult.Applied : TrainerResult.Refused, refusal);
            if (refusal is null)
            {
                _rules[id] = rule;
            }

            Record(result);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public void ClearRule(string id, TrainerSource source = TrainerSource.User)
    {
        lock (_gate)
        {
            if (_rules.Remove(id, out var removed))
            {
                Restore(removed, source);
                Record(new TrainerAction(_time.GetUtcNow(), $"{removed.Name} off", null, null, null, source, TrainerResult.Applied));
            }
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _source.StatusChanged -= OnStatus;
        _source.Polled -= OnPolled;
        _source.EventRaised -= OnEvent;
        lock (_gate)
        {
            RestoreRules(TrainerSource.User); // Lockpick closing: the game goes back to how it was
            CloseWriter();
        }
    }

    private void OnStatus(object? sender, MonitorStatus status)
    {
        lock (_gate)
        {
            if (status.State == ConnectionState.Attached && status.Profile is { } profile && status.Process is { } process)
            {
                if (_processId == process.ProcessId && _profile == profile)
                {
                    return;
                }

                CloseWriter();
                _profile = profile;
                _processId = process.ProcessId;
                if (Mode == TrainerMode.Trainer)
                {
                    OpenWriter();
                }
            }
            else
            {
                CloseWriter();
                _profile = null;
                _processId = null;
                _latest = null;
            }
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPolled(object? sender, GameSnapshot snapshot)
    {
        var changed = false;
        lock (_gate)
        {
            _latest = snapshot;
            if (_writer is null || Mode != TrainerMode.Trainer)
            {
                return;
            }

            var inGame = snapshot.InGameplay is { HasValue: true, Value: true };
            foreach (var (field, value) in _locks)
            {
                if (inGame && Raw(snapshot, field) is { } current && current != value)
                {
                    WriteCore(field, value, TrainerSource.Lock, "Lock held");
                    changed = true;
                }
            }

            foreach (var rule in _rules.Values)
            {
                if (!inGame && !rule.OutsideGameplay)
                {
                    continue;
                }

                foreach (var write in rule.Writes(snapshot))
                {
                    WriteCore(write.Field, write.Value, TrainerSource.Rule, rule.Name);
                    changed = true;
                }
            }
        }

        if (changed)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnEvent(object? sender, GameEvent gameEvent)
    {
        if (gameEvent is GameLoaded)
        {
            // A new game or a loaded save starts a new run: it is clean until the trainer changes it.
            RunModified = false;
            RunModifiedAt = null;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private string? WriteRefusal() =>
        Mode switch
        {
            TrainerMode.Challenge => "Challenge Mode is on: Lockpick doesn't change the game",
            TrainerMode.ReadOnly => "Trainer Mode is off",
            _ when _writer is null => WriteAccessError ?? "not attached to the game",
            _ => null,
        };

    private TrainerAction WriteCore(string field, long value, TrainerSource source, string action)
    {
        var now = _time.GetUtcNow();
        TrainerAction Refuse(string why, long? old = null) =>
            Record(new TrainerAction(now, action, field, old, value, source, TrainerResult.Refused, why));

        if (WriteRefusal() is { } refusal)
        {
            return Refuse(refusal);
        }

        if (_profile?.FindField(field) is not { IsWritable: true, Write: { } bounds } spec)
        {
            return Refuse($"{field} can't be written (not a verified, writable field of this game build)");
        }

        if (!bounds.AnyTime && _latest?.InGameplay is not { HasValue: true, Value: true })
        {
            return Refuse("not in a game");
        }

        var writer = _writer!;
        var max = bounds.Max;
        if (bounds.MaxField is { } bound)
        {
            if (_profile.FindField(bound) is not { } boundSpec || !writer.TryReadValue(boundSpec.Address, boundSpec.Type, out var limit))
            {
                return Refuse($"couldn't read {bound} to check the value");
            }

            max = Math.Min(max, limit);
        }

        if (value < bounds.Min || value > max)
        {
            return Refuse($"{value} is outside {bounds.Min}..{max}");
        }

        if (!writer.TryReadValue(spec.Address, spec.Type, out var old))
        {
            return Record(new TrainerAction(now, action, field, null, value, source, TrainerResult.Failed, "couldn't read the current value"));
        }

        if (old == value && source is TrainerSource.Lock or TrainerSource.Rule)
        {
            // Already right (the snapshot that triggered it was older than the game): nothing to write or log.
            return new TrainerAction(now, action, field, old, value, source, TrainerResult.Applied);
        }

        Span<byte> bytes = stackalloc byte[spec.Type.SizeOf()];
        spec.Type.Encode(value, bytes);
        if (!writer.TryWrite(spec.Address, bytes))
        {
            return Record(new TrainerAction(now, action, field, old, value, source, TrainerResult.Failed, "the write was refused"));
        }

        if (!writer.TryReadValue(spec.Address, spec.Type, out var readBack) || readBack != value)
        {
            return Record(new TrainerAction(now, action, field, old, value, source, TrainerResult.Mismatch, $"read back {readBack}"));
        }

        var applied = Record(new TrainerAction(now, action, field, old, value, source, TrainerResult.Applied));
        if (old != value)
        {
            EventRaised?.Invoke(this, new TrainerActionExecuted(now, applied.Describe()));
            MarkModified(now, applied.Describe());
        }

        return applied;
    }

    private void MarkModified(DateTimeOffset now, string what)
    {
        if (!RunModified)
        {
            RunModified = true;
            RunModifiedAt = now;
            EventRaised?.Invoke(this, new RunInvalidated(now, what));
        }
    }

    /// <summary>Puts back what each rule changed (before the write handle closes).</summary>
    private void RestoreRules(TrainerSource source)
    {
        foreach (var rule in _rules.Values)
        {
            Restore(rule, source);
        }
    }

    private void Restore(TrainerRule rule, TrainerSource source)
    {
        if (_writer is null || Mode != TrainerMode.Trainer)
        {
            return;
        }

        foreach (var write in rule.Restore?.Invoke() ?? [])
        {
            WriteCore(write.Field, write.Value, source, $"{rule.Name} off: put back");
        }

        foreach (var name in Enumerable.Reverse(rule.Patches))
        {
            if (_profile?.FindPatch(name) is { } patch)
            {
                PatchCore(patch, patch.Patched, patch.Original, source, $"{rule.Name} off: put back {name}");
            }
        }
    }

    /// <summary>Applies the rule's code patches; on any failure puts back the ones already applied and says why.</summary>
    private string? ApplyPatches(TrainerRule rule, TrainerSource source)
    {
        var applied = new List<CodePatch>();
        foreach (var name in rule.Patches)
        {
            if (_profile?.FindPatch(name) is not { IsUsable: true } patch)
            {
                return Undo($"{rule.Name} isn't checked on this version of the game yet ({name})");
            }

            if (PatchCore(patch, patch.Original, patch.Patched, source, $"{rule.Name}: {name}") is { } why)
            {
                return Undo(why);
            }

            applied.Add(patch);
        }

        return null;

        string Undo(string why)
        {
            foreach (var patch in Enumerable.Reverse(applied))
            {
                PatchCore(patch, patch.Patched, patch.Original, source, $"{rule.Name}: put back {patch.Name}");
            }

            return why;
        }
    }

    /// <summary>
    /// Writes <paramref name="to"/> over <paramref name="from"/> at the patch's address: only when the game's code is
    /// <paramref name="from"/> (already <paramref name="to"/> counts as done), read back and logged. Null when done.
    /// </summary>
    private string? PatchCore(CodePatch patch, byte[] from, byte[] to, TrainerSource source, string action)
    {
        var now = _time.GetUtcNow();
        string Fail(string why, TrainerResult result)
        {
            Record(new TrainerAction(now, action, null, null, null, source, result, why));
            return why;
        }

        if (_writer is null)
        {
            return Fail("not attached to the game", TrainerResult.Refused);
        }

        var current = new byte[from.Length];
        if (!_writer.TryRead(patch.Address, current))
        {
            return Fail($"couldn't read the game's code at 0x{patch.Address:X}", TrainerResult.Failed);
        }

        if (current.AsSpan().SequenceEqual(to))
        {
            return null;
        }

        if (!current.AsSpan().SequenceEqual(from))
        {
            return Fail($"the game's code at 0x{patch.Address:X} isn't what Lockpick expects ({Convert.ToHexString(current)}); left alone",
                TrainerResult.Refused);
        }

        if (!_writer.TryWrite(patch.Address, to))
        {
            return Fail($"Windows refused the write at 0x{patch.Address:X}", TrainerResult.Failed);
        }

        var back = new byte[to.Length];
        if (!_writer.TryRead(patch.Address, back) || !back.AsSpan().SequenceEqual(to))
        {
            _writer.TryWrite(patch.Address, from);
            return Fail($"read back {Convert.ToHexString(back)} at 0x{patch.Address:X}", TrainerResult.Mismatch);
        }

        Record(new TrainerAction(now, action, null, null, null, source, TrainerResult.Applied,
            $"0x{patch.Address:X}: {Convert.ToHexString(from)} -> {Convert.ToHexString(to)}"));
        MarkModified(now, action);
        return null;
    }

    private TrainerAction Record(TrainerAction action)
    {
        Log.Add(action);
        return action;
    }

    private void OpenWriter()
    {
        WriteAccessError = null;
        if (_writer is not null || _processId is not { } pid)
        {
            return;
        }

        try
        {
            _writer = _host.OpenForWrite(pid);
            _logger.LogInformation("Trainer Mode: write access to PID {ProcessId} opened", pid);
        }
        catch (ProcessAccessException ex)
        {
            WriteAccessError = ex.Message;
            _logger.LogWarning("Trainer Mode: write access to PID {ProcessId} refused: {Reason}", pid, ex.Message);
        }
    }

    private void CloseWriter()
    {
        if (_writer is null)
        {
            return;
        }

        _writer.Dispose();
        _writer = null;
        _logger.LogInformation("Trainer Mode: write access closed");
    }

    private static long? Raw(GameSnapshot snapshot, string field) =>
        snapshot.RawFields.TryGetValue(field, out var value) && value.HasValue ? value.Value : null;
}
