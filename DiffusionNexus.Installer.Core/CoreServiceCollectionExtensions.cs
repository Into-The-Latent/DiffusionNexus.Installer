using DiffusionNexus.Installer.Core.Catalog;
using DiffusionNexus.Installer.Core.Content;
using DiffusionNexus.Installer.Core.Install;
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Updates;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Catalog.Updates;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.Installer.SDK.Services.Installation.Utilities;
using DiffusionNexus.Installer.SDK.Services.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

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

        // TryAdd so a host can pin the strict policy in a Debug build -- the default relaxes it,
        // and a manual check of what users will actually see needs a way to say so.
        services.TryAddSingleton(WorkloadVisibility.Default);
        services.AddSingleton<IWorkloadSource, CatalogWorkloadSource>();
        services.AddSingleton<IInstallSession, InstallSession>();
        services.AddSingleton<IModelPreflight, ModelPreflight>();

        // The one writer of CatalogOptions.Channel. Explicit factory so the env-var reader is a
        // plain delegate (tests pass their own) and the logger stays optional -- the DI test's
        // container registers no logging, exactly like CommunityLinksCache.
        services.AddSingleton<ICatalogUpdateCoordinator>(sp => new CatalogUpdateCoordinator(
            sp.GetRequiredService<ICatalogUpdateService>(),
            sp.GetRequiredService<CatalogOptions>(),
            sp.GetRequiredService<IUserSettingsRepository>(),
            sp.GetRequiredService<IInstallSession>(),
            () => Environment.GetEnvironmentVariable(CatalogChannelResolver.EnvironmentVariable),
            sp.GetService<ILogger<CatalogUpdateCoordinator>>()));

        // The SDK's own AddInstallationServices does not register this one — both Avalonia apps
        // construct it by hand — but the install-folder pre-flight needs it. TryAdd so a host that
        // registers its own implementation still wins.
        services.TryAddSingleton<IPreInstallationService, PreInstallationService>();

        // Transient on purpose: modules hold per-run answers. The registry's factory resolves a
        // fresh set for every plan, so a workload never sees another workload's answers.
        services.AddTransient<IWizardModule, InstallFolderModule>();
        services.AddTransient<IWizardModule, ComfyFoldersModule>();
        services.AddTransient<IWizardModule, VramProfileModule>();
        services.AddTransient<IWizardModule, ModelSelectionModule>();
        services.AddTransient<IWizardModule, WorkflowSelectionModule>();
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
        services.AddSingleton<Gallery.SoftwareGalleryBuilder>();
        services.AddSingleton(sp => new WizardModuleRegistry(() => sp.GetServices<IWizardModule>()));

        return services;
    }
}
