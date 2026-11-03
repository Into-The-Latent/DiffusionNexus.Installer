using DiffusionNexus.Installer.Core.Existing;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.Installer.SDK.Services.Installation.Utilities;
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

    public bool Understood { get; set; }

    private ExistingRun? Run => _selection?.Existing;

    public bool RunUpdate => _selection?.UpdateOnly == true || Run?.RunUpdate == true;

    public bool ChangesTorch => Run?.UpgradeTorch == true;

    /// <summary>"Torch changes from X to Y."</summary>
    public string? TorchChange => ChangesTorch ? Sentence(TorchFromTo()) : null;

    /// <summary>The note about workloads torch stays too old for, without the checkbox's wording around it.</summary>
    public IReadOnlyList<string> NeedsNewerTorch => RunUpdate || Run is null ? [] : ComfyUpdateModule.NeedsNewerLines(Run);

    /// <summary>The "What will change" list.</summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            if (Run is not { Profile: { } profile }) return [];

            var channel = ComfyUpdateModule.ChannelText(profile);
            var lines = new List<string>
            {
                RunUpdate
                    ? $"ComfyUI: updates {char.ToLowerInvariant(channel[0])}{channel[1..]}"
                    : "ComfyUI: stays on its current version.",
                ChangesTorch ? $"Torch: changes {TorchFromTo()}." : "Torch: stays as it is.",
            };

            var newNodes = _selection!.Workload.GitRepositories.Count(r =>
                !Directory.Exists(Path.Combine(profile.RepositoryPath!, "custom_nodes", PathNormalizer.GetRepositoryName(r.Url))));
            if (newNodes > 0) lines.Add($"Custom nodes: {newNodes} to add.");

            lines.Add(profile.UpdateScript switch
            {
                UpdateScriptState.Legacy => "Update script: the old one is replaced.",
                UpdateScriptState.None => "Update script: one is added.",
                _ => "Update script: already current.",
            });

            return lines;
        }
    }

    private string TorchFromTo() => Run is { Target: { } target, Profile: { } profile }
        ? $"from {ComfyUpdateModule.Installed(profile)} to {ComfyUpdateModule.Stack(target.Torch.TorchVersion, target.Torch.CudaVersion)}"
        : string.Empty;

    private static string Sentence(string fromTo) => $"Torch changes {fromTo}.";

    public Task InitializeAsync(WizardSelection selection, CancellationToken ct = default)
    {
        _selection = selection;
        Understood = false;
        if (selection.Existing is { } run) run.Changed += () => Understood = false;
        return Task.CompletedTask;
    }

    public void Contribute(InstallationOptionsDraft draft) { }

    public ModuleValidation Validate() =>
        Understood ? ModuleValidation.Ok() : ModuleValidation.Error("Tick \"I understand what will change\" to continue.");
}
