namespace DiffusionNexus.Installer.Core.Install;

/// <summary>
/// An <see cref="IProgress{T}"/> that invokes its handler inline, on the reporting thread.
/// <para>
/// <see cref="Progress{T}"/> is deliberately NOT used: it hops through the captured
/// SynchronizationContext, or the thread pool when there is none, so the session's state would lag
/// the reports it was given and a caller that awaited the install could observe a half-filled log.
/// The session does its own coalescing and marshals to the UI itself, so it wants the callback
/// inline and synchronous.
/// </para>
/// </summary>
internal sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
