namespace DiffusionNexus.Installer.Core.Host;

/// <summary>
/// The system clipboard, for the Install screen's "Copy log" button (issue #14). Behind an
/// interface for the same reason as <see cref="IPostInstallActions"/>: writing the clipboard
/// leaves the process, so the screen could otherwise only be tested by actually doing it.
/// </summary>
public interface IClipboard
{
    /// <summary>
    /// Whether this host can reach a clipboard at all. A button that silently does nothing is
    /// worse than no button, so the screen hides it when this is false.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>Replaces the clipboard's contents with <paramref name="text"/>. Throws if it cannot.</summary>
    Task SetTextAsync(string text);
}
