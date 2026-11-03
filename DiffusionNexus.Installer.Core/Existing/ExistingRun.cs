using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.Installer.SDK.Services.Installation.Existing;

namespace DiffusionNexus.Installer.Core.Existing;

/// <summary>
/// What an update run has learned so far, and what follows from it. The folder comes after the
/// workload here, so the torch stack is not known when the plan is built: the models, nodes and
/// workflows were merged before the wizard, and this adds the rest whenever an answer changes. It is
/// the one writer of <see cref="WizardSelection.Workload"/> and, on this path, of the target folder.
/// The model list stays the same object through every rewrite, so a row and its tick stay valid.
/// </summary>
public sealed class ExistingRun
{
    private readonly WizardSelection _selection;
    private readonly ExtendUnion _union;
    private readonly IReadOnlyList<InstallationConfiguration> _packs;
    private readonly GitRepository? _manager;
    private ExistingComfyUIProfile? _profile;
    private bool _runUpdate = true;
    private bool _gpuUsable = true;
    private bool _frozen;
    private Task _check = Task.CompletedTask;

    /// <param name="selection">The selection this run writes to.</param>
    /// <param name="union">The chosen workloads, merged.</param>
    /// <param name="packs">The chosen workloads, for the torch target.</param>
    /// <param name="manager">The catalog's ComfyUI-Manager entry, added when the install has none.</param>
    public ExistingRun(WizardSelection selection, ExtendUnion union, IReadOnlyList<InstallationConfiguration> packs, GitRepository? manager)
    {
        _selection = selection;
        _union = union;
        _packs = packs;
        _manager = manager;
    }

    /// <summary>Raised after any answer changed and the selection has followed.</summary>
    public event Action? Changed;

    /// <summary>The chosen workloads.</summary>
    public IReadOnlyList<InstallationConfiguration> Packs => _packs;

    /// <summary>What the chosen folder holds. Null until it has been checked.</summary>
    public ExistingComfyUIProfile? Profile
    {
        get => _profile;
        set
        {
            if (_frozen) return;
            _profile = value;
            Follow();
        }
    }

    /// <summary>The Update checkbox.</summary>
    public bool RunUpdate
    {
        get => _runUpdate;
        set
        {
            if (_frozen || _runUpdate == value) return;
            _runUpdate = value;
            Follow();
        }
    }

    /// <summary>False only when the probe is sure there's no usable NVIDIA GPU.</summary>
    public bool GpuUsable
    {
        get => _gpuUsable;
        set
        {
            if (_frozen || _gpuUsable == value) return;
            _gpuUsable = value;
            Follow();
        }
    }

    /// <summary>The torch the workloads would run on. Null until a supported folder has been checked.</summary>
    public ExtendTorchTarget? Target { get; private set; }

    /// <summary>The one rule for whether torch changes (the owner's decision, 2026-11-03).</summary>
    public bool UpgradeTorch => _runUpdate && _gpuUsable && Target is { ChangesInstalledStack: true };

    /// <summary>Why torch can't change, when it was going to. Unticking the update clears it.</summary>
    public IReadOnlyList<string> TorchHardStop =>
        UpgradeTorch ? Target!.Compatibility.Errors.Select(e => e.Message).ToList() : [];

    /// <summary>The folder is being checked, so what Confirm lists may be about to change.</summary>
    public bool IsChecking => !_check.IsCompleted;

    /// <summary>A check ended. Raised off the page's thread.</summary>
    public event Action? CheckAnswered;

    /// <summary>The check running now. The answer itself arrives through <see cref="Profile"/>.</summary>
    public void Checking(Task check)
    {
        _check = check;
        _ = check.ContinueWith(_ => CheckAnswered?.Invoke(), TaskScheduler.Default);
    }

    /// <summary>What the folder panels will change in this install, as Confirm lists it.</summary>
    public IReadOnlyList<string> FolderChangeLines { get; private set; } = [];

    /// <summary>A different list is a changed answer, so Confirm asks again.</summary>
    public void SetFolderChanges(IReadOnlyList<string> lines)
    {
        if (_frozen || lines.SequenceEqual(FolderChangeLines)) return;

        FolderChangeLines = lines;
        Changed?.Invoke();
    }

    /// <summary>
    /// The install has started on what the selection says now. A check that answers later, like the
    /// one a side trip starts, must not rewrite the workload under it.
    /// </summary>
    public void Freeze() => _frozen = true;

    private void Follow()
    {
        if (_profile is { Support: ExistingInstallSupport.Supported, RepositoryPath: { } repository })
        {
            Target = ExtendTargetResolver.Resolve(_profile, _packs);
            _selection.TargetFolder = repository;
            _selection.Workload = ExtendConfigurationMerger.ForExistingInstall(_union, _profile, Target, UpgradeTorch, _manager);
        }
        else
        {
            Target = null;
            _selection.TargetFolder = string.Empty;
            _selection.Workload = _union.Configuration;
        }

        Changed?.Invoke();
    }
}
