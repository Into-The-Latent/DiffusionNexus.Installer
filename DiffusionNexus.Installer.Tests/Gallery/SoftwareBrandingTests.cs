using System.Reflection;
using DiffusionNexus.Installer.Core.Gallery;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Gallery;

/// <summary>
/// Names for the software cards. Keyed by RepositoryType so a card never shows a raw enum name,
/// and total so a software the catalog adds later still gets a card.
///
/// The ARTWORK used to be asserted here too, against files under the Electron project's wwwroot --
/// a Core test reaching sideways into another project's folder layout. It moved with the switch it
/// was testing: see Services/SoftwareLogosTests.cs.
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

    [Fact]
    public void Names_nothing_that_lives_under_the_Electron_projects_wwwroot()
    {
        // The guard for the finding that moved LogoPath out: Installer.Core is the UI-agnostic half
        // and must not know that a different project serves img/software/*.jpg. Reflected over EVERY
        // public member rather than over DisplayName alone, so re-adding a LogoPath switch here
        // fails this test instead of merely looking out of place.
        var accessors = typeof(SoftwareBranding)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(string))
            .Where(m => m.GetParameters() is [{ ParameterType: var p }] && p == typeof(RepositoryType))
            .ToList();

        accessors.Should().NotBeEmpty("DisplayName at least must still be here");

        foreach (var accessor in accessors)
        foreach (var type in Enum.GetValues<RepositoryType>())
        {
            // ?? "": a re-added LogoPath would return null for an unbranded software, and the
            // assertions below are about what it says when it says anything.
            var value = (string?)accessor.Invoke(null, [type]) ?? string.Empty;

            value.Should().NotContain("img/", $"{accessor.Name}({type}) must not name a wwwroot asset");
            value.Should().NotEndWith(".jpg", $"{accessor.Name}({type}) must not name a file");
            value.Should().NotEndWith(".png", $"{accessor.Name}({type}) must not name a file");
        }
    }

    [Fact]
    public void Falls_back_rather_than_throwing_for_a_software_with_no_name_of_its_own()
    {
        // None is never offered today, but a RepositoryType added to the SDK tomorrow must not
        // take the welcome screen down with a KeyNotFoundException.
        SoftwareBranding.DisplayName(RepositoryType.None).Should().Be("None");
    }
}
