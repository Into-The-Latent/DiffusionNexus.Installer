namespace DiffusionNexus.Installer.Electron.Services;

/// <summary>
/// Whether the welcome screen's software strip is scrolled hard against either end, as reported by
/// wwwroot/js/jukebox.js. Property names match that module's object literal (Blazor's JS interop
/// serialises with the web defaults, so `atStart` binds to <see cref="AtStart"/>).
/// </summary>
public sealed record JukeboxEdges(bool AtStart, bool AtEnd);
