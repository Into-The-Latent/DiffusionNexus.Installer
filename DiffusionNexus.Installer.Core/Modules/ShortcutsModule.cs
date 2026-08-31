using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Services;

namespace DiffusionNexus.Installer.Core.Modules;

/// <summary>Desktop and Start Menu shortcuts, plus the conflict-resolution callback.</summary>
public sealed class ShortcutsModule : IWizardModule
{
    public string Id => "shortcuts";
    public WizardStage Stage => WizardStage.System;
    public int Order => 100;
    public WorkloadCapability Satisfies => WorkloadCapability.None;

    public bool CreateDesktopShortcut { get; set; } = true;
    public bool CreateStartMenuShortcut { get; set; } = true;

    /// <summary>Null or blank leaves the SDK's default name for the repository type.</summary>
    public string? CustomName { get; set; }

    /// <summary>
    /// Set by the host so a name clash can be resolved by the user mid-install. Null overwrites
    /// silently, which is the SDK's documented default.
    /// </summary>
    public Func<string, string, Task<ShortcutConflictResult>>? OnShortcutConflict { get; set; }

    public bool AppliesTo(WizardSelection selection) => true;

    /// <summary>
    /// Resets every field this module owns. It has no per-workload state to load, but it is a
    /// singleton, so "nothing to initialize" is not the same as "nothing to clear": without this a
    /// second workload inherits the first one's custom shortcut name and checkbox states, and the
    /// Confirm summary does not show the name, so the user never sees it.
    /// </summary>
    public Task InitializeAsync(WizardSelection selection, CancellationToken ct = default)
    {
        CreateDesktopShortcut = true;
        CreateStartMenuShortcut = true;
        CustomName = null;
        OnShortcutConflict = null;
        return Task.CompletedTask;
    }

    public void Contribute(InstallationOptionsDraft draft)
    {
        draft.CreateDesktopShortcut = CreateDesktopShortcut;
        draft.CreateStartMenuShortcut = CreateStartMenuShortcut;

        var name = string.IsNullOrWhiteSpace(CustomName) ? null : CustomName;
        draft.DesktopShortcutName = name;
        draft.StartMenuShortcutName = name;
        draft.OnShortcutConflict = OnShortcutConflict;
    }

    public ModuleValidation Validate() => ModuleValidation.Ok();
}
