using Bunit;
using DiffusionNexus.Installer.Core.Gallery;
using DiffusionNexus.Installer.Electron.Components.Shared;
using DiffusionNexus.Installer.SDK.Shared.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

/// <summary>
/// The "Join the Community" footer. The list comes from an operator-edited Gist through the
/// SDK (issue #6); what the footer owns is painting SOMETHING correct before that fetch lands
/// and never waiting for it.
/// </summary>
public class CommunityLinksTests : BunitContext
{
    /// <summary>The fetch, held open until a test decides how it ends.</summary>
    private readonly TaskCompletionSource<CommunityLinksResult> _fetch = new();

    public CommunityLinksTests()
    {
        var service = new Mock<ICommunityLinksService>();
        service.Setup(s => s.GetLinksAsync(It.IsAny<CancellationToken>())).Returns(_fetch.Task);
        Services.AddSingleton(new CommunityLinksCache(service.Object));
    }

    private static readonly string[] DefaultNames = ["YouTube", "Patreon", "Website", "Civitai", "Newsletter", "Linktree"];

    [Fact]
    public void Paints_the_compiled_in_defaults_without_waiting_for_the_fetch()
    {
        // _fetch is still pending here: a slow or unreachable source never delays the window.
        var cut = Render<CommunityLinks>();

        cut.Find(".community h4").TextContent.Should().Contain("Join the Community");
        cut.FindAll(".community-link span").Select(s => s.TextContent).Should().Equal(DefaultNames);
        cut.FindAll(".community-link").Select(a => a.GetAttribute("href"))
            .Should().Equal(CommunityLink.Defaults.Select(l => l.Url));
    }

    [Fact]
    public void Swaps_in_the_remote_list_when_the_fetch_lands()
    {
        var cut = Render<CommunityLinks>();

        _fetch.SetResult(new CommunityLinksResult(
            [new("Forum", "https://forum.example", "globe"), new("Discord", "https://discord.gg/x", "discord")],
            IsFallback: false));

        cut.WaitForAssertion(() =>
            cut.FindAll(".community-link span").Select(s => s.TextContent).Should().Equal("Forum", "Discord"));
    }

    [Fact]
    public void Keeps_the_defaults_when_the_source_falls_back()
    {
        var cut = Render<CommunityLinks>();

        _fetch.SetResult(CommunityLinksResult.Fallback("offline"));

        cut.WaitForAssertion(() =>
            cut.FindAll(".community-link span").Select(s => s.TextContent).Should().Equal(DefaultNames));
    }

    [Fact]
    public void Every_default_chip_carries_its_own_glyph()
    {
        var cut = Render<CommunityLinks>();

        cut.FindAll(".community-link svg.community-icon").Select(s => s.GetAttribute("data-icon"))
            .Should().Equal("youtube", "patreon", "globe", "civitai", "mail", "linktree");
    }

    [Fact]
    public void A_remote_row_with_an_unknown_icon_gets_the_neutral_glyph_not_a_blank_chip()
    {
        var cut = Render<CommunityLinks>();

        _fetch.SetResult(new CommunityLinksResult([new("Forum", "https://forum.example", "discourse")], IsFallback: false));

        cut.WaitForAssertion(() =>
        {
            var chip = cut.Find(".community-link");
            chip.QuerySelector("svg")!.GetAttribute("data-icon").Should().Be(CommunityLinkIcon.Fallback);
            chip.TextContent.Should().Be("Forum");
        });
    }

    [Fact]
    public void Opens_links_outside_the_app_window()
    {
        var cut = Render<CommunityLinks>();

        // Outside Electron these are ordinary anchors, and they MUST carry target=_blank:
        // a same-window navigation strands the user in an installer that has become a browser
        // with no address bar and no way back.
        foreach (var link in cut.FindAll(".community-link"))
        {
            link.GetAttribute("target").Should().Be("_blank");
            link.GetAttribute("rel").Should().Contain("noopener");
        }
    }

    [Fact]
    public async Task Stops_re_rendering_once_disposed()
    {
        // Two footers on one cache, as two screens would be. Dispose one, land the fetch: only
        // the live one re-renders. A leaked handler would re-render the dead one too.
        var live = Render<CommunityLinks>();
        var dead = Render<CommunityLinks>();
        var liveBefore = live.RenderCount;
        var deadBefore = dead.RenderCount;

        dead.Instance.Dispose();
        _fetch.SetResult(new CommunityLinksResult([new("Forum", "https://forum.example", "globe")], IsFallback: false));
        await Services.GetRequiredService<CommunityLinksCache>().LoadAsync();

        live.WaitForAssertion(() => live.RenderCount.Should().BeGreaterThan(liveBefore));
        dead.RenderCount.Should().Be(deadBefore);
    }
}
