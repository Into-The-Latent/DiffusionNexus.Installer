using DiffusionNexus.Installer.SDK.Shared.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiffusionNexus.Installer.Core.Announcements;

/// <summary>
/// The process-wide copy of the operator announcements (issue #12). Every screen carries the
/// banner through <c>ScreenShell</c>, so without this each navigation would hit the Gist again;
/// with it the fetch runs once, on the first screen, and later screens read whatever it produced.
///
/// <see cref="Messages"/> never waits: it is empty until the fetch lands, and forever when the
/// fetch fails. An announcement is an extra, so "nothing to show" is the right first paint and
/// the right answer to every failure.
/// </summary>
public sealed class ServerMessageCache
{
    /// <summary>
    /// The id this app reads the shared document as: a message reaches the installer when its
    /// <see cref="ServerMessage.Targets"/> is empty or names this. The same id the 1.x installer
    /// uses, on purpose -- one row in the Gist addresses both.
    /// </summary>
    public const string AppId = "installer";

    private readonly IServerMessageService _service;
    private readonly DismissedMessageStore _dismissed;
    private readonly ILogger<ServerMessageCache> _logger;
    private readonly Lazy<Task> _load;
    private readonly Lock _gate = new();

    private volatile IReadOnlyList<ServerMessage> _messages = [];
    private volatile ServerMessageResult? _result;

    public ServerMessageCache(
        IServerMessageService service,
        DismissedMessageStore dismissed,
        ILogger<ServerMessageCache>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(dismissed);
        _service = service;
        _dismissed = dismissed;
        _logger = logger ?? NullLogger<ServerMessageCache>.Instance;
        _load = new Lazy<Task>(LoadCoreAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>What to show right now, in the document's order. Shrinks as the user dismisses.</summary>
    public IReadOnlyList<ServerMessage> Messages => _messages;

    /// <summary>What the fetch produced, or null while it is still running / never started.</summary>
    public ServerMessageResult? Result => _result;

    /// <summary>
    /// Raised after <see cref="Messages"/> changed: once when the fetch lands (on the thread that
    /// completed it) and once per dismissal (on the caller's thread). UI subscribers must marshal
    /// to their own dispatcher. A subscriber that throws is logged and skipped; it neither faults
    /// the load nor starves the subscribers after it.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Starts the fetch if nothing has yet, and returns the one shared task. Safe to call from
    /// every banner render: only the first call does anything. The task never faults.
    /// </summary>
    public Task LoadAsync() => _load.Value;

    /// <summary>
    /// Hides a dismissible message now and remembers the id for later launches. Remembering is
    /// best-effort: a dismissal that cannot be written still hides the message for this run,
    /// because a banner that ignores its own X over a disk problem is the worse failure. Unknown
    /// ids and non-dismissible messages are ignored -- the latter so a mandatory notice cannot be
    /// silenced by a markup slip in the banner.
    /// </summary>
    public void Dismiss(string id)
    {
        lock (_gate)
        {
            var current = _messages;
            if (!current.Any(m => m.Dismissible && string.Equals(m.Id, id, StringComparison.Ordinal)))
            {
                return;
            }

            _messages = current.Where(m => !string.Equals(m.Id, id, StringComparison.Ordinal)).ToArray();
        }

        try
        {
            _dismissed.Add(id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Announcements: could not remember that '{Id}' was dismissed; it will show again next launch.", id);
        }

        RaiseChanged();
    }

    private async Task LoadCoreAsync()
    {
        ServerMessageResult result;
        try
        {
            var dismissed = _dismissed.Load();
            result = await _service.GetMessagesAsync(AppId, dismissed, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The SDK service already swallows everything but caller cancellation, and we never
            // cancel. Belt and braces: a banner must not take a screen down.
            result = new ServerMessageResult([], $"Announcements fetch threw: {ex.Message}");
        }

        // The only place the outcome is recorded. Without this, a renamed Gist file means no
        // installer ever shows an announcement again and nothing anywhere says why.
        if (result.ErrorMessage is not null)
        {
            _logger.LogWarning("Announcements: nothing to show. {Reason}", result.ErrorMessage);
        }
        else
        {
            _logger.LogDebug("Announcements: {Count} applicable message(s) loaded.", result.Messages.Count);
        }

        if (result.SkippedMessages > 0)
        {
            _logger.LogWarning(
                "Announcements: {Skipped} row(s) in the remote document could not be read and were skipped.",
                result.SkippedMessages);
        }

        lock (_gate)
        {
            _messages = result.Messages;
            _result = result;
        }

        RaiseChanged();
    }

    private void RaiseChanged()
    {
        // Per handler, so one throwing banner cannot fault the cached task or hide the
        // notification from the banners after it in the invocation list.
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "An announcements subscriber threw and was skipped.");
            }
        }
    }
}
