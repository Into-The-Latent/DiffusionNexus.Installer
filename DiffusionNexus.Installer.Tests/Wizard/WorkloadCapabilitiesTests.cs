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

    [Theory]
    [InlineData("abc")]
    [InlineData(",,")]
    public void Vram_is_not_detected_from_an_unparseable_profile_string(string profiles)
    {
        // Detect and VramProfileModule.AppliesTo share VramTiers.Parse. If Detect kept its old
        // "non-blank string" rule, this workload would be gated as needing a tier (blocking) while
        // the module refused to render one -- a card that can never be installed.
        var w = new InstallationConfiguration();
        w.Vram.VramProfiles = profiles;

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

    [Fact]
    public void LlamaCpp_capability_is_detected_from_the_selected_wheel_id()
    {
        // The wheel id, not InstallLamaCpp. ComfyUIInstallationFlow schedules InstallLlamaCpp when
        // SelectedLamaCppWheelId.HasValue and LlamaCppInstallStepHandler.ShouldExecute reads the
        // same field; InstallLamaCpp is never consulted at install time.
        var w = new InstallationConfiguration();
        w.Repository.Type = RepositoryType.Fooocus; // keep ComfyFolders out of the way
        w.SelectedLamaCppWheelId = Guid.NewGuid();

        WorkloadCapabilities.Detect(w).Should().HaveFlag(WorkloadCapability.LlamaCpp);
    }

    [Fact]
    public void LlamaCpp_is_not_detected_from_the_inert_InstallLamaCpp_flag()
    {
        // The mirror of the bug: keying on this flag hid a workload from the gallery that the SDK
        // would never have scheduled the step for.
        var w = new InstallationConfiguration();
        w.Repository.Type = RepositoryType.Fooocus;
        w.InstallLamaCpp = true;

        WorkloadCapabilities.Detect(w).Should().NotHaveFlag(WorkloadCapability.LlamaCpp);
    }

    [Fact]
    public void LlamaCpp_is_not_detected_when_no_wheel_is_selected()
    {
        var w = new InstallationConfiguration();

        WorkloadCapabilities.Detect(w).Should().NotHaveFlag(WorkloadCapability.LlamaCpp);
    }

    [Fact]
    public void LlamaCpp_is_a_blocking_capability()
    {
        // Same standard as VRAM and model downloads: with a wheel id and nothing to resolve it,
        // the pipeline reaches LlamaCppInstallStepHandler and fails there on a null wheel URL.
        var w = new InstallationConfiguration();
        w.Repository.Type = RepositoryType.Fooocus;
        w.SelectedLamaCppWheelId = Guid.NewGuid();

        WorkloadCapabilities.DetectBlocking(w).Should().Be(WorkloadCapability.LlamaCpp);
    }

    [Fact]
    public void An_impossible_torch_cuda_pairing_is_an_incompatibility()
    {
        // Torch 2.8.0 ships no CUDA 13.0 wheel. InstallationPipeline refuses this before step 1,
        // so offering the card means the user fills in the wizard for a guaranteed failure.
        var w = new InstallationConfiguration();
        w.Repository.Type = RepositoryType.ComfyUI;
        w.Torch.TorchVersion = "2.8.0";
        w.Torch.CudaVersion = "13.0";

        WorkloadCapabilities.DetectIncompatibility(w).Should().Contain("13.0");
    }

    [Fact]
    public void A_supported_torch_cuda_pairing_is_not_an_incompatibility()
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = RepositoryType.ComfyUI;
        w.Torch.TorchVersion = "2.8.0";
        w.Torch.CudaVersion = "12.8";

        WorkloadCapabilities.DetectIncompatibility(w).Should().BeNull();
    }

    [Fact]
    public void A_workload_that_does_not_author_torch_settings_is_never_incompatible()
    {
        // Only ComfyUI authors its own torch settings; TorchSettingsPolicy pins the rest, and
        // InstallationPipeline skips the check for them -- so the gate must skip it too.
        var w = new InstallationConfiguration();
        w.Repository.Type = RepositoryType.AceStep;
        w.Torch.TorchVersion = "2.8.0";
        w.Torch.CudaVersion = "13.0";

        WorkloadCapabilities.DetectIncompatibility(w).Should().BeNull();
    }
}
