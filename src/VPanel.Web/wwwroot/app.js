window.vpanel = {
    getPref: function (key) {
        try { return window.localStorage.getItem("vpanel." + key); } catch { return null; }
    },
    setPref: function (key, value) {
        try { window.localStorage.setItem("vpanel." + key, value); } catch { /* storage unavailable */ }
    },
    prefersDark: function () {
        return !!(window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches);
    },
    waitForMonaco: function (timeoutMs) {
        return new Promise(function (resolve) {
            const started = Date.now();
            (function check() {
                if (window.monaco && window.monaco.editor) return resolve(true);
                if (Date.now() - started > timeoutMs) return resolve(false);
                setTimeout(check, 50);
            })();
        });
    },
    scrollToBottom: function (el) {
        if (!el) return;
        const nearBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 80;
        if (nearBottom || !el.dataset.vpScrolled) {
            el.scrollTop = el.scrollHeight;
            el.dataset.vpScrolled = "1";
        }
    }
};
