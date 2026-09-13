// Horizontal paging for the welcome screen's software strip.
//
// The strip is an ordinary scroll container, so a wheel, a trackpad, a drag and the keyboard
// already move it; this module exists so the < and > buttons move it too, and so those buttons can
// tell whether there is anything left to move towards.
//
// `observe` is the only source of that answer, deliberately. Reading the position once per button
// click cannot work: `scrollTo` with smooth behaviour returns long before the scroll lands, a
// wheel moves the strip without any click at all, and a resized window changes how much of it fits
// without moving it. All three leave a button claiming something the strip has stopped agreeing
// with. Watching the element reports every one of them from the same place.

/// Reports the strip's edge state to .NET, now and whenever it changes.
/// Returns a handle whose dispose() detaches the listeners.
export function observe(track, owner) {
    if (!track) return { dispose() { } };

    let last = null;

    const report = () => {
        const next = edges(track);
        // Only on a real change. A smooth scroll fires `scroll` dozens of times and the edge state
        // turns over at most twice in the whole of it; without this every frame is a round trip
        // across the circuit.
        if (last && last.atStart === next.atStart && last.atEnd === next.atEnd) return;
        last = next;
        owner.invokeMethodAsync('OnEdgesChanged', next);
    };

    // Open at the first tile. The strip has no memory worth restoring -- it is rebuilt on every
    // visit to this screen -- and the packaged app was found opening it scrolled to the far end
    // with the first tile sliced in half. `auto`, not smooth: this is where the strip starts, not
    // somewhere it travels to.
    track.scrollTo({ left: 0, behavior: 'auto' });

    track.addEventListener('scroll', report, { passive: true });

    // Catches a resized window and a changed tile count alike: both change how much of the strip
    // fits without scrolling it, so neither fires `scroll`. Observing fires once immediately,
    // which is also the initial report.
    const resize = new ResizeObserver(report);
    resize.observe(track);

    return {
        dispose() {
            track.removeEventListener('scroll', report);
            resize.disconnect();
        }
    };
}

/// Scrolls the strip one page in `direction` (-1 back, 1 forward). The observer reports where it
/// lands, so this returns nothing.
export function step(track, direction) {
    if (!track) return;

    // A page is most of the visible width, keeping one partly-visible tile on screen as an anchor.
    // The floor matters on a narrow window, where 80% of the width can be less than a single tile.
    const page = Math.max(track.clientWidth * 0.8, 200);
    const furthest = Math.max(0, track.scrollWidth - track.clientWidth);

    track.scrollTo({
        left: Math.min(furthest, Math.max(0, track.scrollLeft + direction * page)),
        behavior: 'smooth'
    });
}

function edges(track) {
    const furthest = Math.max(0, track.scrollWidth - track.clientWidth);
    // A pixel of tolerance: fractional layout widths mean scrollLeft rarely lands exactly on the
    // maximum, and an exact comparison would leave the forward button enabled at the far end.
    return { atStart: track.scrollLeft <= 1, atEnd: track.scrollLeft >= furthest - 1 };
}
