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
    private static GalleryEntry Entry(RepositoryType software, string name, bool installable = true, bool legacy = false) =>
        new(
            new InstallationConfiguration
            {
                Id = Guid.NewGuid(),
                Name = name,
                WorkflowType = WorkflowType.Image,
                IsLegacy = legacy,
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
    public void Carries_the_display_name_for_each_software()
    {
        // Name only. The artwork is resolved by the component from Type, out of the Electron
        // project that actually serves the file -- see Services/SoftwareLogosTests.cs.
        var software = Build(Entry(RepositoryType.AIToolkit, "AI-Toolkit")).Single();

        software.DisplayName.Should().Be("AI Toolkit");
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

    [Fact]
    public void Legacy_workloads_do_not_count_toward_the_card()
    {
        // They ride along for the workload screen's "Show legacy workloads" switch, which starts
        // off: a card reading "3 workloads" above a screen that shows two would be wrong.
        var software = Build(
            Entry(RepositoryType.ComfyUI, "Krea-2-Turbo"),
            Entry(RepositoryType.ComfyUI, "Wan 2.2 - GGUF"),
            Entry(RepositoryType.ComfyUI, "LTX2 - GGUF - Legacy", legacy: true)).Single();

        software.WorkloadCount.Should().Be(2);
        software.CurrentWorkloads.Select(e => e.Workload.Name)
            .Should().Equal("Krea-2-Turbo", "Wan 2.2 - GGUF");
        software.Workloads.Should().HaveCount(3, "the workload screen's switch still needs the legacy one");
    }

    [Fact]
    public void A_legacy_sibling_does_not_stop_a_lone_current_workload_going_straight_to_setup()
    {
        var only = Entry(RepositoryType.Fooocus, "Fooocus");

        var software = Build(only, Entry(RepositoryType.Fooocus, "Fooocus 1.x", legacy: true)).Single();

        software.GoesStraightToSetup.Should().BeTrue();
        software.SingleWorkload.Should().BeSameAs(only);
    }

    [Fact]
    public void A_software_whose_workloads_are_all_legacy_gets_no_card()
    {
        // Its card would read "0 workloads" and open a screen that stays empty until the user
        // finds a switch. No card, as before legacy packs were offered at all.
        var result = Build(
            Entry(RepositoryType.ComfyUI, "Krea-2-Turbo"),
            Entry(RepositoryType.Forge, "Forge 1.x", legacy: true));

        result.Select(s => s.Type).Should().Equal(RepositoryType.ComfyUI);
    }

    [Fact]
    public void Legacy_workloads_do_not_lift_a_software_up_the_order()
    {
        var result = Build(
            Entry(RepositoryType.ComfyUI, "Krea-2-Turbo"),
            Entry(RepositoryType.ComfyUI, "Old pack 1", legacy: true),
            Entry(RepositoryType.ComfyUI, "Old pack 2", legacy: true),
            Entry(RepositoryType.A1111, "Stable Diffusion web UI"),
            Entry(RepositoryType.A1111, "Stable Diffusion web UI dev"));

        result.Select(s => s.Type).Should().Equal(RepositoryType.A1111, RepositoryType.ComfyUI);
    }
}
