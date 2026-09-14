// Keeps the install log showing its newest line.
//
// The log is a fixed-height scroll container that Blazor refills about ten times a second, and a
// scroll container keeps its scrollTop across those updates -- so left alone it sits on the first
// of the 300 lines it holds while the line that matters, the one being written right now, is out
// of sight below. That was survivable when the box was 22rem tall and the page scrolled; it is the
// whole of the left column now.
//
// Watched from here rather than pushed from .NET: scrolling the box after every render would mean
// an interop round trip per log flush, for something the browser can settle on its own.

/// How far from the bottom still counts as being at the bottom. Sub-pixel layout and a partly
/// visible last line mean the distance is rarely exactly zero.
const PinSlack = 24;

/// Pins `box` to its last line until the reader scrolls away from it.
/// Returns a handle whose dispose() detaches the listeners.
export function follow(box) {
    if (!box) return { dispose() { } };

    // Starts pinned, and stays pinned only while the reader is at the bottom. Scrolling up to read
    // something is a deliberate act, and yanking the view back down on the next line would make the
    // log unreadable exactly when someone is trying to read it.
    let pinned = true;

    const onScroll = () => {
        pinned = box.scrollHeight - box.scrollTop - box.clientHeight <= PinSlack;
    };

    const toBottom = () => {
        if (pinned) box.scrollTop = box.scrollHeight;
    };

    box.addEventListener('scroll', onScroll, { passive: true });

    // characterData with subtree, not childList alone: Blazor rewrites the single text node inside
    // the <pre> rather than replacing children, so a childList-only observer never fires.
    const observer = new MutationObserver(toBottom);
    observer.observe(box, { childList: true, characterData: true, subtree: true });

    toBottom();

    return {
        dispose() {
            observer.disconnect();
            box.removeEventListener('scroll', onScroll);
        }
    };
}
