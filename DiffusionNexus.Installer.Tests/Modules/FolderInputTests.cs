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
    [InlineData(@"\\.\D:\Renders", true)]
    [InlineData(@"\\", false)]
    [InlineData(@"\\nas", false)]
    [InlineData(@"//nas", false)]
    [InlineData("Renders", false)]
    [InlineData("D:Renders", false)]
    public void A_network_path_needs_a_server_and_a_share(string path, bool full)
    {
        // IsPathFullyQualified alone says yes to "\\nas": ComfyUI cannot write to a bare server.
        FolderInput.IsFullPath(path).Should().Be(full);
    }

    [Theory]
    [InlineData(@"D:\Renders|old", true)]
    [InlineData(@"D:\Out:Stream", true)]
    [InlineData(@"D:\What?", true)]
    [InlineData("D:\\Tab\tOut", true)]
    [InlineData(@"\\nas\share\a<b", true)]
    [InlineData(@"D:\Renders", false)]
    [InlineData(@"\\.\D:\Renders", false)]
    [InlineData(@"\\nas\share\Renders", false)]
    public void Invalid_names_are_judged_after_the_root(string path, bool invalid)
    {
        // The device path's "D:" is its root, not a folder name with a colon in it.
        FolderInput.HasInvalidName(path).Should().Be(invalid);
    }
}
