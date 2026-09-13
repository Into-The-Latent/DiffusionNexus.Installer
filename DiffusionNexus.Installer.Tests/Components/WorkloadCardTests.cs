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
/// The card shared by the welcome screen (single-workload softwares) and the workload screen.
/// </summary>
public class WorkloadCardTests : BunitContext
{
    private static readonly Guid KreaId = Guid.Parse("e79c079a-2fd7-4fe7-8086-23731092555d");

    private static GalleryEntry Entry(
        bool installable = true,
        string? thumbnailPath = @"C:\catalog\workloads\krea\thumbnail.webp",
        WorkflowType type = WorkflowType.Image) =>
        new(
            new InstallationConfiguration
            {
                Id = KreaId,
                Name = "Krea-2-Turbo",
                WorkflowType = type,
                ThumbnailPath = thumbnailPath,
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

        cut.Find(".workload-card-name").TextContent.Should().Be("Krea-2-Turbo");
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
    public void Shows_the_reason_instead_of_an_install_button_when_it_is_not()
    {
        var cut = Render<WorkloadCard>(p => p.Add(x => x.Entry, Entry(installable: false)));

        cut.FindAll(".workload-card-install").Should().BeEmpty();
        cut.Find(".workload-card-unavailable").TextContent
            .Should().Contain("Coming soon — needs VramProfile");
    }
}
