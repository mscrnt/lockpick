using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lockpick.Core.Profiles;

public sealed class ProfileValidationException(string message) : Exception(message);

/// <summary>Loads and validates <see cref="VersionProfile"/> JSON documents.</summary>
public static class ProfileLoader
{
    /// <summary>Upper bound for one capture region; captures record regions at up to 30 Hz.</summary>
    public const uint MaxRegionLength = 1024 * 1024;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static VersionProfile Parse(string json)
    {
        VersionProfile? profile;
        try
        {
            profile = JsonSerializer.Deserialize<VersionProfile>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or OverflowException)
        {
            throw new ProfileValidationException($"Profile JSON is malformed: {ex.Message}");
        }

        if (profile is null)
        {
            throw new ProfileValidationException("Profile JSON is empty.");
        }

        Validate(profile);
        return profile;
    }

    public static VersionProfile Load(Stream stream)
    {
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>Loads every *.json profile in a directory. Invalid files are reported, not thrown.</summary>
    public static IReadOnlyList<VersionProfile> LoadDirectory(string directory, Action<string, Exception>? onError = null)
    {
        var result = new List<VersionProfile>();
        if (!Directory.Exists(directory))
        {
            return result;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var stream = File.OpenRead(file);
                result.Add(Load(stream));
            }
            catch (Exception ex) when (ex is ProfileValidationException or IOException or UnauthorizedAccessException)
            {
                onError?.Invoke(file, ex);
            }
        }

        return result;
    }

    public static void Validate(VersionProfile profile)
    {
        var errors = new List<string>();
        if (profile.SchemaVersion != 1)
        {
            errors.Add($"unsupported schemaVersion {profile.SchemaVersion}");
        }

        if (string.IsNullOrWhiteSpace(profile.Id))
        {
            errors.Add("id is required");
        }

        var exe = profile.Executable;
        if (exe.Sha256.Length != 64 || !exe.Sha256.All(Uri.IsHexDigit))
        {
            errors.Add("executable.sha256 must be 64 hex characters");
        }

        if (exe.SizeOfImage == 0)
        {
            errors.Add("executable.sizeOfImage is required");
        }

        var image = profile.ImageRange;
        foreach (var anchor in profile.Anchors)
        {
            if (anchor.Bytes.Length == 0 || !image.Contains(anchor.Address, anchor.Bytes.Length))
            {
                errors.Add($"anchor '{anchor.Name}' at 0x{anchor.Address:X} is empty or outside the image {image}");
            }
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in profile.Fields)
        {
            if (!names.Add(field.Name))
            {
                errors.Add($"duplicate field '{field.Name}'");
            }

            if (!Enum.IsDefined(field.Type))
            {
                errors.Add($"field '{field.Name}' has an unknown type");
                continue;
            }

            if (!image.Contains(field.Address, Memory.ValueTypeExtensions.SizeOf(field.Type)))
            {
                errors.Add($"field '{field.Name}' at 0x{field.Address:X} is outside the image {image}");
            }

            if (field.IsTrusted && string.IsNullOrWhiteSpace(field.Evidence))
            {
                errors.Add($"field '{field.Name}' is marked {field.Verification} but has no evidence");
            }

            if (field.Write is { } write)
            {
                var (lowest, highest) = Memory.ValueTypeExtensions.Range(field.Type);
                if (field.Verification != Verification.Runtime || field.Mask is not null)
                {
                    errors.Add($"field '{field.Name}' is writable but not runtime-verified, or masked");
                }

                if (write.Min > write.Max || write.Min < lowest || write.Max > highest)
                {
                    errors.Add($"field '{field.Name}' has write bounds {write.Min}..{write.Max} outside its type");
                }
            }
        }

        foreach (var field in profile.Fields)
        {
            if (field.Write?.MaxField is { } bound && profile.FindField(bound) is null)
            {
                errors.Add($"field '{field.Name}' is bounded by unknown field '{bound}'");
            }
        }

        var addressNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var named in profile.Addresses)
        {
            if (!addressNames.Add(named.Name))
            {
                errors.Add($"duplicate address '{named.Name}'");
            }

            if (named.Length == 0 || named.Length > MaxRegionLength || !image.Contains(named.Address, (int)named.Length))
            {
                errors.Add($"address '{named.Name}' at 0x{named.Address:X} (+0x{named.Length:X}) is empty, too large or outside the image {image}");
            }

            if (named.IsTrusted && string.IsNullOrWhiteSpace(named.Evidence))
            {
                errors.Add($"address '{named.Name}' is marked {named.Verification} but has no evidence");
            }
        }

        var patchNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var patch in profile.Patches)
        {
            if (!patchNames.Add(patch.Name))
            {
                errors.Add($"duplicate patch '{patch.Name}'");
            }

            if (patch.Original.Length == 0 || patch.Original.Length != patch.Patched.Length || !image.Contains(patch.Address, patch.Original.Length))
            {
                errors.Add($"patch '{patch.Name}' at 0x{patch.Address:X} is empty, changes the length or is outside the image {image}");
            }

            if (patch.Verification >= Verification.Static && string.IsNullOrWhiteSpace(patch.Evidence))
            {
                errors.Add($"patch '{patch.Name}' is marked {patch.Verification} but has no evidence");
            }
        }

        var regionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var region in profile.CaptureRegions)
        {
            if (!regionNames.Add(region.Name))
            {
                errors.Add($"duplicate capture region '{region.Name}'");
            }

            if (region.Length == 0 || region.Length > MaxRegionLength || !image.Contains(region.Address, (int)Math.Min(region.Length, int.MaxValue)))
            {
                errors.Add($"capture region '{region.Name}' at 0x{region.Address:X} (+0x{region.Length:X}) is empty, too large or outside the image {image}");
            }
        }

        if (errors.Count > 0)
        {
            throw new ProfileValidationException($"Profile '{profile.Id}' is invalid: {string.Join("; ", errors)}");
        }
    }
}
