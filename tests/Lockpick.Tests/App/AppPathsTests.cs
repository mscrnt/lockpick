using Lockpick.App.Services;

namespace Lockpick.Tests.App;

public sealed class AppPathsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "lockpick-paths-" + Guid.NewGuid().ToString("N"));
    private readonly string _localAppData = Path.Combine(Path.GetTempPath(), "lockpick-localappdata");

    public AppPathsTests() => Directory.CreateDirectory(Path.Combine(_folder, "current"));

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string AppDirectory => Path.Combine(_folder, "current") + Path.DirectorySeparatorChar;

    [Fact]
    public void An_installed_copy_keeps_its_data_in_local_app_data() =>
        Assert.Equal(Path.Combine(_localAppData, "Lockpick"), AppPaths.DataRootFor(AppDirectory, _localAppData));

    [Fact]
    public void A_portable_copy_keeps_its_data_beside_it_outside_the_folder_an_update_replaces()
    {
        File.WriteAllBytes(Path.Combine(_folder, ".portable"), []);
        Assert.Equal(Path.Combine(_folder, "data"), AppPaths.DataRootFor(AppDirectory, _localAppData));
    }
}
