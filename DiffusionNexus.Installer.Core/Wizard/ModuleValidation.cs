namespace DiffusionNexus.Installer.Core.Wizard;

public sealed record ModuleValidation(bool IsValid, string? ErrorMessage)
{
    public static ModuleValidation Ok() => new(true, null);
    public static ModuleValidation Error(string message) => new(false, message);
}
