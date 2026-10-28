using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Lockpick.App.Views;

internal static class FilePickers
{
    private static readonly FilePickerFileType GameExecutable = new("Resident Evil (ResidentEvil.exe, or Biohazard.exe for the Japanese game)")
    {
        Patterns = ["ResidentEvil.exe", "Biohazard.exe", "Bio.exe", "*.exe"],
    };

    private static readonly FilePickerFileType Splits = new("LiveSplit splits (*.lss)") { Patterns = ["*.lss"] };

    private static readonly FilePickerFileType Wave = new("Sounds (*.wav)") { Patterns = ["*.wav"] };

    /// <summary>Lets the user pick a .wav sound; returns its local path or null when cancelled.</summary>
    public static async Task<string?> PickSoundFileAsync(Visual anchor)
    {
        if (TopLevel.GetTopLevel(anchor)?.StorageProvider is not { CanOpen: true } storage)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a sound",
            AllowMultiple = false,
            FileTypeFilter = [Wave],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <summary>Lets the user pick a LiveSplit splits file; returns its local path or null when cancelled.</summary>
    public static async Task<string?> PickSplitsFileAsync(Visual anchor)
    {
        if (TopLevel.GetTopLevel(anchor)?.StorageProvider is not { CanOpen: true } storage)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import LiveSplit splits",
            AllowMultiple = false,
            FileTypeFilter = [Splits],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <summary>Asks where to save a LiveSplit splits file; returns the local path or null when cancelled.</summary>
    public static async Task<string?> SaveSplitsFileAsync(Visual anchor, string suggestedName)
    {
        if (TopLevel.GetTopLevel(anchor)?.StorageProvider is not { CanSave: true } storage)
        {
            return null;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save as LiveSplit splits",
            SuggestedFileName = suggestedName,
            DefaultExtension = "lss",
            FileTypeChoices = [Splits],
        });
        return file?.TryGetLocalPath();
    }

    /// <summary>Asks where to save a run record (JSON); returns the local path or null when cancelled.</summary>
    public static async Task<string?> SaveRecordFileAsync(Visual anchor, string suggestedName)
    {
        if (TopLevel.GetTopLevel(anchor)?.StorageProvider is not { CanSave: true } storage)
        {
            return null;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save run record",
            SuggestedFileName = suggestedName,
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("Run record (*.json)") { Patterns = ["*.json"] }],
        });
        return file?.TryGetLocalPath();
    }

    private static readonly FilePickerFileType Layout = new("Lockpick layout (*.lockpick-layout.json)") { Patterns = ["*.lockpick-layout.json", "*.json"] };

    /// <summary>Asks where to save an overlay layout; returns the local path or null when cancelled.</summary>
    public static async Task<string?> SaveLayoutFileAsync(Visual anchor)
    {
        if (TopLevel.GetTopLevel(anchor)?.StorageProvider is not { CanSave: true } storage)
        {
            return null;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save overlay layout",
            SuggestedFileName = "My layout.lockpick-layout.json",
            DefaultExtension = "json",
            FileTypeChoices = [Layout],
        });
        return file?.TryGetLocalPath();
    }

    /// <summary>Lets the user pick an overlay layout; returns its local path or null when cancelled.</summary>
    public static async Task<string?> PickLayoutFileAsync(Visual anchor)
    {
        if (TopLevel.GetTopLevel(anchor)?.StorageProvider is not { CanOpen: true } storage)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Load overlay layout", AllowMultiple = false, FileTypeFilter = [Layout] });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <summary>Lets the user pick the game executable; returns its local path or null when cancelled.</summary>
    public static async Task<string?> PickGameExecutableAsync(Visual anchor)
    {
        if (TopLevel.GetTopLevel(anchor)?.StorageProvider is not { CanOpen: true } storage)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Find ResidentEvil.exe (or Biohazard.exe)",
            AllowMultiple = false,
            FileTypeFilter = [GameExecutable],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
