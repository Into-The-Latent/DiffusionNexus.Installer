using Bunit;
using DiffusionNexus.Installer.Core.Gallery;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.Electron.Components.Shared;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Enums;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

/// <summary>
/// The card the workload screen renders for each of a software's workloads. Not shared with the
/// welcome screen: Welcome.razor inlines its own software-card markup and never uses this.
/// </summary>
public class WorkloadCardTests : BunitContext
{
    private static readonly Guid KreaId = Guid.Parse("e79c079a-2fd7-4fe7-8086-23731092555d");

    private static GalleryEntry Entry(
        bool installable = true,
        string? thumbnailPath = @"C:\catalog\workloads\krea\thumbnail.webp",
        WorkflowType type = WorkflowType.Image,
        string? description = "Text to image plus upscale, tuned for Krea 2 Turbo.",
        string vramProfiles = "") =>
        new(
            new InstallationConfiguration
            {
                Id = KreaId,
                Name = "Krea-2-Turbo",
                WorkflowType = type,
                ThumbnailPath = thumbnailPath,
                Description = description ?? string.Empty,
                ConfigurationVersion = 2,
                ConfigurationSubVersion = 3,
                Vram = new VramSettings { VramProfiles = vramProfiles },
                Repository = new MainRepositorySettings { Type = RepositoryType.ComfyUI }
            },
            installable,
            WorkloadCapability.None,
            installable ? null : "Coming soon — needs VramProfile");

    [Fact]
    public void Shows_the_thumbnail_through_the_endpoint_not_the_disk_path()
    {
        var cut = Render<WorkloadCard>(p => p.Add(x => x.Entry, Entry()));

        var img = cut.Find(".workload-card-art img");
        img.GetAttribute("src").Should().Be($"thumbnail/{KreaId}");
        img.GetAttribute("src").Should().NotContain(@"C:\", "a disk path is unloadable in a browser");
        img.GetAttribute("alt").Should().Be("Krea-2-Turbo");
    }

    [Fact]
    public void Falls_back_to_a_neutral_tile_when_there_is_no_thumbnail()
    {
        var cut = Render<WorkloadCard>(p => p.Add(x => x.Entry, Entry(thumbnailPath: null)));

        cut.FindAll(".workload-card-art img").Should().BeEmpty();
        cut.Find(".workload-card-art-fallback").TextContent.Should().Contain("Krea-2-Turbo");
    }

    [Fact]
    public void Names_the_workload_and_badges_its_type()
    {
        var cut = Render<WorkloadCard>(p => p.Add(x => x.Entry, Entry(type: WorkflowType.Video)));

        // The name carries its version inline, so the heading reads "Krea-2-Turbo v2.3".
        cut.Find(".workload-card-name").TextContent.Trim().Should().StartWith("Krea-2-Turbo");
        cut.Find(".workload-card-badge").TextContent.Should().Be("Video");
    }

    [Fact]
    public void Offers_install_when_the_workload_is_installable()
    {
        GalleryEntry? installed = null;
        var entry = Entry();
        var cut = Render<WorkloadCard>(p => p
            .Add(x => x.Entry, entry)
            .Add(x => x.OnInstall, e => installed = e));

        cut.Find(".workload-card-install").Click();

        installed.Should().BeSameAs(entry);
    }

    [Fact]
    public void Carries_the_catalogs_description_where_the_user_can_still_reach_it()
    {
        // The deleted Gallery.razor card rendered the description as body text; this tile has no
        // room for it, and after the split nothing in the app showed it at all -- the wizard does
        // not either. The tooltip is the cheap way to keep it reachable before committing to an
        // install.
        var cut = Render<WorkloadCard>(p => p.Add(x => x.Entry, Entry()));

        cut.Find(".workload-card").GetAttribute("title")
            .Should().Be("Text to image plus upscale, tuned for Krea 2 Turbo.");
    }

    [Fact]
    public void Has_no_tooltip_at_all_when_the_catalog_gives_no_description()
    {
        // An empty tooltip is worse than none: it opens a blank box over the card.
        var cut = Render<WorkloadCard>(p => p.Add(x => x.Entry, Entry(description: null)));

        cut.Find(".workload-card").HasAttribute("title").Should().BeFalse();
    }

    [Fact]
    public void Shows_the_reason_instead_of_an_install_button_when_it_is_not()
    {
        var cut = Render<WorkloadCard>(p => p.Add(x => x.Entry, Entry(installable: false)));

        cut.FindAll(".workload-card-install").Should().BeEmpty();
        cut.Find(".workload-card-unavailable").TextContent
            .Should().Contain("Coming soon — needs VramProfile");
    }

    [Fact]
    public void Shows_the_catalog_version_on_the_name()
    {
        var cut = Render<WorkloadCard>(p => p.Add(x => x.Entry, Entry()));

        cut.Find(".workload-card-name .workload-version").TextContent.Should().Be("v2.3");
    }

    [Fact]
    public void Shows_the_vram_range_of_a_workload_that_declares_profiles()
    {
        var cut = Render<WorkloadCard>(p => p.Add(x => x.Entry, Entry(vramProfiles: "24,32")));

        // A chip in the same row as the type badge, not a loose line of text under it.
        cut.Find(".workload-card-tags .vram-chip").TextContent.Should().Be("24–32 GB VRAM");
    }

    [Fact]
    public void Says_nothing_about_vram_when_the_workload_declares_no_profiles()
    {
        var cut = Render<WorkloadCard>(p => p.Add(x => x.Entry, Entry()));

        cut.FindAll(".vram-chip").Should().BeEmpty();
    }
}
