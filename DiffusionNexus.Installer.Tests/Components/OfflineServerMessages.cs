using DiffusionNexus.Installer.Core.Announcements;
using DiffusionNexus.Installer.SDK.Shared.Services;

namespace DiffusionNexus.Installer.Tests.Components;

/// <summary>
/// The announcements source a fixture registers when the banner is incidental to what it tests:
/// answers immediately with nothing to show, never touches the network or the user's real
/// dismissal file.
/// </summary>
public sealed class OfflineServerMessages : IServerMessageService
{
    private readonly IReadOnlyList<ServerMessage> _messages;

    public OfflineServerMessages(params ServerMessage[] messages) => _messages = messages;

    public Task<ServerMessageResult> GetMessagesAsync(
        string appId,
        IReadOnlySet<string>? dismissedIds = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ServerMessageResult(_messages));

    /// <summary>A cache over this source, ready to drop into a bUnit container.</summary>
    public static ServerMessageCache Cache(params ServerMessage[] messages) =>
        new(new OfflineServerMessages(messages), new DismissedMessageStore(ScratchDismissalFile()));

    /// <summary>A path nothing else uses; the file only comes into being if a test dismisses.</summary>
    public static string ScratchDismissalFile() =>
        Path.Combine(Path.GetTempPath(), "dn-installer-tests", Guid.NewGuid().ToString("N"), "dismissed_messages.json");
}
