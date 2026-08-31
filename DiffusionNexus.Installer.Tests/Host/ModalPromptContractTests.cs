using DiffusionNexus.Installer.Core.Host;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Host;

public class ModalPromptContractTests
{
    [Fact]
    public async Task A_dismissed_folder_dialog_yields_null_not_an_exception()
    {
        var picker = new Mock<IFolderPicker>();
        picker.Setup(p => p.PickFolderAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var chosen = await picker.Object.PickFolderAsync("Pick a folder");

        chosen.Should().BeNull();
    }

    [Fact]
    public async Task A_declined_prompt_yields_false()
    {
        var prompt = new Mock<IUserPrompt>();
        prompt.Setup(p => p.ConfirmAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var answer = await prompt.Object.ConfirmAsync("Overwrite?", "A shortcut with that name exists.");

        answer.Should().BeFalse();
    }
}
