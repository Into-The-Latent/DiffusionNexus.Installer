using DiffusionNexus.Installer.Core.Catalog;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Enums;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Catalog;

public class WorkloadVisibilityTests
{
    private static InstallationConfiguration Workload(
        WorkloadTargetType target = WorkloadTargetType.Installer,
        bool isRelease = true,
        bool isLegacy = false) =>
        new() { Name = "A pack", WorkloadTarget = target, IsReleaseConfig = isRelease, IsLegacy = isLegacy };

    [Fact]
    public void A_released_non_legacy_installer_workload_is_offered()
    {
        WorkloadVisibility.ReleaseOnly.IsOffered(Workload()).Should().BeTrue();
    }

    [Fact]
    public void A_core_targeted_workload_is_never_offered()
    {
        var core = Workload(target: WorkloadTargetType.DiffusionNexusCore);

        WorkloadVisibility.ReleaseOnly.IsOffered(core).Should().BeFalse();
        WorkloadVisibility.IncludingNonRelease.IsOffered(core).Should().BeFalse();
    }

    [Fact]
    public void A_released_legacy_workload_is_offered()
    {
        // Offered so the workload screen's "Show legacy workloads" switch has something to reveal
        // and the install page can open what it reveals. Keeping it out of the DEFAULT view is the
        // screen's job (SoftwareEntry, SoftwareWorkloads.razor): dropped here, the switch would
        // have nothing to show and a legacy card's Install would bounce back to the welcome screen.
        WorkloadVisibility.ReleaseOnly.IsOffered(Workload(isLegacy: true)).Should().BeTrue();
    }

    [Fact]
    public void Being_legacy_does_not_get_a_draft_past_the_release_gate()
    {
        // Blanck-ComfyUI, Config535 and Qwen-Image-Edit-2511 - Deprecated are legacy AND
        // unreleased. The switch reveals superseded packs, not drafts.
        var legacyDraft = Workload(isRelease: false, isLegacy: true);

        WorkloadVisibility.ReleaseOnly.IsOffered(legacyDraft).Should().BeFalse();
        WorkloadVisibility.IncludingNonRelease.IsOffered(legacyDraft).Should().BeTrue();
    }

    [Fact]
    public void A_non_release_workload_is_hidden_from_users()
    {
        WorkloadVisibility.ReleaseOnly.IsOffered(Workload(isRelease: false)).Should().BeFalse();
    }

    [Fact]
    public void A_non_release_workload_is_offered_to_developers()
    {
        // isReleaseConfig=false means "catalog entry authored for testing" -- "ComfyUI Llama Cpp
        // test" is one. Hiding it in every build would make it unreachable from the app entirely.
        WorkloadVisibility.IncludingNonRelease.IsOffered(Workload(isRelease: false)).Should().BeTrue();
    }

    [Fact]
    public void The_default_follows_the_build_configuration()
    {
#if DEBUG
        WorkloadVisibility.Default.Should().BeSameAs(WorkloadVisibility.IncludingNonRelease);
#else
        WorkloadVisibility.Default.Should().BeSameAs(WorkloadVisibility.ReleaseOnly);
#endif
    }
}
