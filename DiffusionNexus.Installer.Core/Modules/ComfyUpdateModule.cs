using DiffusionNexus.Installer.Core.Existing;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Services.Installation.Existing;

namespace DiffusionNexus.Installer.Core.Modules;

/// <summary>
/// The Update checkbox. It starts on, with a big warning (the owner's decision), and unticking also
/// keeps torch: one control, so "ComfyUI stays on its current version" is the whole truth. It is
/// locked on when the user picked the tile that does nothing else.
/// </summary>
public sealed class ComfyUpdateModule : IWizardModule
{
    private WizardSelection? _selection;

    public string Id => "comfy-update";
    public WizardStage Stage => WizardStage.Content;

    /// <summary>Above the memory panel and the model lists: it decides what the rest of the run does.</summary>
    public int Order => -10;

    public WorkloadCapability Satisfies => WorkloadCapability.None;

    public bool UsedIn(WizardMode mode) => mode == WizardMode.UpdateExisting;

    public bool AppliesTo(WizardSelection selection) => true;

    private ExistingRun? Run => _selection?.Existing;

    /// <summary>The tile that does nothing else was chosen on the previous screen.</summary>
    public bool Locked => _selection?.UpdateOnly == true;

    public bool RunUpdate
    {
        get => Locked || Run?.RunUpdate == true;
        set
        {
            if (!Locked && Run is { } run) run.RunUpdate = value;
        }
    }

    /// <summary>The folder the warning offers to open for a backup.</summary>
    public string Folder => _selection?.TargetFolder ?? string.Empty;

    /// <summary>Where the update goes. The script follows the same setting.</summary>
    public string Channel => ChannelText(Run?.Profile);

    internal static string ChannelText(ExistingComfyUIProfile? profile) => profile switch
    {
        { ManagerInstalled: false } => "To the newest stable release. ComfyUI-Manager is missing, so we'll install it.",
        { ManagerUpdatePolicy: "nightly-comfyui" } => "To the latest commit, following your ComfyUI-Manager setting.",
        _ => "To the newest stable release, following your ComfyUI-Manager setting.",
    };

    /// <summary>Whether this run changes torch.</summary>
    public bool ChangesTorch => Run?.UpgradeTorch == true;

    /// <summary>"from X to Y", when this run changes torch.</summary>
    public string? TorchChange => ChangesTorch ? TorchChangeText(Run!) : null;

    internal static bool TorchVersionChanges(ExistingRun run) =>
        run is { Target: { } target, Profile: { } profile }
        && Installed(profile) != Stack(target.Torch.TorchVersion, target.Torch.CudaVersion);

    internal static string TorchChangeText(ExistingRun run)
    {
        var (target, profile) = (run.Target!, run.Profile!);

        if (TorchVersionChanges(run))
            return $"Torch changes from {Installed(profile)} to {Stack(target.Torch.TorchVersion, target.Torch.CudaVersion)}.";

        var added = NewAccelerators(run);
        return $"{added} {(added.Contains(" and ") ? "are" : "is")} added to torch {Installed(profile)}.";
    }

    /// <summary>What the run adds beside a torch that stays.</summary>
    internal static string NewAccelerators(ExistingRun run)
    {
        var (target, torch) = (run.Target!, run.Profile!.Torch);
        var triton = target.InstallTriton && torch is not { TritonInstalled: true };
        var sage = target.InstallSageAttention && torch is not { SageAttentionInstalled: true };

        return (triton, sage) switch
        {
            (true, true) => "Triton and SageAttention",
            (true, false) => "Triton",
            _ => "SageAttention",
        };
    }

    /// <summary>The workloads whose own torch differs from the one the run settles on.</summary>
    public IReadOnlyList<string> ConflictingWorkloads => ChangesTorch ? Run!.Target!.ConflictingPackNames : [];

    /// <summary>The target torch, for the sentence about workloads that differ.</summary>
    public string TargetTorch => Run?.Target is { } target ? Stack(target.Torch.TorchVersion, target.Torch.CudaVersion) : string.Empty;

    /// <summary>The red note beside the calm message: one line per torch wanted, then what is there. Empty when nothing is missing.</summary>
    public IReadOnlyList<string> NeedsNewerTorch => RunUpdate || Run is null ? [] : NeedsNewerLines(Run);

    internal static IReadOnlyList<string> NeedsNewerLines(ExistingRun run)
    {
        if (run is not { Target: { } target, Profile: { } profile } || target.PacksNeedingNewer.Count == 0) return [];

        var lines = target.PacksNeedingNewer
            .GroupBy(n => (n.TorchVersion, n.CudaVersion))
            .Select(g =>
            {
                var names = g.Select(n => n.PackName).ToList();
                var joined = names.Count == 1 ? names[0] : $"{string.Join(", ", names.SkipLast(1))} and {names[^1]}";
                return $"{joined} {(names.Count == 1 ? "needs" : "need")} torch {Stack(g.Key.TorchVersion, g.Key.CudaVersion)}.";
            })
            .ToList();

        var these = target.PacksNeedingNewer.Count == 1 ? "this workload" : "these workloads";
        lines.Add($"You have torch {Installed(profile)}, and we won't change it, so {these} may not run until you update.");
        return lines;
    }

    internal static string Installed(ExistingComfyUIProfile profile) =>
        profile.Torch is { } torch ? Stack(torch.TorchVersion, torch.CudaVersion) : "that could not be read";

    internal static string Stack(string torch, string? cuda) =>
        string.IsNullOrEmpty(cuda) ? $"{torch}, a CPU build" : $"{torch} with CUDA {cuda}";

    public Task InitializeAsync(WizardSelection selection, CancellationToken ct = default)
    {
        _selection = selection;
        return Task.CompletedTask;
    }

    public void Contribute(InstallationOptionsDraft draft)
    {
        if (Run is not { Profile.VenvPath: { } venv } run) return;

        draft.Extend = new ExtendExistingOptions(venv, RunUpdate, run.UpgradeTorch, draft.ChangeOutputFolder);
        draft.CreateDesktopShortcut = false;
        draft.CreateStartMenuShortcut = false;
    }

    /// <summary>The one hard stop, and only when torch was going to change: no wheel for this Python.</summary>
    public ModuleValidation Validate() =>
        Run is { TorchHardStop: { Count: > 0 } errors }
            ? ModuleValidation.Error($"{string.Join(" ", errors)} Untick Update ComfyUI to keep torch as it is, or do a fresh install.")
            : ModuleValidation.Ok();
}
