/* ==========================================================================
   Adds the logged-in user's access token to every browser call to the API (fetch, jQuery / XHR, SignalR's negotiate),
   and renews it before it expires. The token only goes to the API or this site - never to another host.
   Links to API downloads can't carry a header: mark them data-auth-download and they are fetched here instead.
   Loaded first in <head>, before any script that calls the API.
   ========================================================================== */
(function () {
    "use strict";

    const meta = document.querySelector('meta[name="qe-api-token"]');
    if (!meta) return;

    const RENEW_EVERY_MS = 30 * 60 * 1000;
    const originalFetch = window.fetch.bind(window);
    const originalOpen = XMLHttpRequest.prototype.open;
    const originalSend = XMLHttpRequest.prototype.send;
    const isLoopback = (host) => host === "localhost" || host === "127.0.0.1";

    let token = meta.content || "";
    let renewing = null;

    const apiUrl = (() => {
        try { return new URL(meta.dataset.apiBase || "", location.href); } catch { return new URL(location.href); }
    })();

    function isApiRequest(url) {
        let target;
        try { target = new URL(url, location.href); } catch { return false; }
        if (target.origin === location.origin || target.origin === apiUrl.origin) return true;
        // Local development: pages have their own fallback API address on another loopback port.
        return isLoopback(apiUrl.hostname) && isLoopback(target.hostname);
    }

    function renew() {
        if (renewing) return renewing;
        renewing = originalFetch(meta.dataset.tokenUrl, { credentials: "same-origin", cache: "no-store" })
            .then(res => (res.ok && !res.redirected ? res.json() : null))
            .then(body => { if (body && body.token) token = body.token; })
            .catch(() => { /* the next API call reports the failure */ })
            .finally(() => { renewing = null; });
        return renewing;
    }

    function withToken(input, init) {
        const headers = new Headers(init?.headers || (input instanceof Request ? input.headers : undefined));
        if (token && !headers.has("Authorization")) headers.set("Authorization", `Bearer ${token}`);
        return { ...init, headers };
    }

    window.fetch = async function (input, init) {
        const url = input instanceof Request ? input.url : String(input);
        if (!isApiRequest(url)) return originalFetch(input, init);

        // A Request's body can only be read once, so only plain calls can be repeated after a renewal.
        const retry = input instanceof Request ? null : () => originalFetch(input, withToken(input, init));
        const first = await originalFetch(input instanceof Request ? input.clone() : input, withToken(input, init));
        if (first.status !== 401 || !retry) return first;

        await renew();
        return retry();
    };

    XMLHttpRequest.prototype.open = function (method, url) {
        this.__qeIsApi = isApiRequest(url);
        return originalOpen.apply(this, arguments);
    };

    XMLHttpRequest.prototype.send = function () {
        if (this.__qeIsApi && token) this.setRequestHeader("Authorization", `Bearer ${token}`);
        return originalSend.apply(this, arguments);
    };

    document.addEventListener("click", async (event) => {
        const link = event.target.closest?.("a[data-auth-download]");
        if (!link) return;
        event.preventDefault();
        try {
            const res = await window.fetch(link.href);
            if (!res.ok) throw new Error(`HTTP ${res.status}`);
            const disposition = res.headers.get("Content-Disposition") || "";
            const name = /filename="?([^";]+)"?/i.exec(disposition)?.[1] || link.href.split("/").pop().split("?")[0];
            const objectUrl = URL.createObjectURL(await res.blob());
            const save = document.createElement("a");
            save.href = objectUrl;
            save.download = name;
            document.body.append(save);
            save.click();
            save.remove();
            setTimeout(() => URL.revokeObjectURL(objectUrl), 10000);
        } catch (err) {
            window.showToast?.(`Download failed: ${err.message}`, "error");
        }
    });

    setInterval(renew, RENEW_EVERY_MS);
    document.addEventListener("visibilitychange", () => { if (!document.hidden) renew(); });
})();
