// DiffusionNexus.Installer.Tests/Support/EmbeddedCatalog.cs
using System.IO.Compression;
using DiffusionNexus.Installer.Electron.Services;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Catalog.Updates;
using DiffusionNexus.Installer.SDK.Models.Configuration;

namespace DiffusionNexus.Installer.Tests.Support;

/// <summary>
/// Reads the catalog.zip the Electron assembly embeds and ships — the same archive Program.cs
/// seeds a fresh install from — into a temp folder, so tests run against real catalog data
/// rather than synthetic fixtures.
/// </summary>
internal static class EmbeddedCatalog
{
    public static async Task<(string Directory, IReadOnlyList<InstallationConfiguration> Workloads)> LoadAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dn-catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        var electronAssembly = typeof(UpdaterLog).Assembly;
        using (var zipStream = electronAssembly.GetManifestResourceStream("catalog.zip")
            ?? throw new InvalidOperationException("catalog.zip is not embedded in the Electron assembly -- check the EmbeddedResource item in DiffusionNexus.Installer.Electron.csproj."))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
        {
            archive.ExtractToDirectory(dir);
        }

        // InstalledCatalogPath pinned under the temp dir: the default points at the real
        // %LocalAppData% catalog, and FileCatalog enumerates and deletes catalog.staging-*
        // folders there on load. A test must never touch a path outside its own temp folder.
        var options = new CatalogOptions
        {
            LocalOverridePath = dir,
            InstalledCatalogPath = Path.Combine(dir, "installed"),
        };
        ICatalog catalog = new FileCatalog(new CatalogLocator(options), options);
        var workloads = await catalog.GetWorkloadsAsync();

        return (dir, workloads);
    }

    public static void Delete(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
