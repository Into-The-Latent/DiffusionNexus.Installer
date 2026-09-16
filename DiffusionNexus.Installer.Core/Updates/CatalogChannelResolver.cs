using DiffusionNexus.Installer.SDK.Catalog.Packaging;

namespace DiffusionNexus.Installer.Core.Updates;

/// <summary>Where the channel the installer follows came from. Shown on /updates and Developer tools.</summary>
public enum CatalogChannelSource { Default, Setting, Environment }

/// <summary>
/// Environment wins over the saved setting, which wins over Stable. The environment value is never
/// written back: a tester sets it for one run and is back on the saved preference when it is gone.
/// </summary>
public static class CatalogChannelResolver
{
    public const string EnvironmentVariable = "DIFFUSIONNEXUS_CATALOG_CHANNEL";

    public static (CatalogChannel Channel, CatalogChannelSource Source) Resolve(string? environmentValue, string? savedValue)
    {
        if (TryParse(environmentValue, out var fromEnvironment)) return (fromEnvironment, CatalogChannelSource.Environment);
        if (TryParse(savedValue, out var fromSetting)) return (fromSetting, CatalogChannelSource.Setting);
        return (CatalogChannel.Stable, CatalogChannelSource.Default);
    }

    /// <summary>
    /// Word match only. Enum.TryParse(ignoreCase) also accepts "0" and "1", and a stray digit in a
    /// settings file must not silently pick a channel.
    /// </summary>
    public static bool TryParse(string? value, out CatalogChannel channel)
    {
        var word = value?.Trim();
        if (string.Equals(word, nameof(CatalogChannel.Stable), StringComparison.OrdinalIgnoreCase)) { channel = CatalogChannel.Stable; return true; }
        if (string.Equals(word, nameof(CatalogChannel.Preview), StringComparison.OrdinalIgnoreCase)) { channel = CatalogChannel.Preview; return true; }
        channel = CatalogChannel.Stable;
        return false;
    }
}
