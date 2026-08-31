using DiffusionNexus.Installer.Core.Catalog;
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Installation;
using DiffusionNexus.Installer.SDK.Services.Hardware;
using DiffusionNexus.Installer.SDK.Services.Settings;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Modules;

/// <summary>
/// Modules are registered as DI singletons, so one instance serves every wizard run the app ever
/// does. Anything InitializeAsync does not reset is a previous workload's answer silently applied
/// to the next one — and because the affected fields also drive Validate(), the symptom is a Next
/// button already enabled before the user has looked at the panel.
/// <para>
/// These tests initialize the same instance twice, which is exactly what
/// <see cref="WizardModuleRegistry.BuildPlanAsync"/> does on a second run.
/// </para>
/// </summary>
public class ModuleStateResetTests
{
    private static WizardSelection Selection(RepositoryType type = RepositoryType.ComfyUI)
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = type;
        return new WizardSelection { Workload = w };
    }

    private static IUserSettingsRepository Settings(UserSettings? settings = null)
    {
        var repo = new Mock<IUserSettingsRepository>();
        repo.Setup(r => r.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings ?? new UserSettings());
        return repo.Object;
    }

    [Fact]
    public async Task Cpu_only_consent_does_not_carry_into_the_next_workload()
    {
        var gpu = new Mock<IGpuDetectionService>();
        gpu.Setup(g => g.DetectAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GpuDetectionResult(GpuDetectionState.NoNvidiaGpu));

        var module = new GpuPreflightModule(gpu.Object);

        await module.InitializeAsync(Selection());
        module.AcceptCpuOnly = true;
        module.Validate().IsValid.Should().BeTrue("the user accepted CPU-only for the first workload");

        // Second run, same instance.
        await module.InitializeAsync(Selection());

        module.AcceptCpuOnly.Should().BeFalse();
        module.Validate().IsValid.Should().BeFalse(
            "consent given for one workload must not silently pre-approve a CPU install of another");

        var draft = new InstallationOptionsDraft();
        module.Contribute(draft);
        draft.CpuTorch.Should().BeFalse();
    }

    [Fact]
    public async Task A_custom_shortcut_name_does_not_carry_into_the_next_workload()
    {
        // ConfirmStage does not show the shortcut name, so a carried-over one is invisible: the
        // user would find a Start Menu entry named after a workload they installed earlier.
        var module = new ShortcutsModule();

        await module.InitializeAsync(Selection());
        module.CustomName = "Workload A";
        module.CreateDesktopShortcut = false;

        await module.InitializeAsync(Selection());

        module.CustomName.Should().BeNull();
        module.CreateDesktopShortcut.Should().BeTrue();

        var draft = new InstallationOptionsDraft();
        module.Contribute(draft);
        draft.StartMenuShortcutName.Should().BeNull();
    }

    [Fact]
    public async Task An_overwrite_choice_does_not_carry_into_the_next_workload()
    {
        var module = new ComfyFoldersModule(Settings(new UserSettings { DefaultModelBaseFolder = @"D:\Models" }));

        await module.InitializeAsync(Selection());
        module.OverwriteExtraModelPaths = true;

        await module.InitializeAsync(Selection());

        module.OverwriteExtraModelPaths.Should().BeFalse(
            "overwriting an existing extra_model_paths.yaml is a per-install decision");
    }

    [Fact]
    public async Task A_resolved_llama_wheel_does_not_carry_into_a_workload_without_one()
    {
        var wheelId = Guid.NewGuid();
        var source = new Mock<IWorkloadSource>();
        source.Setup(s => s.GetLamaCppWheelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new LamaCppWheel { Id = wheelId, Name = "wheel", Url = "https://x.invalid/w.whl" }]);

        var module = new LlamaCppModule(source.Object);

        var withWheel = Selection();
        withWheel.Workload.SelectedLamaCppWheelId = wheelId;
        await module.InitializeAsync(withWheel);
        module.WheelUrl.Should().NotBeNull();

        await module.InitializeAsync(Selection());

        module.WheelUrl.Should().BeNull();
        module.WheelName.Should().BeNull();

        var draft = new InstallationOptionsDraft();
        module.Contribute(draft);
        draft.ResolvedLlamaCppWheelUrl.Should().BeNull();
    }

    [Fact]
    public async Task Disclaimer_acceptance_does_not_carry_into_the_next_install()
    {
        var module = new DisclaimerModule();

        await module.InitializeAsync(Selection());
        module.Accepted = true;

        await module.InitializeAsync(Selection());

        module.Accepted.Should().BeFalse("each install must be accepted on its own");
    }

    [Fact]
    public async Task A_vc_runtime_decline_does_not_carry_into_the_next_workload()
    {
        var detection = new Mock<IVcRuntimeDetectionService>();
        detection.Setup(d => d.Detect()).Returns(new VcRuntimeDetectionResult(VcRuntimeState.Missing));

        var module = new VcRuntimeModule(detection.Object);

        await module.InitializeAsync(Selection());
        module.InstallRuntime = false;

        await module.InitializeAsync(Selection());

        module.InstallRuntime.Should().BeTrue();

        var draft = new InstallationOptionsDraft();
        module.Contribute(draft);
        draft.SkipVcRuntimeProvisioning.Should().BeFalse();
    }
}
