using Bunit;
using DiffusionNexus.Installer.Electron.Components.Pages;
using DiffusionNexus.Installer.Electron.Services;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

/// <summary>
/// The third-party notices ship inside the app so the Confirm page can show them offline. The
/// text is the generated THIRD-PARTY-NOTICES.txt at the repo root, embedded at build time.
/// </summary>
public class LicensesPageTests : BunitContext
{
    [Fact]
    public void The_embedded_notices_are_the_generated_file()
    {
        var text = ThirdPartyNotices.Load();

        text.Should().StartWith("=".PadRight(80, '='));
        text.Should().Contain("THIRD-PARTY SOFTWARE NOTICES AND INFORMATION");
        text.Should().Contain("5. NODE.JS PACKAGES BUNDLED INTO THE APPLICATION");
        text.Should().EndWith("END OF THIRD-PARTY NOTICES");
    }

    [Fact]
    public void The_notices_are_read_once_not_on_every_render()
    {
        // Review finding: 87 KB re-read and re-decoded on every re-render while the dialog was open.
        ReferenceEquals(ThirdPartyNotices.Load(), ThirdPartyNotices.Load()).Should().BeTrue();
    }

    [Fact]
    public void The_page_shows_the_notices_and_a_way_back()
    {
        var cut = Render<Licenses>();

        cut.Find("pre.notices").TextContent.Should().Contain("THIRD-PARTY SOFTWARE NOTICES AND INFORMATION");
        cut.Find("a[href='/']").Should().NotBeNull();
    }
}
