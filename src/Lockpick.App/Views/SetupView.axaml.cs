using Avalonia.Controls;
using Avalonia.Interactivity;
using Lockpick.App.ViewModels;

namespace Lockpick.App.Views;

public partial class SetupView : UserControl
{
    public SetupView() => InitializeComponent();

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SetupViewModel vm && await FilePickers.PickGameExecutableAsync(this) is { } path)
        {
            vm.GameExecutablePath = path;
            vm.CheckBuild();
        }
    }
}
