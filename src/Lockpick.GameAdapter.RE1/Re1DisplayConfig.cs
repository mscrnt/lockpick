using Lockpick.Core.Display;

namespace Lockpick.GameAdapter.RE1;

/// <summary>
/// GOG's DirectDraw wrapper settings (<c>dxcfg.ini</c>, docs/DISPLAY.md) as the player changes them from Lockpick:
/// <c>presentation</c>, <c>display</c>, <c>aspect</c>, <c>scaling</c> and the options below (values as GOG's own
/// <c>dxcfg.exe</c> writes them); any other line is kept. The file as it was before Lockpick's first change is copied to
/// the backup folder (outside the game folder) and can be put back.
/// </summary>
public sealed class Re1DisplayConfig(string iniPath, string backupDirectory) : IGameDisplayConfig
{
    // Named after the file, so GOG's dxcfg.ini and Steam's launcher ini keep their own backups.
    private string BackupPath => System.IO.Path.Combine(backupDirectory, System.IO.Path.GetFileName(iniPath));

    public IReadOnlyList<DisplayOption> Options { get; } =
    [
        new("vsync", "Vertical sync", [new("application", "Set by the game"), new("enabled", "On")], "application"),
        new("gamma", "Gamma (brightness)",
            [.. new[] { "0.5", "0.6", "0.7", "0.8", "0.9", "1.0", "1.1", "1.2", "1.3", "1.4", "1.5" }
                .Select(g => new DisplayOptionChoice(g, g == "1.0" ? "1.0 (normal)" : g))], "1.0"),
        new("anisotropic", "Texture filtering", [new("application", "Set by the game"), new("af_2x", "Anisotropic 2×"),
            new("af_4x", "Anisotropic 4×"), new("af_8x", "Anisotropic 8×"), new("af_16x", "Anisotropic 16×")], "application"),
        new("antialiasing", "Antialiasing", [new("application", "Set by the game"), new("msaa_2x", "MSAA 2×"),
            new("msaa_4x", "MSAA 4×"), new("msaa_8x", "MSAA 8×")], "application"),
    ];

    public IReadOnlyDictionary<string, string> ReadOptions()
    {
        var lines = File.Exists(iniPath) ? File.ReadAllLines(iniPath) : [];
        return Options.ToDictionary(o => o.Key, o =>
            lines.Select(l => l.Split('=', 2)).Where(p => p.Length == 2 && p[0].Trim().Equals(o.Key, StringComparison.OrdinalIgnoreCase))
                .Select(p => p[1].Trim()).LastOrDefault(v => o.Choices.Any(c => c.Value.Equals(v, StringComparison.OrdinalIgnoreCase)))?.ToLowerInvariant()
            ?? o.Default);
    }

    public void WriteOptions(IReadOnlyDictionary<string, string> values) =>
        Set(values.Where(v => Options.Any(o => o.Key == v.Key && o.Choices.Any(c => c.Value == v.Value)))
            .ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase));

    public string Path => iniPath;

    public bool HasBackup => File.Exists(BackupPath);

    public DisplayConfiguration Read()
    {
        var lines = File.Exists(iniPath) ? File.ReadAllLines(iniPath) : [];
        string? Value(string key) => lines.Select(l => l.Split('=', 2)).Where(p => p.Length == 2 && p[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            .Select(p => p[1].Trim()).LastOrDefault();
        var presentation = Value("presentation") is { } p && p.Equals("fullscreen", StringComparison.OrdinalIgnoreCase)
            ? DisplayPresentation.Fullscreen
            : DisplayPresentation.Windowed;
        var display = Value("display") is { Length: > 0 } d ? d.ToLowerInvariant() : DisplayConfiguration.Desktop;
        return new DisplayConfiguration(presentation, display, Re1Display.Parse(lines).Scaling);
    }

    public void Write(DisplayConfiguration configuration) =>
        Set(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["presentation"] = configuration.Presentation == DisplayPresentation.Fullscreen ? "fullscreen" : "windowed",
            ["display"] = configuration.Resolution,
            ["aspect"] = configuration.Scaling == PictureScaling.Stretch ? "disabled" : "enabled",
            ["scaling"] = configuration.Scaling == PictureScaling.WholeMultiple ? "integer" : "fit",
        });

    /// <summary>Sets keys in the file (replacing their lines, adding missing ones to the section), backing it up once.</summary>
    private void Set(Dictionary<string, string> values)
    {
        if (!HasBackup && File.Exists(iniPath))
        {
            Directory.CreateDirectory(backupDirectory);
            File.Copy(iniPath, BackupPath);
        }

        var lines = (File.Exists(iniPath) ? File.ReadAllLines(iniPath) : ["[dxcfg]"]).ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < lines.Count; i++)
        {
            var parts = lines[i].Split('=', 2);
            if (parts.Length == 2 && values.TryGetValue(parts[0].Trim(), out var value))
            {
                lines[i] = $"{parts[0].Trim()}={value}";
                seen.Add(parts[0].Trim());
            }
        }

        var section = lines.FindIndex(l => l.Trim().Equals("[dxcfg]", StringComparison.OrdinalIgnoreCase));
        var insertAt = section < 0 ? lines.Count : section + 1;
        foreach (var (key, value) in values.Where(v => !seen.Contains(v.Key)))
        {
            lines.Insert(insertAt++, $"{key}={value}");
        }

        File.WriteAllLines(iniPath, lines);
    }

    public void RestoreBackup()
    {
        if (HasBackup)
        {
            File.Copy(BackupPath, iniPath, overwrite: true);
        }
    }
}
