//Drives the draggable divider between the main graph view and the query sidebar. The split ratio
//lives in the --sidebar-w CSS variable on <html>, so Blazor re-renders never clobber it (Blazor only
//owns the panels, not :root). resetSplit clears it, falling back to the default ratio from the CSS.
window.splitterInterop = {
    beginResize: function (rowEl) {
        if (!rowEl)
            return;

        const rect = rowEl.getBoundingClientRect();
        const minSidePx = 375;//keep the sidebar at least its min-width
        const minMainPx = 240;//always leave a usable graph view
        const defaultPct = 100 / 3;//the original 2/3 : 1/3 ratio
        const snapPx = 18;//"snap zone" half-width around the default, for easy reset
        const root = document.documentElement;

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

            root.style.setProperty('--sidebar-w', pct.toFixed(3) + '%');
            ev.preventDefault();
        };

        const onUp = () => {
            window.removeEventListener('pointermove', onMove);
            window.removeEventListener('pointerup', onUp);
            document.body.style.userSelect = '';
            document.body.style.cursor = '';
        };

        document.body.style.userSelect = 'none';
        document.body.style.cursor = 'col-resize';
        window.addEventListener('pointermove', onMove);
        window.addEventListener('pointerup', onUp);
    },

    resetSplit: function () {
        document.documentElement.style.removeProperty('--sidebar-w');
    }
};
