using DiffusionNexus.Installer.SDK.Models.Installation;
using DiffusionNexus.Installer.SDK.Services;

namespace DiffusionNexus.Installer.Core.Wizard;

/// <summary>
/// Mutable accumulator that every module contributes to, converted exactly once at Confirm into
/// the SDK's init-only InstallationOptions record.
/// </summary>
public sealed class InstallationOptionsDraft
{
    public int SelectedVramProfile { get; set; }
    public bool VerboseLogging { get; set; }

    /// <summary>
    /// Defaults to true, matching both shipping front-ends. The SDK's own record default is false,
    /// so omitting it would silently drop every install onto pip -- a different resolver from the
    /// one the catalog's pins were validated against, and considerably slower.
    /// </summary>
    public bool UseUvPackageManager { get; set; } = true;

    public bool SkipVcRuntimeProvisioning { get; set; }
    public bool CreateDesktopShortcut { get; set; } = true;
    public bool CreateStartMenuShortcut { get; set; } = true;
    public string? DesktopShortcutName { get; set; }
    public string? StartMenuShortcutName { get; set; }
    public HashSet<Guid> ExcludedModelIds { get; } = [];
    public HashSet<Guid> ExcludedNodeIds { get; } = [];
    public HashSet<Guid> ExcludedWorkflowIds { get; } = [];
    public HashSet<string> ForceRedownloadUrls { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> TrustedUrls { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Func<string, string, Task<ShortcutConflictResult>>? OnShortcutConflict { get; set; }
    public string? ModelBaseFolder { get; set; }
    public bool GenerateExtraModelPaths { get; set; }
    public bool OverwriteExtraModelPaths { get; set; }
    public Dictionary<string, string> FolderPathOverrides { get; } = [];
    public List<AdditionalFolder> AdditionalFolders { get; } = [];
    public string? OutputFolder { get; set; }
    public bool CpuTorch { get; set; }

    /// <summary>Resolved from SelectedLamaCppWheelId by LlamaCppModule; the step fails on null.</summary>
    public string? ResolvedLlamaCppWheelUrl { get; set; }
    public string? ResolvedLlamaCppWheelName { get; set; }

    public SDK.Services.InstallationOptions ToOptions() => new()
    {
        OnlyModelDownload = false,
        SelectedVramProfile = SelectedVramProfile,
        VerboseLogging = VerboseLogging,
        UseUvPackageManager = UseUvPackageManager,
        SkipVcRuntimeProvisioning = SkipVcRuntimeProvisioning,
        CreateDesktopShortcut = CreateDesktopShortcut,
        CreateStartMenuShortcut = CreateStartMenuShortcut,
        DesktopShortcutName = DesktopShortcutName,
        StartMenuShortcutName = StartMenuShortcutName,
        ExcludedModelIds = [.. ExcludedModelIds],
        ExcludedNodeIds = [.. ExcludedNodeIds],
        ExcludedWorkflowIds = [.. ExcludedWorkflowIds],
        ForceRedownloadUrls = new HashSet<string>(ForceRedownloadUrls, StringComparer.OrdinalIgnoreCase),
        TrustedUrls = new HashSet<string>(TrustedUrls, StringComparer.OrdinalIgnoreCase),
        OnShortcutConflict = OnShortcutConflict,
        ModelBaseFolder = ModelBaseFolder,
        GenerateExtraModelPaths = GenerateExtraModelPaths,
        OverwriteExtraModelPaths = OverwriteExtraModelPaths,
        FolderPathOverrides = new Dictionary<string, string>(FolderPathOverrides),
        AdditionalFolders = [.. AdditionalFolders],
        OutputFolder = OutputFolder,
        CpuTorch = CpuTorch,
        ResolvedLlamaCppWheelUrl = ResolvedLlamaCppWheelUrl,
        ResolvedLlamaCppWheelName = ResolvedLlamaCppWheelName,
    };
}
