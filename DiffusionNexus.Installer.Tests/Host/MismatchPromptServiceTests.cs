using DiffusionNexus.Installer.Core.Host;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.Installer.SDK.Services.Installation.Utilities;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Host;

public class MismatchPromptServiceTests
{
    private static ExistingModelMismatch Mismatch(string url) =>
        new(new ModelDownload { Name = "m", Url = url }, @"C:\m\file.bin", 10, 20, url);

    [Fact]
    public async Task Opens_with_the_mismatches_and_completes_with_the_answer()
    {
        var service = new MismatchPromptService();
        var raised = 0;
        service.Changed += () => raised++;

        var pending = service.ResolveAsync([Mismatch("https://h.invalid/a.bin")]);

        service.IsOpen.Should().BeTrue();
        service.Mismatches.Should().ContainSingle();
        raised.Should().Be(1);

        service.Answer(new MismatchResolution(["https://h.invalid/a.bin"], []));

        (await pending)!.RedownloadUrls.Should().BeEquivalentTo(["https://h.invalid/a.bin"]);
        service.IsOpen.Should().BeFalse();
        raised.Should().Be(2);
    }

    [Fact]
    public async Task Dismissal_completes_with_null()
    {
        var service = new MismatchPromptService();
        var pending = service.ResolveAsync([Mismatch("https://h.invalid/a.bin")]);

        service.Answer(null);

        (await pending).Should().BeNull();
    }

    [Fact]
    public async Task Cancellation_dismisses_the_prompt()
    {
        var service = new MismatchPromptService();
        using var cts = new CancellationTokenSource();
        var pending = service.ResolveAsync([Mismatch("https://h.invalid/a.bin")], cts.Token);

        cts.Cancel();

        (await pending).Should().BeNull();
        service.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void A_second_prompt_while_one_is_open_throws_rather_than_replacing_it()
    {
        var service = new MismatchPromptService();
        _ = service.ResolveAsync([Mismatch("https://h.invalid/a.bin")]);

        // ResolveAsync throws synchronously (before returning a Task), so an Action -- not
        // Func<Task<T>> -- is what makes the sync Should().Throw() overload resolve.
        Action act = () => service.ResolveAsync([Mismatch("https://h.invalid/b.bin")]);

        act.Should().Throw<InvalidOperationException>();
    }
}
