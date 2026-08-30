namespace DiffusionNexus.Installer.Core.Host;

/// <summary>Native folder selection. Returns null when the user dismisses the dialog.</summary>
public interface IFolderPicker
{
    Task<string?> PickFolderAsync(string title, string? startIn = null, CancellationToken ct = default);
}
