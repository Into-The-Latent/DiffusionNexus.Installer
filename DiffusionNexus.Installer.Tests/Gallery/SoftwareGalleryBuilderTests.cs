using DiffusionNexus.Installer.Core.Gallery;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Enums;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Gallery;

/// <summary>
/// Software cards are derived from the catalog, never a fixed list of six: a card for a software
/// the catalog does not contain is a dead control, and a software it gains later must appear
/// without a code change.
/// </summary>
public class SoftwareGalleryBuilderTests
{
    private static GalleryEntry Entry(RepositoryType software, string name, bool installable = true) =>
        new(
            new InstallationConfiguration
            {
                Id = Guid.NewGuid(),
                Name = name,
                WorkflowType = WorkflowType.Image,
                Repository = new MainRepositorySettings { Type = software }
            },
            installable,
            WorkloadCapability.None,
            installable ? null : "Coming soon");

    private static IReadOnlyList<SoftwareEntry> Build(params GalleryEntry[] entries) =>
        SoftwareGalleryBuilder.Group(entries);

    [Fact]
    public void Groups_workloads_under_their_software()
    {
        var result = Build(
            Entry(RepositoryType.ComfyUI, "Krea-2-Turbo"),
            Entry(RepositoryType.ComfyUI, "Ideogram-4.0"),
            Entry(RepositoryType.Fooocus, "Fooocus"));

        result.Should().HaveCount(2);
        result.Single(s => s.Type == RepositoryType.ComfyUI).WorkloadCount.Should().Be(2);
        result.Single(s => s.Type == RepositoryType.Fooocus).WorkloadCount.Should().Be(1);
    }

    [Fact]
    public void A_software_with_one_workload_goes_straight_to_setup()
    {
        var only = Entry(RepositoryType.Fooocus, "Fooocus");

        var software = Build(only).Single();

        software.GoesStraightToSetup.Should().BeTrue();
        software.SingleWorkload.Should().BeSameAs(only);
    }

    [Fact]
    public void A_software_with_several_workloads_does_not()
    {
        var software = Build(
            Entry(RepositoryType.ComfyUI, "Krea-2-Turbo"),
            Entry(RepositoryType.ComfyUI, "Wan 2.2 - GGUF")).Single();

        software.GoesStraightToSetup.Should().BeFalse();
        software.SingleWorkload.Should().BeNull();
    }

    [Fact]
    public void Keeps_a_software_whose_only_workload_is_uninstallable()
    {
        // Hiding it makes "why is Fooocus missing?" unanswerable. The card renders, disabled,
        // and carries the reason.
        var blocked = Entry(RepositoryType.Fooocus, "Fooocus", installable: false);

        var software = Build(blocked).Single();

        software.WorkloadCount.Should().Be(1);
        software.SingleWorkload!.UnavailableReason.Should().Be("Coming soon");
    }

    [Fact]
    public void Carries_the_display_name_and_logo_for_each_software()
    {
        var software = Build(Entry(RepositoryType.AIToolkit, "AI-Toolkit")).Single();

        software.DisplayName.Should().Be("AI Toolkit");
        software.LogoPath.Should().Be("img/software/ai-toolkit.jpg");
    }

    [Fact]
    public void Orders_softwares_by_how_much_they_offer_then_by_name()
    {
        // ComfyUI carries 16 of the 21 workloads this installer can offer; it belongs first
        // rather than wherever the catalog's own order happens to place it.
        var result = Build(
            Entry(RepositoryType.Fooocus, "Fooocus"),
            Entry(RepositoryType.ComfyUI, "Krea-2-Turbo"),
            Entry(RepositoryType.ComfyUI, "Ideogram-4.0"),
            Entry(RepositoryType.A1111, "Stable Diffusion web UI"));

        result.Select(s => s.Type).Should().ContainInOrder(
            RepositoryType.ComfyUI, RepositoryType.A1111, RepositoryType.Fooocus);
    }

    [Fact]
    public void Returns_nothing_for_an_empty_catalog()
    {
        Build().Should().BeEmpty();
    }
}
