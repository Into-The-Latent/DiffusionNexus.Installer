using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

/// <summary>
/// The stylesheet is hand-edited and merged like code but never compiled. A conflict resolution
/// once dropped a closing brace, which silently swallowed the next rule (the licences text box
/// lost its scrolling) while every component test stayed green. This is the compiler it lacks.
/// </summary>
public class StylesheetTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DiffusionNexus.Installer.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found above the test output folder.");
    }

    [Fact]
    public void app_css_has_balanced_braces_outside_comments_and_strings()
    {
        var path = Path.Combine(RepoRoot(), "DiffusionNexus.Installer.Electron", "wwwroot", "app.css");
        var css = File.ReadAllText(path);
        css = Regex.Replace(css, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        css = Regex.Replace(css, "\"(?:[^\"\\\\]|\\\\.)*\"|'(?:[^'\\\\]|\\\\.)*'", string.Empty);

        var depth = 0;
        var line = 1;
        foreach (var ch in css)
        {
            if (ch == '\n') line++;
            if (ch == '{') depth++;
            if (ch == '}') depth--;
            depth.Should().BeGreaterThanOrEqualTo(0, $"a '}}' without an opener appears around line {line}");
            depth.Should().BeLessThanOrEqualTo(2, $"blocks nest deeper than @media > rule around line {line}, which means a '}}' is missing above");
        }

        depth.Should().Be(0, "every block that is opened must be closed");
    }
}
