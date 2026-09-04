using System.Reflection;

namespace DiffusionNexus.Installer.Electron.Services;

/// <summary>
/// The generated THIRD-PARTY-NOTICES.txt, embedded at build time so the Licences page works
/// offline and always matches the binaries it ships with. The file itself is produced by
/// Scripts/Generate-ThirdPartyNotices.ps1 and verified fresh by CI; this class only reads it.
/// </summary>
public static class ThirdPartyNotices
{
    public const string ResourceName = "THIRD-PARTY-NOTICES.txt";

    public static string Load()
    {
        using var stream = typeof(ThirdPartyNotices).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{ResourceName}' is missing. Run Scripts/Generate-ThirdPartyNotices.ps1 and rebuild.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().TrimEnd();
    }
}
