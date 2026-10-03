// Thin bridge between xterm.js and the Blazor terminal page.
window.legionTerm = (function () {
    const terms = new Map();

    function b64ToBytes(b64) {
        const bin = atob(b64);
        const bytes = new Uint8Array(bin.length);
        for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
        return bytes;
    }

    // Clipboard write: the async API needs a secure context (localhost is one); fall back to execCommand otherwise.
    async function copyText(text, term) {
        try {
            await navigator.clipboard.writeText(text);
        } catch (e) {
            const ta = document.createElement('textarea');
            ta.value = text;
            ta.style.cssText = 'position:fixed;opacity:0;left:-1000px;top:0';
            document.body.appendChild(ta);
            ta.select();
            try { document.execCommand('copy'); } catch (e2) { /* nothing more to try */ }
            ta.remove();
            term.focus();
        }
    }

    // Terminal conventions for the keys that normally belong to the shell:
    //   Ctrl+C with a selection copies it; without one it stays ^C (interrupt) for the agent.
    //   Ctrl+Shift+C always copies and never sends ^C.
    //   Ctrl+V / Ctrl+Shift+V paste. Returning false only stops xterm from sending ^V; the browser still
    //   fires its paste event, which xterm turns into the pasted text (no clipboard-read permission needed).
    function installClipboardKeys(term) {
        term.attachCustomKeyEventHandler(ev => {
            if (ev.type !== 'keydown' || !ev.ctrlKey || ev.altKey) return true;
            const key = ev.key.toLowerCase();
            if (key === 'c' && (ev.shiftKey || term.hasSelection())) {
                if (term.hasSelection()) copyText(term.getSelection(), term);
                ev.preventDefault();
                return false;
            }
            if (key === 'v') return false;
            return true;
        });
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
            installClipboardKeys(term);

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
