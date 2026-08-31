window.keyboardInterop = {
    dotNetRef: null,
    handler: null,
    debugStepActive: false,

    attach: function (ref) {
        this.dotNetRef = ref;

        if (this.handler) {
            document.removeEventListener('keydown', this.handler);
        }

        const self = this;
        this.handler = function (e) {
            //Don't trigger when the user is typing in an editable field
            const target = e.target;
            if (target) {
                const tag = target.tagName;
                if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || target.isContentEditable) {
                    return;
                }
            }

            //While the debugger's Steps tab is open, arrow keys drive the step cursor (matching the ◀ ▶ ▲ ▼
            //buttons): ◀/▶ step back/over siblings, ▼/▲ step into/out of a sub-traversal.
            if (self.debugStepActive && self.dotNetRef) {
                let dir = null;

                if (e.key === 'ArrowRight')
                    dir = 'over';
                else if (e.key === 'ArrowLeft')
                    dir = 'back';
                else if (e.key === 'ArrowDown')
                    dir = 'into';
                else if (e.key === 'ArrowUp')
                    dir = 'out';

                if (dir) {
                    e.preventDefault();
                    self.dotNetRef.invokeMethodAsync('OnDebugStepKey', dir);
                    return;
                }
            }

            if (e.key === 'Delete') {
                if (self.dotNetRef) {
                    self.dotNetRef.invokeMethodAsync('OnDeleteKeyPressed');
                }
            }
        };

        document.addEventListener('keydown', this.handler);
    },

    setDebugStepActive: function (active) {
        this.debugStepActive = !!active;
    },

    detach: function () {
        if (this.handler) {
            document.removeEventListener('keydown', this.handler);
            this.handler = null;
        }
        this.dotNetRef = null;
        this.debugStepActive = false;
    }
};
