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

    [Fact]
    public void Content_capabilities_come_from_collection_counts()
    {
        var w = new InstallationConfiguration();
        w.ModelDownloads.Add(new ModelDownload());
        w.GitRepositories.Add(new GitRepository());
        w.Workflows.Add(new ComfUIWorkflow());

        var caps = WorkloadCapabilities.Detect(w);

        caps.Should().HaveFlag(WorkloadCapability.ModelDownloads);
        caps.Should().HaveFlag(WorkloadCapability.CustomNodes);
        caps.Should().HaveFlag(WorkloadCapability.Workflows);
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
