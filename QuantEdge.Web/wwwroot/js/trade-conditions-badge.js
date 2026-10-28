/* Trade Conditions header badge (Auto Paper / Auto Real pages): conditions ON per side, from /api/tradeconditions. */
(function () {
    "use strict";

    function sideText(label, s) {
        return `${label} ${s.enabledCount}/${s.totalCount}`;
    }

    function sideTooltip(label, s) {
        const hardOff = s.hardFiltersTotal - s.hardFiltersOn;
        let text = `${label}: ${s.enabledCount} of ${s.totalCount} conditions on, at least ${s.minConditionsMatch} must match`;
        if (hardOff > 0) text += ` (${hardOff} hard filter${hardOff > 1 ? "s" : ""} OFF)`;
        return text + (s.isDefault ? " - default." : " - custom.");
    }

    async function fill(badge) {
        const text = badge.querySelector("[data-tc-text]");
        const mode = badge.dataset.mode || "AUTO_PAPER";
        const query = mode === "AUTO_REAL" ? `mode=AUTO_REAL&userId=${encodeURIComponent(badge.dataset.userId || "1")}` : "mode=AUTO_PAPER";

        try {
            const res = await fetch(`${badge.dataset.apiBaseUrl || ""}/api/tradeconditions?${query}`);
            if (!res.ok) throw new Error(`HTTP ${res.status}`);
            const dto = await res.json();

            text.textContent = `Conditions · ${sideText("BUY", dto.buy)} · ${sideText("SHORT", dto.short)}`;
            const custom = !dto.buy.isDefault || !dto.short.isDefault;
            const hardOff = [dto.buy, dto.short].some(s => s.hardFiltersOn < s.hardFiltersTotal);
            badge.classList.toggle("is-custom", custom && !hardOff);
            badge.classList.toggle("has-risk", hardOff);

            let tip = `${sideTooltip("BUY", dto.buy)}\n${sideTooltip("SHORT", dto.short)}`;
            if (badge.dataset.canConfigure === "true") tip += "\nClick to change them on the Trade Conditions page.";
            badge.title = tip;
        } catch (err) {
            text.textContent = "Conditions: unavailable";
            badge.title = `Trade Conditions could not be loaded (${err.message}).`;
        }
    }

    document.addEventListener("DOMContentLoaded", () => {
        document.querySelectorAll(".js-trade-conditions-badge").forEach(fill);
    });
})();
