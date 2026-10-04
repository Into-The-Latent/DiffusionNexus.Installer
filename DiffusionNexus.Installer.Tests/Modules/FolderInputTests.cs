using DiffusionNexus.Installer.Core.Modules;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Modules;

/// <summary>What every folder box on the Location page does with what was typed.</summary>
public class FolderInputTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("\"\"", null)]
    [InlineData(@" ""D:\My Output\"" ", @"D:\My Output")]
    [InlineData(@"D:\Out//", @"D:\Out")]
    [InlineData(@"D:\", @"D:\")]
    public void Clean_drops_spaces_quotes_and_trailing_separators(string? typed, string? expected)
        => FolderInput.Clean(typed).Should().Be(expected);

    [Theory]
    [InlineData(@"D:\Renders", true)]
    [InlineData(@"\\nas\share", true)]
    [InlineData(@"\\nas\share\Renders", true)]
    [InlineData(@"\\", false)]
    [InlineData(@"\\nas", false)]
    [InlineData(@"//nas", false)]
    [InlineData("Renders", false)]
    [InlineData("D:Renders", false)]
    [InlineData(@"\\.\D:\Renders", false)]
    [InlineData(@"\\?\D:\Renders", false)]
    [InlineData(@"\\?\UNC\nas\share", false)]
    public void A_network_path_needs_a_server_and_a_share_and_device_paths_are_not_folders(string path, bool full)
    {
        // IsPathFullyQualified alone says yes to "\\nas": ComfyUI cannot write to a bare server.
        // \\.\ and \\?\ paths come from no picker or Explorer, and slipped past the
        // inside-the-install check, which compares plain paths.
        FolderInput.IsFullPath(path).Should().Be(full);
    }

    [Theory]
    [InlineData(@"D:\Renders|old", true)]
    [InlineData(@"D:\Out:Stream", true)]
    [InlineData(@"D:\What?", true)]
    [InlineData("D:\\Tab\tOut", true)]
    [InlineData(@"\\nas\share\a<b", true)]
    [InlineData(@"\\nas\Renders|old", true)]
    [InlineData(@"\\nas\out>x\sub", true)]
    [InlineData(@"\\n|as\share", true)]
    [InlineData(@"D:\Renders", false)]
    [InlineData(@"D:\", false)]
    [InlineData(@"\\nas\share\Renders", false)]
    public void Every_name_is_judged_server_and_share_included(string path, bool invalid)
    {
        // GetPathRoot of a UNC path takes in server and share -- "\\nas\Renders|old" is all
        // root -- so judging only what follows it let | and > through to the launcher line.
        FolderInput.HasInvalidName(path).Should().Be(invalid);
    }
}
