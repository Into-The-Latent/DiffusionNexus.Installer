using Bunit;
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Electron.Components.Wizard;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

public class DisclaimerPanelTests : BunitContext
{
    [Fact]
    public void Links_to_the_third_party_licences_of_the_installer_itself()
    {
        // The disclaimer talks about third-party frameworks the installer downloads. The
        // installer is itself built from open-source components, and their notices must be one
        // click away from the place the user accepts the terms.
        var cut = Render<DisclaimerPanel>(p => p.Add(x => x.Module, new DisclaimerModule()));

        var link = cut.Find("a[href='/licenses']");
        link.TextContent.Should().Contain("Third-party licences");
        cut.Markup.Should().Contain("open-source components");
    }
}
