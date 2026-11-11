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
    // 同步 SHA-256（输入为 Uint8Array，返回 32 字节 Uint8Array）。
    // crypto.subtle 只在安全上下文（HTTPS / localhost）可用，这里保留一份
    // 纯 JS 实现，供通过 http://<服务器IP> 访问时回退使用。
    sha256: function (bytes) {
        var K = [
            0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
            0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
            0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
            0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
            0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
            0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
            0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
            0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2
        ];
        var H = [0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19];
        var length = bytes.length;
        var padded = new Uint8Array(((((length + 8) >> 6) + 1) << 6));
        padded.set(bytes);
        padded[length] = 0x80;
        var bitLength = length * 8;
        padded[padded.length - 4] = (bitLength >>> 24) & 0xff;
        padded[padded.length - 3] = (bitLength >>> 16) & 0xff;
        padded[padded.length - 2] = (bitLength >>> 8) & 0xff;
        padded[padded.length - 1] = bitLength & 0xff;

        var w = new Int32Array(64);
        for (var offset = 0; offset < padded.length; offset += 64) {
            for (var t = 0; t < 16; t++) {
                var p = offset + t * 4;
                w[t] = (padded[p] << 24) | (padded[p + 1] << 16) | (padded[p + 2] << 8) | padded[p + 3];
            }
            for (t = 16; t < 64; t++) {
                var x = w[t - 15];
                var y = w[t - 2];
                var s0 = ((x >>> 7) | (x << 25)) ^ ((x >>> 18) | (x << 14)) ^ (x >>> 3);
                var s1 = ((y >>> 17) | (y << 15)) ^ ((y >>> 19) | (y << 13)) ^ (y >>> 10);
                w[t] = (w[t - 16] + s0 + w[t - 7] + s1) | 0;
            }

            var a = H[0], b = H[1], c = H[2], d = H[3], e = H[4], f = H[5], g = H[6], h = H[7];
            for (t = 0; t < 64; t++) {
                var S1 = ((e >>> 6) | (e << 26)) ^ ((e >>> 11) | (e << 21)) ^ ((e >>> 25) | (e << 7));
                var ch = (e & f) ^ (~e & g);
                var temp1 = (h + S1 + ch + K[t] + w[t]) | 0;
                var S0 = ((a >>> 2) | (a << 30)) ^ ((a >>> 13) | (a << 19)) ^ ((a >>> 22) | (a << 10));
                var maj = (a & b) ^ (a & c) ^ (b & c);
                var temp2 = (S0 + maj) | 0;
                h = g;
                g = f;
                f = e;
                e = (d + temp1) | 0;
                d = c;
                c = b;
                b = a;
                a = (temp1 + temp2) | 0;
            }

            H[0] = (H[0] + a) | 0;
            H[1] = (H[1] + b) | 0;
            H[2] = (H[2] + c) | 0;
            H[3] = (H[3] + d) | 0;
            H[4] = (H[4] + e) | 0;
            H[5] = (H[5] + f) | 0;
            H[6] = (H[6] + g) | 0;
            H[7] = (H[7] + h) | 0;
        }

        var out = new Uint8Array(32);
        for (var i = 0; i < 8; i++) {
            out[i * 4] = (H[i] >>> 24) & 0xff;
            out[i * 4 + 1] = (H[i] >>> 16) & 0xff;
            out[i * 4 + 2] = (H[i] >>> 8) & 0xff;
            out[i * 4 + 3] = H[i] & 0xff;
        }
        return out;
    },
    // 登录前置校验：找 counter 使 SHA256("<nonce>:<counter>") 前两字节为 0。
    // 注意：crypto.subtle 只在安全上下文（HTTPS / localhost）存在；通过
    // http://<服务器IP> 访问时它是 undefined。旧实现直接抛错，而 onsubmit
    // 拿到的是 rejected Promise（truthy），表单会在 counter 为空时提交，
    // 服务端因此返回 error=pow。这里改为兜底纯 JS 实现，并只在拿到结果后提交。
    prepareLogin: function (form) {
        if (form._powRunning) return false;

        var nonce = form.elements.powNonce ? form.elements.powNonce.value : "";
        var counter = form.elements.powCounter;
        if (!nonce || !counter) return true;

        form._powRunning = true;
        var max = 10000000;
        var encoder = new TextEncoder();
        var sha256 = ctf.sha256;
        var done = function (value) {
            counter.value = value;
            form.submit();
        };
        var fail = function () {
            form._powRunning = false;
            window.alert("登录请求验证失败：浏览器无法完成校验，请刷新页面后重试。");
        };

        var subtle = window.crypto && window.crypto.subtle;
        if (subtle && typeof subtle.digest === "function") {
            var attempt = function (value) {
                if (value > max) { fail(); return; }
                subtle.digest("SHA-256", encoder.encode(nonce + ":" + value)).then(function (buffer) {
                    var digest = new Uint8Array(buffer);
                    if (digest[0] === 0 && digest[1] === 0) done(value);
                    else attempt(value + 1);
                }).catch(fail);
            };
            attempt(0);
            return false;
        }

        // 非安全上下文：同步计算（平均 6.5 万次短哈希，约 1 秒内完成）。
        for (var value = 0; value <= max; value++) {
            var digest = sha256(encoder.encode(nonce + ":" + value));
            if (digest[0] === 0 && digest[1] === 0) {
                done(value);
                return false;
            }
        }
        fail();
        return false;
    }
};
