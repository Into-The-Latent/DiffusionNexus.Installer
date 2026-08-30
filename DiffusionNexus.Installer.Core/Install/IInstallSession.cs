using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Services;

namespace DiffusionNexus.Installer.Core.Install;

/// <summary>
/// Owns a running installation for the lifetime of the app, not of a UI component.
/// Registered as a singleton: a Blazor circuit reconnect disposes components, and an install that
/// can run for hours must not go with them.
/// </summary>
public interface IInstallSession
{
    InstallPhase Phase { get; }
    InstallationProgress? Progress { get; }
    DownloadProgress? CurrentDownload { get; }
    IReadOnlyList<InstallLogLine> LogLines { get; }
    InstallationResult? Result { get; }

    /// <summary>Raised when any of the above changes. Subscribers re-render; they never own state.</summary>
    event Action? Changed;

    Task StartAsync(WizardPlan plan, CancellationToken ct = default);
    void Cancel();
    void SkipCurrentDownload();
}
