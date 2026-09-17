using DiffusionNexus.Installer.Core.Announcements;
using DiffusionNexus.Installer.SDK.Shared.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Announcements;

/// <summary>
/// The once-per-process copy of the operator announcements every screen's banner reads (issue #12).
/// </summary>
public sealed class ServerMessageCacheTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dn-installer-tests", Guid.NewGuid().ToString("N"));
    private readonly string _dismissedPath;

    public ServerMessageCacheTests() => _dismissedPath = Path.Combine(_folder, "dismissed_messages.json");

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static ServerMessage Message(string id, bool dismissible = true) =>
        new() { Id = id, Message = $"body of {id}", Dismissible = dismissible };

    private ServerMessageCache CacheOver(Mock<IServerMessageService> service) =>
        new(service.Object, new DismissedMessageStore(_dismissedPath));

    private static Mock<IServerMessageService> ServiceReturning(params ServerMessage[] messages)
    {
        var service = new Mock<IServerMessageService>();
        service.Setup(s => s.GetMessagesAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServerMessageResult(messages));
        return service;
    }

    [Fact]
    public void Starts_empty_before_anything_is_loaded()
    {
        var cache = CacheOver(new Mock<IServerMessageService>());

        cache.Messages.Should().BeEmpty();
        cache.Result.Should().BeNull();
    }

    [Fact]
    public async Task Publishes_the_messages_and_raises_Changed_when_the_fetch_lands()
    {
        var cache = CacheOver(ServiceReturning(Message("a"), Message("b")));
        var changed = 0;
        cache.Changed += () => changed++;

        await cache.LoadAsync();

        cache.Messages.Select(m => m.Id).Should().Equal("a", "b");
        cache.Result!.ErrorMessage.Should().BeNull();
        changed.Should().Be(1);
    }

    [Fact]
    public async Task Asks_as_the_installer_and_hands_over_what_was_already_dismissed()
    {
        // The Gist's rows target "installer"; any other id and the operator's row never matches.
        new DismissedMessageStore(_dismissedPath).Add("old-news");
        var service = ServiceReturning();
        var cache = CacheOver(service);

        await cache.LoadAsync();

        service.Verify(s => s.GetMessagesAsync(
            "installer",
            It.Is<IReadOnlySet<string>?>(ids => ids != null && ids.Contains("old-news")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Fetches_once_no_matter_how_many_screens_ask()
    {
        var gate = new TaskCompletionSource<ServerMessageResult>();
        var service = new Mock<IServerMessageService>();
        service.Setup(s => s.GetMessagesAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>?>(), It.IsAny<CancellationToken>()))
            .Returns(gate.Task);
        var cache = CacheOver(service);

        var first = cache.LoadAsync();
        var second = cache.LoadAsync();
        gate.SetResult(new ServerMessageResult([Message("a")]));
        await Task.WhenAll(first, second);
        await cache.LoadAsync();

        second.Should().BeSameAs(first);
        service.Verify(s => s.GetMessagesAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Stays_empty_when_the_source_reports_an_error()
    {
        var service = new Mock<IServerMessageService>();
        service.Setup(s => s.GetMessagesAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServerMessageResult([], "dns"));
        var cache = CacheOver(service);

        await cache.LoadAsync();

        cache.Messages.Should().BeEmpty();
        cache.Result!.ErrorMessage.Should().Be("dns");
    }

    [Fact]
    public async Task A_source_that_throws_leaves_it_empty_and_never_faults_the_task()
    {
        // The SDK service contracts not to throw, but a banner must not be the thing that takes
        // a screen down if that contract is ever broken.
        var service = new Mock<IServerMessageService>();
        service.Setup(s => s.GetMessagesAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var cache = CacheOver(service);

        var act = () => cache.LoadAsync();

        await act.Should().NotThrowAsync();
        cache.Messages.Should().BeEmpty();
        cache.Result!.ErrorMessage.Should().Contain("boom");
    }

    [Fact]
    public async Task A_throwing_subscriber_neither_faults_the_load_nor_starves_the_next_one()
    {
        var cache = CacheOver(ServiceReturning(Message("a")));
        var laterSubscriberRan = false;
        cache.Changed += () => throw new InvalidOperationException("bad banner");
        cache.Changed += () => laterSubscriberRan = true;

        var act = () => cache.LoadAsync();

        await act.Should().NotThrowAsync();
        cache.LoadAsync().IsFaulted.Should().BeFalse("the Lazy<Task> is cached, so a fault would be permanent");
        laterSubscriberRan.Should().BeTrue();
    }

    [Fact]
    public async Task Dismissing_hides_the_message_remembers_it_and_raises_Changed()
    {
        var cache = CacheOver(ServiceReturning(Message("a"), Message("b")));
        await cache.LoadAsync();
        var changed = 0;
        cache.Changed += () => changed++;

        cache.Dismiss("a");

        cache.Messages.Select(m => m.Id).Should().Equal("b");
        changed.Should().Be(1);
        new DismissedMessageStore(_dismissedPath).Load().Should().Contain("a", "the next launch must not show it again");
    }

    [Fact]
    public async Task A_message_the_operator_made_mandatory_cannot_be_dismissed()
    {
        // The banner renders no dismiss button for these; this is the cache refusing anyway, so a
        // security notice cannot be silenced by a markup slip.
        var cache = CacheOver(ServiceReturning(Message("mandatory", dismissible: false)));
        await cache.LoadAsync();

        cache.Dismiss("mandatory");

        cache.Messages.Select(m => m.Id).Should().Equal("mandatory");
        new DismissedMessageStore(_dismissedPath).Load().Should().BeEmpty();
    }

    [Fact]
    public async Task A_dismissal_that_cannot_be_saved_still_hides_the_message_for_this_run()
    {
        // A directory where the file should be: every write fails. The user clicked the X; a
        // banner that refuses to leave because of a disk problem it never mentions is worse than
        // one that comes back next launch.
        Directory.CreateDirectory(_dismissedPath);
        var cache = CacheOver(ServiceReturning(Message("a")));
        await cache.LoadAsync();

        var act = () => cache.Dismiss("a");

        act.Should().NotThrow();
        cache.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task A_repeated_id_keeps_only_its_first_row()
    {
        // A row copied in the Gist with its id left unchanged. The banner keys its rows on the
        // id, and Blazor throws on a duplicate key the first time the list shifts -- so the next
        // dismissal took the whole screen down (PR #22 review).
        var second = new ServerMessage { Id = "dup", Message = "the forgotten copy" };
        var cache = CacheOver(ServiceReturning(Message("a"), Message("dup"), second, Message("b")));

        await cache.LoadAsync();

        cache.Messages.Select(m => m.Id).Should().Equal("a", "dup", "b");
        cache.Messages[1].Message.Should().Be("body of dup", "the first row wins, as the document orders them");
    }

    [Fact]
    public async Task Dismissing_cannot_take_a_mandatory_row_that_shares_the_id()
    {
        // The mandatory row is first, so it is the one that survives the de-duplication -- and
        // the dismissible copy after it must not become a way to silence it.
        var cache = CacheOver(ServiceReturning(Message("dup", dismissible: false), Message("dup")));
        await cache.LoadAsync();

        cache.Dismiss("dup");

        cache.Messages.Should().ContainSingle().Which.Dismissible.Should().BeFalse();
    }

    [Fact]
    public async Task Dismissing_an_unknown_id_changes_nothing()
    {
        var cache = CacheOver(ServiceReturning(Message("a")));
        await cache.LoadAsync();
        var changed = 0;
        cache.Changed += () => changed++;

        cache.Dismiss("never-existed");

        cache.Messages.Select(m => m.Id).Should().Equal("a");
        changed.Should().Be(0);
    }
}
