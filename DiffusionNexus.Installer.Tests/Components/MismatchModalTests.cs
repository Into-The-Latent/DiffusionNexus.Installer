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
    private static ExistingModelMismatch Mismatch(string name, string url) =>
        new(new ModelDownload { Name = name, Url = url }, $@"C:\models\{name}.bin", 1_000, 2_000, url);

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
    public async Task Cancel_dismisses_with_null()
    {
        var service = new MismatchPromptService();
        Services.AddSingleton(service);
        var cut = Render<MismatchModal>();

        var pending = service.ResolveAsync([Mismatch("a", "https://h.invalid/a.bin")]);
        cut.WaitForAssertion(() => cut.FindAll("button").Should().NotBeEmpty());

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Cancel installation").ClickAsync(new MouseEventArgs());

        (await pending).Should().BeNull();
    }
}
