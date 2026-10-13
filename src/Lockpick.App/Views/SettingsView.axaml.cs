using Avalonia.Controls;
using Avalonia.Interactivity;
using Lockpick.App.ViewModels;

namespace Lockpick.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && await FilePickers.PickGameExecutableAsync(this) is { } path)
        {
            vm.GameExecutablePath = path;
            vm.CheckInstalledBuild();
        }
    }
}
