using DiffusionNexus.Installer.SDK.Models.Configuration;

namespace DiffusionNexus.Installer.Core.Gallery;

/// <summary>
/// Display names and logo assets for the software cards on the welcome screen.
///
/// Deliberately total over <see cref="RepositoryType"/> rather than a dictionary lookup that
/// throws: the catalog drives which cards render, so a RepositoryType added to the SDK before its
/// artwork exists must degrade to a neutral tile, not an unhandled exception on the first screen.
/// </summary>
public static class SoftwareBranding
{
    /// <summary>The name shown on the card. Never a raw enum value.</summary>
    public static string DisplayName(RepositoryType type) => type switch
    {
        RepositoryType.ComfyUI => "ComfyUI",
        RepositoryType.A1111 => "Automatic 1111",
        RepositoryType.Forge => "Forge",
        RepositoryType.AIToolkit => "AI Toolkit",
        RepositoryType.Fooocus => "Fooocus",
        RepositoryType.AceStep => "ACE-Step",
        _ => type.ToString()
    };

    /// <summary>
    /// wwwroot-relative URL of the card artwork, or null when none ships for this software.
    /// No leading slash: the app is served from the root but the asset URLs stay base-href
    /// relative, matching how app.css is referenced.
    /// </summary>
    public static string? LogoPath(RepositoryType type) => type switch
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
