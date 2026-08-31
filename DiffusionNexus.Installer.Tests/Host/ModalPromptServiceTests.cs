using DiffusionNexus.Installer.Core.Host;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Host;

public class ModalPromptServiceTests
{
    [Fact]
    public async Task Answering_true_completes_the_awaited_task_with_true_and_closes_the_prompt()
    {
        var service = new ModalPromptService();

        var task = service.ConfirmAsync("Title", "Message");
        service.Answer(true);

        var answer = await task;

        answer.Should().BeTrue();
        service.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task Answering_false_completes_the_awaited_task_with_false()
    {
        var service = new ModalPromptService();

        var task = service.ConfirmAsync("Title", "Message");
        service.Answer(false);

        var answer = await task;

        answer.Should().BeFalse();
    }

    [Fact]
    public void Answer_with_nothing_pending_is_a_noop_and_does_not_throw()
    {
        var service = new ModalPromptService();

        var act = () => service.Answer(true);

        act.Should().NotThrow();
    }

    [Fact]
    public void A_second_ConfirmAsync_while_one_is_unanswered_throws_and_does_not_orphan_the_first()
    {
        var service = new ModalPromptService();

        var first = service.ConfirmAsync("First", "Message");

        Action act = () => service.ConfirmAsync("Second", "Message");

        act.Should().Throw<InvalidOperationException>();
        first.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task Cancelling_the_token_of_a_pending_prompt_completes_it_with_false()
    {
        var service = new ModalPromptService();
        using var cts = new CancellationTokenSource();

        var task = service.ConfirmAsync("Title", "Message", ct: cts.Token);
        cts.Cancel();

        var answer = await task;

        answer.Should().BeFalse();
    }

    [Fact]
    public async Task A_stale_cancellation_registration_from_an_already_answered_prompt_does_not_affect_a_later_prompt()
    {
        var service = new ModalPromptService();
        using var firstCts = new CancellationTokenSource();

        var first = service.ConfirmAsync("First", "Message", ct: firstCts.Token);
        service.Answer(true);
        await first;

        var second = service.ConfirmAsync("Second", "Message", ct: CancellationToken.None);

        // The first prompt's own token firing late must not reach into the second prompt.
        firstCts.Cancel();

        second.IsCompleted.Should().BeFalse();
        service.IsOpen.Should().BeTrue();
    }

    [Fact]
    public void Changed_fires_when_a_prompt_opens_and_when_it_is_answered()
    {
        var service = new ModalPromptService();
        var raiseCount = 0;
        service.Changed += () => raiseCount++;

        service.ConfirmAsync("Title", "Message");
        raiseCount.Should().Be(1);

        service.Answer(true);
        raiseCount.Should().Be(2);
    }
}
