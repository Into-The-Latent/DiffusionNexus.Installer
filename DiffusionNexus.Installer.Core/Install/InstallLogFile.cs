using System.Globalization;
using System.Text;

namespace DiffusionNexus.Installer.Core.Install;

/// <summary>
/// The one text rendering of an install log (issue #14). Two consumers share it so that what a
/// user pastes from the "Copy log" button is what they would find on disk: the file
/// <see cref="InstallSession"/> writes into the install folder when a run ends -- the same
/// <c>installation-log-verbose-&lt;timestamp&gt;.txt</c> the 1.x wizard wrote -- and the clipboard.
/// </summary>
public static class InstallLogFile
{
    public const string Title = "Into the Latent Easy Installer - Installation Log";

    /// <summary>
    /// The 1.x wizard's file name, kept so a user who knows where to look still finds it. Invariant
    /// culture throughout this file: under a Buddhist or Umm al-Qura default calendar (th-TH,
    /// ar-SA) a culture-formatted <c>yyyy</c> is the era year, and support telling a user to look
    /// for the 2026 file finds a 2569 one.
    /// </summary>
    public static string FileName(DateTimeOffset now) =>
        $"installation-log-verbose-{now.ToString("yyyy-MM-dd-HH-mm-ss", CultureInfo.InvariantCulture)}.txt";

    /// <summary>
    /// The lines as the live view shows them, one per row with time and level. When the session's
    /// bounded buffer dropped its oldest lines, that is said up front: a file that merely starts
    /// mid-pip looks complete and is not.
    /// </summary>
    public static string Format(IReadOnlyList<InstallLogLine> lines, int truncatedLines)
    {
        var sb = new StringBuilder();
        AppendLines(sb, lines, truncatedLines);
        return sb.ToString();
    }

    /// <summary>The whole file: a header naming the run, then <see cref="Format"/>.</summary>
    public static string Compose(
        string workload, string installFolder, string outcome, DateTimeOffset now,
        IReadOnlyList<InstallLogLine> lines, int truncatedLines)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Title);
        sb.AppendLine($"Generated: {now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}");
        sb.AppendLine($"Workload: {workload}");
        sb.AppendLine($"Install folder: {installFolder}");
        sb.AppendLine($"Outcome: {outcome}");
        sb.AppendLine(new string('=', 60));
        AppendLines(sb, lines, truncatedLines);
        return sb.ToString();
    }

    /// <summary>
    /// Writes <paramref name="text"/> into <paramref name="installFolder"/> and returns the path,
    /// or null when there is nowhere to write it. An install that died before creating its folder
    /// has no folder; the session must not create one on the user's disk just to leave a log in it,
    /// and a log that cannot be written must never turn a finished run into a failed one -- so
    /// nothing escapes here, exactly as the 1.x wizard did. Every exception, not only the I/O
    /// family: the caller is InstallSession.StartAsync's <c>finally</c>, ahead of the notification
    /// that ends the run on screen, and a path the OS accepted for Directory.Exists but rejects for
    /// a file write (a device name, an odd UNC form) throws ArgumentException or
    /// NotSupportedException -- which would leave the Install screen on "Installing" forever after
    /// an install that succeeded.
    /// </summary>
    public static string? TryWrite(string? installFolder, string text, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(installFolder) || !Directory.Exists(installFolder)) return null;

        try
        {
            var path = Path.Combine(installFolder, FileName(now));
            File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void AppendLines(StringBuilder sb, IReadOnlyList<InstallLogLine> lines, int truncatedLines)
    {
        if (truncatedLines > 0)
            sb.AppendLine($"[{truncatedLines} earlier lines truncated: the installer keeps the newest {InstallSession.MaxLogLines}]");

        foreach (var line in lines)
            sb.AppendLine($"[{line.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)}] [{line.Level}] {line.Message}");
    }
}
