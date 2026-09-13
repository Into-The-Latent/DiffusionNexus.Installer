using DiffusionNexus.Installer.Core.Gallery;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Gallery;

/// <summary>
/// Names and logos for the software cards. Keyed by RepositoryType so a card never shows a raw
/// enum name, and null-safe so a software the catalog adds later renders a neutral tile instead
/// of a broken image.
/// </summary>
public class SoftwareBrandingTests
{
    [Theory]
    [InlineData(RepositoryType.ComfyUI, "ComfyUI")]
    [InlineData(RepositoryType.A1111, "Automatic 1111")]
    [InlineData(RepositoryType.Forge, "Forge")]
    [InlineData(RepositoryType.AIToolkit, "AI Toolkit")]
    [InlineData(RepositoryType.Fooocus, "Fooocus")]
    [InlineData(RepositoryType.AceStep, "ACE-Step")]
    public void Gives_every_offerable_software_a_human_name(RepositoryType type, string expected)
    {
        SoftwareBranding.DisplayName(type).Should().Be(expected);
    }

    [Theory]
    [InlineData(RepositoryType.ComfyUI)]
    [InlineData(RepositoryType.A1111)]
    [InlineData(RepositoryType.Forge)]
    [InlineData(RepositoryType.AIToolkit)]
    [InlineData(RepositoryType.Fooocus)]
    [InlineData(RepositoryType.AceStep)]
    public void Gives_every_offerable_software_a_logo_under_wwwroot(RepositoryType type)
    {
        var path = SoftwareBranding.LogoPath(type);

        path.Should().NotBeNull();
        path!.Should().StartWith("img/software/");
        path.Should().NotStartWith("/", "a leading slash breaks the base-href-relative asset URL");
    }

    [Fact]
    public void Falls_back_rather_than_throwing_for_a_software_with_no_artwork()
    {
        // None is never offered today, but a RepositoryType added to the SDK tomorrow must not
        // take the welcome screen down with a KeyNotFoundException.
        SoftwareBranding.LogoPath(RepositoryType.None).Should().BeNull();
        SoftwareBranding.DisplayName(RepositoryType.None).Should().Be("None");
    }
}
