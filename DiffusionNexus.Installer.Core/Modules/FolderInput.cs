namespace DiffusionNexus.Installer.Core.Modules;

/// <summary>
/// What the folder boxes on the Location page do with what was typed. Shared so the three boxes
/// agree: the output and library folders are checked against the install folder, and a pasted
/// path cleaned in two of them but not the third made that check compare unlike things.
/// </summary>
public static class FolderInput
{
    /// <summary>
    /// The folder as typed, minus what the user did not mean: surrounding spaces, the quotes
    /// Explorer's "Copy as path" adds, and trailing separators. Null when nothing is left -- null
    /// included, since a settings file can hold "OutputFolder": null. A pasted " D:\Out" is not a
    /// rooted path, so an untrimmed value would send ComfyUI's output under its working directory;
    /// and "D:\My Output\" is quoted by the launcher as "D:\My Output\", where \" is a literal quote
    /// in argv. A drive root keeps its separator ("D:" alone is relative).
    /// </summary>
    public static string? Clean(string? folder)
    {
        var path = (folder ?? string.Empty).Trim();
        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"') path = path[1..^1].Trim();
        if (path.Length == 0) return null;

        string trimmed;
        while ((trimmed = Path.TrimEndingDirectorySeparator(path)) != path) path = trimmed;
        return path;
    }

    /// <summary>
    /// Whether <paramref name="path"/> names a folder by its full path. IsPathFullyQualified alone
    /// accepts anything that starts with two separators: "\\", "\\nas" and "\\nas\" all pass, and
    /// none of them is a folder. A network path needs a server and a share. Device paths
    /// (\\.\D:\..., \\?\D:\...) are refused: no picker or Explorer produces them, and the
    /// inside-the-install check compares plain paths, so \\.\E:\Installer\9\ComfyUI\output
    /// slipped past it.
    /// </summary>
    public static bool IsFullPath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) return false;
        if (!IsSeparator(path[0]) || !IsSeparator(path[1])) return true;
        if (path.Length > 2 && path[2] is '.' or '?' && (path.Length == 3 || IsSeparator(path[3]))) return false;
        return path.Split(Separators, StringSplitOptions.RemoveEmptyEntries).Length >= 2;
    }

    /// <summary>
    /// Whether a name in <paramref name="path"/> holds a character Windows refuses (&lt; &gt; : "
    /// | ? * and control characters). Every name is judged, a UNC server and share included --
    /// GetPathRoot takes those in, so "\\nas\Renders|old" is all root -- and only a leading drive
    /// ("D:") is skipped, so a colon anywhere else is caught. Without it "D:\Renders|old" passes
    /// as a full path, and an unquoted | or &gt; on the launcher line pipes or redirects ComfyUI
    /// instead of naming a folder. Meant for paths <see cref="IsFullPath"/> accepts.
    /// </summary>
    public static bool HasInvalidName(string path)
    {
        var names = path.Length >= 2 && path[1] == ':' ? path[2..] : path;
        return names
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .Any(name => name.IndexOfAny(InvalidNameChars) >= 0);
    }

    /// <summary>
    /// The checks every folder box runs on the cleaned folder, in order: a full path, then names
    /// Windows accepts. Null when both pass. One place, so a rule added here reaches all three
    /// boxes; each box adds only what its own carrier cannot hold.
    /// </summary>
    public static string? ShapeProblem(string folder, string notFullMessage) =>
        !IsFullPath(folder) ? notFullMessage
        : HasInvalidName(folder) ? InvalidNameMessage
        : null;

    /// <summary>
    /// Why <paramref name="folder"/> cannot be the install folder, judged on its text alone, or
    /// null. Shared by the install box and by everything that derives folders from it, so a
    /// refused install folder never shows up as "Will be created" or as a default folder.
    /// </summary>
    public static string? InstallFolderProblem(string folder) =>
        ShapeProblem(folder, @"Enter the full path of the install folder, for example D:\AI.")
        ?? (BreaksLauncher(folder, quoted: true)
            ? "The start scripts cannot work in a folder whose path contains ! % or ^. Choose a folder without them."
            : null);

    /// <summary>
    /// Whether the generated run_nvidia.bat (SDK BatchScriptGenerator) mangles a path holding
    /// these characters. It runs setlocal enabledelayedexpansion, and run in real cmd: the install
    /// folder, quoted in call "%~dp0venv\...", breaks on ! % ^ (CALL expands % a second time and
    /// doubles ^, delayed expansion drops !), while &amp; and spaces are fine. The output folder is
    /// written unquoted unless it holds a space, so there an &amp; ends the command as well.
    /// Refused until the SDK's launcher quotes and escapes properly.
    /// </summary>
    public static bool BreaksLauncher(string folder, bool quoted) =>
        folder.IndexOfAny(quoted ? QuotedLauncherBreakers : UnquotedLauncherBreakers) >= 0;

    private static readonly char[] QuotedLauncherBreakers = ['!', '%', '^'];
    private static readonly char[] UnquotedLauncherBreakers = ['&', '!', '%', '^'];

    public const string InvalidNameMessage =
        "A Windows folder name cannot contain < > : \" | ? or *. Choose a folder without them.";

    private static readonly char[] Separators = ['\\', '/'];
    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

    private static bool IsSeparator(char c) => c is '\\' or '/';
}
