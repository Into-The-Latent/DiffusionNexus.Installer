using DiffusionNexus.Installer.Core.Host;

namespace DiffusionNexus.Installer.Electron.Services;

/// <summary>
/// Bridges a pipeline call that must block on a human answer to a Blazor modal.
/// Singleton, for the same reason the install session is: the pipeline outlives any component.
/// </summary>
public sealed class ModalPromptService : IUserPrompt
{
    private TaskCompletionSource<bool>? _pending;

    public string Title { get; private set; } = string.Empty;
    public string Message { get; private set; } = string.Empty;
    public string ConfirmLabel { get; private set; } = "Continue";
    public string CancelLabel { get; private set; } = "Cancel";
    public bool IsOpen => _pending is not null;

    public event Action? Changed;

    public Task<bool> ConfirmAsync(
        string title,
        string message,
        string confirmLabel = "Continue",
        string cancelLabel = "Cancel",
        CancellationToken ct = default)
    {
        Title = title;
        Message = message;
        ConfirmLabel = confirmLabel;
        CancelLabel = cancelLabel;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = tcs;
        Changed?.Invoke();

        // A cancelled install must not leave the pipeline awaiting an answer nobody will give.
        ct.Register(() => Answer(false));
        return tcs.Task;
    }

    public void Answer(bool confirmed)
    {
        var pending = _pending;
        if (pending is null) return;

        _pending = null;
        pending.TrySetResult(confirmed);
        Changed?.Invoke();
    }
}
