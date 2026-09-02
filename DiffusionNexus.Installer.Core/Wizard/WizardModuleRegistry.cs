using DiffusionNexus.Installer.SDK.Models.Configuration;

namespace DiffusionNexus.Installer.Core.Wizard;

/// <summary>
/// Every capability module the app knows about. Also the installability gate: the gallery may only
/// offer a workload whose every <em>blocking</em> detected capability has a registered module behind it.
/// The gate considers only <see cref="WorkloadCapabilities.Blocking"/> — non-blocking capabilities
/// like CustomNodes and Accelerators are correct without a module, since the catalog's own
/// declarations (gitRepositories, installTriton) handle them.
/// <para>
/// Modules are per run. The factory is invoked once per <see cref="BuildPlanAsync"/>, so every plan
/// owns fresh instances and nothing a user answered for one workload can leak into the next. The
/// factory is also invoked once at construction to learn <see cref="SatisfiedCapabilities"/>, which
/// is a constant of the module types and must be answerable without building a plan.
/// </para>
/// </summary>
public sealed class WizardModuleRegistry
{
    private readonly Func<IEnumerable<IWizardModule>> _modules;

    public WizardModuleRegistry(Func<IEnumerable<IWizardModule>> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        _modules = modules;
        SatisfiedCapabilities = modules().Aggregate(WorkloadCapability.None, (acc, m) => acc | m.Satisfies);
    }

    /// <summary>The union of what the registered modules can handle.</summary>
    public WorkloadCapability SatisfiedCapabilities { get; }

    /// <summary>
    /// A workload is installable when nothing it needs is missing AND the pipeline would not refuse
    /// it outright. Deliberately asks WorkloadCapabilities.DetectBlocking rather than the modules
    /// themselves — a module that is not registered cannot be asked whether it applies.
    /// </summary>
    public bool IsInstallable(InstallationConfiguration workload)
    {
        var needed = WorkloadCapabilities.DetectBlocking(workload);
        return (needed & ~SatisfiedCapabilities) == WorkloadCapability.None
            && WorkloadCapabilities.DetectIncompatibility(workload) is null;
    }

    /// <summary>
    /// Builds fresh modules, initializes every one against the selection, then keeps the ones that
    /// apply. Modules are initialized before AppliesTo is read because applicability can depend on
    /// work done during initialization (GPU detection, for one).
    /// </summary>
    public async Task<WizardPlan> BuildPlanAsync(WizardSelection selection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selection);

        var modules = _modules().ToList();

        // Same Stage-then-Order sequence WizardPlan.ToOptions uses for Contribute: a downstream
        // module's InitializeAsync can depend on an upstream module's answer.
        foreach (var module in modules.OrderBy(m => (int)m.Stage).ThenBy(m => m.Order))
            await module.InitializeAsync(selection, ct).ConfigureAwait(false);

        var byStage = modules
            .Where(m => m.AppliesTo(selection))
            .GroupBy(m => m.Stage)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<IWizardModule>)g.OrderBy(m => m.Order).ToList());

        // Confirm and Install always run. TryAdd so a module targeting one of them is kept.
        byStage.TryAdd(WizardStage.Confirm, []);
        byStage.TryAdd(WizardStage.Install, []);

        return new WizardPlan(selection, byStage);
    }
}
