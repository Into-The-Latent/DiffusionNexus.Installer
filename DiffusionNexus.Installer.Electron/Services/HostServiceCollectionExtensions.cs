using DiffusionNexus.Installer.Core.Host;
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

    public static IServiceCollection AddInstallerHostServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<UpdaterLog>();

        services.AddSingleton<IFeedbackReportingService>(_ => new FeedbackReportingService(
            new FeedbackReportingServiceOptions { RelayUrl = FeedbackRelayUrl }));

        // Registered as the concrete type AND the interface, deliberately: the modal component
        // resolves the concrete service to subscribe to it, the wizard resolves the interface.
        services.AddSingleton<ModalPromptService>();
        services.AddSingleton<IUserPrompt>(sp => sp.GetRequiredService<ModalPromptService>());
        services.AddSingleton<MismatchPromptService>();
        services.AddSingleton<IMismatchedFilePrompt>(sp => sp.GetRequiredService<MismatchPromptService>());

        services.AddSingleton<IFolderPicker, ElectronFolderPicker>();

        return services;
    }
}
