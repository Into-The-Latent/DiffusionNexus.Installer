using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Compatibility;
using DiffusionNexus.Installer.SDK.Services.Hardware;

namespace DiffusionNexus.Installer.Core.Modules;

/// <summary>
/// Announces the Visual C++ runtime install before it happens.
/// <para>
/// Triton loads the MSVC 2015-2022 x64 runtime to JIT-compile CUDA kernels. When it is missing the
/// pipeline's VcRuntimeSetup step installs it via winget or vc_redist.x64.exe — and the redist is
/// machine-scope only, so exactly one UAC dialog is unavoidable. Asking here is the difference
/// between an expected prompt and an unexplained one appearing partway through a long install.
/// </para>
/// <para>
/// Declining is a real answer, not a nag to be overridden: SkipVcRuntimeProvisioning makes the
/// pipeline report the gap as a warning row instead of installing. A question we override is a
/// question we should not have asked.
/// </para>
/// </summary>
public sealed class VcRuntimeModule(IVcRuntimeDetectionService detection) : IWizardModule
{
    private VcRuntimeState _state = VcRuntimeState.Unknown;

    public string Id => "vc-runtime";
    public WizardStage Stage => WizardStage.System;
    public int Order => 10;
    public WorkloadCapability Satisfies => WorkloadCapability.None;

    /// <summary>Version found on the machine, when the probe found one at all.</summary>
    public Version? InstalledVersion { get; private set; }

    /// <summary>True when the runtime is outdated rather than absent — different wording.</summary>
    public bool IsOutdated => _state == VcRuntimeState.Outdated;

    /// <summary>
    /// Installed since the wizard was built (#45): the user ran vc_redist themselves during a side
    /// trip. The module stays in the plan -- applicability is decided once, at build -- so the
    /// panel says there is nothing left to do, and nothing is skipped or prompted for.
    /// </summary>
    public bool IsPresent => _state == VcRuntimeState.Present;

    /// <summary>
    /// Default true: the silent, unattended behaviour is to provision, matching the pipeline's own
    /// default. Unticking it records that the user was shown the consequence and declined.
    /// </summary>
    public bool InstallRuntime { get; set; } = true;

    /// <summary>
    /// Only when this workload actually resolves Triton or SageAttention on, only on Windows, and
    /// only when the runtime is genuinely missing or outdated. Unknown fails open — an inconclusive
    /// probe must never put a scary dialog in front of a machine that would have worked.
    /// </summary>
    public bool AppliesTo(WizardSelection selection)
    {
        if (!OperatingSystem.IsWindows()) return false;

        var needsRuntime =
            selection.Workload.GetEffectiveInstallTriton() ||
            selection.Workload.GetEffectiveInstallSageAttention();

        return needsRuntime && _state is VcRuntimeState.Missing or VcRuntimeState.Outdated;
    }

    public Task InitializeAsync(WizardSelection selection, CancellationToken ct = default)
    {
        InstallRuntime = true;

        // Detect() never throws and returns Unknown off Windows, so it is safe to call
        // unconditionally -- AppliesTo does the platform and workload filtering.
        var result = detection.Detect();
        _state = result.State;
        InstalledVersion = result.InstalledVersion;

        return Task.CompletedTask;
    }

    /// <summary>
    /// Detected again after a side trip, so an install the user ran meanwhile is seen. An
    /// inconclusive probe keeps the earlier answer rather than guessing.
    /// </summary>
    public void RefreshAfterResume()
    {
        var result = detection.Detect();
        if (result.State == VcRuntimeState.Unknown) return;
        _state = result.State;
        InstalledVersion = result.InstalledVersion;
    }

    // A runtime found present is never "skipped": an earlier decline was about a runtime that was
    // missing, and the pipeline's own step finds it in place and does nothing.
    public void Contribute(InstallationOptionsDraft draft) =>
        draft.SkipVcRuntimeProvisioning = !InstallRuntime && !IsPresent;

    /// <summary>Never blocks: declining is a supported outcome, not an error.</summary>
    public ModuleValidation Validate() => ModuleValidation.Ok();
}
