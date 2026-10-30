/* ==========================================================================
   Trade Conditions - which of the 11 BUY / SHORT conditions the Auto Paper and Auto Real bots apply.
   Loads and saves through /api/tradeconditions; the scan workers read the saved selection on every scan.
   ========================================================================== */
(function () {
    "use strict";

    const MODES = ["AUTO_PAPER", "AUTO_REAL"];
    const SIDES = ["BUY", "SHORT"];
    const MODE_LABEL = { AUTO_PAPER: "Auto Paper", AUTO_REAL: "Auto Real" };

    // The 11 conditions, in the order both engines build their checklist.
    const CONDITIONS = [
        { key: "MARKET", no: 1, hard: true, pts: 0, tf: "1D",
          name: { BUY: "NIFTY Market Filter", SHORT: "NIFTY Bearish Filter" },
          rule: { BUY: "NIFTY 50 Close > 50 DMA and EMA20 > EMA50", SHORT: "NIFTY 50 Close < 50 DMA and EMA20 < EMA50" },
          why: "Keeps the bot out when the whole market is moving against the trade. Missing NIFTY data counts as a fail." },
        { key: "TREND", no: 2, hard: true, pts: 0, tf: "1D",
          name: { BUY: "EMA Trend Alignment", SHORT: "EMA Downtrend Alignment" },
          rule: { BUY: "Daily Close > EMA20 > EMA50, both rising; EMA200 not falling", SHORT: "Daily Close < EMA20 < EMA50, both falling; EMA200 not rising" },
          why: "Only trade stocks already trending in the trade's direction." },
        { key: "ADX", no: 3, hard: true, pts: 0, tf: "1D",
          name: { BUY: "ADX Trend Strength", SHORT: "ADX Trend Strength" },
          rule: { BUY: "Daily ADX(14) ≥ 20", SHORT: "Daily ADX(14) ≥ 20" },
          why: "Filters out choppy, sideways stocks." },
        { key: "BREAKOUT", no: 4, hard: false, pts: 20, tf: "15m",
          name: { BUY: "Breakout", SHORT: "Breakdown" },
          rule: { BUY: "Above previous day high or 15-bar swing high, a ≤10% range breakout, or within 10% of the 52W high",
                  SHORT: "Below previous day low or swing low, a ≤10% range breakdown, or within 10% of the 52W low" },
          why: "The price is leaving its range." },
        { key: "VOLUME", no: 5, hard: false, pts: 15, tf: "15m",
          name: { BUY: "Volume Confirmation", SHORT: "Volume Confirmation" },
          rule: { BUY: "≥ 1.5× usual volume for this time slot = 10 pts; ≥ 2.5× and above the previous bar = 15 pts",
                  SHORT: "≥ 1.5× usual volume for this time slot = 10 pts; ≥ 2.5× and above the previous bar = 15 pts" },
          why: "Real participation behind the move." },
        { key: "REL_STRENGTH", no: 6, hard: false, pts: 15, tf: "1D",
          name: { BUY: "Relative Strength vs NIFTY", SHORT: "Relative Weakness vs NIFTY" },
          rule: { BUY: "Stock's 1-month return beats NIFTY 50's over the same dates", SHORT: "Stock's 1-month return is below NIFTY 50's" },
          why: "Prefer leaders for longs and laggards for shorts." },
        { key: "MTF_60M", no: 7, hard: false, pts: 15, tf: "60m",
          name: { BUY: "60m Trend Confirmation", SHORT: "60m Downtrend Confirmation" },
          rule: { BUY: "60m Close > 60m EMA20 and 60m RSI ≥ 40", SHORT: "60m Close < 60m EMA20 and 60m RSI ≤ 60" },
          why: "The hourly chart agrees. No 60m data = no points." },
        { key: "RSI", no: 8, hard: false, pts: 10, tf: "15m",
          name: { BUY: "RSI Momentum Zone", SHORT: "RSI Bearish Zone" },
          rule: { BUY: "RSI(14) 55–70 = 10 pts; 50–75 = 7 pts", SHORT: "RSI(14) 30–45 = 10 pts; 25–50 = 7 pts" },
          why: "Momentum without being overbought / oversold." },
        { key: "MACD", no: 9, hard: false, pts: 10, tf: "15m",
          name: { BUY: "MACD Bullish", SHORT: "MACD Bearish" },
          rule: { BUY: "MACD line above the signal line", SHORT: "MACD line below the signal line" },
          why: "Short-term momentum direction." },
        { key: "CANDLE", no: 10, hard: false, pts: 8, tf: "15m",
          name: { BUY: "Bullish Candle", SHORT: "Bearish Candle" },
          rule: { BUY: "Green candle closing in the top 25% of its range, or body ≥ 1.2× ATR",
                  SHORT: "Red candle closing in the bottom 25% of its range, or body ≥ 1.2× ATR" },
          why: "The latest bar closes strongly in the trade's direction." },
        { key: "RISK_REWARD", no: 11, hard: false, pts: 7, tf: "15m",
          name: { BUY: "Risk : Reward", SHORT: "Risk : Reward" },
          rule: { BUY: "Stop 1.5× ATR, Target 1 = 2R → R:R ≥ 1:2", SHORT: "Stop 1.5× ATR, Target 1 = 2R → R:R ≥ 1:2" },
          why: "The suggested target is worth the risk." }
    ];

    // The saved rule numbers (strategy-rules.js): the points and rule text above are the originals, shown until these arrive.
    const POINT_KEY = {
        BREAKOUT: "breakoutPoints", VOLUME: "volumePoints", REL_STRENGTH: "relativeStrengthPoints", MTF_60M: "mtf60mPoints",
        RSI: "rsiPoints", MACD: "macdPoints", CANDLE: "candlePoints", RISK_REWARD: "riskRewardPoints"
    };
    const LIVE_RULE = {
        MARKET: (r, s) => s === "BUY"
            ? `NIFTY 50 Close > ${r.niftySmaPeriod} DMA and EMA${r.niftyEmaFast} > EMA${r.niftyEmaSlow}`
            : `NIFTY 50 Close < ${r.niftySmaPeriod} DMA and EMA${r.niftyEmaFast} < EMA${r.niftyEmaSlow}`,
        TREND: (r, s) => s === "BUY"
            ? `Daily Close > EMA${r.trendEmaFast} > EMA${r.trendEmaSlow}, both rising; EMA${r.trendEmaLong} not falling`
            : `Daily Close < EMA${r.trendEmaFast} < EMA${r.trendEmaSlow}, both falling; EMA${r.trendEmaLong} not rising`,
        ADX: (r) => `Daily ADX(${r.adxPeriod}) ≥ ${r.adxMin}`,
        BREAKOUT: (r, s) => s === "BUY"
            ? `Above previous day high or ${r.swingLookbackBars}-bar swing high, a ≤${r.consolidationMaxRangePct}% range breakout, or within ${r.near52WeekPct}% of the 52W high`
            : `Below previous day low or swing low, a ≤${r.consolidationMaxRangePct}% range breakdown, or within ${r.near52WeekPct}% of the 52W low`,
        VOLUME: (r) => `≥ ${r.volumeGoodMultiple}× usual volume for this time slot = ${r.volumeGoodPoints} pts; ≥ ${r.volumeHeavyMultiple}× and above the previous bar = ${r.volumePoints} pts`,
        REL_STRENGTH: (r, s) => s === "BUY"
            ? `Stock's ${r.relativeStrengthBars}-day return beats NIFTY 50's over the same dates`
            : `Stock's ${r.relativeStrengthBars}-day return is below NIFTY 50's`,
        MTF_60M: (r, s) => s === "BUY"
            ? `60m Close > 60m EMA${r.mtf60mEmaPeriod} and 60m RSI ≥ ${r.mtf60mRsiMinBuy}`
            : `60m Close < 60m EMA${r.mtf60mEmaPeriod} and 60m RSI ≤ ${r.mtf60mRsiMaxShort}`,
        RSI: (r, s) => s === "BUY"
            ? `RSI(${r.rsiPeriod}) ${r.rsiBuyBestLow}–${r.rsiBuyBestHigh} = ${r.rsiPoints} pts; ${r.rsiBuyOkLow}–${r.rsiBuyOkHigh} = ${r.rsiGoodPoints} pts`
            : `RSI(${r.rsiPeriod}) ${r.rsiShortBestLow}–${r.rsiShortBestHigh} = ${r.rsiPoints} pts; ${r.rsiShortOkLow}–${r.rsiShortOkHigh} = ${r.rsiGoodPoints} pts`,
        CANDLE: (r, s) => s === "BUY"
            ? `Green candle closing in the top ${r.candleCloseZonePct}% of its range, or body ≥ ${r.candleBodyAtrMultiple}× ATR`
            : `Red candle closing in the bottom ${r.candleCloseZonePct}% of its range, or body ≥ ${r.candleBodyAtrMultiple}× ATR`,
        RISK_REWARD: (r) => `Stop ${r.stopAtrMultiple}× ATR, Target 1 = ${r.target1R}R → R:R ≥ 1:${r.minRiskReward}`
    };

    // ---------------------------------------------------------------- state
    let apiBaseUrl = "";
    let realUserId = 1;
    let liveRules = null;
    let watchThreshold = 50;
    let buyThreshold = 70;
    let regimeMode = false;
    let loaded = false;
    const settingsMin = { AUTO_PAPER: 10, AUTO_REAL: 10 };
    const riskAccepted = { AUTO_PAPER: false, AUTO_REAL: false };

    const allOn = (m, s) => ({ side: s, enabled: Object.fromEntries(CONDITIONS.map(c => [c.key, true])), minMatch: settingsMin[m] });
    const freshState = () => Object.fromEntries(MODES.map(m => [m, Object.fromEntries(SIDES.map(s => [s, allOn(m, s)]))]));
    const clone = (o) => JSON.parse(JSON.stringify(o));

    let state = freshState();
    let saved = clone(state);
    let mode = "AUTO_PAPER";
    let side = "BUY";

    const $ = (id) => document.getElementById(id);
    const profile = () => state[mode][side];

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

    // In REGIME gate mode the market regime decides the BUY side's condition 1; shorts keep their own NIFTY filter.
    const isRegimeManaged = (c, s) => regimeMode && s === "BUY" && c.key === "MARKET";
    const isApplied = (p, c) => p.enabled[c.key] && !isRegimeManaged(c, p.side);
    const appliedCount = (p) => CONDITIONS.filter(c => isApplied(p, c)).length;
    const pointsOf = (c) => liveRules && POINT_KEY[c.key] ? liveRules[POINT_KEY[c.key]] : c.pts;
    const ruleText = (c, s) => liveRules && LIVE_RULE[c.key] ? LIVE_RULE[c.key](liveRules, s) : c.rule[s];
    const scoringPoints = (p) => CONDITIONS.filter(c => !c.hard && isApplied(p, c)).reduce((s, c) => s + pointsOf(c), 0);
    const hardFilterOff = (p) => CONDITIONS.some(c => c.hard && !p.enabled[c.key] && !isRegimeManaged(c, p.side));

    // ---------------------------------------------------------------- API
    function modeQuery(m) {
        return m === "AUTO_REAL" ? `mode=AUTO_REAL&userId=${realUserId}` : "mode=AUTO_PAPER";
    }

    function fromDto(m, dto) {
        settingsMin[m] = dto.settingsMinConditionsMatch || settingsMin[m];
        buyThreshold = dto.buyScoreThreshold || buyThreshold;
        regimeMode = (dto.gateMode || "").toUpperCase() === "REGIME";
        for (const s of SIDES) {
            const sideDto = s === "BUY" ? dto.buy : dto.short;
            const off = new Set(sideDto?.disabledConditions || []);
            state[m][s] = {
                side: s,
                enabled: Object.fromEntries(CONDITIONS.map(c => [c.key, !off.has(c.key)])),
                minMatch: sideDto?.minConditionsMatch || settingsMin[m]
            };
        }
        riskAccepted[m] = [dto.buy, dto.short].some(x => x && x.riskAcceptedAt);
        saved[m] = clone(state[m]);
    }

    async function loadMode(m) {
        const res = await fetch(`${apiBaseUrl}/api/tradeconditions?${modeQuery(m)}`);
        if (!res.ok) throw new Error(`${MODE_LABEL[m]}: HTTP ${res.status}`);
        fromDto(m, await res.json());
    }

    async function readError(res) {
        try {
            const body = await res.json();
            return body.message || `HTTP ${res.status}`;
        } catch {
            return `HTTP ${res.status}`;
        }
    }

    // Shown next to Save and kept until the next change, so the result can't be missed.
    function setSaveStatus(kind, text) {
        const status = $("tcSaveStatus");
        status.hidden = !kind;
        status.className = kind ? `tc-save-status is-${kind}` : "tc-save-status";
        status.textContent = text || "";
    }

    function setStatus(text, kind) {
        const pill = $("tcStatusPill");
        pill.textContent = text;
        pill.className = `tc-pill ${kind === "ok" ? "tc-pill-on" : kind === "error" ? "tc-pill-error" : "tc-pill-muted"}`;
    }

    // ---------------------------------------------------------------- rendering
    function renderRows() {
        const p = profile();
        const hardList = $("tcHardList");
        const scoreList = $("tcScoreList");
        hardList.replaceChildren();
        scoreList.replaceChildren();

        for (const c of CONDITIONS) {
            const locked = isRegimeManaged(c, side);
            const on = p.enabled[c.key];
            const input = el("input", {
                type: "checkbox",
                checked: on && !locked,
                disabled: locked || !loaded,
                "aria-label": `Apply condition ${c.no}: ${c.name[side]}`,
                onchange: (e) => setEnabled(c.key, e.target.checked)
            });

            const row = el("div", { class: `tc-row ${locked ? "is-locked" : on ? "is-on" : "is-off"}` },
                el("div", { class: "tc-no", text: String(c.no) }),
                el("div", { class: "tc-row-body" },
                    el("div", { class: "tc-row-title" },
                        el("span", { text: c.name[side] }),
                        el("span", { class: `tc-badge ${c.hard ? "tc-badge-hard" : "tc-badge-score"}`, text: c.hard ? "Hard" : `${pointsOf(c)} pts` }),
                        el("span", { class: "tc-badge tc-badge-tf", text: c.tf }),
                        locked ? el("span", { class: "tc-badge tc-badge-lock", text: "Managed by market regime" }) : null),
                    el("p", { class: "tc-row-rule", text: ruleText(c, side) }),
                    el("p", { class: "tc-row-why", text: c.why })),
                el("label", { class: "tc-switch" }, input, el("span", { class: "tc-slider" })));

            (c.hard ? hardList : scoreList).append(row);
        }
    }

    function renderSummary() {
        const p = profile();
        const applied = CONDITIONS.filter(c => isApplied(p, c));
        const configurable = CONDITIONS.filter(c => !isRegimeManaged(c, side));
        const hardTotal = configurable.filter(c => c.hard).length;
        const hardOn = applied.filter(c => c.hard).length;
        const maxPts = scoringPoints(p);

        $("tcEnabledCount").textContent = `${applied.length} / ${configurable.length}`;
        $("tcHardCount").textContent = `${hardOn} / ${hardTotal}`;
        $("tcPointsText").textContent = `${maxPts} / 100`;
        $("tcPointsBar").style.width = `${maxPts}%`;
        $("tcRescaleText").textContent = maxPts === 100
            ? "Score = points earned out of 100."
            : maxPts === 0
                ? "No scoring condition is on - there is no score."
                : `Score = points earned ÷ ${maxPts} × 100 (rescaled so ${buyThreshold} still means a signal).`;

        const minInput = $("tcMinMatch");
        minInput.max = String(Math.max(1, applied.length));
        minInput.value = String(p.minMatch);
        minInput.disabled = !loaded;
        $("tcMinMatchOf").textContent = `of ${applied.length} on`;
        $("tcSignalChip").textContent = `${side} ≥ ${buyThreshold}`;
        $("tcWatchChip").textContent = `WATCH ≥ ${watchThreshold}`;
        $("tcScoreIntro").textContent = `Each adds points to the score. A score of ${buyThreshold} or more is a signal; ${watchThreshold}–${buyThreshold - 1} is WATCH.`;

        const alerts = $("tcAlerts");
        alerts.replaceChildren();
        const add = (kind, text) => alerts.append(el("div", { class: `tc-alert tc-alert-${kind}`, text }));

        const hardOff = CONDITIONS.filter(c => c.hard && !p.enabled[c.key] && !isRegimeManaged(c, side));
        if (maxPts === 0) add("error", "Turn on at least one scoring condition (4–11). Without one there is no score, so this selection can't be saved.");
        if (hardOff.length) {
            add("warn", `${hardOff.map(c => c.name[side]).join(", ")} ${hardOff.length > 1 ? "are" : "is"} OFF - stocks that fail ${hardOff.length > 1 ? "them" : "it"} can still be ${side === "BUY" ? "bought" : "shorted"}.`);
        }
        if (regimeMode && side === "BUY") add("info", "Gate mode is REGIME: condition 1 is decided by the daily market regime and its policy, so it isn't set here.");
        if (side === "SHORT") add("info", "Shorts are intraday only: no new short after the short entry cut-off, and open shorts are squared off the same day.");

        const anyRealHardOff = mode === "AUTO_REAL" && SIDES.some(s => hardFilterOff(state.AUTO_REAL[s]));
        $("tcRiskWrap").hidden = !anyRealHardOff;
        $("tcRiskAccept").checked = riskAccepted.AUTO_REAL;

        $("tcCopySide").textContent = `Copy ${side} → ${side === "BUY" ? "SHORT" : "BUY"}`;
        $("tcCopyMode").textContent = `Copy to ${MODE_LABEL[mode === "AUTO_PAPER" ? "AUTO_REAL" : "AUTO_PAPER"]}`;
        for (const id of ["tcReset", "tcCopySide", "tcCopyMode"]) $(id).disabled = !loaded;
        $("tcSave").disabled = !loaded || SIDES.some(s => scoringPoints(state[mode][s]) === 0);

        renderDirty();
    }

    function changesIn(m) {
        let changes = 0;
        for (const s of SIDES) {
            for (const c of CONDITIONS) if (state[m][s].enabled[c.key] !== saved[m][s].enabled[c.key]) changes++;
            if (state[m][s].minMatch !== saved[m][s].minMatch) changes++;
        }
        return changes;
    }

    function renderDirty() {
        const parts = MODES.map(m => [m, changesIn(m)]).filter(([, n]) => n > 0);
        const badge = $("tcDirty");
        badge.hidden = parts.length === 0;
        badge.textContent = "● Unsaved: " + parts.map(([m, n]) => `${MODE_LABEL[m]} (${n})`).join(", ");
    }

    function render() {
        renderRows();
        renderSummary();
    }

    // ---------------------------------------------------------------- actions
    function clampMinMatch(p) {
        const total = appliedCount(p);
        if (p.minMatch > total) {
            p.minMatch = Math.max(1, total);
            return true;
        }
        return false;
    }

    function setEnabled(key, on) {
        setSaveStatus(null);
        const p = profile();
        p.enabled[key] = on;
        if (clampMinMatch(p)) {
            window.showToast?.(`Min conditions lowered to ${p.minMatch} - only ${p.minMatch} condition${p.minMatch === 1 ? " is" : "s are"} on.`, "info");
        }
        render();
    }

    function setBulk(group, on) {
        setSaveStatus(null);
        const p = profile();
        for (const c of CONDITIONS) {
            if ((group === "hard") === c.hard) p.enabled[c.key] = on;
        }
        clampMinMatch(p);
        render();
    }

    function switchTab(containerId, attr, value) {
        for (const btn of document.querySelectorAll(`#${containerId} .tc-seg-btn`)) {
            const active = btn.dataset[attr] === value;
            btn.classList.toggle("active", active);
            btn.setAttribute("aria-selected", String(active));
        }
    }

    async function save() {
        if (mode === "AUTO_REAL" && SIDES.some(s => hardFilterOff(state.AUTO_REAL[s])) && !riskAccepted.AUTO_REAL) {
            setSaveStatus("error", "✗ Not saved: a hard filter is OFF on Auto Real. Tick the risk confirmation above, then Save.");
            window.showToast?.("A hard filter is OFF on Auto Real - tick the risk confirmation before saving.", "warning");
            return;
        }

        const sideBody = (s) => ({
            disabledConditions: CONDITIONS.filter(c => !state[mode][s].enabled[c.key]).map(c => c.key),
            minConditionsMatch: state[mode][s].minMatch
        });
        const body = { buy: sideBody("BUY"), short: sideBody("SHORT"), riskAccepted: riskAccepted[mode] };

        const btn = $("tcSave");
        btn.disabled = true;
        setSaveStatus("saving", "Saving…");
        try {
            const res = await fetch(`${apiBaseUrl}/api/tradeconditions?${modeQuery(mode)}`, {
                method: "PUT",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify(body)
            });
            if (!res.ok) {
                const problem = await readError(res);
                setSaveStatus("error", `✗ Not saved: ${problem}`);
                window.showToast?.(`Not saved: ${problem}`, "error");
                return;
            }
            fromDto(mode, await res.json());
            render();
            setSaveStatus("ok", `✓ ${MODE_LABEL[mode]} saved at ${new Date().toLocaleTimeString()}. The bot uses it from its next scan.`);
            window.showToast?.(`${MODE_LABEL[mode]} Trade Conditions saved - the bot uses them from its next scan.`, "success");
        } catch (err) {
            setSaveStatus("error", `✗ Not saved: ${err.message}`);
            window.showToast?.(`Not saved: ${err.message}`, "error");
        } finally {
            renderSummary();
        }
    }

    async function init() {
        const config = $("tc-config");
        apiBaseUrl = config?.dataset.apiBaseUrl || "";
        realUserId = parseInt(config?.dataset.userId || "1", 10) || 1;

        for (const btn of document.querySelectorAll("#tcModeTabs .tc-seg-btn")) {
            btn.addEventListener("click", () => { mode = btn.dataset.mode; switchTab("tcModeTabs", "mode", mode); render(); });
        }
        for (const btn of document.querySelectorAll("#tcSideTabs .tc-seg-btn")) {
            btn.addEventListener("click", () => { side = btn.dataset.side; switchTab("tcSideTabs", "side", side); render(); });
        }
        for (const btn of document.querySelectorAll("[data-bulk]")) {
            btn.addEventListener("click", () => { if (loaded) setBulk(btn.dataset.bulk, btn.dataset.on === "true"); });
        }

        $("tcMinMatch").addEventListener("change", (e) => {
            const p = profile();
            const total = appliedCount(p);
            const v = parseInt(e.target.value, 10);
            p.minMatch = Number.isFinite(v) ? Math.min(Math.max(v, 1), Math.max(1, total)) : p.minMatch;
            setSaveStatus(null);
            render();
        });

        $("tcRiskAccept").addEventListener("change", (e) => { riskAccepted.AUTO_REAL = e.target.checked; });

        $("tcReset").addEventListener("click", () => {
            state[mode][side] = allOn(mode, side);
            render();
            window.showToast?.(`${MODE_LABEL[mode]} ${side} reset to the default (all on, min ${settingsMin[mode]}). Save to keep it.`, "info");
        });

        $("tcCopySide").addEventListener("click", () => {
            const other = side === "BUY" ? "SHORT" : "BUY";
            state[mode][other] = { ...clone(state[mode][side]), side: other };
            clampMinMatch(state[mode][other]);
            renderDirty();
            window.showToast?.(`${side} selection copied to ${other} (${MODE_LABEL[mode]}). Save to keep it.`, "info");
        });

        $("tcCopyMode").addEventListener("click", () => {
            const other = mode === "AUTO_PAPER" ? "AUTO_REAL" : "AUTO_PAPER";
            state[other] = clone(state[mode]);
            renderDirty();
            window.showToast?.(`${MODE_LABEL[mode]} BUY and SHORT selections copied to ${MODE_LABEL[other]} - open ${MODE_LABEL[other]} and Save to keep them.`, "info");
        });

        $("tcSave").addEventListener("click", save);

        render();
        try {
            await Promise.all(MODES.map(loadMode));
            loaded = true;
            $("tcGateLabel").textContent = regimeMode ? "REGIME" : "NIFTY_FILTER";
            setStatus("Saved settings loaded", "ok");
        } catch (err) {
            setStatus("Could not load", "error");
            window.showToast?.(`Trade Conditions could not be loaded (${err.message}). Nothing can be saved until the page reloads.`, "error");
        }
        render();
    }

    document.addEventListener("strategyrules:changed", (e) => {
        liveRules = e.detail.rules;
        buyThreshold = e.detail.buyScoreThreshold;
        watchThreshold = e.detail.watchScoreThreshold;
        regimeMode = (e.detail.marketGateMode || "").toUpperCase() === "REGIME";
        $("tcGateLabel").textContent = regimeMode ? "REGIME" : "NIFTY_FILTER";
        render();
    });

    document.addEventListener("DOMContentLoaded", init);
})();
