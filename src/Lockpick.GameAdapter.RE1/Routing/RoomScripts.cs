using System.Buffers.Binary;

namespace Lockpick.GameAdapter.RE1.Routing;

/// <summary>
/// One room-script command or condition, decoded (the route simulator's reading of RDT script bytes; the same as
/// tools/routesim/world.py). An <c>if</c> is a node with <see cref="Conds"/>, <see cref="Then"/> and <see cref="Else"/>;
/// <see cref="Chunk"/> marks the end of one of a script's chunks (each runs on its own).
/// </summary>
internal sealed class ScriptNode
{
    public int Op { get; init; }

    public IReadOnlyList<ScriptNode>? Conds { get; init; }

    public IReadOnlyList<ScriptNode> Then { get; init; } = [];

    public IReadOnlyList<ScriptNode> Else { get; init; } = [];

    public bool Chunk { get; init; }

    public int Bank { get; init; }

    public int Flag { get; init; }

    /// <summary>For a bit test: true when the bit has to be set.</summary>
    public bool IsSet { get; init; }

    /// <summary>For a bit operation: 0 set, 1 clear, 2 toggle.</summary>
    public int Mode { get; init; }

    public int Field { get; init; }

    public int Cmp { get; init; }

    public int Value { get; init; }

    public int Slot { get; init; }

    public IReadOnlyList<int> Zone { get; init; } = [];

    public int Stage { get; init; }

    public int Room { get; init; }

    public int Lock { get; init; }

    public int Key { get; init; }

    public int Probe { get; init; }

    public IReadOnlyList<int> Arrival { get; init; } = [];

    public int Handler { get; init; }

    public IReadOnlyList<int> Words { get; init; } = [];

    public int Item { get; init; }

    public int Quantity { get; init; }

    public int Script { get; init; }

    public int Type { get; init; }

    public int KillFlag { get; init; }

    public IReadOnlyList<int> Pos { get; init; } = [];

    public int List { get; init; }

    public int Index { get; init; }

    public IReadOnlyList<int> Box { get; init; } = [];

    public int Action { get; init; }

    public bool IsIf => Conds is not null;
}

internal enum EventStepKind
{
    Block,
    Wait,
    Start,
    Stop,
    Goto,
    End,
}

/// <summary>One step of an event script: an inline block of room-script commands, a condition the event waits on, or
/// starting / stopping / switching events.</summary>
internal sealed record EventStep(EventStepKind Kind, IReadOnlyList<ScriptNode> Nodes, int Slot = 0, int Script = 0);

/// <summary>A room's scripts: init (on entering), main (every frame) and its event scripts (RDT +0x68).</summary>
internal sealed record RoomScripts(IReadOnlyList<ScriptNode> Init, IReadOnlyList<ScriptNode> Main, IReadOnlyList<IReadOnlyList<EventStep>> Events)
{
    /// <summary>Bytes per room-script opcode, including the opcode (tools/rdt/rdt_index.py, BioRand).</summary>
    private static readonly byte[] Sizes =
    [
        2, 2, 2, 2, 4, 4, 4, 6, 4, 2, 2, 4, 26, 18, 2, 8,
        2, 2, 10, 4, 4, 2, 2, 10, 26, 4, 2, 22, 6, 2, 4, 28,
        14, 14, 4, 2, 4, 4, 0, 2, 6, 2, 12, 4, 2, 4, 0, 4,
        12, 4, 4, 2, 8, 4, 4, 4, 4, 2, 4, 6, 6, 12, 2, 6,
        16, 4, 4, 4, 2, 2, 44, 14, 2, 2, 2, 2, 4, 2, 4, 2,
        2,
    ];

    /// <summary>Opcodes that are conditions (they return true or false to the interpreter).</summary>
    public static readonly HashSet<int> ConditionOps = [0x04, 0x06, 0x07, 0x10, 0x11, 0x1A, 0x1D, 0x22, 0x2C, 0x36, 0x38, 0x3C, 0x3F, 0x50];

    // The event machine's widths per state (tools/rdt/events.py, as the decomp's evt_disasm.py verified them).
    private static readonly Dictionary<int, int> State0 = new() { [0x00] = 1, [0x01] = 1, [0x02] = 1, [0x03] = 1, [0x04] = 3, [0x05] = 4, [0x08] = 2, [0x09] = 2 };
    private static readonly Dictionary<int, int> State1 = new()
    {
        [0x00] = 1, [0x80] = 1, [0x8B] = 1, [0x82] = 1, [0x83] = 7, [0x84] = 4, [0x85] = 4, [0x86] = 1, [0x87] = 4, [0x88] = 4, [0x89] = 2, [0x8A] = 2,
    };

    private static readonly Dictionary<int, int> State2 = new()
    {
        [0x00] = 1, [0x01] = 1, [0x02] = 1, [0x03] = 1, [0x04] = 1, [0x05] = 4, [0x06] = 4, [0x07] = 8, [0x08] = 3, [0x09] = 2, [0x0A] = 4, [0x0B] = 8,
    };

    private static readonly Dictionary<int, int> Control = new() { [0xF6] = 1, [0xF7] = 1, [0xF8] = 4, [0xF9] = 3, [0xFA] = 4, [0xFB] = 1, [0xFD] = 1, [0xFE] = 1, [0xFF] = 1 };

    public static RoomScripts Read(byte[] data)
    {
        var offsets = Enumerable.Range(0, 19).Select(i => (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x48 + (4 * i)))).ToArray();
        List<ScriptNode> Chain(int pos)
        {
            var nodes = new List<ScriptNode>();
            while (pos + 2 <= data.Length && U16(data, pos) is var length and not 0)
            {
                nodes.AddRange(Block(data, pos + 2, pos + length));
                nodes.Add(new ScriptNode { Op = 0, Chunk = true });
                pos += length;
            }

            return nodes;
        }

        var starts = EventStarts(data);
        var bounds = starts.Distinct().Order().Append(data.Length).ToList();
        var events = starts.Select(s => (IReadOnlyList<EventStep>)Event(data, s, bounds.First(b => b > s))).ToList();
        return new RoomScripts(Chain(offsets[6]), Chain(offsets[7]), events);
    }

    private static List<int> EventStarts(byte[] data)
    {
        var starts = new List<int>();
        var baseOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x68));
        if (baseOffset == 0)
        {
            return starts;
        }

        for (var pos = baseOffset; pos + 4 <= data.Length; pos += 4)
        {
            var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
            if (offset == 0)
            {
                break;
            }

            starts.Add(baseOffset + offset);
        }

        return starts;
    }

    private sealed class Frame
    {
        public required List<ScriptNode> Target { get; set; }

        public required int End { get; set; }

        public (List<ScriptNode> Conds, List<ScriptNode> Then, List<ScriptNode> Else)? Node { get; init; }

        public bool Collecting { get; set; }
    }

    /// <summary>A script block as a tree, the if/else nesting kept (tools/routesim/world.py block()).</summary>
    public static List<ScriptNode> Block(byte[] data, int start, int end)
    {
        var root = new List<ScriptNode>();
        // An if is built from lists that fill as the block is read, then frozen into a node at the end.
        var pending = new List<(List<ScriptNode> Owner, int Index, List<ScriptNode> Conds, List<ScriptNode> Then, List<ScriptNode> Else)>();
        var stack = new List<Frame> { new() { Target = root, End = end } };
        var pos = start;
        while (pos < end)
        {
            while (stack.Count > 1 && pos >= stack[^1].End)
            {
                stack.RemoveAt(stack.Count - 1);
            }

            var op = data[pos];
            var size = op < Sizes.Length ? Sizes[op] : 0;
            if (size == 0 || pos + size > end)
            {
                break;
            }

            var b = data.AsSpan(pos, size);
            var top = stack[^1];
            if (op == 0x01)
            {
                List<ScriptNode> conds = [], then = [], otherwise = [];
                pending.Add((top.Target, top.Target.Count, conds, then, otherwise));
                top.Target.Add(new ScriptNode()); // replaced below once the if is complete
                stack.Add(new Frame { Target = then, End = pos + 2 + b[1], Node = (conds, then, otherwise), Collecting = true });
            }
            else if (op == 0x02)
            {
                if (top.Node is { } node)
                {
                    top.Target = node.Else;
                    top.End = pos + b[1];
                    top.Collecting = false;
                }
            }
            else if (op == 0x03)
            {
                if (top.Node is not null)
                {
                    stack.RemoveAt(stack.Count - 1);
                }
            }
            else if (op != 0x00)
            {
                if (ConditionOps.Contains(op) && top.Collecting && top.Node is { } node)
                {
                    node.Conds.Add(Decode(op, b));
                }
                else
                {
                    top.Collecting = false;
                    top.Target.Add(Decode(op, b));
                }
            }

            pos += size;
        }

        // Inner ifs were pending before outer ones, so freezing in reverse fills nested lists first.
        for (var i = pending.Count - 1; i >= 0; i--)
        {
            var (owner, index, conds, then, otherwise) = pending[i];
            owner[index] = new ScriptNode { Op = 0x01, Conds = conds, Then = then, Else = otherwise };
        }

        return root;
    }

    private static List<EventStep> Event(byte[] data, int start, int end)
    {
        var steps = new List<EventStep>();
        var pos = start;
        var state = 0;
        List<ScriptNode>? call = null;
        while (pos < end && steps.Count < 600)
        {
            var op = data[pos];
            if (op == 0xFC)
            {
                // A call: the command that follows is run by the next 0xFD, which loops back while it fails.
                var length = Math.Max(data[pos + 1], (byte)2);
                call = length > 2 ? Block(data, pos + 2, Math.Min(pos + length, end)) : [];
                pos += length;
                continue;
            }

            if (Control.TryGetValue(op, out var controlSize))
            {
                if (op == 0xFD && call is not null)
                {
                    steps.Add(new EventStep(EventStepKind.Wait, call));
                    call = null;
                }
                else if (op == 0xFF)
                {
                    steps.Add(new EventStep(EventStepKind.End, []));
                    break;
                }

                pos += controlSize;
                continue;
            }

            if (state == 0)
            {
                if (op is 0x06 or 0x07)
                {
                    var length = data[pos + 1];
                    if (length < 2 || pos + length > end)
                    {
                        break;
                    }

                    int bodyStart, bodyEnd;
                    if (op == 0x06)
                    {
                        // run_command_functions gets p+2 and reads a u16 block size there before the first command.
                        bodyStart = pos + 4;
                        bodyEnd = Math.Min(pos + 2 + U16(data, pos + 2), pos + length);
                    }
                    else
                    {
                        // One command, dispatched at p+2 and read at its own size (some length bytes are shorter).
                        var size = data[pos + 2] < Sizes.Length ? Sizes[data[pos + 2]] : 0;
                        bodyStart = pos + 2;
                        bodyEnd = Math.Min(pos + 2 + Math.Max(size, length - 2), data.Length);
                    }

                    steps.Add(new EventStep(EventStepKind.Block, Block(data, bodyStart, bodyEnd)));
                    pos += length;
                    continue;
                }

                if (!State0.TryGetValue(op, out var width))
                {
                    break;
                }

                switch (op)
                {
                    case 0x01:
                        state = 1;
                        break;
                    case 0x02 or 0x03:
                        state = 2;
                        break;
                    case 0x05:
                        steps.Add(new EventStep(EventStepKind.Start, [], data[pos + 1], data[pos + 2]));
                        break;
                    case 0x08:
                        steps.Add(new EventStep(EventStepKind.Goto, [], Script: data[pos + 1]));
                        return steps;
                    case 0x09:
                        steps.Add(new EventStep(EventStepKind.Stop, [], data[pos + 1]));
                        break;
                    default:
                        break;
                }

                pos += width;
            }
            else if (state == 1)
            {
                int? width = op == 0x81 ? ((data[pos + 1] & 0x0F) != 0 ? 10 : 2) : State1.TryGetValue(op, out var w) ? w : null;
                if (width is null)
                {
                    break;
                }

                if (op is 0x80 or 0x8B)
                {
                    state = 0;
                }

                pos += width.Value;
            }
            else
            {
                if (!State2.TryGetValue(op, out var width))
                {
                    break;
                }

                if (op == 0x01)
                {
                    state = 0;
                }

                pos += width;
            }
        }

        return steps;
    }

    private static int U16(byte[] data, int at) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at));

    private static int U16(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b[at..]);

    private static int S16(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadInt16LittleEndian(b[at..]);

    /// <summary>One command or condition (tools/routesim/world.py decode()).</summary>
    private static ScriptNode Decode(int op, ReadOnlySpan<byte> b) => op switch
    {
        0x04 => new ScriptNode { Op = op, Bank = b[1], Flag = b[2], IsSet = b[3] == 0 },
        0x05 => new ScriptNode { Op = op, Bank = b[1], Flag = b[2], Mode = b[3] is 1 or 2 ? b[3] : 0 },
        0x06 => new ScriptNode { Op = op, Field = b[1], Cmp = b[2], Value = b[3] },
        0x07 => new ScriptNode { Op = op, Field = b[2], Cmp = b[3], Value = U16(b, 4) },
        0x08 => new ScriptNode { Op = op, Field = b[1], Value = b[2] },
        0x0C => new ScriptNode
        {
            Op = op, Slot = b[1], Zone = [U16(b, 2), U16(b, 4), U16(b, 6), U16(b, 8)], Stage = b[0x0F] >> 5, Room = b[0x0F] & 0x1F,
            Lock = b[0x0E], Key = b[0x18], Probe = b[0x19], Arrival = [S16(b, 0x10), S16(b, 0x14)],
        },
        0x0D => new ScriptNode
        {
            Op = op, Slot = b[1], Zone = [U16(b, 2), U16(b, 4), U16(b, 6), U16(b, 8)], Handler = b[0x0A], Probe = b[0x0B],
            Words = [U16(b, 0x0C), U16(b, 0x0E), U16(b, 0x10)],
        },
        0x10 or 0x11 or 0x1A or 0x1D or 0x2C => new ScriptNode { Op = op, Item = b[1] },
        0x12 => new ScriptNode { Op = op, Slot = b[1], Handler = b[2], Probe = b[3], Words = [U16(b, 4), U16(b, 6), U16(b, 8)] },
        0x13 => new ScriptNode { Op = op, Slot = b[1], Handler = b[2], Probe = b[3] },
        0x14 => new ScriptNode { Op = op, Slot = b[2], Script = b[3] },
        0x18 => new ScriptNode
        {
            Op = op, Slot = b[1] & 0x7F, Zone = [S16(b, 2), S16(b, 4), U16(b, 6), U16(b, 8)], Item = b[0x0A], Quantity = b[0x0B],
            Flag = b[0x16], Probe = b[0x17],
        },
        0x1B => new ScriptNode { Op = op, Type = b[1], KillFlag = b[3] },
        0x1F => new ScriptNode { Op = op, Slot = b[1] & 0x3F, Pos = [S16(b, 4), S16(b, 6), S16(b, 8)] },
        0x20 => new ScriptNode { Op = op, Pos = [S16(b, 8), S16(b, 10), S16(b, 12)] },
        0x22 => new ScriptNode { Op = op, Field = b[1], Cmp = b[2], Value = b[3] },
        0x24 or 0x2D => new ScriptNode { Op = op, Slot = b[1], Action = b[2] },
        0x30 => new ScriptNode { Op = op, List = b[1], Index = b[2], Mode = b[3], Box = [U16(b, 8), U16(b, 10), U16(b, 4), U16(b, 6)] },
        0x44 => new ScriptNode { Op = op, Slot = b[1] },
        _ => new ScriptNode { Op = op },
    };
}
