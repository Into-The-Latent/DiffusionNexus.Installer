using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Wizard;

public class WorkloadCapabilitiesTests
{
    [Theory]
    [InlineData(RepositoryType.ComfyUI, true)]
    [InlineData(RepositoryType.AIToolkit, true)]
    [InlineData(RepositoryType.A1111, false)]
    [InlineData(RepositoryType.Forge, false)]
    [InlineData(RepositoryType.Fooocus, false)]
    [InlineData(RepositoryType.AceStep, false)]
    public void ComfyFolders_applies_to_comfyui_and_aitoolkit_only(RepositoryType type, bool expected)
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = type;

        var has = WorkloadCapabilities.Detect(w).HasFlag(WorkloadCapability.ComfyFolders);

        has.Should().Be(expected);
    }

    [Fact]
    public void Vram_is_detected_from_a_non_empty_profile_string()
    {
        var w = new InstallationConfiguration();
        w.Vram.VramProfiles = "8,12,16,24,32";

        WorkloadCapabilities.Detect(w).Should().HaveFlag(WorkloadCapability.VramProfile);
    }

    [Fact]
    public void Vram_is_not_detected_from_whitespace()
    {
        var w = new InstallationConfiguration();
        w.Vram.VramProfiles = "   ";

        WorkloadCapabilities.Detect(w).Should().NotHaveFlag(WorkloadCapability.VramProfile);
    }

    // Split from a single Content_capabilities_come_from_collection_counts test that populated all
    // three collections together, so a copy-paste swap between the three .Count > 0 checks inside
    // Detect (e.g. GitRepositories.Count > 0 setting Workflows instead of CustomNodes) would still
    // have passed. Each fact here populates exactly one collection and checks the other two
    // capabilities stay unset, which a swap would break.

    [Fact]
    public void ModelDownloads_capability_comes_from_the_ModelDownloads_collection()
    {
        var w = new InstallationConfiguration();
        w.ModelDownloads.Add(new ModelDownload());

        var caps = WorkloadCapabilities.Detect(w);

        caps.Should().HaveFlag(WorkloadCapability.ModelDownloads);
        caps.Should().NotHaveFlag(WorkloadCapability.CustomNodes);
        caps.Should().NotHaveFlag(WorkloadCapability.Workflows);
    }

    [Fact]
    public void CustomNodes_capability_comes_from_the_GitRepositories_collection()
    {
        var w = new InstallationConfiguration();
        w.GitRepositories.Add(new GitRepository());

        var caps = WorkloadCapabilities.Detect(w);

        caps.Should().HaveFlag(WorkloadCapability.CustomNodes);
        caps.Should().NotHaveFlag(WorkloadCapability.ModelDownloads);
        caps.Should().NotHaveFlag(WorkloadCapability.Workflows);
    }

    [Fact]
    public void Workflows_capability_comes_from_the_Workflows_collection()
    {
        var w = new InstallationConfiguration();
        w.Workflows.Add(new ComfUIWorkflow());

        var caps = WorkloadCapabilities.Detect(w);

        caps.Should().HaveFlag(WorkloadCapability.Workflows);
        caps.Should().NotHaveFlag(WorkloadCapability.ModelDownloads);
        caps.Should().NotHaveFlag(WorkloadCapability.CustomNodes);
    }

    [Fact]
    public void Accelerators_are_detected_from_either_toggle()
    {
        var triton = new InstallationConfiguration();
        triton.Python.InstallTriton = true;

        var sage = new InstallationConfiguration();
        sage.Python.InstallSageAttention = true;

        WorkloadCapabilities.Detect(triton).Should().HaveFlag(WorkloadCapability.Accelerators);
        WorkloadCapabilities.Detect(sage).Should().HaveFlag(WorkloadCapability.Accelerators);
    }

    [Fact]
    public void A_thin_non_comfy_workload_reports_no_capabilities()
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = RepositoryType.Fooocus;

        WorkloadCapabilities.Detect(w).Should().Be(WorkloadCapability.None);
    }
}
