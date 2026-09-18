using DiffusionNexus.Installer.SDK.Models.Configuration;

namespace DiffusionNexus.Installer.Core.Gallery;

/// <summary>
/// How a workload's catalog version reads on screen. One place, so the workload card and the
/// wizard hero cannot drift apart; the form matches the SDK's exporters ("version.subVersion").
/// </summary>
public static class WorkloadVersion
{
    public static string Display(InstallationConfiguration workload) =>
        $"v{workload.ConfigurationVersion}.{workload.ConfigurationSubVersion}";
}
