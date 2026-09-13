using DiffusionNexus.Installer.SDK.Models.Configuration;

namespace DiffusionNexus.Installer.Electron.Services;

/// <summary>
/// Card artwork for the software tiles on the welcome screen.
///
/// Lives here rather than in Installer.Core beside SoftwareBranding.DisplayName, because every
/// value below names a file THIS project serves out of THIS project's wwwroot. Core is the
/// UI-agnostic half; a rename under wwwroot/img/software must break next to the assets, not in a
/// project that has no wwwroot at all. DisplayName is genuinely presentation-neutral and stays
/// where it was.
///
/// Deliberately total over <see cref="RepositoryType"/> rather than a dictionary lookup that
/// throws: the catalog drives which cards render, so a RepositoryType added to the SDK before its
/// artwork exists must degrade to a neutral tile, not an unhandled exception on the first screen.
/// </summary>
public static class SoftwareLogos
{
    /// <summary>
    /// wwwroot-relative URL of the card artwork, or null when none ships for this software.
    /// No leading slash: the app is served from the root but the asset URLs stay base-href
    /// relative, matching how app.css is referenced.
    /// </summary>
    public static string? PathFor(RepositoryType type) => type switch
    {
        RepositoryType.ComfyUI => "img/software/comfyui.jpg",
        RepositoryType.A1111 => "img/software/automatic1111.jpg",
        RepositoryType.Forge => "img/software/forge.jpg",
        RepositoryType.AIToolkit => "img/software/ai-toolkit.jpg",
        RepositoryType.Fooocus => "img/software/fooocus.jpg",
        RepositoryType.AceStep => "img/software/ace-step.jpg",
        _ => null
    };
}
