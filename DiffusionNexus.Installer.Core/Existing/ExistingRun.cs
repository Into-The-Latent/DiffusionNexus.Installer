using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.Installer.SDK.Services.Installation.Existing;
using DiffusionNexus.Installer.SDK.Services.Installation.Utilities;

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
    private readonly Lock _gate = new();
    private FolderFacts _facts = FolderFacts.None;

    /// <summary>What the rest of the run needs from the folder, read once per answer: it can be a share that takes 21 seconds to reply.</summary>
    private sealed record FolderFacts(IReadOnlySet<string> NodeFolders, bool HasModelPathsFile)
    {
        public static readonly FolderFacts None = new(new HashSet<string>(), false);

        public bool Same(FolderFacts other) =>
            HasModelPathsFile == other.HasModelPathsFile && NodeFolders.SetEquals(other.NodeFolders);

        public static FolderFacts Read(ExistingComfyUIProfile? profile)
        {
            if (profile is not { Support: ExistingInstallSupport.Supported, RepositoryPath: { } repository }) return None;

            // Apart, so an unreadable custom_nodes doesn't report that the yaml is missing: Confirm
            // would say a file is added, and the run would rewrite the one that's there.
            var hasModelPathsFile = File.Exists(Path.Combine(repository, "extra_model_paths.yaml"));

            try
            {
                var nodes = Path.Combine(repository, "custom_nodes");
                return new FolderFacts(
                    Directory.Exists(nodes)
                        ? Directory.EnumerateDirectories(nodes).Select(d => Path.GetFileName(d)).ToHashSet(StringComparer.OrdinalIgnoreCase)
                        : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    hasModelPathsFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new FolderFacts(new HashSet<string>(StringComparer.OrdinalIgnoreCase), hasModelPathsFile);
            }
        }
    }

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
            // Outside the lock, so a click on the page doesn't wait behind a slow folder.
            var facts = FolderFacts.Read(value);
            Answer(() =>
            {
                _profile = value;
                _facts = facts;
            });
        }
    }

    /// <summary>The Update checkbox.</summary>
    public bool RunUpdate
    {
        get => _runUpdate;
        set
        {
            if (_runUpdate != value) Answer(() => _runUpdate = value);
        }
    }

    /// <summary>False only when the probe is sure there's no usable NVIDIA GPU.</summary>
    public bool GpuUsable
    {
        get => _gpuUsable;
        set
        {
            if (_gpuUsable != value) Answer(() => _gpuUsable = value);
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

    /// <summary>Asks the folder panels what they will change, from the answers as they are at that moment.</summary>
    public Func<IReadOnlyList<string>>? FolderChanges { private get; set; }

    /// <summary>What the folder panels will change in this install, as Confirm lists it.</summary>
    public IReadOnlyList<string> FolderChangeLines => FolderChanges?.Invoke() ?? [];

    /// <summary>The nodes this run clones. Read once per answer: the folder can be on a share that takes 21 seconds to reply.</summary>
    public IReadOnlyList<GitRepository> NewNodes { get; private set; } = [];

    /// <summary>The install has an extra_model_paths.yaml, whatever its blocks are called.</summary>
    public bool HasModelPathsFile { get; private set; }

    /// <summary>
    /// The install has started on what the selection says now. A check that answers later, like the
    /// one a side trip starts, must not rewrite the workload under it.
    /// </summary>
    public void Freeze()
    {
        lock (_gate) _frozen = true;
    }

    /// <summary>A check found the same install: the profile holds no node list, so those are read again.</summary>
    public void CheckedAgain()
    {
        var facts = FolderFacts.Read(_profile);
        if (!facts.Same(_facts)) Answer(() => _facts = facts);
    }

    /// <summary>The folder check answers off the page's thread, and Target and Workload must describe one answer.</summary>
    private void Answer(Action change)
    {
        lock (_gate)
        {
            if (_frozen) return;

            change();
            Follow();
        }

        Changed?.Invoke();
    }

    private void Follow()
    {
        if (_profile is { Support: ExistingInstallSupport.Supported, RepositoryPath: { } repository } profile)
        {
            Target = ExtendTargetResolver.Resolve(profile, _packs);
            _selection.TargetFolder = repository;
            _selection.Workload = ExtendConfigurationMerger.ForExistingInstall(_union, profile, Target, UpgradeTorch, _manager);
        }
        else
        {
            Target = null;
            _selection.TargetFolder = string.Empty;
            _selection.Workload = _union.Configuration;
        }

        NewNodes = _profile is null ? [] : _selection.Workload.GitRepositories
            .Where(r => !_facts.NodeFolders.Contains(PathNormalizer.GetRepositoryName(r.Url)))
            .ToList();
        HasModelPathsFile = _facts.HasModelPathsFile;
    }
}
