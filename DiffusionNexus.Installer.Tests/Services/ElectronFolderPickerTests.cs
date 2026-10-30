using DiffusionNexus.Installer.Electron.Services;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Services;

/// <summary>
/// Since Electron 43 a folder dialog without a defaultPath opens in Downloads instead of where
/// Windows last left it (issue #57). The install folder usually does not exist yet, so the picker
/// starts at the nearest folder of that path that does.
/// </summary>
public sealed class ElectronFolderPickerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dn-folder-picker-" + Guid.NewGuid().ToString("N"));

    public ElectronFolderPickerTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void An_existing_folder_is_the_starting_folder()
    {
        ElectronFolderPicker.StartingFolder(_root).Should().Be(_root);
    }

    [Fact]
    public void A_folder_that_does_not_exist_yet_starts_at_its_nearest_existing_parent()
    {
        var target = Path.Combine(_root, "ComfyUI", "new-install");

        ElectronFolderPicker.StartingFolder(target).Should().Be(_root);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative\\folder")]
    public void No_usable_path_leaves_the_choice_to_the_dialog(string? startIn)
    {
        ElectronFolderPicker.StartingFolder(startIn).Should().BeNull();
    }
}
