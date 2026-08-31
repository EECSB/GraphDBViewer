//Makes the AI popups behave like windows rather than modals: dragged by their header, resized from
//their corner, and remembered where you left them.
//
//Geometry is written straight to the element's inline style and never round-trips through Blazor. A
//drag fires pointermove at screen refresh rate, and a re-render per frame would both stutter and fight
//the mouse; Blazor owns what is inside the panel, this owns where the panel is. It is the same division
//SplitterInterop draws with --sidebar-w.
window.floatingDialog = (function () {
    const storePrefix = 'graphdbviewer:dialog:';
    const margin = 8;//keep this much of the panel on screen, always

    //Panels stack in the order they were last touched, starting above Bootstrap's own modal layer so an
    //About or Style dialog still covers them rather than appearing behind.
    let topZ = 1060;

    function clamp(value, min, max) {
        if (max < min)
            return min;

        return Math.max(min, Math.min(max, value));
    }

    //A panel is never left where it cannot be grabbed: not off the right or bottom edge, and never with
    //its header above the top of the window. Restoring onto a smaller screen than it was saved on is the
    //normal way that happens.
    function place(panel, left, top) {
        const width = panel.offsetWidth;
        const height = panel.offsetHeight;

        panel.style.left = clamp(left, margin - width + 120, window.innerWidth - margin - 120) + 'px';
        panel.style.top = clamp(top, margin, window.innerHeight - margin - 40) + 'px';
    }

    //Where a window should sit when it opens: wholly on screen, which is stricter than what dragging
    //allows. A window may be dragged half off the bottom on purpose; one that OPENS there is a window
    //whose resize grip cannot be reached, and that happens by itself when a saved position comes back
    //onto a shorter screen.
    function fit(panel) {
        const room = window.innerHeight - margin - panel.offsetHeight;

        place(panel, panel.offsetLeft, Math.max(margin, Math.min(panel.offsetTop, room)));
    }

    //Collapse it, read what the content actually wants, then take that up to a ceiling — past which it
    //scrolls, so a long paste cannot grow until there is no transcript left to see.
    function grow(el) {
        if (!el)
            return;

        //Nothing to measure: the window is hidden, and sizing to nothing would stick.
        if (el.scrollHeight === 0 && el.clientHeight === 0)
            return;

        const max = el.gdbvMaxHeight || 200;

        el.style.height = 'auto';

        const needed = el.scrollHeight;

        el.style.height = Math.min(needed, max) + 'px';

        if (needed > max)
            el.style.overflowY = 'auto';
        else
            el.style.overflowY = 'hidden';
    }

    function save(key, panel) {
        if (!key)
            return;

        //A hidden window measures nothing. The observer at the end of attach() fires when one is hidden,
        //so without this, closing a window recorded nothing-by-nothing as its size and reopening it
        //restored that — clamped up to the stylesheet's minimum, half off screen. That was the whole of
        //"they get shrunk a lot when they first open": not the defaults, but the last close.
        if (!panel || !panel.offsetWidth || !panel.offsetHeight)
            return;

        try {
            const geometry = {
                left: parseInt(panel.style.left, 10),
                top: parseInt(panel.style.top, 10),
                width: panel.offsetWidth,
                height: panel.offsetHeight
            };

            localStorage.setItem(storePrefix + key, JSON.stringify(geometry));
        }
        catch (e) { }//private browsing, a full quota: a forgotten position is not worth failing over
    }

    function restore(key) {
        if (!key)
            return null;

        try {
            const raw = localStorage.getItem(storePrefix + key);

            if (!raw)
                return null;

            const geometry = JSON.parse(raw);

            if (typeof geometry.left !== 'number' || typeof geometry.top !== 'number')
                return null;

            //Heals what the bug above already wrote into somebody's browser: a stored size that is
            //missing or absurdly small is treated as no stored size at all, so the window opens at its
            //default instead of at the minimum forever.
            if (!(geometry.width > 200) || !(geometry.height > 150)) {
                delete geometry.width;
                delete geometry.height;
            }

            return geometry;
        }
        catch (e) {
            return null;
        }
    }

    function raise(panel) {
        topZ += 1;
        panel.style.zIndex = topZ;
    }

    return {
        //Called once per dialog, when it first appears. Safe to call again: the second call only
        //re-clamps, so a panel reopened after the window shrank comes back on screen.
        attach: function (panel, handle, grip, key) {
            if (!panel || !handle)
                return;

            if (panel.dataset.floating === 'on') {
                fit(panel);
                raise(panel);
                return;
            }

            panel.dataset.floating = 'on';

            const saved = restore(key);

            if (saved) {
                //A geometry restored without a size is one whose stored size was rejected above; leaving
                //the width and height alone keeps the default the markup set.
                if (saved.width && saved.height) {
                    panel.style.width = saved.width + 'px';
                    panel.style.height = saved.height + 'px';
                }

                place(panel, saved.left, saved.top);
                fit(panel);
            }
            else {
                //Centered horizontally and high enough to leave the query editor visible underneath,
                //which is the point of not dimming the page behind it. A window too tall for that to
                //fit moves up instead of hanging off the bottom, since only the top of it is grabbable.
                place(panel, (window.innerWidth - panel.offsetWidth) / 2, 64);
                fit(panel);
            }

            raise(panel);

            //Any press inside brings the panel forward, so two open dialogs can be swapped between.
            panel.addEventListener('pointerdown', () => raise(panel));

            handle.addEventListener('pointerdown', (ev) => {
                //Only a drag of the bar itself. The close button lives in the header too, and dragging
                //from it should press it, not move the window.
                if (ev.button !== 0 || ev.target.closest('button, a, input, select, textarea'))
                    return;

                const startX = ev.clientX;
                const startY = ev.clientY;
                const startLeft = panel.offsetLeft;
                const startTop = panel.offsetTop;

                const onMove = (move) => {
                    place(panel, startLeft + (move.clientX - startX), startTop + (move.clientY - startY));
                    move.preventDefault();
                };

                const onUp = () => {
                    window.removeEventListener('pointermove', onMove);
                    window.removeEventListener('pointerup', onUp);
                    document.body.style.userSelect = '';
                    save(key, panel);
                };

                document.body.style.userSelect = 'none';
                window.addEventListener('pointermove', onMove);
                window.addEventListener('pointerup', onUp);
                ev.preventDefault();
            });

            if (grip) {
                grip.addEventListener('pointerdown', (ev) => {
                    if (ev.button !== 0)
                        return;

                    const startX = ev.clientX;
                    const startY = ev.clientY;
                    const startW = panel.offsetWidth;
                    const startH = panel.offsetHeight;

                    const onMove = (move) => {
                        //The minimums come from the stylesheet, so one place decides how small is too
                        //small and the drag cannot argue with the layout.
                        const style = getComputedStyle(panel);
                        const minW = parseInt(style.minWidth, 10) || 320;
                        const minH = parseInt(style.minHeight, 10) || 200;

                        panel.style.width = Math.max(minW, startW + (move.clientX - startX)) + 'px';
                        panel.style.height = Math.max(minH, startH + (move.clientY - startY)) + 'px';
                        move.preventDefault();
                    };

                    const onUp = () => {
                        window.removeEventListener('pointermove', onMove);
                        window.removeEventListener('pointerup', onUp);
                        document.body.style.userSelect = '';
                        save(key, panel);
                    };

                    document.body.style.userSelect = 'none';
                    window.addEventListener('pointermove', onMove);
                    window.addEventListener('pointerup', onUp);
                    ev.preventDefault();
                    ev.stopPropagation();
                });
            }

            //A window can also be resized by something other than the grip — a zoom, a rotated phone —
            //so what it settles at is watched rather than only recorded at the end of a drag.
            if (window.ResizeObserver) {
                let pending = 0;

                const observer = new ResizeObserver(() => {
                    clearTimeout(pending);
                    pending = setTimeout(() => save(key, panel), 250);
                });

                observer.observe(panel);
            }
        },

        //Turns a textarea into the chat composer: one line until there is more in it than one line, and
        //Enter as send rather than as a newline.
        //
        //The newline is refused here rather than in Blazor because a Blazor handler's preventDefault is
        //decided when the component renders, not when the key is pressed. The keydown still reaches
        //Blazor afterwards, which is what decides that Enter means send.
        attachComposer: function (el, maxHeight) {
            if (!el || el.gdbvComposer)
                return;

            el.addEventListener('keydown', function (e) {
                //isComposing: an IME is mid-word, where Enter picks a candidate rather than sending.
                if (e.key !== 'Enter' || e.shiftKey || e.isComposing)
                    return;

                e.preventDefault();
            });

            this.attachGrowing(el, maxHeight);
        },

        //Just the growing. For a box that has no send, where swallowing Enter would take the newline
        //away and give nothing back for it.
        attachGrowing: function (el, maxHeight) {
            if (!el || el.gdbvComposer)
                return;

            el.gdbvComposer = true;
            el.gdbvMaxHeight = maxHeight || 200;

            el.addEventListener('input', function () {
                grow(el);
            });

            grow(el);
        },

        //Re-measures when the text changed without anybody typing: a box first sized while its window
        //was hidden, or one C# has just cleared.
        growComposer: function (el) {
            grow(el);
        },

        //Empties it and shrinks it, in that order, so it is measured against what is left.
        resetComposer: function (el) {
            if (!el)
                return;

            el.value = '';
            grow(el);
        },

        //Pins a scrolling box to its last line, for a conversation that grows downward.
        scrollToEnd: function (el) {
            if (!el)
                return;

            el.scrollTop = el.scrollHeight;
        },

        //Forgets a panel's remembered geometry, so it opens where a first-time one would.
        reset: function (key) {
            try {
                localStorage.removeItem(storePrefix + key);
            }
            catch (e) { }
        }
    };
})();
