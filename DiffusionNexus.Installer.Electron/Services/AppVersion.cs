using System.Reflection;

namespace DiffusionNexus.Installer.Electron.Services;

/// <summary>
/// The version this build reports, in the one form a user should ever see it.
///
/// One accessor rather than one per screen: the top bar, the feedback report and the updater page
/// had three copies of this, two of which stripped the build-metadata suffix and one of which did
/// not. A CI build stamped "3.1.0+9f6dc2f" therefore read "v3.1.0" in the bar and
/// "Version 3.1.0+9f6dc2f" on /updates, one click apart.
/// </summary>
public static class AppVersion
{
    /// <summary>
    /// InformationalVersion carries a "+&lt;commit sha&gt;" suffix in CI builds. That is build
    /// metadata; the user wants a version number.
    /// </summary>
    public static string Display
    {
        get
        {
            var raw = typeof(AppVersion).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? "unknown";
            var plus = raw.IndexOf('+');
            return plus < 0 ? raw : raw[..plus];
        }
    }
}
