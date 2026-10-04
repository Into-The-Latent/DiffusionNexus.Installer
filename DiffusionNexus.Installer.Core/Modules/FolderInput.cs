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
    /// none of them is a folder. A network path needs a server and a share.
    /// </summary>
    public static bool IsFullPath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) return false;
        if (!IsSeparator(path[0]) || !IsSeparator(path[1])) return true;
        return path.Split(Separators, StringSplitOptions.RemoveEmptyEntries).Length >= 2;
    }

    /// <summary>
    /// Whether a folder name in <paramref name="path"/> holds a character Windows refuses
    /// (&lt; &gt; : " | ? * and control characters). Judged per name after the root, so a colon
    /// is caught wherever the root ends and "\\.\D:\Renders" is not refused for its drive. Without
    /// it "D:\Renders|old" passes as a full path, and an unquoted | or &gt; on the launcher line
    /// pipes or redirects ComfyUI instead of naming a folder.
    /// </summary>
    public static bool HasInvalidName(string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;
        return path[root.Length..]
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .Any(name => name.IndexOfAny(InvalidNameChars) >= 0);
    }

    public const string InvalidNameMessage =
        "A Windows folder name cannot contain < > : \" | ? or *. Choose a folder without them.";

    private static readonly char[] Separators = ['\\', '/'];
    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

    private static bool IsSeparator(char c) => c is '\\' or '/';
}
