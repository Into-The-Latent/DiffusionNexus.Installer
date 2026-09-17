using Bunit;
using DiffusionNexus.Installer.Electron.Components.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

public class WorkloadHeroTests : BunitContext
{
    private static InstallationConfiguration Workload(RepositoryType software, string name, string description = "") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Description = description,
        Repository = new MainRepositorySettings { Type = software }
    };

    private static InstallationConfiguration Krea(string? thumbnailPath, string vramProfiles = "")
    {
        var krea = Workload(RepositoryType.ComfyUI, "Krea-2-Turbo");
        krea.ThumbnailPath = thumbnailPath;
        krea.ConfigurationVersion = 2;
        krea.ConfigurationSubVersion = 0;
        krea.Vram = new VramSettings { VramProfiles = vramProfiles };
        return krea;
    }

    private IRenderedComponent<WorkloadHero> RenderFromWorkloadScreen(InstallationConfiguration workload) =>
        Render<WorkloadHero>(p => p.Add(h => h.Workload, workload).Add(h => h.FromWorkloadScreen, true));

    [Fact]
    public void A_workload_picked_from_a_workload_screen_shows_its_own_thumbnail()
    {
        // The card the user clicked one navigation ago carried this picture, not the ComfyUI logo.
        var krea = Krea(@"C:\catalog\workloads\krea\thumbnail.webp");

        var cut = RenderFromWorkloadScreen(krea);

        cut.Find(".hero-art img").GetAttribute("src").Should().Be($"thumbnail/{krea.Id}");
        cut.Find(".hero-software").TextContent.Should().Be("ComfyUI");
    }

    [Fact]
    public void A_workload_without_a_thumbnail_falls_back_to_the_software_logo()
    {
        var cut = RenderFromWorkloadScreen(Krea(thumbnailPath: null));

        cut.Find(".hero-art img").GetAttribute("src").Should().Be("img/software/comfyui.jpg");
    }

    [Fact]
    public void A_workload_picked_from_a_workload_screen_shows_its_version_and_vram_range()
    {
        var cut = RenderFromWorkloadScreen(Krea(thumbnailPath: null, vramProfiles: "8,12,16,24"));

        cut.Find(".hero-name .workload-version").TextContent.Should().Be("v2.0");
        cut.Find(".hero-tags .vram-chip").TextContent.Should().Be("8–24 GB VRAM");
    }

    [Fact]
    public void A_software_reached_from_its_tile_shows_no_version()
    {
        // One workload, one tile: there is no "which revision of which pack" to answer.
        var cut = Render<WorkloadHero>(p => p.Add(h => h.Workload, Workload(RepositoryType.Fooocus, "Fooocus")));

        cut.FindAll(".workload-version").Should().BeEmpty();
    }

    [Fact]
    public void Shows_the_software_artwork_at_size()
    {
        var cut = Render<WorkloadHero>(p => p.Add(h => h.Workload, Workload(RepositoryType.Fooocus, "Fooocus")));

        cut.Find(".hero-art img").GetAttribute("src").Should().Be("img/software/fooocus.jpg");
    }

    [Fact]
    public void Names_the_workload_and_renders_its_description()
    {
        var workload = Workload(RepositoryType.AceStep, "ACE-Step-1.5", "**Music** model.\n\n- full songs");

        var cut = Render<WorkloadHero>(p => p.Add(h => h.Workload, workload));

        cut.Find(".hero-name").TextContent.Should().Be("ACE-Step-1.5");
        cut.Find(".hero-desc strong").TextContent.Should().Be("Music");
        cut.Find(".hero-desc li").TextContent.Should().Be("full songs");
    }

    [Fact]
    public void The_software_name_is_shown_when_the_workload_is_called_something_else()
    {
        // A1111's single workload is "Stable Diffusion web UI" but the tile the user clicked said
        // "Automatic 1111". Without the eyebrow nothing on this screen connects the two, which is
        // exactly the "did I click the wrong thing?" moment the hero exists to answer.
        var cut = Render<WorkloadHero>(p => p.Add(h => h.Workload, Workload(RepositoryType.A1111, "Stable Diffusion web UI")));

        cut.Find(".hero-software").TextContent.Should().Be("Automatic 1111");
    }

    [Fact]
    public void The_software_name_is_not_repeated_when_it_is_already_the_heading()
    {
        var cut = Render<WorkloadHero>(p => p.Add(h => h.Workload, Workload(RepositoryType.Fooocus, "Fooocus")));

        cut.FindAll(".hero-software").Should().BeEmpty();
    }

    [Fact]
    public void A_software_with_no_artwork_falls_back_to_its_name_rather_than_a_broken_image()
    {
        // SoftwareLogos is total over RepositoryType and returns null for one the catalog gains
        // before its artwork ships.
        var cut = Render<WorkloadHero>(p => p.Add(h => h.Workload, Workload(RepositoryType.None, "Some workload")));

        cut.FindAll(".hero-art img").Should().BeEmpty();
        cut.Find(".hero-art-fallback").TextContent.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_workload_with_no_description_still_renders_the_rest()
    {
        var cut = Render<WorkloadHero>(p => p.Add(h => h.Workload, Workload(RepositoryType.Fooocus, "Fooocus")));

        cut.Find(".hero-name").TextContent.Should().Be("Fooocus");
        cut.Find(".hero-desc").TextContent.Trim().Should().BeEmpty();
    }

    [Fact]
    public void The_compact_form_keeps_the_picture_and_the_name_and_drops_the_description()
    {
        // The Confirm stage: a reminder of the choice above the summary, not a second presentation.
        var workload = Workload(RepositoryType.AceStep, "ACE-Step-1.5", "**Music** model.");

        var cut = Render<WorkloadHero>(p => p.Add(h => h.Workload, workload).Add(h => h.Compact, true));

        cut.Find(".hero.hero-compact .hero-art img").Should().NotBeNull();
        cut.Find(".hero-name").TextContent.Should().Be("ACE-Step-1.5");
        cut.FindAll(".hero-desc").Should().BeEmpty();
    }
}
