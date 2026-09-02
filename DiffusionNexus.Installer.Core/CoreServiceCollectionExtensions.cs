using DiffusionNexus.Installer.Core.Catalog;
using DiffusionNexus.Installer.Core.Content;
using DiffusionNexus.Installer.Core.Install;
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.Installer.SDK.Services.Installation.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DiffusionNexus.Installer.Core;

public static class CoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers the wizard. Call after AddInstallationServices, AddDiffusionNexusCatalog and
    /// AddDiffusionNexusUserSettings — the modules depend on services those register.
    /// </summary>
    public static IServiceCollection AddInstallerCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IWorkloadSource, CatalogWorkloadSource>();
        services.AddSingleton<IInstallSession, InstallSession>();

        // The SDK's own AddInstallationServices does not register this one — both Avalonia apps
        // construct it by hand — but the install-folder pre-flight needs it. TryAdd so a host that
        // registers its own implementation still wins.
        services.TryAddSingleton<IPreInstallationService, PreInstallationService>();

        // Transient on purpose: modules hold per-run answers. The registry's factory resolves a
        // fresh set for every plan, so a workload never sees another workload's answers.
        services.AddTransient<IWizardModule, InstallFolderModule>();
        services.AddTransient<IWizardModule, ComfyFoldersModule>();
        services.AddTransient<IWizardModule, GpuPreflightModule>();
        services.AddTransient<IWizardModule, VcRuntimeModule>();
        services.AddTransient<IWizardModule, LlamaCppModule>();
        services.AddTransient<IWizardModule, ShortcutsModule>();
        services.AddTransient<IWizardModule, DisclaimerModule>();

        // Size lookups get their OWN bounded client, never the container's: AddInstallationServices
        // registers HttpClient with an infinite timeout on purpose (model downloads run for hours)
        // and documents that size-resolution consumers must construct their own. A HEAD against a
        // dead host on the shared client would hang the Content stage with no way out. One
        // resolver instance so the disk-space estimate and the pre-flight verification share a
        // size cache -- 1.x learned that a second resolver adds a full HEAD pass after Install.
        services.AddSingleton(_ => new UrlSizeResolver(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }));
        services.AddSingleton<IDiskSpaceEstimator, SdkDiskSpaceEstimator>();
        services.AddSingleton<IExistingModelVerifier, SdkExistingModelVerifier>();
        services.AddSingleton<IModelPresenceScanner, ModelPresenceScanner>();

        services.AddSingleton<DevTools.LauncherScriptPreview>();
        services.AddSingleton<Gallery.GalleryBuilder>();
        services.AddSingleton(sp => new WizardModuleRegistry(() => sp.GetServices<IWizardModule>()));

        return services;
    }
}
