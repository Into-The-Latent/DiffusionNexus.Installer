using Bunit;
using DiffusionNexus.Installer.Core.Host;
using DiffusionNexus.Installer.Electron.Components.Shared;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.Installer.SDK.Services.Installation.Utilities;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

public class MismatchModalTests : BunitContext
{
    private static ExistingModelMismatch Mismatch(
        string name, string url, ExistingModelVerdict verdict = ExistingModelVerdict.Unknown, long actual = 1_000, long expected = 2_000) =>
        new(new ModelDownload { Name = name, Url = url }, $@"C:\models\{name}.bin", actual, expected, url, verdict);

    private (IRenderedComponent<MismatchModal> Cut, MismatchPromptService Service) Open()
    {
        var service = new MismatchPromptService();
        Services.AddSingleton(service);
        return (Render<MismatchModal>(), service);
    }

    private static Task ContinueAsync(IRenderedComponent<MismatchModal> cut) =>
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Continue").ClickAsync(new MouseEventArgs());

    [Fact]
    public async Task Lists_every_file_redownload_ticked_and_returns_the_split_by_url()
    {
        var service = new MismatchPromptService();
        Services.AddSingleton(service);
        var cut = Render<MismatchModal>();
        cut.Markup.Should().NotContain("modal-card", "closed until a prompt is raised");

        var pending = service.ResolveAsync([Mismatch("a", "https://h.invalid/a.bin"), Mismatch("b", "https://h.invalid/b.bin")]);

        cut.WaitForAssertion(() => cut.FindAll("input[type=checkbox]").Should().HaveCount(2));
        cut.FindAll("input[type=checkbox]").Should().OnlyContain(i => i.HasAttribute("checked"));
        cut.Markup.Should().Contain("a.bin").And.Contain("b.bin");

        cut.FindAll("input[type=checkbox]")[1].Change(false);
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Continue").ClickAsync(new MouseEventArgs());

        var resolution = (await pending)!;
        resolution.RedownloadUrls.Should().BeEquivalentTo(["https://h.invalid/a.bin"]);
        resolution.TrustedUrls.Should().BeEquivalentTo(["https://h.invalid/b.bin"]);
    }

    [Fact]
    public async Task A_different_copy_is_offered_unticked_and_stays_if_left_alone()
    {
        var (cut, service) = Open();

        var pending = service.ResolveAsync([Mismatch("a", "https://h.invalid/a.bin", ExistingModelVerdict.CompleteDifferent)]);

        cut.WaitForAssertion(() => cut.FindAll("input[type=checkbox]").Should().ContainSingle());
        cut.Find("input[type=checkbox]").HasAttribute("checked").Should().BeFalse();
        cut.Markup.Should().Contain("should load").And.Contain("a.bin");
        cut.Markup.Should().NotContain("On disk", "two sizes never helped anyone decide");

        await ContinueAsync(cut);

        var resolution = (await pending)!;
        resolution.RedownloadUrls.Should().BeEmpty();
        resolution.TrustedUrls.Should().BeEquivalentTo(["https://h.invalid/a.bin"]);
    }

    [Fact]
    public void A_different_copy_shows_where_it_is_because_ticking_it_can_remove_it_from_there()
    {
        var (cut, service) = Open();

        _ = service.ResolveAsync([Mismatch("a", "https://h.invalid/a.bin", ExistingModelVerdict.CompleteDifferent)]);

        cut.WaitForAssertion(() => cut.FindAll("input[type=checkbox]").Should().ContainSingle());
        cut.FindAll("td").Select(c => c.TextContent.Trim()).Should().Contain(@"C:\models\a.bin");
        cut.Markup.Should().Contain("removed once");
    }

    [Fact]
    public async Task Ticking_a_different_copy_replaces_it()
    {
        var (cut, service) = Open();

        var pending = service.ResolveAsync([Mismatch("a", "https://h.invalid/a.bin", ExistingModelVerdict.CompleteDifferent)]);

        cut.WaitForAssertion(() => cut.FindAll("input[type=checkbox]").Should().ContainSingle());
        cut.Find("input[type=checkbox]").Change(true);
        await ContinueAsync(cut);

        var resolution = (await pending)!;
        resolution.RedownloadUrls.Should().BeEquivalentTo(["https://h.invalid/a.bin"]);
        resolution.TrustedUrls.Should().BeEmpty();
    }

    [Fact]
    public async Task An_incomplete_file_is_a_notice_and_not_a_question()
    {
        var (cut, service) = Open();

        var pending = service.ResolveAsync([Mismatch("a", "https://h.invalid/a.bin", ExistingModelVerdict.Truncated)]);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("a.bin"));
        cut.FindAll("input[type=checkbox]").Should().BeEmpty();
        cut.Markup.Should().Contain("can't load").And.Contain("downloaded again").And.Contain("removed once");
        cut.FindAll("td").Select(c => c.TextContent.Trim()).Should().Contain(@"C:\models\a.bin", "it can be removed from there");

        await ContinueAsync(cut);

        var resolution = (await pending)!;
        resolution.RedownloadUrls.Should().BeEquivalentTo(["https://h.invalid/a.bin"]);
        resolution.TrustedUrls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_file_with_no_verdict_keeps_the_ticked_box_and_its_sizes()
    {
        var (cut, service) = Open();

        _ = service.ResolveAsync([Mismatch("a", "https://h.invalid/a.bin", actual: 1_000_000_000, expected: 2_000_000_000)]);

        cut.WaitForAssertion(() => cut.FindAll("input[type=checkbox]").Should().ContainSingle());
        cut.Find("input[type=checkbox]").HasAttribute("checked").Should().BeTrue();
        cut.Markup.Should().Contain(DiskSpaceRequirement.FormatBytes(1_000_000_000))
            .And.Contain(DiskSpaceRequirement.FormatBytes(2_000_000_000));
    }

    [Fact]
    public void Sizes_that_print_the_same_are_shown_to_the_byte()
    {
        var (cut, service) = Open();

        _ = service.ResolveAsync([Mismatch("a", "https://h.invalid/a.bin", actual: 336_213_556, expected: 336_211_292)]);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("a.bin"));
        DiskSpaceRequirement.FormatBytes(336_213_556).Should().Be(DiskSpaceRequirement.FormatBytes(336_211_292));
        cut.Markup.Should().Contain(336_213_556L.ToString("N0")).And.Contain(336_211_292L.ToString("N0"));
    }

    [Fact]
    public async Task Each_kind_gets_its_own_section_and_the_answer_splits_by_kind()
    {
        var (cut, service) = Open();

        var pending = service.ResolveAsync(
        [
            Mismatch("cut", "https://h.invalid/cut.bin", ExistingModelVerdict.Truncated),
            Mismatch("other", "https://h.invalid/other.bin", ExistingModelVerdict.CompleteDifferent),
            Mismatch("odd", "https://h.invalid/odd.bin"),
        ]);

        cut.WaitForAssertion(() => cut.FindAll("h3").Should().HaveCount(3));
        cut.FindAll("input[type=checkbox]").Should().HaveCount(2);

        await ContinueAsync(cut);

        var resolution = (await pending)!;
        resolution.RedownloadUrls.Should().BeEquivalentTo(["https://h.invalid/cut.bin", "https://h.invalid/odd.bin"]);
        resolution.TrustedUrls.Should().BeEquivalentTo(["https://h.invalid/other.bin"]);
    }

    [Fact]
    public void Two_rows_for_the_same_file_both_show_whatever_their_kind()
    {
        var (cut, service) = Open();
        var row = Mismatch("a", "https://h.invalid/a.bin");

        _ = service.ResolveAsync([row, row]);

        cut.WaitForAssertion(() => cut.FindAll("input[type=checkbox]").Should().HaveCount(2));
    }

    [Fact]
    public void A_kind_that_has_no_files_has_no_section()
    {
        var (cut, service) = Open();

        _ = service.ResolveAsync([Mismatch("a", "https://h.invalid/a.bin", ExistingModelVerdict.CompleteDifferent)]);

        cut.WaitForAssertion(() => cut.FindAll("h3").Should().ContainSingle());
    }

    [Fact]
    public async Task One_link_is_never_both_replaced_and_kept()
    {
        const string Url = "https://h.invalid/a.bin";
        var (cut, service) = Open();

        var pending = service.ResolveAsync(
        [
            Mismatch("a", Url, ExistingModelVerdict.CompleteDifferent),
            Mismatch("b", Url, ExistingModelVerdict.Truncated),
        ]);

        cut.WaitForAssertion(() => cut.FindAll("h3").Should().HaveCount(2));
        await ContinueAsync(cut);

        var resolution = (await pending)!;
        resolution.RedownloadUrls.Should().BeEquivalentTo([Url]);
        resolution.TrustedUrls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_choice_does_not_carry_into_the_next_dialog()
    {
        const string Url = "https://h.invalid/a.bin";
        var (cut, service) = Open();

        var first = service.ResolveAsync([Mismatch("a", Url, ExistingModelVerdict.CompleteDifferent)]);
        cut.WaitForAssertion(() => cut.FindAll("input[type=checkbox]").Should().ContainSingle());
        cut.Find("input[type=checkbox]").Change(true);
        await ContinueAsync(cut);
        await first;

        _ = service.ResolveAsync([Mismatch("a", Url, ExistingModelVerdict.CompleteDifferent)]);

        cut.WaitForAssertion(() => cut.FindAll("input[type=checkbox]").Should().ContainSingle());
        cut.Find("input[type=checkbox]").HasAttribute("checked").Should().BeFalse();
    }

    [Fact]
    public async Task Cancel_dismisses_with_null()
    {
        var service = new MismatchPromptService();
        Services.AddSingleton(service);
        var cut = Render<MismatchModal>();

        var pending = service.ResolveAsync([Mismatch("a", "https://h.invalid/a.bin")]);
        cut.WaitForAssertion(() => cut.FindAll("button").Should().NotBeEmpty());

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Cancel").ClickAsync(new MouseEventArgs());

        (await pending).Should().BeNull();
    }
}
