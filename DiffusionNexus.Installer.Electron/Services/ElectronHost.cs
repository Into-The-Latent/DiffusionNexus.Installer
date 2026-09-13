using ElectronNET.API;

namespace DiffusionNexus.Installer.Electron.Services;

/// <summary>
/// Whether the app is running inside the Electron shell.
///
/// One accessor, because this probe is not a one-liner and a copied one drifts: the same
/// try/catch had already been written twice -- CommunityLinks.razor and Home.razor -- with two
/// doc comments that disagreed about why it exists.
/// </summary>
public static class ElectronHost
{
    /// <summary>
    /// True only when the ElectronNET shell is actually hosting this process.
    ///
    /// HybridSupport.IsElectronActive reads an AssemblyMetadataAttribute the ElectronNET build
    /// step stamps onto the Electron project's own output. Under bUnit the entry assembly is the
    /// test host, which carries no such attribute, and the property THROWS rather than returning
    /// false. That is not a signal the app is inside Electron, so it is treated the same as "no" --
    /// consistent with this codebase's other host probes (VcRuntimeDetectionService) failing open
    /// rather than taking a screen down over a detection gap. A thrown static constructor also
    /// poisons the type for the whole process, so the first test to touch it would take every
    /// later Electron probe with it.
    /// </summary>
    public static bool IsActive
    {
        get
        {
            try
            {
                return HybridSupport.IsElectronActive;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
