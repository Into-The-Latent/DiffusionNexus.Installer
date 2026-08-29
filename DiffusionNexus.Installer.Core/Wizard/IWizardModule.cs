namespace DiffusionNexus.Installer.Core.Wizard;

/// <summary>
/// One capability the wizard can ask about. A module never references another module: it reads
/// what it needs from WizardSelection and is sequenced by Order.
/// <para>
/// A module is a UI concern, not a pipeline step. The pipeline always installs whatever the
/// catalog declares; a module only ever narrows that (Excluded*Ids) or configures it.
/// </para>
/// </summary>
public interface IWizardModule
{
    string Id { get; }

    WizardStage Stage { get; }

    /// <summary>Sequences modules inside a stage. Lower runs and renders first.</summary>
    int Order { get; }

    /// <summary>
    /// The single capability this module satisfies, or <see cref="WorkloadCapability.None"/> for
    /// unconditional modules. Used by the installability gate.
    /// </summary>
    WorkloadCapability Satisfies { get; }

    /// <summary>Reads the selected catalog workload. Never an enum switch on software name.</summary>
    bool AppliesTo(WizardSelection selection);

    Task InitializeAsync(WizardSelection selection, CancellationToken ct = default);

    void Contribute(InstallationOptionsDraft draft);

    ModuleValidation Validate();
}
