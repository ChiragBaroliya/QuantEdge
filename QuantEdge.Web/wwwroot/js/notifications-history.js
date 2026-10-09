/**
 * View All Notifications page (Notifications/Index).
 *
 * Loads GET /api/notifications/history?from=&to= (IST dates, max 31 days - the same sources as the header bell) and
 * filters, groups by IST day and pages it in the browser. Today's items use the bell's per-browser read state
 * (localStorage, notifications.js), so anything the bell hasn't shown as read is marked NEW here too.
 */
(function () {
    const page = document.getElementById("notificationHistoryPage");
    if (!page) return;

    const API_BASE = (page.dataset.api || "").replace(/\/+$/, "");
    const USER_ID = parseInt(page.dataset.user, 10) || 1;
    const PAGE_SIZE = 50;
    const CATEGORY_LINKS = { "real-trade": "/RealTrading", "swing": "/SwingTrading", "nifty": "/", "zerodha": "/Token" };
    const CATEGORY_LABELS = { "real-trade": "Real Trade", "swing": "Swing", "nifty": "NIFTY", "zerodha": "Zerodha/NSE" };
    const LEVEL_LABELS = { error: "Error", warning: "Warning", info: "Info", success: "Success" };

    const $ = (id) => document.getElementById(id);
    const els = {
        from: $("nhFrom"), to: $("nhTo"), level: $("nhLevel"), search: $("nhSearch"),
        list: $("nhList"), summary: $("nhSummary"), truncated: $("nhTruncated"),
        pageInfo: $("nhPageInfo"), prev: $("nhPrev"), next: $("nhNext"), refresh: $("nhRefresh"),
        presets: page.querySelectorAll(".nh-presets .nh-chip"),
        categories: page.querySelectorAll("#nhCategories .nh-chip")
    };

    let items = [];
    let category = "all";
    let pageIndex = 0;
    let todayKey = "";
    let seenToday = new Set();

    const escapeHtml = (s) => String(s ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
    const istDateKey = (d) => new Intl.DateTimeFormat("en-CA", { timeZone: "Asia/Kolkata" }).format(d); // yyyy-MM-dd
    const timeIst = (iso) => new Date(iso).toLocaleTimeString("en-IN", { timeZone: "Asia/Kolkata", hour: "2-digit", minute: "2-digit", second: "2-digit" });
    const dayLabel = (key) => {
        const d = new Date(`${key}T00:00:00`);
        const label = d.toLocaleDateString("en-IN", { weekday: "short", day: "numeric", month: "short", year: "numeric" });
        return key === todayKey ? `Today · ${label}` : label;
    };

    function addDays(key, days) {
        const d = new Date(`${key}T00:00:00`);
        d.setDate(d.getDate() + days);
        return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
    }

    function loadSeenToday() {
        try { return new Set(JSON.parse(localStorage.getItem(`qe-notif-seen-${USER_ID}-${todayKey}`) || "[]")); }
        catch (e) { return new Set(); }
    }

    // ---- filtering / rendering ----
    function filtered() {
        const level = els.level.value;
        const q = els.search.value.trim().toLowerCase();
        return items.filter(i =>
            (category === "all" || i.category === category) &&
            (level === "all" || i.level === level) &&
            (!q || `${i.title} ${i.message} ${i.symbol || ""}`.toLowerCase().includes(q)));
    }

    function renderCounts() {
        const counts = { all: items.length };
        items.forEach(i => { counts[i.category] = (counts[i.category] || 0) + 1; });
        page.querySelectorAll("[data-count]").forEach(el => { el.textContent = counts[el.dataset.count] || 0; });

        const byLevel = { error: 0, warning: 0, info: 0, success: 0 };
        items.forEach(i => { if (i.level in byLevel) byLevel[i.level]++; });
        els.summary.innerHTML = `
            <div class="nh-stat"><div class="k">Total</div><div class="v">${items.length}</div></div>
            ${Object.keys(byLevel).map(l => `<div class="nh-stat ${l}"><div class="k">${LEVEL_LABELS[l]}</div><div class="v">${byLevel[l]}</div></div>`).join("")}`;
    }

    function render() {
        const rows = filtered();
        const pages = Math.max(1, Math.ceil(rows.length / PAGE_SIZE));
        if (pageIndex >= pages) pageIndex = pages - 1;
        const slice = rows.slice(pageIndex * PAGE_SIZE, (pageIndex + 1) * PAGE_SIZE);

        if (slice.length === 0) {
            els.list.innerHTML = `<div class="nh-empty">${items.length === 0 ? "No notifications in this date range." : "No notifications match these filters."}</div>`;
        } else {
            let html = "";
            let currentDay = null;
            slice.forEach(i => {
                const day = istDateKey(new Date(i.timeUtc));
                if (day !== currentDay) {
                    currentDay = day;
                    html += `<div class="nh-day">${escapeHtml(dayLabel(day))}</div>`;
                }
                const unread = day === todayKey && !seenToday.has(i.id);
                html += `
                    <a class="nh-item level-${escapeHtml(i.level)} ${unread ? "unread" : ""}" href="${CATEGORY_LINKS[i.category] || "#"}">
                        <span class="nh-dot" aria-hidden="true"></span>
                        <span class="nh-body">
                            <span class="nh-row">
                                <span class="nh-title">${escapeHtml(i.title)}</span>
                                <span class="nh-time">${timeIst(i.timeUtc)}</span>
                            </span>
                            <div class="nh-msg">${escapeHtml(i.message)}</div>
                            <span class="nh-tags">
                                <span class="nh-tag">${escapeHtml(CATEGORY_LABELS[i.category] || i.category)}</span>
                                <span class="nh-tag">${escapeHtml(LEVEL_LABELS[i.level] || i.level)}</span>
                            </span>
                        </span>
                    </a>`;
            });
            els.list.innerHTML = html;
        }

        const first = rows.length === 0 ? 0 : pageIndex * PAGE_SIZE + 1;
        els.pageInfo.textContent = `${first}-${pageIndex * PAGE_SIZE + slice.length} of ${rows.length}` + (rows.length !== items.length ? ` (filtered from ${items.length})` : "");
        els.prev.disabled = pageIndex === 0;
        els.next.disabled = pageIndex >= pages - 1;
    }

    // ---- data ----
    async function load() {
        els.list.innerHTML = `<div class="nh-empty">Loading…</div>`;
        try {
            const params = new URLSearchParams({ userId: USER_ID, from: els.from.value, to: els.to.value });
            const res = await fetch(`${API_BASE}/api/notifications/history?${params}`);
            if (!res.ok) throw new Error(`HTTP ${res.status}`);
            const data = await res.json();

            items = Array.isArray(data.items) ? data.items : [];
            // The server may have clamped the range (max 31 days, never past today) - show what was actually used.
            if (data.fromIst) els.from.value = String(data.fromIst).slice(0, 10);
            if (data.toIst) els.to.value = String(data.toIst).slice(0, 10);
            els.truncated.hidden = !data.truncated;
            seenToday = loadSeenToday();
            pageIndex = 0;
            renderCounts();
            render();
        } catch (ex) {
            console.error("Notification history load failed:", ex);
            items = [];
            renderCounts();
            els.list.innerHTML = `<div class="nh-empty">Couldn't load notifications. Check that the API is reachable and try Refresh.</div>`;
        }
    }

    function setRange(days) {
        els.to.value = todayKey;
        els.from.value = addDays(todayKey, -(days - 1));
        els.presets.forEach(b => b.classList.toggle("active", Number(b.dataset.days) === days));
        load();
    }

    // ---- events ----
    els.presets.forEach(b => b.addEventListener("click", () => setRange(Number(b.dataset.days))));
    [els.from, els.to].forEach(input => input.addEventListener("change", () => {
        els.presets.forEach(b => b.classList.remove("active"));
        load();
    }));
    els.categories.forEach(b => b.addEventListener("click", () => {
        els.categories.forEach(x => x.classList.toggle("active", x === b));
        category = b.dataset.category;
        pageIndex = 0;
        render();
    }));
    els.level.addEventListener("change", () => { pageIndex = 0; render(); });
    let searchTimer = null;
    els.search.addEventListener("input", () => {
        clearTimeout(searchTimer);
        searchTimer = setTimeout(() => { pageIndex = 0; render(); }, 200);
    });
    els.prev.addEventListener("click", () => { pageIndex--; render(); window.scrollTo({ top: 0, behavior: "smooth" }); });
    els.next.addEventListener("click", () => { pageIndex++; render(); window.scrollTo({ top: 0, behavior: "smooth" }); });
    els.refresh.addEventListener("click", load);

    // Deep link: /Notifications?category=zerodha
    const qsCategory = new URLSearchParams(location.search).get("category");
    if (qsCategory && CATEGORY_LABELS[qsCategory]) {
        category = qsCategory;
        els.categories.forEach(x => x.classList.toggle("active", x.dataset.category === category));
    }

    todayKey = istDateKey(new Date());
    setRange(7);
})();
