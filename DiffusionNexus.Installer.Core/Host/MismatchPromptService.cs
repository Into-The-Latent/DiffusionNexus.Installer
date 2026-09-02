using DiffusionNexus.Installer.SDK.Services.Installation.Utilities;

namespace DiffusionNexus.Installer.Core.Host;

/// <summary>
/// State behind the mismatch modal, shaped like <see cref="ModalPromptService"/>: singleton, one
/// prompt at a time, completed by the modal's Answer or by the caller's token.
/// </summary>
public sealed class MismatchPromptService : IMismatchedFilePrompt
{
    private readonly Lock _gate = new();
    private Pending? _pending;

    private sealed class Pending
    {
        public required TaskCompletionSource<MismatchResolution?> Completion { get; init; }
        public CancellationTokenRegistration Registration { get; set; }
    }

    public IReadOnlyList<ExistingModelMismatch> Mismatches { get; private set; } = [];

    public bool IsOpen { get { lock (_gate) return _pending is not null; } }

    public event Action? Changed;

    public Task<MismatchResolution?> ResolveAsync(IReadOnlyList<ExistingModelMismatch> mismatches, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mismatches);

        var pending = new Pending
        {
            Completion = new TaskCompletionSource<MismatchResolution?>(TaskCreationOptions.RunContinuationsAsynchronously),
        };

        lock (_gate)
        {
            if (_pending is not null)
                throw new InvalidOperationException("A mismatch dialog is already awaiting an answer.");

            Mismatches = mismatches;
            _pending = pending;
        }

        // Keyed to THIS prompt and disposed with it, so a stale registration can never answer a later one.
        pending.Registration = ct.Register(() => Complete(pending, null));

        Changed?.Invoke();
        return pending.Completion.Task;
    }

    /// <summary>Answers the dialog on screen; null is a dismissal. No-op when nothing is pending.</summary>
    public void Answer(MismatchResolution? resolution)
    {
        Pending? pending;
        lock (_gate) pending = _pending;

        if (pending is not null) Complete(pending, resolution);
    }

    private void Complete(Pending pending, MismatchResolution? resolution)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_pending, pending)) return;
            _pending = null;
            Mismatches = [];
        }

        pending.Registration.Dispose();
        pending.Completion.TrySetResult(resolution);
        Changed?.Invoke();
    }
}
