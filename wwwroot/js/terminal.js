// Thin bridge between xterm.js and the Blazor terminal page.
window.legionTerm = (function () {
    const terms = new Map();

    function b64ToBytes(b64) {
        const bin = atob(b64);
        const bytes = new Uint8Array(bin.length);
        for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
        return bytes;
    }

    return {
        init(id, el, ref) {
            this.dispose(id);
            const term = new Terminal({
                cursorBlink: true,
                fontFamily: 'Consolas, "Cascadia Mono", "Courier New", monospace',
                fontSize: 14,
                scrollback: 5000,
                theme: { background: '#0b1020' }
            });
            const fit = new FitAddon.FitAddon();
            term.loadAddon(fit);
            term.open(el);
            fit.fit();

            term.onData(d => ref.invokeMethodAsync('OnInput', d));
            term.onResize(s => ref.invokeMethodAsync('OnResize', s.cols, s.rows));
            const ro = new ResizeObserver(() => { try { fit.fit(); } catch (e) { /* hidden */ } });
            ro.observe(el);

            terms.set(id, { term, fit, ro });
            term.focus();
            return { cols: term.cols, rows: term.rows };
        },
        write(id, b64) {
            const t = terms.get(id);
            if (t) t.term.write(b64ToBytes(b64));
        },
        reset(id) {
            const t = terms.get(id);
            if (t) t.term.reset();
        },
        focus(id) {
            const t = terms.get(id);
            if (t) t.term.focus();
        },
        size(id) {
            const t = terms.get(id);
            return t ? { cols: t.term.cols, rows: t.term.rows } : { cols: 120, rows: 30 };
        },
        dispose(id) {
            const t = terms.get(id);
            if (!t) return;
            t.ro.disconnect();
            t.term.dispose();
            terms.delete(id);
        }
    };
})();
