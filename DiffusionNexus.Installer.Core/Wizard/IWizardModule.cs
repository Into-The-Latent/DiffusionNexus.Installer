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

    /// <summary>
    /// Whether the page renders this module behind the stage's "Advanced settings" bar rather
    /// than in first view. Declared by the module so the page needs no per-type knowledge and a
    /// new question is shown, not hidden, unless it says otherwise.
    /// </summary>
    bool IsAdvanced => false;

    /// <summary>
    /// Whether the module's panel shows its validation message itself, next to the box it is
    /// about. The page then leaves it out of its list under the buttons, so the message is not
    /// shown twice -- one of them off screen. Declared here, as <see cref="IsAdvanced"/> is, so
    /// the page keeps no list of such modules.
    /// </summary>
    bool ShowsOwnValidation => false;

    /// <summary>
    /// Writes the module's answers back to user settings so the next run starts from them. The
    /// page calls it for every module of the stage being left, on every stage's Next (including
    /// Confirm's "Start installation"). Implementations must re-read the
    /// settings before writing: several modules save in a row and each must build on the file as
    /// it is now, not on the copy it loaded at initialization.
    /// </summary>
    Task PersistAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// The wizard is shown again after a side trip (#45) without being rebuilt. Refresh what the
    /// machine may have changed meanwhile -- a detection, a scan -- never the user's answers. It
    /// runs before the page's first render, so it must be quick; a probe that is not sure keeps
    /// the earlier answer. A no-op by default.
    /// </summary>
    Task RefreshAfterResumeAsync(CancellationToken ct = default) => Task.CompletedTask;
}
