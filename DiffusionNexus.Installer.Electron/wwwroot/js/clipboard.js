// Puts text on the system clipboard for the Install screen's "Copy log" button.
//
// navigator.clipboard is the right tool and is what the Electron renderer offers (the app is served
// from localhost, which counts as a secure context). It is async and can be refused -- a browser
// tab without focus, a policy that blocks it -- so the older hidden-textarea + execCommand('copy')
// route stays as the fallback rather than leaving the user with a button that did nothing.

/// Copies `text`. Rejects when neither route managed it, so .NET can say so on the screen.
export async function copyText(text) {
    if (navigator.clipboard && window.isSecureContext) {
        try {
            await navigator.clipboard.writeText(text);
            return;
        } catch {
            // Fall through to the legacy route below.
        }
    }

    const area = document.createElement('textarea');
    area.value = text;
    area.setAttribute('readonly', '');
    area.style.position = 'fixed';
    area.style.top = '0';
    area.style.left = '0';
    area.style.opacity = '0';
    document.body.appendChild(area);
    area.select();

    try {
        if (!document.execCommand('copy')) throw new Error('The browser refused to copy to the clipboard.');
    } finally {
        area.remove();
    }
}
