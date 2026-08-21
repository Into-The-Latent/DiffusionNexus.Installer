namespace DiffusionNexus.Installer.Electron.Services;

/// <summary>
/// Process-wide sink for auto-updater activity.
/// </summary>
/// <remarks>
/// Electron's <c>AutoUpdater</c> is a singleton owned by the main process, and its events fire on
/// Electron's socket thread rather than on a Blazor circuit. Subscribing to it directly from a
/// component leaks: the component cannot detach its handlers again (each lambda is a distinct
/// delegate instance, so <c>-=</c> silently removes nothing), and after a Blazor reconnect the old
/// handlers keep firing against a dead circuit.
///
/// So the Electron events are wired exactly once at startup into this service, and components
/// subscribe to <see cref="Changed"/> — a plain <see cref="Action"/> they CAN unsubscribe.
/// </remarks>
public sealed class UpdaterLog
{
    private readonly List<string> _lines = new();
    private readonly Lock _gate = new();

    /// <summary>Raised after every mutation. Handlers must marshal to their own sync context.</summary>
    public event Action? Changed;

    /// <summary>True once an update has been downloaded and is waiting to be installed.</summary>
    public bool UpdateReady { get; private set; }

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_gate)
            {
                return _lines.ToArray();
            }
        }
    }

    public void Append(string message)
    {
        lock (_gate)
        {
            _lines.Add(message);
        }

        Changed?.Invoke();
    }

    public void MarkUpdateReady()
    {
        UpdateReady = true;
        Changed?.Invoke();
    }
}
