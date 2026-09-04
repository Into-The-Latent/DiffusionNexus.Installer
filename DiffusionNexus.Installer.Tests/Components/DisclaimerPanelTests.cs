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

    [Fact]
    public void The_dialog_always_offers_a_way_out_and_a_scrollable_body()
    {
        // 1,800 lines of notices: the body must be the scrolling part, the card must never grow
        // past the window (which pushed Close off-screen), and a click outside must close it.
        var cut = Render<DisclaimerPanel>(p => p.Add(x => x.Module, new DisclaimerModule()));
        cut.Find("button[data-role='licences']").Click();

        cut.Find(".modal-backdrop .modal-card").ClassList.Should().Contain("modal-card-scroll");
        cut.Find(".modal-backdrop .modal-head button[data-role='close-top']").Should().NotBeNull("a close control must be visible without scrolling");

        cut.Find(".modal-backdrop .modal-card").Click();
        cut.FindAll(".modal-backdrop").Should().ContainSingle("a click inside the card is not a dismissal");

        cut.Find(".modal-backdrop").Click();
        cut.FindAll(".modal-backdrop").Should().BeEmpty("a click on the dark backdrop closes it");
    }
}
