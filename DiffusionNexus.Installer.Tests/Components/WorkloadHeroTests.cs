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
}
