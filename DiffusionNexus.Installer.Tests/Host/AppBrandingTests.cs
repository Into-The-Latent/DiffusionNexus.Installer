using DiffusionNexus.Installer.Electron.Services;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Host;

public class AppBrandingTests
{
    [Fact]
    public void Window_icon_is_an_absolute_path_to_the_shipped_ico()
    {
        // Electron resolves a relative icon path against the Electron process's cwd, which is
        // node_modules/electron/dist in a Debug session and the install root when packaged, so
        // only an absolute path lands on the same file in both.
        var path = AppBranding.WindowIconPath;

        Path.IsPathRooted(path).Should().BeTrue();
        path.Should().EndWith(Path.Combine("Assets", "Branding", "app.ico"));
    }

    [Fact]
    public void Window_icon_file_ships_beside_the_host_assembly()
    {
        // The Content item in the Electron csproj copies the .ico into every consuming output
        // directory, this test's included. A move of the asset must update AppBranding too.
        File.Exists(AppBranding.WindowIconPath).Should().BeTrue(AppBranding.WindowIconPath);
    }
}
