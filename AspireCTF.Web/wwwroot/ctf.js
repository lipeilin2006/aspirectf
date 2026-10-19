window.ctf = {
    // Log pane auto-scroll: follow new output only while the viewport is
    // already pinned at the bottom; never yank the view when the user has
    // scrolled up to read history. A freshly mounted pane starts pinned so
    // the initial load jumps to the latest output.
    _logStick: {},
    initLogScroll: function (id) {
        var el = document.getElementById(id);
        if (!el || el._ocScrollInit) return;
        el._ocScrollInit = true;
        this._logStick[id] = true;
        var stick = this._logStick;
        var update = function () {
            var gap = el.scrollHeight - el.scrollTop - el.clientHeight;
            stick[id] = gap <= 32;
        };
        el.addEventListener("scroll", update, { passive: true });
    },
    scrollLog: function (id) {
        var el = document.getElementById(id);
        if (el) {
            this.initLogScroll(id);
            if (this._logStick[id] !== false) {
                el.scrollTop = el.scrollHeight;
            }
        }
    },
    // Clipboard API 只在安全上下文（HTTPS / localhost）可用；通过 IP 以
    // HTTP 访问时 navigator.clipboard 为 undefined，必须回退到
    // 隐藏 textarea + execCommand('copy')。返回 Promise<bool> 表示是否成功。
    copyText: function (text) {
        var fallback = function () {
            try {
                var ta = document.createElement("textarea");
                ta.value = text;
                ta.setAttribute("readonly", "");
                ta.style.position = "fixed";
                ta.style.top = "-1000px";
                document.body.appendChild(ta);
                ta.select();
                var ok = document.execCommand("copy");
                document.body.removeChild(ta);
                return ok;
            } catch (e) {
                return false;
            }
        };
        if (navigator.clipboard && window.isSecureContext) {
            return navigator.clipboard.writeText(text)
                .then(function () { return true; })
                .catch(fallback);
        }
        return Promise.resolve(fallback());
    },
    prepareLogin: async function (form) {
        const nonce = form.elements.powNonce.value;
        const counter = form.elements.powCounter;
        for (let value = 0; value <= 10000000; value++) {
            const data = new TextEncoder().encode(`${nonce}:${value}`);
            const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', data));
            if (digest[0] === 0 && digest[1] === 0) {
                counter.value = value;
                form.submit();
                return false;
            }
        }
        return false;
    }
};
