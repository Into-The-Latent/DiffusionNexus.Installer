using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.SDK.Models.Installation;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Modules;

public class UserModelFolderMapTests
{
    [Fact]
    public void Every_folder_type_has_a_label_and_a_standard_name_equal_to_its_key()
    {
        UserModelFolderMap.FolderTypes.Should().HaveCount(21);
        UserModelFolderMap.FolderTypes.Should().OnlyContain(t => t.Standard == t.Key && !string.IsNullOrWhiteSpace(t.Label));
        UserModelFolderMap.FolderTypes.Select(t => t.Key).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Apply_writes_the_current_field_and_the_legacy_duplicate()
    {
        var settings = new UserSettings { DefaultLorasFolder = "old", DefaultLoraFolder = "old" };

        UserModelFolderMap.Apply(settings, new Dictionary<string, string>
        {
            ["loras"] = "MyLoras",
            ["vae"] = "VAEs",
        });

        settings.DefaultLorasFolder.Should().Be("MyLoras");
        settings.DefaultLoraFolder.Should().Be("MyLoras");
        settings.DefaultVAEFolder.Should().Be("VAEs");
    }

    [Fact]
    public void Apply_blanks_a_type_that_is_missing_from_the_values()
    {
        // Reset-to-standard must actually clear a previously saved custom name.
        var settings = new UserSettings { DefaultLorasFolder = "Lora", DefaultLoraFolder = "Lora" };

        UserModelFolderMap.Apply(settings, new Dictionary<string, string>());

        settings.DefaultLorasFolder.Should().BeEmpty();
        settings.DefaultLoraFolder.Should().BeEmpty();
    }

    [Fact]
    public void Build_round_trips_what_Apply_wrote()
    {
        var settings = new UserSettings();
        var values = new Dictionary<string, string> { ["checkpoints"] = "Ckpt", ["text_encoders"] = "TE" };

        UserModelFolderMap.Apply(settings, values);

        UserModelFolderMap.Build(settings).Should().Equal(values);
    }
}
