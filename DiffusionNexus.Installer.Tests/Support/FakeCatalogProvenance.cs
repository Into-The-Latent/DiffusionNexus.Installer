using DiffusionNexus.Installer.Core.Updates;

namespace DiffusionNexus.Installer.Tests.Support;

/// <summary>A catalog provenance line from a delegate, counting reads. A throwing delegate is a failed reading.</summary>
internal sealed class FakeCatalogProvenance(Func<string> describe) : ICatalogProvenance
{
    public Func<string> Describe { get; set; } = describe;
    public int Reads { get; private set; }

    public InstalledCatalogReading Read()
    {
        Reads++;
        return InstalledCatalogReading.Take(Describe);
    }
}
