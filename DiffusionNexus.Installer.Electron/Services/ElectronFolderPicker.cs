using DiffusionNexus.Installer.Core.Host;
using ElectronNET.API;
using ElectronNET.API.Entities;

// This project's own RootNamespace is "DiffusionNexus.Installer.Electron", so an unqualified
// `Electron` written inside DiffusionNexus.Installer.Electron.Services resolves to that
// namespace segment, not to the ElectronNET.API.Electron static class -- the same collision
// Program.cs already documents for `App`. Aliased instead of fully qualifying inline everywhere.
// NOT aliased to `ElectronHost`: this namespace now HAS a type by that name, and a type in the
// enclosing namespace beats a using alias, so that spelling silently binds to the wrong thing.
using ElectronApi = ElectronNET.API.Electron;

namespace DiffusionNexus.Installer.Electron.Services;

/// <summary>
/// Native folder chooser. Falls back to null outside Electron (plain `dotnet run` in a browser),
/// which callers already treat as "user dismissed" — so UI work in a browser stays possible.
/// </summary>
public sealed class ElectronFolderPicker : IFolderPicker
{
    public async Task<string?> PickFolderAsync(string title, string? startIn = null, CancellationToken ct = default)
    {
        if (!HybridSupport.IsElectronActive) return null;

        var window = ElectronApi.WindowManager.BrowserWindows.FirstOrDefault();
        if (window is null) return null;

        var options = new OpenDialogOptions
        {
            Title = title,
            // `promptToCreate` is [SupportedOSPlatform("windows")] and this project's TFM is plain
            // net10.0 (not OS-qualified), so the platform-compat analyzer treats every call site as
            // reachable on all platforms and flags it (CA1416) even though the app only ships
            // win-x64 (RuntimeIdentifier pinned, self-contained, in the .csproj). Suppressed locally
            // rather than dropped, to keep the same create-folder-from-dialog behaviour.
#pragma warning disable CA1416
            Properties = [OpenDialogProperty.openDirectory, OpenDialogProperty.promptToCreate],
#pragma warning restore CA1416
        };

        if (StartingFolder(startIn) is { } folder)
            options.DefaultPath = folder;

        var paths = await ElectronApi.Dialog.ShowOpenDialogAsync(window, options);
        return paths is { Length: > 0 } ? paths[0] : null;
    }

    /// <summary>
    /// The folder the dialog opens in: <paramref name="startIn"/>, or its nearest existing parent
    /// when it does not exist yet (a new install folder usually doesn't). Since Electron 43 a dialog
    /// without a defaultPath opens in Downloads instead of where Windows last left it (issue #57),
    /// so walking up beats leaving it unset. Null when there is no absolute path to go by.
    /// </summary>
    public static string? StartingFolder(string? startIn)
    {
        if (string.IsNullOrWhiteSpace(startIn) || !Path.IsPathFullyQualified(startIn)) return null;

        for (var folder = startIn; folder is not null; folder = Path.GetDirectoryName(folder))
        {
            if (Directory.Exists(folder)) return folder;
        }
        return null;
    }
}
