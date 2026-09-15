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
    public void A_legacy_workload_is_never_offered_in_either_mode()
    {
        // Legacy is not a build-configuration question: the 1.x wizard hid legacy workloads
        // unconditionally and only the classic list had the opt-in toggle. This installer has no
        // such toggle, so legacy is simply not offered.
        var legacy = Workload(isLegacy: true);

        WorkloadVisibility.ReleaseOnly.IsOffered(legacy).Should().BeFalse();
        WorkloadVisibility.IncludingNonRelease.IsOffered(legacy).Should().BeFalse();
    }

    [Fact]
    public void A_legacy_workload_is_not_rescued_by_being_a_release_config()
    {
        // The catalog's two LTX entries are exactly this shape -- isReleaseConfig true AND
        // isLegacy true -- so an OR between the two flags would leave them on the ComfyUI card.
        WorkloadVisibility.ReleaseOnly.IsOffered(Workload(isRelease: true, isLegacy: true))
            .Should().BeFalse();
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
