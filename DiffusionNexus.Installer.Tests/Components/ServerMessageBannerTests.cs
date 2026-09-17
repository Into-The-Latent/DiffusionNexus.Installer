using Bunit;
using DiffusionNexus.Installer.Core.Announcements;
using DiffusionNexus.Installer.Electron.Components.Shared;
using DiffusionNexus.Installer.SDK.Shared.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

/// <summary>
/// The operator-announcements banner (issue #12). The messages come from the shared Gist through
/// the SDK; what the banner owns is staying out of the way until there is something to say, and
/// honouring what the operator marked mandatory.
/// </summary>
public class ServerMessageBannerTests : BunitContext
{
    /// <summary>The fetch, held open until a test decides how it ends.</summary>
    private readonly TaskCompletionSource<ServerMessageResult> _fetch = new();
    private readonly string _dismissedPath = OfflineServerMessages.ScratchDismissalFile();

    public ServerMessageBannerTests()
    {
        var service = new Mock<IServerMessageService>();
        service.Setup(s => s.GetMessagesAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>?>(), It.IsAny<CancellationToken>()))
            .Returns(_fetch.Task);
        Services.AddSingleton(new ServerMessageCache(service.Object, new DismissedMessageStore(_dismissedPath)));
    }

    private void Land(params ServerMessage[] messages) => _fetch.SetResult(new ServerMessageResult(messages));

    [Fact]
    public void Renders_nothing_at_all_while_there_is_nothing_to_say()
    {
        // _fetch is still pending. Not even an empty wrapper: an empty flex child with padding
        // would be a stripe across every screen of every install.
        var cut = Render<ServerMessageBanner>();

        cut.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Shows_the_message_when_the_fetch_lands()
    {
        var cut = Render<ServerMessageBanner>();

        Land(new ServerMessage { Id = "fix", Title = "Fixed build available", Message = "Download it from Patreon." });

        cut.WaitForAssertion(() =>
        {
            cut.Find(".server-message-title").TextContent.Should().Be("Fixed build available");
            cut.Find(".server-message-text").TextContent.Should().Be("Download it from Patreon.");
        });
    }

    [Fact]
    public void A_message_without_a_title_renders_no_empty_heading()
    {
        var cut = Render<ServerMessageBanner>();

        Land(new ServerMessage { Id = "a", Message = "body only" });

        cut.WaitForAssertion(() => cut.Find(".server-message-text").TextContent.Should().Be("body only"));
        cut.FindAll(".server-message-title").Should().BeEmpty();
    }

    [Theory]
    [InlineData(ServerMessageSeverity.Info, "server-message-info")]
    [InlineData(ServerMessageSeverity.Warning, "server-message-warning")]
    [InlineData(ServerMessageSeverity.Critical, "server-message-critical")]
    public void Carries_the_severity_as_a_class_for_the_stylesheet(ServerMessageSeverity severity, string expected)
    {
        var cut = Render<ServerMessageBanner>();

        Land(new ServerMessage { Id = "a", Message = "m", Severity = severity });

        cut.WaitForAssertion(() => cut.Find(".server-message").ClassList.Should().Contain(expected));
    }

    [Fact]
    public void Critical_messages_are_announced_as_alerts_and_the_rest_as_status()
    {
        var cut = Render<ServerMessageBanner>();

        Land(
            new ServerMessage { Id = "c", Message = "m", Severity = ServerMessageSeverity.Critical },
            new ServerMessage { Id = "i", Message = "m", Severity = ServerMessageSeverity.Info });

        cut.WaitForAssertion(() =>
            cut.FindAll(".server-message").Select(m => m.GetAttribute("role")).Should().Equal("alert", "status"));
    }

    [Fact]
    public void Shows_every_applicable_message_in_the_documents_order()
    {
        var cut = Render<ServerMessageBanner>();

        Land(new ServerMessage { Id = "1", Message = "first" }, new ServerMessage { Id = "2", Message = "second" });

        cut.WaitForAssertion(() =>
            cut.FindAll(".server-message-text").Select(t => t.TextContent).Should().Equal("first", "second"));
    }

    [Fact]
    public void The_action_button_is_a_link_that_opens_outside_the_app_window()
    {
        var cut = Render<ServerMessageBanner>();

        Land(new ServerMessage { Id = "a", Message = "m", ActionLabel = "Get it", ActionUrl = "https://example.com/fix" });

        cut.WaitForAssertion(() =>
        {
            // Outside Electron this is an ordinary anchor, and it MUST carry target=_blank: a
            // same-window navigation strands the user in an installer that has become a browser.
            var action = cut.Find(".server-message-action");
            action.TextContent.Should().Be("Get it");
            action.GetAttribute("href").Should().Be("https://example.com/fix");
            action.GetAttribute("target").Should().Be("_blank");
            action.GetAttribute("rel").Should().Contain("noopener");
        });
    }

    [Fact]
    public void An_action_url_with_no_label_still_gets_a_button_with_a_default_label()
    {
        var cut = Render<ServerMessageBanner>();

        Land(new ServerMessage { Id = "a", Message = "m", ActionUrl = "https://example.com/fix" });

        cut.WaitForAssertion(() => cut.Find(".server-message-action").TextContent.Should().Be("Learn more"));
    }

    [Fact]
    public void No_action_url_means_no_button_even_when_a_label_was_given()
    {
        // The SDK nulls a non-https ActionUrl and delivers the message anyway; the label alone
        // must not produce a button that goes nowhere.
        var cut = Render<ServerMessageBanner>();

        Land(new ServerMessage { Id = "a", Message = "m", ActionLabel = "Get it" });

        cut.WaitForAssertion(() => cut.Find(".server-message").Should().NotBeNull());
        cut.FindAll(".server-message-action").Should().BeEmpty();
    }

    [Fact]
    public void Dismissing_removes_the_message_and_remembers_it()
    {
        var cut = Render<ServerMessageBanner>();
        Land(new ServerMessage { Id = "gone", Message = "bye" }, new ServerMessage { Id = "stays", Message = "still here" });
        cut.WaitForAssertion(() => cut.FindAll(".server-message").Should().HaveCount(2));

        cut.Find(".server-message-dismiss").Click();

        cut.WaitForAssertion(() =>
            cut.FindAll(".server-message-text").Select(t => t.TextContent).Should().Equal("still here"));
        new DismissedMessageStore(_dismissedPath).Load().Should().Contain("gone");
    }

    [Fact]
    public void A_mandatory_message_has_no_dismiss_button()
    {
        var cut = Render<ServerMessageBanner>();

        Land(new ServerMessage { Id = "sec", Message = "update now", Dismissible = false });

        cut.WaitForAssertion(() => cut.Find(".server-message").Should().NotBeNull());
        cut.FindAll(".server-message-dismiss").Should().BeEmpty();
    }

    [Fact]
    public void Dismissing_beside_a_repeated_id_does_not_take_the_screen_down()
    {
        // The rows are keyed on the id. Two rows sharing one render fine at first, and then the
        // renderer throws "More than one sibling ... has the same key value" on the first diff
        // that shifts the list -- an unhandled render exception, which ends the circuit.
        var cut = Render<ServerMessageBanner>();
        Land(
            new ServerMessage { Id = "a", Message = "first" },
            new ServerMessage { Id = "dup", Message = "original" },
            new ServerMessage { Id = "dup", Message = "forgotten copy" });
        cut.WaitForAssertion(() => cut.FindAll(".server-message").Should().NotBeEmpty());

        cut.Find(".server-message-dismiss").Click();

        cut.WaitForAssertion(() =>
            cut.FindAll(".server-message-text").Select(e => e.TextContent).Should().Equal("original"));
    }

    [Fact]
    public void The_dismiss_button_says_what_it_does_to_a_screen_reader()
    {
        var cut = Render<ServerMessageBanner>();

        Land(new ServerMessage { Id = "a", Message = "m" });

        cut.WaitForAssertion(() =>
            cut.Find(".server-message-dismiss").GetAttribute("aria-label").Should().Be("Dismiss"));
    }

    [Fact]
    public async Task Stops_re_rendering_once_disposed()
    {
        // Two banners on one cache, as two screens would be. Dispose one, land the fetch: only
        // the live one re-renders. A leaked handler would re-render the dead one too.
        var live = Render<ServerMessageBanner>();
        var dead = Render<ServerMessageBanner>();
        var liveBefore = live.RenderCount;
        var deadBefore = dead.RenderCount;

        dead.Instance.Dispose();
        Land(new ServerMessage { Id = "a", Message = "m" });
        await Services.GetRequiredService<ServerMessageCache>().LoadAsync();

        live.WaitForAssertion(() => live.RenderCount.Should().BeGreaterThan(liveBefore));
        dead.RenderCount.Should().Be(deadBefore);
    }
}
