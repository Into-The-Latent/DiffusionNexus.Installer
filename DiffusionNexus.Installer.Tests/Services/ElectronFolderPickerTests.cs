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
    [InlineData(@"\\.\C:\Windows")]
    public void No_usable_path_leaves_the_choice_to_the_dialog(string? startIn)
    {
        ElectronFolderPicker.StartingFolder(startIn).Should().BeNull();
    }

    // The boxes hold the raw text and the wizard cleans it wherever it acts on it, so what it shows as
    // "Will be created" must also be where Browse starts. Explorer's "Copy as path" adds the quotes.
    [Theory]
    [InlineData("\"{0}\"")]
    [InlineData("  {0}  ")]
    [InlineData("{0}\\")]
    public void A_pasted_path_is_read_the_way_the_wizard_reads_it(string pasted)
    {
        var target = Path.Combine(_root, "new-install");

        ElectronFolderPicker.StartingFolder(string.Format(pasted, target)).Should().Be(_root);
    }

    // Windows' shell refuses a path with forward slashes (E_INVALIDARG, checked), and the dialog then
    // ignores it. The wizard accepts one, and only the walk up used to normalize it.
    [Fact]
    public void An_existing_folder_typed_with_forward_slashes_reaches_the_dialog_with_backslashes()
    {
        ElectronFolderPicker.StartingFolder(_root.Replace('\\', '/')).Should().Be(_root);
    }

    [Fact]
    public async Task A_disk_that_does_not_answer_leaves_the_choice_to_the_dialog()
    {
        using var unreachable = new ManualResetEventSlim();

        var folder = await ElectronFolderPicker.StartingFolderWithinAsync(TimeSpan.FromMilliseconds(50), @"\\nas\share\AI",
            _ => { unreachable.Wait(); return true; });
        unreachable.Set();

        folder.Should().BeNull();
    }

    [Fact]
    public async Task A_disk_that_answers_in_time_gives_the_starting_folder()
    {
        var target = Path.Combine(_root, "new-install");

        var folder = await ElectronFolderPicker.StartingFolderWithinAsync(TimeSpan.FromSeconds(30), target);

        folder.Should().Be(_root);
    }
}
