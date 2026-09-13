using DiffusionNexus.Installer.SDK.Models.Configuration;

namespace DiffusionNexus.Installer.Core.Gallery;

/// <summary>
/// Display names for the software cards on the welcome screen.
///
/// Deliberately total over <see cref="RepositoryType"/> rather than a dictionary lookup that
/// throws: the catalog drives which cards render, so a RepositoryType added to the SDK before it
/// is named here must degrade to its raw enum name, not an unhandled exception on the first screen.
///
/// Names only. The card ARTWORK used to live here too, and should not have: every path named a
/// file served out of the Electron project's wwwroot, so this UI-agnostic project knew another
/// project's folder layout and a rename under wwwroot/img/software broke here. It now lives with
/// the assets it names, in Electron/Services/SoftwareLogos.cs.
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
}
