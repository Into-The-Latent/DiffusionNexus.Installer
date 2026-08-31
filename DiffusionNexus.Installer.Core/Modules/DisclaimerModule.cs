using DiffusionNexus.Installer.Core.Wizard;

namespace DiffusionNexus.Installer.Core.Modules;

/// <summary>
/// The software disclaimer, and the acceptance that gates Install.
/// <para>
/// Both existing front-ends require it — the Avalonia installer's IsReadyToInstall and the 2.x
/// wizard's ConfirmationStep.CanGoNext both AND in DisclaimerAccepted. Nothing fails when a
/// rewrite drops a check like this, which is exactly why it needs a module of its own: it lands on
/// the Confirm stage, which otherwise has no modules and therefore no validation at all, leaving
/// Next unconditionally enabled in front of an irreversible install.
/// </para>
/// </summary>
public sealed class DisclaimerModule : IWizardModule
{
    /// <summary>Same substance as the Avalonia installer's SOFTWARE DISCLAIMER card.</summary>
    public const string Text =
        "This software is provided free of charge and is NOT for sale. It is provided AS IS, " +
        "without warranty of any kind, express or implied. It installs and runs third-party " +
        "frameworks and downloads third-party models, none of which are under our control. " +
        "No legal action of any kind can be taken against the developer(s) for any damage, data " +
        "loss, or cost arising from its use. Use at your own risk.";

    public string Id => "disclaimer";
    public WizardStage Stage => WizardStage.Confirm;
    public int Order => 0;
    public WorkloadCapability Satisfies => WorkloadCapability.None;

    public bool Accepted { get; set; }

    public bool AppliesTo(WizardSelection selection) => true;

    public Task InitializeAsync(WizardSelection selection, CancellationToken ct = default)
    {
        // Acceptance is per install, never inherited from the previous one.
        Accepted = false;
        return Task.CompletedTask;
    }

    public void Contribute(InstallationOptionsDraft draft) { }

    public ModuleValidation Validate() =>
        Accepted
            ? ModuleValidation.Ok()
            : ModuleValidation.Error("Accept the disclaimer to continue.");
}
