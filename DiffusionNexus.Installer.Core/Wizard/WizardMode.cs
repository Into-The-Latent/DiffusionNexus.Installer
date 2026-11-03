namespace DiffusionNexus.Installer.Core.Wizard;

/// <summary>Which of the app's paths a wizard run is on.</summary>
public enum WizardMode
{
    /// <summary>Installs the software and the workload into a new folder.</summary>
    FreshInstall,

    /// <summary>Downloads a workload's models into any folder, and nothing else.</summary>
    ModelsOnly
}
