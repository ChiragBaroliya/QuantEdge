/* ==========================================================================
   Trade Conditions > Rule numbers - every number behind the 11 conditions, the score thresholds, the market gate and the
   regime policies. Drawn from /api/strategyrules; the fields, limits and labels come from the server's SwingRuleCatalog.
   Tells trade-conditions.js the saved numbers through the "strategyrules:changed" event.
   ========================================================================== */
(function () {
    "use strict";

    const GROUPS = [
        { key: "MARKET", title: "1. NIFTY Market Filter", tag: "Hard" },
        { key: "TREND", title: "2. EMA Trend Alignment", tag: "Hard" },
        { key: "ADX", title: "3. ADX Trend Strength", tag: "Hard" },
        { key: "BREAKOUT", title: "4. Breakout / Breakdown", tag: "Score" },
        { key: "VOLUME", title: "5. Volume Confirmation", tag: "Score" },
        { key: "REL_STRENGTH", title: "6. Relative Strength vs NIFTY", tag: "Score" },
        { key: "MTF_60M", title: "7. 60m Confirmation", tag: "Score" },
        { key: "RSI", title: "8. RSI Zone", tag: "Score" },
        { key: "MACD", title: "9. MACD", tag: "Score" },
        { key: "CANDLE", title: "10. Candle", tag: "Score" },
        { key: "RISK_REWARD", title: "11. Risk : Reward", tag: "Score" }
    ];
    const POINT_KEYS = ["breakoutPoints", "volumePoints", "relativeStrengthPoints", "mtf60mPoints", "rsiPoints", "macdPoints", "candlePoints", "riskRewardPoints"];
    const REGIME_LABEL = { BULLISH: "Bullish", BULLISH_WEAKENING: "Bullish weakening", SIDEWAYS: "Sideways", BEARISH: "Bearish", STRONG_BEARISH: "Strong bearish" };

    let apiBaseUrl = "";
    let server = null;      // last response: fields, defaults, defaultPolicies, requiredTotalPoints
    let state = null;       // what the form shows
    let saved = null;       // what is stored
    let serverErrors = [];

    const $ = (id) => document.getElementById(id);
    const clone = (o) => JSON.parse(JSON.stringify(o));

    function el(tag, attrs, ...children) {
        const node = document.createElement(tag);
        for (const [k, v] of Object.entries(attrs || {})) {
            if (v === undefined || v === null || v === false) continue;
            if (k === "class") node.className = v;
            else if (k === "text") node.textContent = v;
            else if (k.startsWith("on")) node.addEventListener(k.slice(2), v);
            else node.setAttribute(k, v === true ? "" : v);
        }
        for (const child of children.flat()) {
            if (child !== null && child !== undefined && child !== false) node.append(child);
        }
        return node;
    }

    function stateFrom(dto) {
        return {
            rules: clone(dto.rules),
            policies: clone(dto.policies),
            buyScoreThreshold: dto.buyScoreThreshold,
            watchScoreThreshold: dto.watchScoreThreshold,
            marketGateMode: dto.marketGateMode
        };
    }

    const totalPoints = () => POINT_KEYS.reduce((sum, k) => sum + (Number(state.rules[k]) || 0), 0);

    function fieldProblem(f, raw) {
        const value = Number(raw);
        if (raw === null || raw === "" || !Number.isFinite(value)) return "Enter a number";
        if (value < f.min || value > f.max) return `Between ${f.min} and ${f.max}`;
        if (f.isInteger && !Number.isInteger(value)) return "Whole numbers only";
        return null;
    }

    function changeCount() {
        let n = 0;
        for (const f of server.fields) if (Number(state.rules[f.key]) !== saved.rules[f.key]) n++;
        for (const k of ["buyScoreThreshold", "watchScoreThreshold"]) if (Number(state[k]) !== saved[k]) n++;
        if (state.marketGateMode !== saved.marketGateMode) n++;
        state.policies.forEach((p, i) => {
            const o = saved.policies[i];
            if (Number(p.minStockScore) !== o.minStockScore || p.requireRelativeStrength !== o.requireRelativeStrength
                || Number(p.maxPositions) !== o.maxPositions || Number(p.riskPct) !== o.riskPct) n++;
        });
        return n;
    }

    // ---------------------------------------------------------------- rendering
    function renderField(f) {
        const problem = fieldProblem(f, state.rules[f.key]);
        const def = server.defaults[f.key];
        const changed = Number(state.rules[f.key]) !== def;
        const id = `srf-${f.key}`;
        return el("div", { class: `sr-field${changed ? " is-changed" : ""}${problem ? " is-invalid" : ""}`, "data-key": f.key },
            el("label", { for: id, text: f.label }),
            el("div", { class: "sr-input-row" },
                el("input", {
                    id, type: "number", class: "tc-input", min: f.min, max: f.max, step: f.step, value: state.rules[f.key],
                    oninput: (e) => { state.rules[f.key] = e.target.value; refresh(f.key); }
                }),
                f.unit ? el("span", { class: "sr-unit", text: f.unit }) : null),
            el("p", { class: "tc-hint sr-field-hint", text: problem || `Default ${def}${f.help ? ` · ${f.help}` : ""}` }));
    }

    function renderGroups() {
        const host = $("srGroups");
        host.replaceChildren();
        for (const g of GROUPS) {
            const fields = server.fields.filter(f => f.group === g.key);
            host.append(el("section", { class: "tc-card sr-group" },
                el("div", { class: "tc-card-head" },
                    el("h3", {}, g.title, " ", el("span", { class: `tc-badge ${g.tag === "Hard" ? "tc-badge-hard" : "tc-badge-score"}`, text: g.tag })),
                    el("button", { type: "button", class: "tc-link-btn", text: "Default", onclick: () => resetGroup(g.key) })),
                el("div", { class: "sr-grid" }, fields.map(renderField))));
        }
    }

    function renderPolicies() {
        const body = $("srPolicyBody");
        body.replaceChildren();
        state.policies.forEach((p, i) => {
            const number = (key, attrs) => el("input", {
                type: "number", class: "tc-input", value: p[key], "aria-label": `${REGIME_LABEL[p.regime] || p.regime}: ${attrs.label}`, min: attrs.min, max: attrs.max, step: attrs.step,
                oninput: (e) => { p[key] = e.target.value; refreshSummary(); }
            });
            body.append(el("tr", {},
                el("th", { scope: "row", text: REGIME_LABEL[p.regime] || p.regime }),
                el("td", { "data-label": "Min score" }, number("minStockScore", { label: "minimum score", min: 0, max: 100, step: 1 })),
                el("td", { "data-label": "Must beat NIFTY" }, el("input", {
                    type: "checkbox", checked: p.requireRelativeStrength, "aria-label": `${REGIME_LABEL[p.regime] || p.regime}: must beat NIFTY`,
                    onchange: (e) => { p.requireRelativeStrength = e.target.checked; refreshSummary(); }
                })),
                el("td", { "data-label": "Max positions" }, number("maxPositions", { label: "maximum positions", min: 0, max: 50, step: 1 })),
                el("td", { "data-label": "Risk % (Auto Paper)" }, number("riskPct", { label: "risk percent, Auto Paper only", min: 0.05, max: 5, step: 0.05 }))));
        });
    }

    function renderTop() {
        $("srBuy").value = state.buyScoreThreshold;
        $("srWatch").value = state.watchScoreThreshold;
        $("srGate").value = state.marketGateMode;
    }

    function refresh(key) {
        const f = server.fields.find(x => x.key === key);
        const node = document.querySelector(`.sr-field[data-key="${key}"]`);
        if (f && node) {
            const problem = fieldProblem(f, state.rules[key]);
            const def = server.defaults[key];
            node.classList.toggle("is-changed", Number(state.rules[key]) !== def);
            node.classList.toggle("is-invalid", !!problem);
            node.querySelector(".sr-field-hint").textContent = problem || `Default ${def}${f.help ? ` · ${f.help}` : ""}`;
        }
        refreshSummary();
    }

    function refreshSummary() {
        const total = totalPoints();
        const need = server.requiredTotalPoints;
        const pts = $("srPointsText");
        pts.textContent = `${total} / ${need}`;
        pts.classList.toggle("is-bad", total !== need);
        $("srPointsHint").textContent = total === need
            ? "Scoring points add up correctly."
            : `${total < need ? `Add ${need - total}` : `Remove ${total - need}`} points: the total must be exactly ${need}.`;

        const regime = state.marketGateMode === "REGIME";
        $("srGateHint").textContent = regime
            ? "Market regime: NIFTY no longer blocks every BUY. A strong stock can still be bought in a falling market, on the terms in the Market regime table below."
            : "NIFTY filter: all or nothing. No stock is bought while NIFTY fails condition 1.";
        $("srPolicyState").textContent = regime ? "In use" : "Not in use (market gate is NIFTY filter)";
        $("srPolicyState").className = `tc-pill ${regime ? "tc-pill-on" : "tc-pill-muted"}`;

        const changes = changeCount();
        const invalid = server.fields.filter(f => fieldProblem(f, state.rules[f.key]));
        const dirty = $("srDirty");
        dirty.hidden = changes === 0;
        dirty.textContent = `● Unsaved: ${changes} change${changes === 1 ? "" : "s"}`;
        $("srSave").disabled = changes === 0 || invalid.length > 0 || total !== need || !$("srConfirm").checked;

        const alerts = $("srAlerts");
        alerts.replaceChildren();
        for (const message of serverErrors) alerts.append(el("div", { class: "tc-alert tc-alert-error", text: message }));
        if (invalid.length) alerts.append(el("div", { class: "tc-alert tc-alert-error", text: `${invalid.length} value${invalid.length === 1 ? " is" : "s are"} out of range. Fix the fields marked in red.` }));
    }

    function render() {
        renderTop();
        renderGroups();
        renderPolicies();
        refreshSummary();
    }

    // ---------------------------------------------------------------- actions
    function resetGroup(group) {
        for (const f of server.fields) if (f.group === group) state.rules[f.key] = server.defaults[f.key];
        serverErrors = [];
        renderGroups();
        refreshSummary();
    }

    function resetAll() {
        state.rules = clone(server.defaults);
        state.policies = clone(server.defaultPolicies);
        serverErrors = [];
        renderGroups();
        renderPolicies();
        refreshSummary();
        window.showToast?.("Original numbers restored on this form, including the market regime table. Save to keep them.", "info");
    }

    function publish(dto) {
        document.dispatchEvent(new CustomEvent("strategyrules:changed", {
            detail: { rules: dto.rules, buyScoreThreshold: dto.buyScoreThreshold, watchScoreThreshold: dto.watchScoreThreshold, marketGateMode: dto.marketGateMode }
        }));
    }

    function accept(dto) {
        server = dto;
        saved = stateFrom(dto);
        state = stateFrom(dto);
        serverErrors = [];
        $("srConfirm").checked = false;
        render();
        publish(dto);
    }

    async function save() {
        const numbers = {};
        for (const f of server.fields) numbers[f.key] = Number(state.rules[f.key]);
        const body = {
            rules: numbers,
            policies: state.policies.map(p => ({
                regime: p.regime,
                minStockScore: Number(p.minStockScore),
                requireRelativeStrength: p.requireRelativeStrength,
                maxPositions: Number(p.maxPositions),
                riskPct: Number(p.riskPct)
            })),
            buyScoreThreshold: Number(state.buyScoreThreshold),
            watchScoreThreshold: Number(state.watchScoreThreshold),
            marketGateMode: state.marketGateMode,
            confirmLive: $("srConfirm").checked
        };

        const btn = $("srSave");
        btn.disabled = true;
        try {
            const res = await fetch(`${apiBaseUrl}/api/strategyrules`, {
                method: "PUT",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify(body)
            });
            if (!res.ok) {
                let payload = null;
                try { payload = await res.json(); } catch { /* not JSON */ }
                serverErrors = payload?.errors?.length ? payload.errors : [payload?.message || `HTTP ${res.status}`];
                refreshSummary();
                window.showToast?.("Not saved - see the problems listed above the Save button.", "error");
                return;
            }
            accept(await res.json());
            window.showToast?.("Rule numbers saved. Both bots use them within 5 minutes, from their next scan.", "success");
        } catch (err) {
            serverErrors = [err.message];
            refreshSummary();
            window.showToast?.(`Not saved: ${err.message}`, "error");
        }
    }

    async function init() {
        const config = $("tc-config");
        if (!$("srGroups")) return;
        apiBaseUrl = config?.dataset.apiBaseUrl || "";

        for (const btn of document.querySelectorAll("#tcViewTabs .tc-seg-btn")) {
            btn.addEventListener("click", () => {
                const view = btn.dataset.view;
                for (const other of document.querySelectorAll("#tcViewTabs .tc-seg-btn")) {
                    const active = other === btn;
                    other.classList.toggle("active", active);
                    other.setAttribute("aria-selected", String(active));
                }
                $("tcViewConditions").hidden = view !== "conditions";
                $("tcViewRules").hidden = view !== "rules";
            });
        }

        const top = (id, key) => $(id).addEventListener("input", (e) => { state[key] = e.target.value; refreshSummary(); });
        top("srBuy", "buyScoreThreshold");
        top("srWatch", "watchScoreThreshold");
        $("srGate").addEventListener("change", (e) => { state.marketGateMode = e.target.value; refreshSummary(); });
        $("srConfirm").addEventListener("change", refreshSummary);
        $("srResetAll").addEventListener("click", resetAll);
        $("srSave").addEventListener("click", save);

        try {
            const res = await fetch(`${apiBaseUrl}/api/strategyrules`);
            if (!res.ok) throw new Error(`HTTP ${res.status}`);
            accept(await res.json());
            $("srLoading").hidden = true;
            $("srForm").hidden = false;
        } catch (err) {
            $("srLoading").textContent = `Rule numbers could not be loaded (${err.message}). Nothing can be changed until the page reloads.`;
            $("srLoading").className = "tc-alert tc-alert-error";
        }
    }

    document.addEventListener("DOMContentLoaded", init);
})();
