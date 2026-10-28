using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Lockpick.App.ViewModels;

namespace Lockpick.App.Views;

public partial class StreamView : UserControl
{
    public StreamView()
    {
        InitializeComponent();
        StreamEditor.Placed += (_, placed) => (DataContext as StreamViewModel)?.PlaceLayoutWidget(placed.Id, placed.Spot);
    }

    private void OnShowScript(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.StreamViewModel { ObsScript: { } path } && File.Exists(path))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
    }

    private async void OnCopyObsLink(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url } && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetValueAsync(Avalonia.Input.DataFormat.Text, url);
            Copied.IsVisible = true;
        }
    }
}
