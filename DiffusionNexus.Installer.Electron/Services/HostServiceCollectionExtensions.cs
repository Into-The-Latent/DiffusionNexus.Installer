using DiffusionNexus.Installer.Core.Gallery;
using DiffusionNexus.Installer.Core.Host;
using DiffusionNexus.Installer.SDK.Shared.Services;
using DiffusionNexus.Installer.SDK.Shared.Services.Feedback;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.Installer.Electron.Services;

/// <summary>
/// The host-owned half of the container: services the UI resolves that neither AddInstallerCore
/// nor the SDK's own extensions register.
///
/// These used to be loose statements in Program.cs, which no test can reach -- Program.cs is
/// top-level statements in an executable with its own Main. Deleting the IFeedbackReportingService
/// line left the whole suite green (every bUnit fixture registers its own mock) while the app threw
/// "Cannot provide a value for property 'Feedback'" on the very first screen. An extension method
/// is a thing a test can call, so DependencyInjectionTests now covers exactly what the app runs.
/// </summary>
public static class HostServiceCollectionExtensions
{
    /// <summary>
    /// Where feedback reports are posted. A Cloudflare Worker relay that files the GitHub issue --
    /// the same relay the 2.x installer uses; the service itself ships in SDK.Shared.
    /// </summary>
    public const string FeedbackRelayUrl = "https://diffusionnexus-feedback-relay.diffusionnexus.workers.dev";

    /// <summary>
    /// Unpinned raw URL of the community-links document: a second file in the same Gist that
    /// holds the in-app announcements, so one place edits both and no release is needed
    /// (issue #6). Always serves the latest revision.
    /// </summary>
    public const string CommunityLinksGistUrl =
        "https://gist.githubusercontent.com/Little-God1983/358c5fccc6655f6e56aef8470bb17c1c/raw/community-links.json";

    public static IServiceCollection AddInstallerHostServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<UpdaterLog>();
        services.AddSingleton<IAppUpdaterShell, ElectronAppUpdaterShell>();
        services.AddSingleton<AppUpdateChecker>();

        // The catalog counterpart of Program.cs's startup AutoUpdater check.
        services.AddHostedService<CatalogUpdateStartupCheck>();

        services.AddSingleton<IFeedbackReportingService>(_ => new FeedbackReportingService(
            new FeedbackReportingServiceOptions { RelayUrl = FeedbackRelayUrl }));

        // Its own HttpClient, never the container's: AddInstallationServices registers one with an
        // infinite timeout for model downloads. The service applies its own short timeout on top,
        // and the cache makes it one request per process rather than one per screen.
        services.AddSingleton<ICommunityLinksService>(_ => new GistCommunityLinksService(
            new CommunityLinksServiceOptions { Url = CommunityLinksGistUrl, UserAgent = "DiffusionNexus-Installer/3" }));
        services.AddSingleton<CommunityLinksCache>();

        // Registered as the concrete type AND the interface, deliberately: the modal component
        // resolves the concrete service to subscribe to it, the wizard resolves the interface.
        services.AddSingleton<ModalPromptService>();
        services.AddSingleton<IUserPrompt>(sp => sp.GetRequiredService<ModalPromptService>());
        services.AddSingleton<MismatchPromptService>();
        services.AddSingleton<IMismatchedFilePrompt>(sp => sp.GetRequiredService<MismatchPromptService>());

        services.AddSingleton<IFolderPicker, ElectronFolderPicker>();
        services.AddSingleton<IPostInstallActions, ElectronPostInstallActions>();

        // Scoped, not singleton: it holds the circuit's IJSRuntime -- see JsClipboard.
        services.AddScoped<IClipboard, JsClipboard>();

        return services;
    }
}
