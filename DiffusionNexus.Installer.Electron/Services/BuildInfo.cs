using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiffusionNexus.Installer.SDK.Catalog;

namespace DiffusionNexus.Installer.Electron.Services;

/// <summary>What this build says about itself: the `--build-info` answer, uploaded by
/// New-Release.ps1 as the `build-info.json` release asset. The property names are read by the
/// release scripts and by the catalog repo's schema gate, so they are a contract.</summary>
public sealed record BuildInfoDocument(
    [property: JsonPropertyName("app")] string App,
    [property: JsonPropertyName("sdk")] string Sdk,
    [property: JsonPropertyName("catalogSchema")] int CatalogSchema,
    [property: JsonPropertyName("builtAt")] DateTimeOffset BuiltAt);

public static class BuildInfo
{
    public const string Flag = "--build-info";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Exactly this argument, anywhere in the list. Electron passes its own arguments to
    /// the .NET side, and none of them may ever turn a normal launch into a print-and-exit.</summary>
    public static bool Handles(string[] args) => args.Contains(Flag, StringComparer.Ordinal);

    public static BuildInfoDocument Create()
    {
        // The Catalog assembly is the one whose version the release gates judge: the pin check
        // reads the same version from the csproj, and the schema constant lives here.
        var catalogAssembly = typeof(CatalogSchema).Assembly;
        var sdk = AppVersion.Strip(catalogAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
        // This assembly's write time: no MSBuild-generated timestamp, so nothing forces a rebuild on
        // every build, and the packaged file is what the release script runs anyway. Not the entry
        // assembly: under a test host that is testhost.dll, dated whenever the .NET SDK was installed.
        var self = typeof(BuildInfo).Assembly.Location;
        var builtAt = File.Exists(self) ? new DateTimeOffset(File.GetLastWriteTimeUtc(self), TimeSpan.Zero) : DateTimeOffset.UtcNow;
        return new BuildInfoDocument(AppVersion.Display, sdk, CatalogSchema.Supported, builtAt);
    }

    public static string ToJson() => JsonSerializer.Serialize(Create(), Json);
}
