using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lockpick.Core.Fingerprinting;
using Lockpick.Core.Memory;
using ValueType = Lockpick.Core.Memory.ValueType;

namespace Lockpick.Core.Profiles;

/// <summary>How strongly a profile field's address/meaning has been confirmed.</summary>
public enum Verification
{
    /// <summary>Lead only (e.g. from a third-party source). Never used for normalized state.</summary>
    Unverified = 0,

    /// <summary>Confirmed by static analysis of this exact executable (code references with matching width/semantics).</summary>
    Static,

    /// <summary>Confirmed by observing the live game process behave as expected.</summary>
    Runtime,
}

/// <summary>
/// A build-specific address profile. Profiles are data (JSON), so adding a build does not require
/// code changes in the adapter. Every address must fall inside the executable's image.
/// </summary>
public sealed record VersionProfile
{
    public int SchemaVersion { get; init; } = 1;

    public required string Id { get; init; }

    public required string GameId { get; init; }

    public required string DisplayName { get; init; }

    public required ExecutableSpec Executable { get; init; }

    public IReadOnlyList<CodeAnchor> Anchors { get; init; } = [];

    public IReadOnlyList<FieldSpec> Fields { get; init; } = [];

    /// <summary>
    /// Addresses the adapter reads as a whole (tables, pointers, arrays) rather than as one field. Like fields, each has
    /// its own verification; the adapter uses only trusted ones (<see cref="TrustedAddress"/>).
    /// </summary>
    public IReadOnlyList<NamedAddress> Addresses { get; init; } = [];

    /// <summary>
    /// Memory blocks recorded verbatim by captures (e.g. the whole game-state block), so captures keep everything
    /// needed for later analysis — including values no field describes yet.
    /// </summary>
    public IReadOnlyList<MemoryRegionSpec> CaptureRegions { get; init; } = [];

    public IReadOnlyList<string> Notes { get; init; } = [];

    public FieldSpec? FindField(string name) =>
        Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The named address when it is confirmed (static or runtime); null when missing or only a lead.</summary>
    public ulong? TrustedAddress(string name) =>
        Addresses.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) is { IsTrusted: true } a ? a.Address : null;

    /// <summary>
    /// The build has been checked in a live game (some field is runtime-verified). A profile ported from another build's
    /// code only (tools/re1/port_profile.py) has none until a live trace: its state is shown, its writes stay off.
    /// </summary>
    public bool IsLiveVerified => Fields.Any(f => f.Verification == Verification.Runtime);

    /// <summary>The executable image range in memory, derived from the expected image base and size.</summary>
    public AddressRange ImageRange => new(Executable.ImageBase, Executable.SizeOfImage);
}

public sealed record ExecutableSpec
{
    public required string FileName { get; init; }

    public long FileSize { get; init; }

    public required string Sha256 { get; init; }

    public PeMachine Machine { get; init; }

    [JsonConverter(typeof(HexUInt32Converter))]
    public uint TimeDateStamp { get; init; }

    [JsonConverter(typeof(HexUInt64Converter))]
    public ulong ImageBase { get; init; }

    [JsonConverter(typeof(HexUInt32Converter))]
    public uint SizeOfImage { get; init; }
}

/// <summary>
/// Bytes expected at a code address in the running process. Anchors are chosen to be instructions that
/// reference the profile's globals, so a match proves the running code uses the addresses we read.
/// </summary>
public sealed record CodeAnchor
{
    public required string Name { get; init; }

    [JsonConverter(typeof(HexUInt64Converter))]
    public ulong Address { get; init; }

    [JsonConverter(typeof(HexBytesConverter))]
    public required byte[] Bytes { get; init; }

    public string? Evidence { get; init; }
}

/// <summary>A named address (a table, pointer or array start) and the bytes the adapter reads there.</summary>
public sealed record NamedAddress
{
    public required string Name { get; init; }

    [JsonConverter(typeof(HexUInt64Converter))]
    public ulong Address { get; init; }

    /// <summary>Bytes read from <see cref="Address"/> (all must lie in the image).</summary>
    [JsonConverter(typeof(HexUInt32Converter))]
    public uint Length { get; init; } = 1;

    public Verification Verification { get; init; }

    public string? Description { get; init; }

    public string? Evidence { get; init; }

    public bool IsTrusted => Verification >= Verification.Static;
}

public sealed record MemoryRegionSpec
{
    public required string Name { get; init; }

    [JsonConverter(typeof(HexUInt64Converter))]
    public ulong Address { get; init; }

    [JsonConverter(typeof(HexUInt32Converter))]
    public uint Length { get; init; }

    public string? Description { get; init; }
}

public sealed record FieldSpec
{
    public required string Name { get; init; }

    [JsonConverter(typeof(HexUInt64Converter))]
    public ulong Address { get; init; }

    public ValueType Type { get; init; }

    /// <summary>Optional mask applied to the raw value before interpretation.</summary>
    [JsonConverter(typeof(HexNullableUInt32Converter))]
    public uint? Mask { get; init; }

    public Verification Verification { get; init; }

    public string? Description { get; init; }

    public string? Evidence { get; init; }

    public IReadOnlyList<string> Sources { get; init; } = [];

    /// <summary>Present only on fields the trainer may write; see <see cref="WriteSpec"/>.</summary>
    public WriteSpec? Write { get; init; }

    public bool IsTrusted => Verification >= Verification.Static;

    /// <summary>Writable: the profile declares bounds for it and its meaning is confirmed in the live game.</summary>
    public bool IsWritable => Write is not null && Verification == Verification.Runtime && Mask is null;

    public override string ToString() => $"{Name} @0x{Address:X} {Type} ({Verification})";
}

/// <summary>
/// Bounds for a field the trainer may write (Trainer Mode only). A write outside them is refused before it reaches the
/// game; <see cref="MaxField"/> bounds the value by another field's current value (HP ≤ max HP).
/// </summary>
public sealed record WriteSpec
{
    public long Min { get; init; }

    public long Max { get; init; }

    public string? MaxField { get; init; }

    /// <summary>
    /// May be written outside gameplay too (at the title, in menus): a value that means the same everywhere, such as the
    /// window's focus flag or the keyboard map. Other fields are written only in a game.
    /// </summary>
    public bool AnyTime { get; init; }
}

internal sealed class HexUInt64Converter : JsonConverter<ulong>
{
    public override ulong Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Number ? reader.GetUInt64() : HexParsing.ParseUInt64(reader.GetString());

    public override void Write(Utf8JsonWriter writer, ulong value, JsonSerializerOptions options) =>
        writer.WriteStringValue($"0x{value:X}");
}

internal sealed class HexUInt32Converter : JsonConverter<uint>
{
    public override uint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Number ? reader.GetUInt32() : checked((uint)HexParsing.ParseUInt64(reader.GetString()));

    public override void Write(Utf8JsonWriter writer, uint value, JsonSerializerOptions options) =>
        writer.WriteStringValue($"0x{value:X}");
}

internal sealed class HexNullableUInt32Converter : JsonConverter<uint?>
{
    public override bool HandleNull => true;

    public override uint? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.Number => reader.GetUInt32(),
            _ => checked((uint)HexParsing.ParseUInt64(reader.GetString())),
        };

    public override void Write(Utf8JsonWriter writer, uint? value, JsonSerializerOptions options)
    {
        if (value is { } v)
        {
            writer.WriteStringValue($"0x{v:X}");
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

internal sealed class HexBytesConverter : JsonConverter<byte[]>
{
    public override byte[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        HexParsing.ParseBytes(reader.GetString());

    public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options) =>
        writer.WriteStringValue(string.Join(' ', value.Select(b => b.ToString("X2", CultureInfo.InvariantCulture))));
}

public static class HexParsing
{
    public static ulong ParseUInt64(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new FormatException("Expected a hexadecimal value.");
        }

        var s = text.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.Parse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return ulong.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    public static byte[] ParseBytes(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new FormatException("Expected a byte sequence like \"A0 20 98 BE 00\".");
        }

        return text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(b => byte.Parse(b, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
            .ToArray();
    }
}
