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

    //The class a box that takes files wears while one is held over it: the whole of the highlight.
    const dropOverClass = 'gdbv-drop-over';

    let fileDropStarted = false;

    //Whether a drag carries files at all. A drag inside the page, a text selection or a link, has types of its own and
    //goes on doing what it did before. types is a DOMStringList in older browsers rather than an array.
    function carriesFiles(e) {
        const carrying = e.dataTransfer;

        if (carrying == null || carrying.types == null)
            return false;

        return Array.prototype.indexOf.call(carrying.types, 'Files') >= 0;
    }

    //Whether Blazor's own listener is on an InputFile yet: a change dispatched before it is carries no files, and C#
    //takes the silent return a canceled dialog takes. _blazorInputFileNextFileId is InputFile's own, an internal detail
    //to check again on a framework upgrade (GDS Viewer's note; there in .NET 10).
    function inputReady(input) {
        return input != null && !input.disabled && input._blazorInputFileNextFileId !== undefined;
    }

    //Files handed to an InputFile as if they had been chosen in its dialog, so the component has one way in.
    function handFiles(input, files) {
        input.files = files;
        input.dispatchEvent(new Event('change', { bubbles: true }));
    }

    function clearDropHighlight() {
        document.querySelectorAll('.' + dropOverClass).forEach(function (lit) {
            lit.classList.remove(dropOverClass);
        });
    }

    //What a drag event is over, when it is an element that can be asked where it sits.
    function dragTarget(e) {
        const target = e.target;

        if (target == null || typeof target.closest !== 'function')
            return null;

        return target;
    }

    //dragover rather than dragenter decides: the browser reads the answer to it throughout the drag, so preventing its
    //default is what makes the drop possible, and it keeps saying where the pointer is, so the highlight goes off from
    //where it goes on rather than from a dragleave that fires on every child boundary crossed.
    function onFileDragOver(e) {
        if (!carriesFiles(e))
            return;

        const target = dragTarget(e);

        if (target == null)
            return;

        const zone = target.closest('[data-drop-into]');

        if (zone == null) {
            clearDropHighlight();

            //A miss inside a window that takes files lands on nothing rather than on the browser's own file viewer,
            //which would open in place of the page.
            if (target.closest('[data-drop-guard]') != null) {
                e.preventDefault();
                e.dataTransfer.dropEffect = 'none';
            }

            return;
        }

        e.preventDefault();
        e.dataTransfer.dropEffect = 'copy';

        if (!zone.classList.contains(dropOverClass)) {
            clearDropHighlight();
            zone.classList.add(dropOverClass);
        }
    }

    //The drop: its files onto the input the zone names, and the input's own change event to carry them across.
    function onFileDrop(e) {
        if (!carriesFiles(e))
            return;

        const target = dragTarget(e);

        if (target == null)
            return;

        const zone = target.closest('[data-drop-into]');

        if (zone == null && target.closest('[data-drop-guard]') == null)
            return;

        e.preventDefault();
        clearDropHighlight();

        if (zone == null)
            return;

        const input = document.getElementById(zone.dataset.dropInto);
        const files = e.dataTransfer.files;

        if (!inputReady(input) || files == null || files.length === 0)
            return;

        handFiles(input, files);
    }

    //Delegated from the document, once, so a window drawn again keeps taking files: a listener on the element there was
    //at the start would be on one that is gone. Only a zone or a guard is answered; a drop anywhere else on the page is
    //left as it was, the knowledge-graph window's own file box among it.
    function startFileDrop() {
        if (fileDropStarted)
            return;

        fileDropStarted = true;

        document.addEventListener('dragover', onFileDragOver);
        document.addEventListener('drop', onFileDrop);

        //The drag leaving the window, which no dragover reports: relatedTarget is null then, and an element when it has
        //only moved within the page.
        document.addEventListener('dragleave', function (e) {
            if (e.relatedTarget == null)
                clearDropHighlight();
        });

        //A drag given up with Escape, which fires neither.
        document.addEventListener('dragend', clearDropHighlight);
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
                //the width and height alone keeps the default the markup set. A window with no grip keeps
                //them too: it is the size its content asks for, not one it was dragged to.
                if (grip && saved.width && saved.height) {
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

            if (grip)
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
                //While the slash commands' list is up, its keys move through it rather than the caret and the focus
                //(2026-11-01, as GDS Viewer's): the box says so on data-slash-menu.
                if (el.dataset.slashMenu === 'open' && !e.isComposing
                    && (e.key === 'ArrowUp' || e.key === 'ArrowDown' || e.key === 'Tab' || e.key === 'Escape')) {
                    e.preventDefault();

                    return;
                }

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

        //Files for Ask AI's next question (2026-11-02, GDS Viewer's): pasted into its box, they go to the paperclip's
        //input as if chosen there, and dropped on anything marked data-drop-into they go to the input it names (see
        //startFileDrop). A pasted screenshot arrives called image.png every time, so it is named for when it was pasted;
        //a paste that also has text on the clipboard pastes the text, since cells copied out of Excel come with a
        //picture of themselves.
        attachFiles: function (box, inputId) {
            startFileDrop();

            if (!box || box.gdbvFiles)
                return;

            box.gdbvFiles = true;

            box.addEventListener('paste', function (e) {
                const data = e.clipboardData;

                if (!data || !data.files || data.files.length === 0)
                    return;

                if (data.getData('text/plain'))
                    return;

                const input = document.getElementById(inputId);

                if (!inputReady(input))
                    return;

                const stamp = new Date().toTimeString().slice(0, 8).replace(/:/g, '');
                const transfer = new DataTransfer();

                Array.from(data.files).forEach(function (file, index) {
                    let named = file;

                    if (/^image\.\w+$/.test(file.name)) {
                        let suffix = '';

                        if (index > 0)
                            suffix = '-' + (index + 1);

                        named = new File([file], 'pasted-' + stamp + suffix + '.' + file.name.split('.').pop(), { type: file.type });
                    }

                    transfer.items.add(named);
                });

                e.preventDefault();

                handFiles(input, transfer.files);
            });
        },

        //Files handed to the InputFile named as a paste hands them, for Ask AI's camera and screenshot (CameraInterop.js),
        //so a picture taken there meets every check a chosen one does. False while the input is not there or not ready.
        giveFiles: function (inputId, files) {
            const input = document.getElementById(inputId);

            if (!inputReady(input))
                return false;

            handFiles(input, files);

            return true;
        },

        //The dividers between Ask AI's conversation and its panels, the chats on its left and the files on its right
        //(2026-11-02, GDS Viewer's attachSplits): dragging one sizes its panel, kept in this browser for the next visit.
        //The width is a custom property on the row rather than a style on the panel, which Blazor takes away and puts back
        //as it is collapsed and opened: the row stays, and so does what is set on it. A divider is there only while its
        //panel is.
        attachSplits: function (row) {
            if (!row || row.gdbvSplits)
                return;

            row.gdbvSplits = true;

            const panels = { chats: '.gdbv-ai-chats', files: '.gdbv-ai-files' };

            Object.keys(panels).forEach(function (name) {
                let kept = null;

                try {
                    kept = localStorage.getItem(storePrefix + 'ai-panel-' + name);
                }
                catch (e) { }

                if (kept && /^\d+$/.test(kept))
                    row.style.setProperty('--gdbv-ai-' + name + '-width', kept + 'px');
            });

            row.addEventListener('pointerdown', function (e) {
                const split = e.target.closest('.gdbv-ai-split');

                if (!split || !row.contains(split) || e.button !== 0)
                    return;

                const name = split.dataset.split;
                const panel = row.querySelector(panels[name]);

                if (!panel)
                    return;

                e.preventDefault();

                //The files are right of their divider, and grow as it goes left.
                let sign = 1;

                if (name === 'files')
                    sign = -1;

                const startX = e.clientX;
                const startWidth = panel.getBoundingClientRect().width;
                const most = Math.max(160, row.getBoundingClientRect().width * 0.6);
                let width = startWidth;

                split.setPointerCapture(e.pointerId);
                split.classList.add('gdbv-ai-split-dragging');

                function move(m) {
                    width = Math.round(Math.max(120, Math.min(most, startWidth + (sign * (m.clientX - startX)))));
                    row.style.setProperty('--gdbv-ai-' + name + '-width', width + 'px');
                }

                function done() {
                    split.removeEventListener('pointermove', move);
                    split.removeEventListener('pointerup', done);
                    split.removeEventListener('pointercancel', done);
                    split.classList.remove('gdbv-ai-split-dragging');

                    try {
                        localStorage.setItem(storePrefix + 'ai-panel-' + name, String(width));
                    }
                    catch (e) { }
                }

                split.addEventListener('pointermove', move);
                split.addEventListener('pointerup', done);
                split.addEventListener('pointercancel', done);
            });
        },

        //A press anywhere but a menu in the window, or the button that opens it, tells the component to put that menu
        //away (2026-11-01, GDS Viewer's registerBarMenu). The opener is not outside: pressing it is how the menu is
        //asked for. Each menu found by its selector inside the window, since it is only on the page while it is open;
        //registered again for the same window, the list is replaced rather than added to.
        dismissOnOutsidePress: function (owner, panel, menus) {
            if (!panel)
                return;

            panel.gdbvMenus = menus;

            if (panel.gdbvMenuListener)
                return;

            panel.gdbvMenuListener = function (e) {
                for (const one of panel.gdbvMenus || []) {
                    const menu = panel.querySelector(one.menu);

                    if (menu == null || menu.contains(e.target))
                        continue;

                    const opener = panel.querySelector(one.opener);

                    if (opener != null && opener.contains(e.target))
                        continue;

                    owner.invokeMethodAsync(one.method).catch(function () { });
                }
            };

            document.addEventListener('pointerdown', panel.gdbvMenuListener, true);
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
