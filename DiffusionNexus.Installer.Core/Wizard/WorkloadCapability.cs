namespace DiffusionNexus.Installer.Core.Wizard;

/// <summary>
/// What a workload needs the wizard to ask about. Detected from catalog data alone, independent
/// of which modules happen to be registered — that independence is what lets the gallery decide
/// whether a workload is installable at all.
/// </summary>
[Flags]
public enum WorkloadCapability
{
    None           = 0,
    ComfyFolders   = 1 << 0,
    VramProfile    = 1 << 1,
    ModelDownloads = 1 << 2,
    CustomNodes    = 1 << 3,
    Workflows      = 1 << 4,
    Accelerators   = 1 << 5,
    LlamaCpp       = 1 << 6,
}
