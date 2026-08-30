namespace DiffusionNexus.Installer.Core.Host;

/// <summary>A yes/no question the pipeline can block on mid-install.</summary>
public interface IUserPrompt
{
    Task<bool> ConfirmAsync(
        string title,
        string message,
        string confirmLabel = "Continue",
        string cancelLabel = "Cancel",
        CancellationToken ct = default);
}
