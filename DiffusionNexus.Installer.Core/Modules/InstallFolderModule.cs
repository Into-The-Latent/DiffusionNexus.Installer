using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Services.Settings;

namespace DiffusionNexus.Installer.Core.Modules;

/// <summary>Where the workload gets installed. Applies to everything.</summary>
public sealed class InstallFolderModule(IUserSettingsRepository settings) : IWizardModule
{
    private WizardSelection? _selection;

    public string Id => "install-folder";
    public WizardStage Stage => WizardStage.Location;
    public int Order => 0;
    public WorkloadCapability Satisfies => WorkloadCapability.None;

    public string TargetFolder { get; set; } = string.Empty;

    public bool AppliesTo(WizardSelection selection) => true;

    public async Task InitializeAsync(WizardSelection selection, CancellationToken ct = default)
    {
        _selection = selection;
        var user = await settings.GetOrCreateForCurrentUserAsync(ct).ConfigureAwait(false);
        TargetFolder = user.DefaultTargetInstallFolder;
    }

    public void Contribute(InstallationOptionsDraft draft)
    {
        // The target folder is not an InstallationOptions field — the orchestrator takes it as a
        // separate argument — so it lands on the selection instead.
        if (_selection is not null)
            _selection.TargetFolder = TargetFolder;
    }

    public ModuleValidation Validate() =>
        string.IsNullOrWhiteSpace(TargetFolder)
            ? ModuleValidation.Error("Choose a folder to install into.")
            : ModuleValidation.Ok();
}
