using DiffusionNexus.Installer.Core.Existing;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.Installer.SDK.Services.Installation.Existing;

namespace DiffusionNexus.Installer.Core.Modules;

/// <summary>
/// What will change in this ComfyUI, said once more before anything does, and one tick to say it was
/// read. A changed answer asks again: what was read is no longer what will happen.
/// </summary>
public sealed class ExistingReviewModule : IWizardModule
{
    private WizardSelection? _selection;

    public string Id => "existing-review";
    public WizardStage Stage => WizardStage.Confirm;

    /// <summary>Above the disclaimer: that one applies to everything said before it.</summary>
    public int Order => -1;

    public WorkloadCapability Satisfies => WorkloadCapability.None;

    public bool UsedIn(WizardMode mode) => mode == WizardMode.UpdateExisting;

    public bool AppliesTo(WizardSelection selection) => true;

    /// <summary>
    /// Ticked for the list that was on screen. Another list is something else to understand, however
    /// it came to change: a check that answered late, or a driver installed during a side trip.
    /// </summary>
    public bool Understood
    {
        get => _ticked is { } ticked && ticked.SequenceEqual(Lines);
        set => _ticked = value ? Lines : null;
    }

    private IReadOnlyList<string>? _ticked;

    /// <summary>The run this confirms, whose checks the panel follows.</summary>
    public ExistingRun? Run => _selection?.Existing;

    public bool RunUpdate => _selection?.UpdateOnly == true || Run?.RunUpdate == true;

    public bool ChangesTorch => Run?.UpgradeTorch == true;

    /// <summary>"Torch changes from X to Y", or which accelerators join a torch that stays.</summary>
    public string? TorchChange => ChangesTorch ? ComfyUpdateModule.TorchChangeText(Run!) : null;

    /// <summary>Whether torch itself moves, not only what is added beside it.</summary>
    public bool TorchVersionChanges => ChangesTorch && ComfyUpdateModule.TorchVersionChanges(Run!);

    /// <summary>The note about workloads torch stays too old for, without the checkbox's wording around it.</summary>
    public IReadOnlyList<string> NeedsNewerTorch => Run is null || Run.UpgradeTorch ? [] : ComfyUpdateModule.NeedsNewerLines(Run);

    /// <summary>The "What will change" list.</summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            // A folder that stopped being usable has no repository path. Validate says what happened.
            if (Run is not { Profile: { Support: ExistingInstallSupport.Supported } profile }) return [];

            var channel = ComfyUpdateModule.ChannelText(profile);
            var lines = new List<string>
            {
                RunUpdate
                    ? $"ComfyUI: updates {char.ToLowerInvariant(channel[0])}{channel[1..]}"
                    : "ComfyUI: stays on its current version.",
                profile.Torch is null ? "Torch: could not be read, so it is left as it is."
                : !ChangesTorch ? "Torch: stays as it is."
                : ComfyUpdateModule.TorchVersionChanges(Run)
                    ? $"Torch: changes from {ComfyUpdateModule.Installed(profile)} to {ComfyUpdateModule.Stack(Run.Target!.Torch.TorchVersion, Run.Target.Torch.CudaVersion)}."
                    : $"Torch: stays at {ComfyUpdateModule.Installed(profile)}, and {ComfyUpdateModule.NewAccelerators(Run)} {(ComfyUpdateModule.NewAccelerators(Run).Contains(" and ") ? "are" : "is")} added.",
            };

            // By name when it's the Manager: the update panel says only the workload's nodes get added.
            if (Run.NewNodes is { Count: > 0 } newNodes)
                lines.Add(ExtendConfigurationMerger.HasManagerRepository(new SDK.Models.Configuration.InstallationConfiguration { GitRepositories = [.. newNodes] })
                    ? $"Custom nodes: {newNodes.Count} to add, including ComfyUI-Manager."
                    : $"Custom nodes: {newNodes.Count} to add.");

            if (_selection!.Workload.SelectedLamaCppWheelId.HasValue)
                lines.Add("llama.cpp: its package is installed, which also sets the versions of numpy and pillow.");

            lines.Add(profile.UpdateScript switch
            {
                UpdateScriptState.Legacy => "Update script: the old one is replaced.",
                UpdateScriptState.None => "Update script: one is added.",
                _ => "Update script: already current.",
            });

            lines.AddRange(Run.FolderChangeLines);

            return lines;
        }
    }

    public Task InitializeAsync(WizardSelection selection, CancellationToken ct = default)
    {
        _selection = selection;
        _ticked = null;
        return Task.CompletedTask;
    }

    public void Contribute(InstallationOptionsDraft draft) { }

    public ModuleValidation Validate()
    {
        if (Run is { IsChecking: true }) return ModuleValidation.Error("Checking your ComfyUI folder again...");

        // Checked again here: coming back from a side trip looks at the install again.
        if (Run is { Profile: not { Support: ExistingInstallSupport.Supported } })
            return ModuleValidation.Error("This ComfyUI folder can't be used any more. Go back to Location and check it.");

        // Checked on Content, and again here: a side trip can make torch change after that.
        if (Run is { TorchHardStop: { Count: > 0 } errors })
            return ModuleValidation.Error($"{string.Join(" ", errors)} Go back to Content and untick Update ComfyUI to keep torch as it is, or do a fresh install.");

        return Understood ? ModuleValidation.Ok() : ModuleValidation.Error("Tick \"I understand what will change\" to continue.");
    }
}
