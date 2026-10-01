using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Catalog.Packaging;

namespace DiffusionNexus.Installer.Electron.Services;

/// <summary>The catalog this build embeds as its seed, read from the embedded manifest.json: the
/// values Test-CatalogSeed.ps1 -Expect judges. Channel and pack time too, because the SDK records both
/// from the seed, so a Preview stamp or a hand-made generatedAt is a wrong seed over the right archive.</summary>
public sealed record BuildInfoCatalogSeed(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("commit")] string Commit,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("generatedAt")] DateTimeOffset GeneratedAt);

/// <summary>What this build says about itself: the `--build-info` answer, uploaded by
/// New-Release.ps1 as the `build-info.json` release asset. The property names are read by the
/// release scripts and by the catalog repo's schema gate, so they are a contract.</summary>
public sealed record BuildInfoDocument(
    [property: JsonPropertyName("app")] string App,
    [property: JsonPropertyName("sdk")] string Sdk,
    [property: JsonPropertyName("catalogSchema")] int CatalogSchema,
    [property: JsonPropertyName("catalogSeed")] BuildInfoCatalogSeed CatalogSeed,
    [property: JsonPropertyName("builtAt")] DateTimeOffset? BuiltAt);

public static class BuildInfo
{
    public const string Flag = "--build-info";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Exactly this argument, anywhere in the list. Electron passes its own arguments to
    /// the .NET side, and none of them may ever turn a normal launch into a print-and-exit.</summary>
    public static bool Handles(string[] args) => args.Contains(Flag, StringComparer.Ordinal);

    public static BuildInfoDocument Create() => Create(typeof(BuildInfo).Assembly.Location);

    /// <param name="assemblyLocation">The file whose write time is the build stamp: this assembly's, normally.
    /// No MSBuild-generated timestamp, so nothing forces a rebuild on every build, and the packaged file is
    /// what the release script runs anyway. Not the entry assembly: under a test host that is testhost.dll,
    /// dated whenever the .NET SDK was installed.</param>
    public static BuildInfoDocument Create(string? assemblyLocation) =>
        // The same resources Program.cs hands the SDK as CatalogOptions.EmbeddedManifest and EmbeddedArchive.
        Create(assemblyLocation,
            () => typeof(BuildInfo).Assembly.GetManifestResourceStream("manifest.json"),
            () => typeof(BuildInfo).Assembly.GetManifestResourceStream("catalog.zip"));

    /// <param name="assemblyLocation">See <see cref="Create(string?)"/>.</param>
    /// <param name="embeddedManifest">The embedded seed manifest, or null when the build has none. A build
    /// with no seed, or a seed manifest without a commit or an archive hash, gets no document: the exception
    /// ends the process with a non-zero exit and the release script refuses the build, instead of an asset
    /// that promotion would trust.</param>
    /// <param name="embeddedArchive">The embedded catalog.zip. Its bytes must hash to the manifest's sha256,
    /// so the answer states what this binary carries, not what its manifest claims.</param>
    public static BuildInfoDocument Create(string? assemblyLocation, Func<Stream?> embeddedManifest, Func<Stream?> embeddedArchive)
    {
        // The Catalog assembly is the one whose version the release gates judge: the pin check
        // reads the same version from the csproj, and the schema constant lives here.
        var catalogAssembly = typeof(CatalogSchema).Assembly;
        var sdk = AppVersion.Strip(catalogAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
        // Absent rather than "now" when there is no file (a single-file publish, an assembly loaded from
        // bytes): promotion reads this asset as fact, and the moment --build-info ran is not one.
        DateTimeOffset? builtAt = !string.IsNullOrEmpty(assemblyLocation) && File.Exists(assemblyLocation)
            ? new DateTimeOffset(File.GetLastWriteTimeUtc(assemblyLocation), TimeSpan.Zero)
            : null;
        return new BuildInfoDocument(AppVersion.Display, sdk, CatalogSchema.Supported, ReadSeed(embeddedManifest, embeddedArchive), builtAt);
    }

    private static BuildInfoCatalogSeed ReadSeed(Func<Stream?> embeddedManifest, Func<Stream?> embeddedArchive)
    {
        using var stream = embeddedManifest()
            ?? throw new InvalidOperationException("The embedded catalog manifest 'manifest.json' is missing: this build carries no catalog seed and must not be released.");
        using var reader = new StreamReader(stream);
        // The SDK's own reader of this file, so the seed reported is the seed the app will use.
        var manifest = CatalogManifest.Parse(reader.ReadToEnd());
        if (string.IsNullOrWhiteSpace(manifest.Commit))
            throw new InvalidOperationException("The embedded catalog manifest names no commit: this seed cannot be checked against a release and must not be released.");
        if (string.IsNullOrWhiteSpace(manifest.Archive?.Sha256))
            throw new InvalidOperationException("The embedded catalog manifest has no archive sha256: this seed cannot be checked against a release and must not be released.");
        using var archive = embeddedArchive()
            ?? throw new InvalidOperationException("The embedded catalog archive 'catalog.zip' is missing: this build carries no catalog seed and must not be released.");
        var actual = Convert.ToHexStringLower(SHA256.HashData(archive));
        if (!string.Equals(actual, manifest.Archive.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The embedded catalog.zip has sha256 {actual}; its manifest says {manifest.Archive.Sha256}. This build does not carry the seed its manifest names and must not be released.");
        // The channel by name: the release scripts compare it as text, and an enum number would read as "0".
        return new BuildInfoCatalogSeed(manifest.CatalogVersion, manifest.Commit, manifest.Archive.Sha256, manifest.Channel.ToString(), manifest.GeneratedAt);
    }

    public static string ToJson() => JsonSerializer.Serialize(Create(), Json);
}
