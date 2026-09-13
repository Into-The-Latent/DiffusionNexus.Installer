using DiffusionNexus.Installer.Electron.Services;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Services;

/// <summary>
/// Card artwork for the software tiles. Lives in the Electron project because every path it
/// returns names a file that project serves out of its own wwwroot -- which is also why the
/// file-exists guard below is a same-project check now rather than Core reaching sideways.
/// </summary>
public class SoftwareLogosTests
{
    [Theory]
    [InlineData(RepositoryType.ComfyUI)]
    [InlineData(RepositoryType.A1111)]
    [InlineData(RepositoryType.Forge)]
    [InlineData(RepositoryType.AIToolkit)]
    [InlineData(RepositoryType.Fooocus)]
    [InlineData(RepositoryType.AceStep)]
    public void Gives_every_offerable_software_a_logo_under_wwwroot(RepositoryType type)
    {
        var path = SoftwareLogos.PathFor(type);

        path.Should().NotBeNull();
        path!.Should().StartWith("img/software/");
        path.Should().NotStartWith("/", "a leading slash breaks the base-href-relative asset URL");
    }

    [Theory]
    [InlineData(RepositoryType.ComfyUI)]
    [InlineData(RepositoryType.A1111)]
    [InlineData(RepositoryType.Forge)]
    [InlineData(RepositoryType.AIToolkit)]
    [InlineData(RepositoryType.Fooocus)]
    [InlineData(RepositoryType.AceStep)]
    public void Ships_the_logo_file_it_names(RepositoryType type)
    {
        // The theory above only checks the shape of the string. Renaming a .jpg under
        // wwwroot/img/software without updating the switch would pass it and break six cards with
        // a silent 404 -- the kind of failure no unit test sees and every user does.
        var path = SoftwareLogos.PathFor(type)!;

        var file = Path.Combine(WwwRoot(), path.Replace('/', Path.DirectorySeparatorChar));

        File.Exists(file).Should().BeTrue($"SoftwareLogos maps {type} to '{path}', so that file must exist");
    }

    [Fact]
    public void Falls_back_rather_than_throwing_for_a_software_with_no_artwork()
    {
        // None is never offered today, but a RepositoryType added to the SDK tomorrow must render
        // a neutral tile, not a broken image or a KeyNotFoundException on the first screen.
        SoftwareLogos.PathFor(RepositoryType.None).Should().BeNull();
    }

    /// <summary>
    /// Walked up from the test assembly rather than hardcoded: the absolute path differs per
    /// machine and per CI agent, and the repo root is identifiable by the solution file.
    /// </summary>
    private static string WwwRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DiffusionNexus.Installer.slnx")))
            dir = dir.Parent;

        var root = dir?.FullName ?? throw new InvalidOperationException(
            "Repo root not found above the test output folder.");

        return Path.Combine(root, "DiffusionNexus.Installer.Electron", "wwwroot");
    }
}
