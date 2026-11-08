//Drives the draggable divider between the main graph view and the query sidebar, and (since 2026-10-29) the
//handles on the page's two outer edges. The split ratio lives in the --sidebar-w CSS variable on <html>, so
//Blazor re-renders never clobber it (Blazor only owns the panels, not :root). resetSplit clears it, falling
//back to the default ratio from the CSS. The page's side margins live in --gdbv-page-ml and --gdbv-page-mr the
//same way, and are remembered in this browser; dragging the left edge grows the graph and leaves the sidebar
//its width, dragging the right edge grows the sidebar and leaves the graph its width. No drag makes the page scroll a
//way it did not when the drag began: it stops where it last fit (2026-11-03, the user).
window.splitterInterop = {
    beginResize: function (rowEl) {
        if (!rowEl)
            return;

        const rect = rowEl.getBoundingClientRect();
        const minSidePx = 375;//keep the sidebar at least its min-width
        const minMainPx = 240;//always leave a usable graph view
        const defaultPct = 100 / 3;//the original 2/3 : 1/3 ratio
        const snapPx = 18;//"snap zone" half-width around the default, for easy reset
        const start = window.splitterInterop._scrolls();
        let fits = rowEl.querySelector('.split-side').getBoundingClientRect().width / rect.width * 100;

        const onMove = (ev) => {
            let sideW = rect.right - ev.clientX;
            const maxSidePx = rect.width - minMainPx;
            sideW = Math.max(minSidePx, Math.min(maxSidePx, sideW));

            const defaultW = rect.width * defaultPct / 100;
            let pct;
            if (Math.abs(sideW - defaultW) < snapPx)
                pct = defaultPct;
            else
                pct = (sideW / rect.width) * 100;

            fits = window.splitterInterop._fit(start, fits, pct, p => window.splitterInterop._setSplit(p, p === defaultPct));
            ev.preventDefault();
        };

        window.splitterInterop._track(onMove);
    },

    resetSplit: function () {
        window.splitterInterop._setSplit(0, true);
    },

    //Drags the query panel's height (2026-11-05, the user): what it has over its default lives in --gdbv-editor-extra on
    //<html>, which the box and its editors add to their own, and is remembered in this browser. Near the default it sticks
    //there, so that size is easy to find again. It stops where the sidebar's card has no more room, or the page would scroll.
    beginEditorResize: function () {
        const pane = document.querySelector('.gdbv-editor-pane');
        const handle = document.querySelector('.gdbv-editor-resize');

        if (!pane || !handle)
            return;

        const em = parseFloat(getComputedStyle(pane).fontSize);
        const snapPx = 18;
        const fullPx = pane.getBoundingClientRect().height - window.splitterInterop._editorExtra();
        const top = pane.getBoundingClientRect().top;
        const minPx = -9 * em;//leaves the editor 6em
        const start = window.splitterInterop._scrolls();
        const tooTall = () => window.splitterInterop._editorOverflow(pane) > 0;
        let fits = window.splitterInterop._editorExtra();

        handle.classList.add('is-dragging');
        handle.classList.toggle('is-default', fits === 0);

        const onMove = (ev) => {
            let wanted = Math.max(minPx, ev.clientY - top - fullPx);

            if (Math.abs(wanted) < snapPx)
                wanted = 0;

            //The default size and under are always allowed: only growing past it can be stopped by the room there is.
            if (wanted <= 0) {
                window.splitterInterop._setEditorExtra(wanted);
                fits = wanted;
            }
            else {
                fits = window.splitterInterop._fit(start, fits, wanted, px => window.splitterInterop._setEditorExtra(px), tooTall);
            }

            handle.classList.toggle('is-default', fits === 0);
            ev.preventDefault();
        };

        window.splitterInterop._track(onMove, 'row-resize', () => handle.classList.remove('is-dragging', 'is-default'));
    },

    resetEditorSize: function () {
        window.splitterInterop._setEditorExtra(0);
    },

    _editorExtra: function () {
        return parseFloat(document.documentElement.style.getPropertyValue('--gdbv-editor-extra')) || 0;
    },

    //How far the query panel's card runs past its own foot: none in the stacked layout, where the card is as tall as it needs.
    _editorOverflow: function (pane) {
        const card = pane.parentElement;

        return card.scrollHeight - card.clientHeight - 1;
    },

    _setEditorExtra: function (px) {
        try {
            if (px === 0) {
                document.documentElement.style.removeProperty('--gdbv-editor-extra');
                localStorage.removeItem('graphdbviewer:editor-extra');
            }
            else {
                document.documentElement.style.setProperty('--gdbv-editor-extra', px.toFixed(1) + 'px');
                localStorage.setItem('graphdbviewer:editor-extra', px.toFixed(1));
            }
        }
        catch { }
    },

    //The sidebar's share of the row, remembered in this browser (2026-10-29) like the margins, so a sidebar widened
    //from the right edge is still that wide after a reload.
    _setSplit: function (pct, isDefault) {
        try {
            if (isDefault) {
                document.documentElement.style.removeProperty('--sidebar-w');
                localStorage.removeItem('graphdbviewer:sidebar-w');
            }
            else {
                document.documentElement.style.setProperty('--sidebar-w', pct.toFixed(3) + '%');
                localStorage.setItem('graphdbviewer:sidebar-w', pct.toFixed(3));
            }
        }
        catch { }
    },

    //Drags the page's edge on one side ('left' or 'right') toward or away from the window's edge. The margin on
    //that side becomes a pixel value; within the snap zone of the default it goes back to the default.
    beginEdgeResize: function (rowEl, side) {
        if (!rowEl)
            return;

        const page = rowEl.closest('.gdbv-page');
        const parent = page.parentElement.getBoundingClientRect();
        const parentStyle = getComputedStyle(page.parentElement);
        const inner = { left: parent.left + parseFloat(parentStyle.paddingLeft), right: parent.right - parseFloat(parentStyle.paddingRight) };
        const row = rowEl.getBoundingClientRect();
        const side0 = rowEl.querySelector('.split-side').getBoundingClientRect().width;
        const main0 = rowEl.querySelector('.split-main').getBoundingClientRect().width;
        const gutter = row.width - side0 - main0;
        let otherMargin;

        if (side === 'left')
            otherMargin = inner.right - row.right;
        else
            otherMargin = row.left - inner.left;

        const minPagePx = 700;
        const snapPx = 32;//a wide sticky spot around the default, so a drag finds the familiar width again
        const defaultMargin = window.splitterInterop._defaultMargin();
        const start = window.splitterInterop._scrolls();
        let fits;

        if (side === 'left')
            fits = row.left - inner.left;
        else
            fits = inner.right - row.right;

        //The page's edge at a margin, and the sidebar's share of the row that keeps the other panel's width as it was.
        const apply = (margin) => {
            window.splitterInterop._setMargin(side, margin, margin === defaultMargin);

            const rowW = (inner.right - inner.left) - otherMargin - margin;
            let sideW;

            if (side === 'left')
                sideW = side0;
            else
                sideW = rowW - gutter - main0;

            window.splitterInterop._setSplit(sideW / rowW * 100, false);
        };

        const onMove = (ev) => {
            let margin;

            if (side === 'left')
                margin = ev.clientX - inner.left;
            else
                margin = inner.right - ev.clientX;

            const maxMargin = (inner.right - inner.left) - otherMargin - minPagePx;
            margin = Math.max(0, Math.min(maxMargin, margin));

            if (Math.abs(margin - defaultMargin) < snapPx)
                margin = defaultMargin;

            fits = window.splitterInterop._fit(start, fits, margin, apply);
            ev.preventDefault();
        };

        window.splitterInterop._track(onMove);
    },

    resetEdge: function (side) {
        window.splitterInterop._setMargin(side, 0, true);
    },

    _defaultMargin: function () {
        const rem = parseFloat(getComputedStyle(document.documentElement).fontSize) || 16;

        return Math.max(0, window.innerWidth * 0.10 - 1.5 * rem);
    },

    _setMargin: function (side, px, isDefault) {
        let name;

        if (side === 'left')
            name = '--gdbv-page-ml';
        else
            name = '--gdbv-page-mr';

        const key = 'graphdbviewer:page-margin-' + side;

        try {
            if (isDefault) {
                document.documentElement.style.removeProperty(name);
                localStorage.removeItem(key);
            }
            else {
                document.documentElement.style.setProperty(name, Math.round(px) + 'px');
                localStorage.setItem(key, String(Math.round(px)));
            }
        }
        catch { }
    },

    //Takes the sidebar's share back toward the default until the page scrolls no way and nothing sticks out of the row
    //of tabs (2026-11-03, the user): a share stored before drags stopped short, or one too wide for a window made
    //narrower since. Run when the cards are first on screen and whenever the window is resized; not for the stacked
    //layout, which has no divider and scrolls as it should.
    settle: function () {
        const pane = document.querySelector('.gdbv-editor-pane');
        const extra = window.splitterInterop._editorExtra();

        //An editor dragged taller than a window made smaller since, or than the card holds now, gives back what does not fit.
        if (pane && extra > 0 && window.splitterInterop._editorOverflow(pane) > 0)
            window.splitterInterop._setEditorExtra(Math.max(0, extra - window.splitterInterop._editorOverflow(pane)));

        const gutter = document.querySelector('.split-gutter');
        const row = document.querySelector('.split-row');

        if (!gutter || !row || getComputedStyle(gutter).display === 'none')
            return;

        const none = { x: false, y: false };

        if (!window.splitterInterop._overflows(none))
            return;

        const defaultPct = 100 / 3;
        const pct = row.querySelector('.split-side').getBoundingClientRect().width / row.getBoundingClientRect().width * 100;

        if (pct <= defaultPct)
            return;

        window.splitterInterop._fit(none, defaultPct, pct, p => window.splitterInterop._setSplit(p, p === defaultPct));
    },

    //Whether the page scrolls across and down. Across counts the row of tabs over the graph's column too, whose end may
    //stick out past its own (Import/Export past the graph's card) with neither the row nor the page reporting it as a
    //scroll width: its last child ending past the row's own right edge.
    _scrolls: function () {
        const el = document.scrollingElement;
        const strip = document.querySelector('.gdbv-tabstrip-row');
        let across = el.scrollWidth > el.clientWidth;

        if (strip && strip.lastElementChild && strip.lastElementChild.getBoundingClientRect().right > strip.getBoundingClientRect().right + 0.5)
            across = true;

        return { x: across, y: el.scrollHeight > el.clientHeight };
    },

    //Whether the page now scrolls a way it did not when the drag began (start, from _scrolls).
    _overflows: function (start) {
        const now = window.splitterInterop._scrolls();

        return (now.x && !start.x) || (now.y && !start.y);
    },

    //Applies a drag's wanted value; when that makes the page scroll a new way, halves the way back toward the last value
    //that fit until it is within a pixel or so of the edge of fitting, and applies that. Returns the value applied.
    _fit: function (start, fits, wanted, apply, alsoTooMuch) {
        const tooMuch = () => window.splitterInterop._overflows(start) || (alsoTooMuch && alsoTooMuch());

        apply(wanted);

        if (!tooMuch())
            return wanted;

        let lo = fits;
        let hi = wanted;

        for (let i = 0; i < 10; i++) {
            const mid = (lo + hi) / 2;

            apply(mid);

            if (tooMuch())
                hi = mid;
            else
                lo = mid;
        }

        apply(lo);

        return lo;
    },

    _track: function (onMove, cursor, onEnd) {
        const onUp = () => {
            window.removeEventListener('pointermove', onMove);
            window.removeEventListener('pointerup', onUp);
            document.body.style.userSelect = '';
            document.body.style.cursor = '';

            if (onEnd)
                onEnd();
        };

        document.body.style.userSelect = 'none';
        document.body.style.cursor = cursor || 'col-resize';
        window.addEventListener('pointermove', onMove);
        window.addEventListener('pointerup', onUp);
    }
};

//The margins and the split dragged last time, back before the page draws.
(function () {
    try {
        for (const side of ['left', 'right']) {
            const saved = localStorage.getItem('graphdbviewer:page-margin-' + side);
            let name;

            if (side === 'left')
                name = '--gdbv-page-ml';
            else
                name = '--gdbv-page-mr';

            if (saved !== null)
                document.documentElement.style.setProperty(name, saved + 'px');
        }

        const split = localStorage.getItem('graphdbviewer:sidebar-w');

        if (split !== null)
            document.documentElement.style.setProperty('--sidebar-w', split + '%');

        const editor = localStorage.getItem('graphdbviewer:editor-extra');

        if (editor !== null)
            document.documentElement.style.setProperty('--gdbv-editor-extra', editor + 'px');
    }
    catch { }

    window.addEventListener('resize', () => window.splitterInterop.settle());
})();
//The table view's columns sized as a spreadsheet's are (2026-11-05, the user): a header's right edge dragged sizes its
//column, and double-clicked fits the column to its widest cell on one line. The first size fixes every column at the
//width it had, so only the one dragged changes. Widths are kept by column name for the page's life, per table
//(data-resize-key), and put back when the table is drawn again or its columns change (restore, after each render).
window.gdbvTableColumns = (function () {
    const saved = {};
    const minWidth = 24;

    function headerCells(table) {
        const row = table.tHead && table.tHead.rows[0];

        if (!row)
            return [];

        return Array.from(row.cells);
    }

    function keyOf(th) {
        return th.dataset.col || th.textContent.trim();
    }

    function signature(table) {
        return headerCells(table).map(keyOf).join('\u0001');
    }

    function widthsOf(table) {
        const key = table.dataset.resizeKey;

        if (!saved[key])
            saved[key] = {};

        return saved[key];
    }

    function isSized(table) {
        return table.classList.contains('is-sized') && table.dataset.sizedFor === signature(table);
    }

    //Every column at a width of its own: the one kept for its name, or the one it has now.
    function size(table) {
        const cells = headerCells(table);
        const kept = widthsOf(table);

        table.classList.remove('is-sized');
        table.style.width = '';
        cells.forEach(th => th.style.width = '');

        const widths = cells.map(th => kept[keyOf(th)] || Math.ceil(th.getBoundingClientRect().width));
        let total = 0;

        cells.forEach((th, i) => {
            th.style.width = widths[i] + 'px';
            total += widths[i];
        });

        table.style.width = total + 'px';
        table.classList.add('is-sized');
        table.dataset.sizedFor = signature(table);
    }

    function setWidth(table, th, width) {
        const next = Math.max(minWidth, Math.round(width));
        const old = parseFloat(th.style.width) || th.getBoundingClientRect().width;

        th.style.width = next + 'px';
        table.style.width = ((parseFloat(table.style.width) || 0) + next - old) + 'px';
        widthsOf(table)[keyOf(th)] = next;
    }

    //The widest of the column's cells on one line, the header's among them. Measured on a copy of the column laid out at
    //its natural width, out of sight beside the table: read on the cells in place, a cell is never narrower than its
    //column, so a column wider than its contents could only grow and never fit back (2026-11-06, the user: "double-clicking
    //doesn't resize the column to the maximum content width").
    function fit(table, th) {
        const index = th.cellIndex;
        const probe = table.cloneNode(false);
        const body = document.createElement('tbody');

        probe.classList.remove('is-sized');
        probe.removeAttribute('data-resize-key');
        probe.style.cssText = 'position: absolute; visibility: hidden; left: -100000px; top: 0; width: auto; min-width: 0; table-layout: auto;';

        for (const row of table.rows) {
            const cell = row.cells[index];

            if (!cell)
                continue;

            const copy = cell.cloneNode(true);
            const line = document.createElement('tr');

            copy.classList.add('is-measured');
            copy.style.width = '';
            copy.style.maxWidth = 'none';
            line.appendChild(copy);
            body.appendChild(line);
        }

        probe.appendChild(body);
        table.parentNode.appendChild(probe);

        const widest = probe.getBoundingClientRect().width;

        probe.remove();
        setWidth(table, th, Math.ceil(widest) + 1);
    }

    function gripOf(target) {
        if (!target || !target.closest)
            return null;

        return target.closest('.gdbv-col-grip');
    }

    function install() {
        if (window.gdbvTableColumns._installed)
            return;

        window.gdbvTableColumns._installed = true;

        document.addEventListener('pointerdown', ev => {
            const grip = gripOf(ev.target);

            if (!grip || ev.button !== 0)
                return;

            const th = grip.closest('th');
            const table = th.closest('table');

            ev.preventDefault();
            ev.stopPropagation();

            if (!isSized(table))
                size(table);

            const startX = ev.clientX;
            const startWidth = parseFloat(th.style.width);

            document.body.classList.add('gdbv-col-resizing');

            const onMove = e => setWidth(table, th, startWidth + e.clientX - startX);

            const onUp = () => {
                document.removeEventListener('pointermove', onMove);
                document.removeEventListener('pointerup', onUp);
                document.removeEventListener('pointercancel', onUp);
                document.body.classList.remove('gdbv-col-resizing');
            };

            document.addEventListener('pointermove', onMove);
            document.addEventListener('pointerup', onUp);
            document.addEventListener('pointercancel', onUp);
        });

        document.addEventListener('dblclick', ev => {
            const grip = gripOf(ev.target);

            if (!grip)
                return;

            const th = grip.closest('th');
            const table = th.closest('table');

            ev.preventDefault();
            ev.stopPropagation();

            if (!isSized(table))
                size(table);

            fit(table, th);
        });
    }

    return {
        _installed: false,

        //After each render: listens once, and gives a table drawn anew, or with other columns, the widths kept for it.
        restore: function () {
            install();

            document.querySelectorAll('table[data-resize-key]').forEach(table => {
                const kept = saved[table.dataset.resizeKey];

                if (!kept || Object.keys(kept).length === 0 || isSized(table))
                    return;

                size(table);
            });
        }
    };
})();
