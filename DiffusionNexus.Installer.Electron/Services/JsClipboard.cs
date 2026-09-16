using DiffusionNexus.Installer.Core.Host;
using Microsoft.JSInterop;

namespace DiffusionNexus.Installer.Electron.Services;

/// <summary>
/// The clipboard through the renderer's own <c>navigator.clipboard</c>, via wwwroot/js/clipboard.js.
/// Not through ElectronNET: the renderer is the one place that has a clipboard both inside the
/// Electron shell and under a plain <c>dotnet run</c> in a browser, so the button works in the
/// configuration UI work is done in as well as the one users get.
/// </summary>
/// <remarks>
/// Scoped, not singleton: <see cref="IJSRuntime"/> belongs to one Blazor circuit, and a singleton
/// holding the first circuit's runtime would write to a dead connection after a reconnect.
/// </remarks>
public sealed class JsClipboard(IJSRuntime js) : IClipboard
{
    public bool IsAvailable => true;

    public async Task SetTextAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        await using var module = await js.InvokeAsync<IJSObjectReference>("import", "./js/clipboard.js");
        await module.InvokeVoidAsync("copyText", text);
    }
}
