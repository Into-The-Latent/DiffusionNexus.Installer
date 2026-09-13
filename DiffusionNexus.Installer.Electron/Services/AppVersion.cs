using System.Reflection;

namespace DiffusionNexus.Installer.Electron.Services;

/// <summary>
/// The version this build reports, in the one form a user should ever see it.
///
/// One accessor rather than one per screen: the top bar, the feedback report and the updater page
/// all show the version, and three copies of the same two lines is how they come to disagree.
/// </summary>
public static class AppVersion
{
    // Resolved once. The attribute cannot change for the life of the process, and this is read on
    // every TopBar render and on every feedback report.
    private static readonly string _display = Strip(typeof(AppVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>The version to show a user. Never the raw informational version.</summary>
    public static string Display => _display;

    /// <summary>
    /// Drops the "+&lt;build metadata&gt;" suffix an InformationalVersion can carry.
    ///
    /// DEFENCE, not a repair: Directory.Build.props sets
    /// IncludeSourceRevisionInInformationalVersion=false, so no build of THIS repo produces a
    /// suffix to strip, and no screen has ever shown one. It stays so that a build which loses
    /// that property -- a new project added without it, a packaging change -- degrades to a clean
    /// version number instead of putting a commit hash in front of a user. Exposed rather than
    /// inlined so the stripping can be tested against a value that actually has a suffix; a test
    /// against this build's own version proves nothing.
    /// </summary>
    public static string Strip(string? informationalVersion)
    {
        var raw = informationalVersion ?? "unknown";
        var plus = raw.IndexOf('+');
        return plus < 0 ? raw : raw[..plus];
    }
}
