using DiffusionNexus.Installer.Core.Gallery;
using DiffusionNexus.Installer.SDK.Shared.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Gallery;

/// <summary>
/// The once-per-process copy of the community links every screen's footer reads.
/// </summary>
public class CommunityLinksCacheTests
{
    private static readonly CommunityLink Remote = new("Forum", "https://forum.example", "discourse");

    [Fact]
    public void Starts_with_the_compiled_in_defaults_before_anything_is_loaded()
    {
        var cache = new CommunityLinksCache(Mock.Of<ICommunityLinksService>());

        cache.Links.Should().Equal(CommunityLink.Defaults);
        cache.Result.Should().BeNull();
    }

    [Fact]
    public async Task Swaps_in_the_remote_links_and_raises_Changed_when_the_fetch_lands()
    {
        var service = new Mock<ICommunityLinksService>();
        service.Setup(s => s.GetLinksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommunityLinksResult([Remote], IsFallback: false));
        var cache = new CommunityLinksCache(service.Object);
        var changed = 0;
        cache.Changed += () => changed++;

        await cache.LoadAsync();

        cache.Links.Should().Equal(Remote);
        cache.Result!.IsFallback.Should().BeFalse();
        changed.Should().Be(1);
    }

    [Fact]
    public async Task Fetches_once_no_matter_how_many_screens_ask()
    {
        var gate = new TaskCompletionSource<CommunityLinksResult>();
        var service = new Mock<ICommunityLinksService>();
        service.Setup(s => s.GetLinksAsync(It.IsAny<CancellationToken>())).Returns(gate.Task);
        var cache = new CommunityLinksCache(service.Object);

        var first = cache.LoadAsync();
        var second = cache.LoadAsync();
        gate.SetResult(new CommunityLinksResult([Remote], IsFallback: false));
        await Task.WhenAll(first, second);
        await cache.LoadAsync();

        second.Should().BeSameAs(first);
        service.Verify(s => s.GetLinksAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Keeps_the_defaults_when_the_source_reports_a_fallback()
    {
        var service = new Mock<ICommunityLinksService>();
        service.Setup(s => s.GetLinksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommunityLinksResult.Fallback("dns"));
        var cache = new CommunityLinksCache(service.Object);

        await cache.LoadAsync();

        cache.Links.Should().Equal(CommunityLink.Defaults);
        cache.Result!.IsFallback.Should().BeTrue();
        cache.Result.ErrorMessage.Should().Be("dns");
    }

    [Fact]
    public async Task A_source_that_throws_leaves_the_defaults_and_never_faults_the_task()
    {
        // The SDK service contracts not to throw, but the footer must not be the thing that
        // takes a screen down if that contract is ever broken.
        var service = new Mock<ICommunityLinksService>();
        service.Setup(s => s.GetLinksAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var cache = new CommunityLinksCache(service.Object);

        var act = () => cache.LoadAsync();

        await act.Should().NotThrowAsync();
        cache.Links.Should().Equal(CommunityLink.Defaults);
        cache.Result!.IsFallback.Should().BeTrue();
        cache.Result.ErrorMessage.Should().Contain("boom");
    }

    [Fact]
    public async Task A_throwing_subscriber_neither_faults_the_load_nor_starves_the_next_one()
    {
        var service = new Mock<ICommunityLinksService>();
        service.Setup(s => s.GetLinksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommunityLinksResult([Remote], IsFallback: false));
        var cache = new CommunityLinksCache(service.Object);
        var laterSubscriberRan = false;
        cache.Changed += () => throw new InvalidOperationException("bad footer");
        cache.Changed += () => laterSubscriberRan = true;

        var act = () => cache.LoadAsync();

        await act.Should().NotThrowAsync();
        cache.LoadAsync().IsFaulted.Should().BeFalse("the Lazy<Task> is cached, so a fault would be permanent");
        laterSubscriberRan.Should().BeTrue();
        cache.Links.Should().Equal(Remote);
    }

    [Fact]
    public async Task Links_and_Result_are_published_as_one_snapshot()
    {
        var service = new Mock<ICommunityLinksService>();
        var result = new CommunityLinksResult([Remote], IsFallback: false);
        service.Setup(s => s.GetLinksAsync(It.IsAny<CancellationToken>())).ReturnsAsync(result);
        var cache = new CommunityLinksCache(service.Object);

        await cache.LoadAsync();

        cache.Result.Should().BeSameAs(result);
        cache.Links.Should().BeSameAs(result.Links);
    }
}
