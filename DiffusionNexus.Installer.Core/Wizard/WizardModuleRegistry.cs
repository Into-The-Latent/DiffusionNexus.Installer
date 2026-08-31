using DiffusionNexus.Installer.SDK.Models.Configuration;

namespace DiffusionNexus.Installer.Core.Wizard;

/// <summary>
/// Every capability module the app knows about. Also the installability gate: the gallery may only
/// offer a workload whose every <em>blocking</em> detected capability has a registered module behind it.
/// The gate considers only <see cref="WorkloadCapabilities.Blocking"/> — non-blocking capabilities
/// like CustomNodes, Workflows, and Accelerators are correct without a module, since the catalog's own
/// declarations (gitRepositories, workflows, installTriton) handle them. Only VramProfile, ModelDownloads,
/// and LlamaCpp block an install if missing.
/// <para>
/// Exactly one plan may be in flight at a time. The registry hands out its own long-lived module
/// instances and modules hold per-run state, so a second <see cref="BuildPlanAsync"/> re-initializes
/// those instances and invalidates any plan still held from an earlier call. The wizard drives one
/// plan at a time, which is what makes that safe.
/// </para>
/// </summary>
public sealed class WizardModuleRegistry(IEnumerable<IWizardModule> modules)
{
    private readonly IReadOnlyList<IWizardModule> _modules = modules.ToList();

    /// <summary>The union of what the registered modules can handle.</summary>
    public WorkloadCapability SatisfiedCapabilities =>
        _modules.Aggregate(WorkloadCapability.None, (acc, m) => acc | m.Satisfies);

    /// <summary>
    /// A workload is installable when nothing it needs is missing AND the pipeline would not refuse
    /// it outright. Deliberately asks WorkloadCapabilities.DetectBlocking rather than the modules
    /// themselves — a module that is not registered cannot be asked whether it applies.
    /// <para>
    /// The incompatibility half is not a capability question and no module can ever answer it: a
    /// catalog entry pairing a torch version with a CUDA version that has no wheel is refused by
    /// InstallationPipeline before step 1. Both halves live here so the gallery and any other
    /// caller get one answer rather than two that can drift.
    /// </para>
    /// </summary>
    public bool IsInstallable(InstallationConfiguration workload)
    {
        var needed = WorkloadCapabilities.DetectBlocking(workload);
        return (needed & ~SatisfiedCapabilities) == WorkloadCapability.None
            && WorkloadCapabilities.DetectIncompatibility(workload) is null;
    }

    /// <summary>
    /// Initializes every module against the selection, then keeps the ones that apply. Modules are
    /// initialized before AppliesTo is read because applicability can depend on work done during
    /// initialization (GPU detection, for one).
    /// </summary>
    public async Task<WizardPlan> BuildPlanAsync(WizardSelection selection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selection);

        // Same Stage-then-Order sequence WizardPlan.ToOptions uses for Contribute, not registration
        // order: a downstream module's InitializeAsync can depend on an upstream module's answer
        // (the spec's VRAM -> ModelSelection example), and that dependency is only honoured if
        // initialization runs in the same order the modules are meant to be sequenced in.
        foreach (var module in _modules.OrderBy(m => (int)m.Stage).ThenBy(m => m.Order))
            await module.InitializeAsync(selection, ct).ConfigureAwait(false);

        var byStage = _modules
            .Where(m => m.AppliesTo(selection))
            .GroupBy(m => m.Stage)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<IWizardModule>)g.OrderBy(m => m.Order).ToList());

        // Confirm and Install always run: they are the summary and the install itself, not modules.
        // TryAdd, not indexer assignment: a future module actually targeting one of those stages
        // would otherwise have its whole entry silently overwritten with an empty list here.
        byStage.TryAdd(WizardStage.Confirm, []);
        byStage.TryAdd(WizardStage.Install, []);

        return new WizardPlan(selection, byStage);
    }
}
