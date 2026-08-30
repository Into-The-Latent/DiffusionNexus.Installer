namespace DiffusionNexus.Installer.Core.Host;

/// <summary>
/// Bridges a pipeline call that must block on a human answer to a UI prompt.
/// Singleton, for the same reason the install session is: the pipeline outlives any component.
/// <para>
/// One prompt at a time. A second <see cref="ConfirmAsync"/> raised while one is still unanswered
/// throws rather than replacing it — silently overwriting would strand the first caller awaiting a
/// result nobody will ever produce.
/// </para>
/// </summary>
public sealed class ModalPromptService : IUserPrompt
{
    private readonly Lock _gate = new();
    private Pending? _pending;

    private sealed class Pending
    {
        public required TaskCompletionSource<bool> Completion { get; init; }
        public CancellationTokenRegistration Registration { get; set; }
    }

    public string Title { get; private set; } = string.Empty;
    public string Message { get; private set; } = string.Empty;
    public string ConfirmLabel { get; private set; } = "Continue";
    public string CancelLabel { get; private set; } = "Cancel";

    public bool IsOpen { get { lock (_gate) return _pending is not null; } }

    public event Action? Changed;

    public Task<bool> ConfirmAsync(
        string title,
        string message,
        string confirmLabel = "Continue",
        string cancelLabel = "Cancel",
        CancellationToken ct = default)
    {
        var pending = new Pending
        {
            Completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
        };

        lock (_gate)
        {
            if (_pending is not null)
                throw new InvalidOperationException(
                    "A prompt is already awaiting an answer. The wizard shows one prompt at a time.");

            Title = title;
            Message = message;
            ConfirmLabel = confirmLabel;
            CancelLabel = cancelLabel;
            _pending = pending;
        }

        // Keyed to THIS prompt, and disposed when it completes. A registration that outlived its own
        // prompt would fire later and answer someone else's -- a cancelled install silently
        // declining an unrelated dialog the user never touched.
        pending.Registration = ct.Register(() => Complete(pending, false));

        Changed?.Invoke();
        return pending.Completion.Task;
    }

    /// <summary>Answers the prompt currently on screen. No-op when nothing is pending.</summary>
    public void Answer(bool confirmed)
    {
        Pending? pending;
        lock (_gate) pending = _pending;

        if (pending is not null) Complete(pending, confirmed);
    }

    private void Complete(Pending pending, bool confirmed)
    {
        lock (_gate)
        {
            // Only the prompt still on screen may be completed by this call.
            if (!ReferenceEquals(_pending, pending)) return;
            _pending = null;
        }

        pending.Registration.Dispose();
        pending.Completion.TrySetResult(confirmed);
        Changed?.Invoke();
    }
}
