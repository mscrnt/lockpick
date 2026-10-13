using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Lockpick.App.Views;

internal static class FilePickers
{
    private static readonly FilePickerFileType GameExecutable = new("Resident Evil (ResidentEvil.exe)")
    {
        Patterns = ["ResidentEvil.exe", "*.exe"],
    };

    /// <summary>Lets the user pick the game executable; returns its local path or null when cancelled.</summary>
    public static async Task<string?> PickGameExecutableAsync(Visual anchor)
    {
        if (TopLevel.GetTopLevel(anchor)?.StorageProvider is not { CanOpen: true } storage)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Find ResidentEvil.exe",
            AllowMultiple = false,
            FileTypeFilter = [GameExecutable],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
