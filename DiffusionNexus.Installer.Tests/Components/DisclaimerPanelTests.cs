using Bunit;
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Electron.Components.Wizard;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

public class DisclaimerPanelTests : BunitContext
{
    [Fact]
    public void Shows_the_third_party_licences_in_a_dialog_without_leaving_the_wizard()
    {
        // The first version linked to a page. That threw away the wizard the user was one click
        // from finishing, and "Back" landed on the gallery. The notices open over the Confirm
        // screen instead, so nothing the user answered is lost.
        var cut = Render<DisclaimerPanel>(p => p.Add(x => x.Module, new DisclaimerModule()));

        cut.FindAll("a[href='/licenses']").Should().BeEmpty("a navigation would discard the wizard");
        cut.FindAll(".modal-backdrop").Should().BeEmpty();
        cut.Markup.Should().Contain("open-source components");

        cut.Find("button[data-role='licences']").Click();

        cut.Find(".modal-backdrop pre.notices").TextContent.Should().Contain("THIRD-PARTY SOFTWARE NOTICES AND INFORMATION");

        cut.Find(".modal-backdrop button[data-role='close']").Click();

        cut.FindAll(".modal-backdrop").Should().BeEmpty();
    }
}
