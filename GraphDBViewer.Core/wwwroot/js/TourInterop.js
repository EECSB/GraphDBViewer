//
//Where the guided tour's two pieces go (GuidedTour.razor): the shade over the page, with a hole cut in it where the
//part a step is about shows through, and the card that says what that part is. GDS Viewer's tour.js, brought across
//on 2026-11-03.
//
//**Measured here rather than in C#**, because where something is on screen is the browser's to know and changes
//without telling anybody: a window resized, a panel scrolled. So the tour hands over a selector, and this keeps both
//pieces where that element is for as long as the step lasts - resizes and scrolls included.
//
//**The hole is a clip-path, not a shadow.** The usual spotlight is an element the size of the hole with a shadow
//spread far enough to cover the window - and "far enough" is a number picked for a window nobody has measured. The
//shade is the window's own size whatever that is, and the hole is cut out of it.
//

//The selector of the part being shown, or null for a step about the whole app, whose card sits in the middle.
let gdbvTourTarget = null;

//The tour's component while it is open, which the keys it answers to are passed to.
let gdbvTourHandle = null;

//The gap between the part and its card, and between either and the window's edge, in pixels.
const GDBV_TOUR_GAP = 12;

//The first part a selector names that is on the page and has a size - a part that is hidden has none. The query panel's
//tabs are drawn twice, over its card and in it, and only one of the two shows at a time.
function gdbvTourPart(selector) {
    if (selector == null)
        return null;

    for (const part of document.querySelectorAll(selector)) {
        const box = part.getBoundingClientRect();

        if (box.width !== 0 || box.height !== 0)
            return part;
    }

    return null;
}

//
//Cuts the hole in the shade: everything dimmed but this rectangle. Round the window, then round the hole - the same
//ten points every time, so a change of step slides the one hole to the next rather than swapping them.
//
function gdbvTourCut(shade, left, top, width, height) {
    const right = left + width;
    const bottom = top + height;

    shade.style.clipPath = `polygon(evenodd, 0 0, 100% 0, 100% 100%, 0 100%, 0 0, ${left}px ${top}px, ${right}px ${top}px, ${right}px ${bottom}px, ${left}px ${bottom}px, ${left}px ${top}px)`;
}

//
//Cuts the hole round the part and puts the card beside it - under it when there is room, else over it, else inside.
//The card is only shown from here, so it never appears anywhere it has not been put.
//
function gdbvTourPlaceNow() {
    const shade = document.getElementById('gdbvTourShade');
    const card = document.getElementById('gdbvTourCard');

    if (shade == null || card == null)
        return;

    const part = gdbvTourPart(gdbvTourTarget);
    const width = window.innerWidth;
    const height = window.innerHeight;
    const cardBox = card.getBoundingClientRect();

    card.style.visibility = 'visible';

    if (part == null) {
        const left = Math.max(GDBV_TOUR_GAP, (width - cardBox.width) / 2);
        const top = Math.max(GDBV_TOUR_GAP, (height - cardBox.height) / 3);

        //A hole of no size under the middle of the card, so the whole page dims - and the next step's grows out of it.
        gdbvTourCut(shade, left + (cardBox.width / 2), top + (cardBox.height / 2), 0, 0);

        card.style.left = left + 'px';
        card.style.top = top + 'px';

        return;
    }

    const box = part.getBoundingClientRect();
    const pad = 4;

    gdbvTourCut(shade, box.left - pad, box.top - pad, box.width + (pad * 2), box.height + (pad * 2));

    let top = box.bottom + GDBV_TOUR_GAP;

    if (top + cardBox.height > height - GDBV_TOUR_GAP)
        top = box.top - GDBV_TOUR_GAP - cardBox.height;

    //Neither under nor over: a part as tall as the window, like the canvas - the card goes inside it, at the top.
    if (top < GDBV_TOUR_GAP)
        top = Math.max(GDBV_TOUR_GAP, box.top + GDBV_TOUR_GAP);

    let left = box.left + (box.width / 2) - (cardBox.width / 2);

    left = Math.min(Math.max(GDBV_TOUR_GAP, left), width - GDBV_TOUR_GAP - cardBox.width);

    card.style.left = Math.max(GDBV_TOUR_GAP, left) + 'px';
    card.style.top = top + 'px';
}

//
//Every key is the tour's while it is open, wherever the focus is - caught on its way down, before anything on the
//page hears it. **The canvas's keys are on the window too** (KeyboardInterop.js), and without this Delete would
//delete what is chosen behind the shade.
//
//Only the propagation stops: Enter and Space still press the card's buttons, which is the browser's own doing.
//
function gdbvOnTourKey(event) {
    if (gdbvTourHandle == null)
        return;

    event.stopPropagation();

    if (event.key === 'Escape' || event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
        event.preventDefault();
        gdbvTourHandle.invokeMethodAsync('OnTourKey', event.key);

        return;
    }

    if (event.key === 'Tab')
        gdbvTourKeepFocus(event);
}

//Tab goes round the card's own buttons rather than out to the page behind the shade.
function gdbvTourKeepFocus(event) {
    const card = document.getElementById('gdbvTourCard');

    if (card == null)
        return;

    const stops = Array.from(card.querySelectorAll('button:not([disabled])'));

    if (stops.length === 0)
        return;

    event.preventDefault();

    let next = stops.indexOf(document.activeElement);

    if (event.shiftKey)
        next = next - 1;
    else
        next = next + 1;

    if (next < 0)
        next = stops.length - 1;
    else if (next >= stops.length)
        next = 0;

    stops[next].focus();
}

window.gdbvTour = {
    //The tour has opened: its keys, and the window that moves its parts, are watched until stop.
    start: function (handle) {
        gdbvTourHandle = handle;

        window.addEventListener('keydown', gdbvOnTourKey, true);
        window.addEventListener('resize', gdbvTourPlaceNow);
        window.addEventListener('scroll', gdbvTourPlaceNow, true);
    },

    //Which of these selectors name a part that is on the page now - a null one, about the whole app, always is.
    present: function (selectors) {
        return selectors.map(each => each == null || gdbvTourPart(each) != null);
    },

    //
    //Shows a step: true when its part is on the page, or it has none; false when it names one that is not here,
    //which the tour takes as a step to go past.
    //
    place: function (selector) {
        const part = gdbvTourPart(selector);

        if (selector != null && part == null)
            return false;

        gdbvTourTarget = selector;

        if (part != null)
            part.scrollIntoView({ block: 'nearest', inline: 'nearest' });

        gdbvTourPlaceNow();

        //Once more after the card has its new words laid out, since its size is what decides where it fits.
        requestAnimationFrame(gdbvTourPlaceNow);

        return true;
    },

    //The tour is over: no keys to hold and nothing to follow round the window any more.
    stop: function () {
        gdbvTourTarget = null;
        gdbvTourHandle = null;

        window.removeEventListener('keydown', gdbvOnTourKey, true);
        window.removeEventListener('resize', gdbvTourPlaceNow);
        window.removeEventListener('scroll', gdbvTourPlaceNow, true);
    }
};
