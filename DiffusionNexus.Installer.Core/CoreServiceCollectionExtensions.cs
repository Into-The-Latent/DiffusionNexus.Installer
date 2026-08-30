using DiffusionNexus.Installer.Core.Catalog;
using DiffusionNexus.Installer.Core.Install;
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using Microsoft.Extensions.DependencyInjection;

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

        // Slice 1 modules. Adding a slice-2 module here is the only change needed to make the
        // workloads that need it installable -- the gallery gate reads the registry.
        services.AddSingleton<IWizardModule, InstallFolderModule>();
        services.AddSingleton<IWizardModule, ComfyFoldersModule>();
        services.AddSingleton<IWizardModule, GpuPreflightModule>();
        services.AddSingleton<IWizardModule, ShortcutsModule>();

        services.AddSingleton(sp => new WizardModuleRegistry(sp.GetServices<IWizardModule>()));

        return services;
    }
}
