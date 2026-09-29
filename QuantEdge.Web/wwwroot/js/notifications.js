/**
 * Header notification bell - today only.
 *
 * The server builds the feed (GET /api/notifications/today) from data it already records: real trade
 * execution logs, swing scan slots with BUY picks and a NIFTY market filter flip - always scoped to the
 * IST calendar day, so nothing from yesterday ever shows. SignalR events only trigger a refetch; the
 * server stays the single source of truth. Read state is per browser: the ids already seen are kept in
 * localStorage under a key that includes the IST date, so it resets itself at midnight.
 */
(function () {
    const root = document.getElementById("qeNotif");
    if (!root) return;

    const API_BASE = (root.dataset.apiBaseUrl || "").replace(/\/+$/, "");
    const USER_ID = parseInt(root.dataset.userId, 10) || 1;
    const POLL_MS = 60000;
    const REFRESH_DEBOUNCE_MS = 1500;
    const SERVER_CACHE_MS = 35000; // server caches the swing/NIFTY part for 30s - refetch once more after it expires
    const STORAGE_PREFIX = `qe-notif-seen-${USER_ID}-`;
    const NOTIFY_ACTIONS = new Set([
        "REAL_BUY", "REAL_SELL", "BUY_ORDER_OPEN", "SELL_ORDER_OPEN", "TRAILING_SL_ACTIVATED",
        "HOLDING_MONITOR_ENABLED", "ORDER_REJECTED", "SELL_FAILED", "SYSTEM_ERROR", "LIVE_ENABLE_FAILED",
        "TOKEN_EXPIRED", "KILL_SWITCH_ACTIVE", "CIRCUIT_BREAKER"
    ]);
    const CATEGORY_LINKS = { "real-trade": "/RealTrading", "swing": "/SwingTrading", "nifty": "/" };
    const CATEGORY_LABELS = { "real-trade": "Real Trade", "swing": "Swing", "nifty": "NIFTY" };

    const els = {
        btn: document.getElementById("qeNotifBtn"),
        count: document.getElementById("qeNotifCount"),
        list: document.getElementById("qeNotifList"),
        date: document.getElementById("qeNotifDate"),
        markAll: document.getElementById("qeNotifMarkAll"),
        filters: root.querySelectorAll(".qe-notif-filters button")
    };

    let items = [];
    let dateKey = null;          // IST date from the server, yyyy-MM-dd
    let seen = new Set();
    let knownIds = null;         // ids from the previous fetch; null until the first load (no toasts then)
    let filter = "all";
    let refreshTimer = null;

    // ---- localStorage (per-browser read state, one key per IST day) ----
    function loadSeen(key) {
        try {
            // Drop keys from previous days so storage doesn't grow.
            for (let i = localStorage.length - 1; i >= 0; i--) {
                const k = localStorage.key(i);
                if (k && k.startsWith(STORAGE_PREFIX) && k !== STORAGE_PREFIX + key) localStorage.removeItem(k);
            }
            return new Set(JSON.parse(localStorage.getItem(STORAGE_PREFIX + key) || "[]"));
        } catch (e) {
            return new Set();
        }
    }

    function saveSeen() {
        if (!dateKey) return;
        try { localStorage.setItem(STORAGE_PREFIX + dateKey, JSON.stringify([...seen])); } catch (e) { }
    }

    function markRead(ids) {
        let changed = false;
        ids.forEach(id => { if (!seen.has(id)) { seen.add(id); changed = true; } });
        if (changed) { saveSeen(); render(); }
    }

    // ---- rendering ----
    const escapeHtml = (s) => String(s ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
    const timeIst = (iso) => new Date(iso).toLocaleTimeString("en-IN", { timeZone: "Asia/Kolkata", hour: "2-digit", minute: "2-digit" });

    function render() {
        const unread = items.filter(i => !seen.has(i.id)).length;
        els.count.hidden = unread === 0;
        els.count.textContent = unread > 99 ? "99+" : String(unread);
        els.btn.classList.toggle("has-unread", unread > 0);
        els.btn.setAttribute("aria-label", unread ? `Today's notifications, ${unread} unread` : "Today's notifications");

        const visible = filter === "all" ? items : items.filter(i => i.category === filter);
        if (visible.length === 0) {
            els.list.innerHTML = `<div class="qe-notif-empty">${items.length === 0 ? "No notifications today yet." : "Nothing in this category today."}</div>`;
            return;
        }

        els.list.innerHTML = visible.map(i => `
            <a class="qe-notif-item level-${escapeHtml(i.level)} ${seen.has(i.id) ? "" : "unread"}"
               href="${CATEGORY_LINKS[i.category] || "#"}" data-id="${escapeHtml(i.id)}">
                <span class="qe-notif-dot" aria-hidden="true"></span>
                <span class="qe-notif-body">
                    <span class="qe-notif-row">
                        <span class="qe-notif-item-title">${escapeHtml(i.title)}</span>
                        <span class="qe-notif-time">${timeIst(i.timeUtc)}</span>
                    </span>
                    <span class="qe-notif-msg">${escapeHtml(i.message)}</span>
                    <span class="qe-notif-cat">${CATEGORY_LABELS[i.category] || escapeHtml(i.category)}</span>
                </span>
            </a>`).join("");
    }

    function toastNew(newItems) {
        if (typeof window.showToast !== "function") return;
        // The Real Trading page already toasts its own trade alerts.
        const onRealTrading = location.pathname.toLowerCase().startsWith("/realtrading");
        newItems
            .filter(i => !(onRealTrading && i.category === "real-trade"))
            .slice(0, 3)
            .forEach(i => {
                const type = i.level === "error" ? "error" : i.level === "warning" ? "warning" : i.level === "success" ? "success" : "info";
                window.showToast(`<strong>${escapeHtml(i.title)}</strong><br>${escapeHtml(i.message)}`, type, 6000);
            });
    }

    // ---- data ----
    async function load() {
        try {
            const res = await fetch(`${API_BASE}/api/notifications/today?userId=${USER_ID}`);
            if (!res.ok) throw new Error(`HTTP ${res.status}`);
            const data = await res.json();

            const newDateKey = String(data.dateIst || "").slice(0, 10);
            if (newDateKey !== dateKey) {
                dateKey = newDateKey;
                seen = loadSeen(dateKey);
            }
            items = Array.isArray(data.items) ? data.items : [];

            if (knownIds) toastNew(items.filter(i => !knownIds.has(i.id) && !seen.has(i.id)));
            knownIds = new Set(items.map(i => i.id));

            const d = new Date(`${dateKey}T00:00:00`);
            els.date.textContent = `${d.toLocaleDateString("en-IN", { day: "numeric", month: "short" })} · clears at midnight IST`;
            render();
        } catch (ex) {
            console.error("Notifications refresh failed:", ex);
            if (!items.length) els.list.innerHTML = `<div class="qe-notif-empty">Couldn't load notifications.</div>`;
        }
    }

    function scheduleRefresh(followUpAfterServerCache) {
        clearTimeout(refreshTimer);
        refreshTimer = setTimeout(load, REFRESH_DEBOUNCE_MS);
        if (followUpAfterServerCache) setTimeout(load, SERVER_CACHE_MS);
    }

    // ---- live updates ----
    function connect() {
        if (typeof signalR === "undefined") return;

        const conn = new signalR.HubConnectionBuilder()
            .withUrl(`${API_BASE}/hubs/marketdata`)
            .withAutomaticReconnect()
            .build();

        const forMe = (d) => !d || !d.userId || Number(d.userId) === USER_ID;
        conn.on("ReceiveRealTradeLogEvent", (log) => {
            if (log && forMe(log) && NOTIFY_ACTIONS.has(String(log.actionType || "").toUpperCase())) scheduleRefresh(false);
        });
        conn.on("ReceiveRealTradeAlert", (d) => { if (forMe(d)) scheduleRefresh(false); });
        conn.on("ReceiveSwingSlotUpdate", () => scheduleRefresh(true));
        conn.on("ReceiveSwingDashboardUpdate", () => scheduleRefresh(true)); // NIFTY status rides along
        conn.onreconnected(() => scheduleRefresh(false));

        conn.start().catch(err => {
            console.error("Notifications SignalR connection error:", err);
            setTimeout(connect, 10000);
        });
    }

    function ensureSignalR(callback) {
        if (typeof signalR !== "undefined") return callback();
        const s = document.createElement("script");
        s.src = "/js/theme/signalr.min.js";
        s.onload = callback;
        document.head.appendChild(s);
    }

    // ---- UI events ----
    els.filters.forEach(b => b.addEventListener("click", () => {
        els.filters.forEach(x => x.classList.toggle("active", x === b));
        filter = b.dataset.filter;
        render();
    }));

    els.markAll.addEventListener("click", () => markRead(items.map(i => i.id)));

    els.list.addEventListener("click", (e) => {
        const a = e.target.closest(".qe-notif-item");
        if (a) markRead([a.dataset.id]);
    });

    // Items stay highlighted while the panel is open, then count as read once it's closed.
    root.addEventListener("hidden.bs.dropdown", () => markRead(items.map(i => i.id)));

    document.addEventListener("visibilitychange", () => { if (!document.hidden) load(); });

    load();
    setInterval(() => { if (!document.hidden) load(); }, POLL_MS);
    ensureSignalR(connect);
})();
