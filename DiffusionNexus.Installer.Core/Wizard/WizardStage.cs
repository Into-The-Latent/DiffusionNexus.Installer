namespace DiffusionNexus.Installer.Core.Wizard;

/// <summary>
/// The fixed screens a wizard run passes through. Modules render into a stage; a stage with no
/// applicable modules is skipped whole, which is what keeps wizard length flat as workload
/// complexity grows.
/// </summary>
public enum WizardStage
{
    Location = 0,
    Content = 1,
    System = 2,
    Confirm = 3,
    Install = 4,
}
