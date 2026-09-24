/**
 * Signal Dashboard - overall verdict card (window.QeStockVerdict).
 *
 * One answer per stock across all timeframes, from GET /api/signals/verdict - the same
 * SwingDecisionEngine evaluation the auto-trading bot runs:
 *   BUY / WAIT / AVOID for a new buyer, HOLD when the user already owns the stock.
 * The timeframe flow reads NIFTY -> 1 day (trend) -> 60 min (setup) -> 15 min (timing) -> verdict;
 * a failed daily trend stops the path, because a short-timeframe move never overrides it. When the
 * verdict is BUY, a "Bot" box previews whether the 13 pre-trade guards would let the order through.
 * dashboard.js calls QeStockVerdict.load(symbol) on every symbol switch; it refreshes every 60 s.
 */
window.QeStockVerdict = (function () {
    "use strict";

    const REFRESH_MS = 60000;
    const Flow = window.QeFlow;
    const cfg = window.QuantEdgeConfig || {};
    const apiBaseUrl = cfg.apiBaseUrl || "";
    const userId = cfg.userId || 1;

    let symbol = "", timer = null, inFlight = false, nodeMap = {}, selectedId = null;

    const el = id => document.getElementById(id);
    const esc = s => Flow.esc(s == null ? "" : s);
    const money = v => "₹" + Math.abs(Number(v) || 0).toLocaleString("en-IN", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
    const timeIst = utc => new Date(utc).toLocaleTimeString("en-IN", { hour: "2-digit", minute: "2-digit", timeZone: "Asia/Kolkata" });
    const num = v => Number(v || 0).toLocaleString("en-IN", { maximumFractionDigits: 1 });

    const VERDICT = {
        BUY: { color: "#34d399", kind: "ok" },
        WAIT: { color: "#fbbf24", kind: "warn" },
        AVOID: { color: "#f87171", kind: "bad" },
        HOLD: { color: "#4f9cf9", kind: "info" },
        NO_DATA: { color: "#94a3b8", kind: "end" }
    };
    const FACTOR_COLORS = {
        BREAKOUT_GROUP: "#a78bfa", VOL_CONFIRMATION: "#4f9cf9", RELATIVE_STRENGTH: "#34d399", MULTITIMEFRAME: "#22d3ee",
        RSI_MOMENTUM: "#fbbf24", MACD_BULLISH: "#f472b6", BULLISH_CANDLE: "#fb923c", RISK_REWARD: "#94a3b8"
    };
    const FACTOR_TIPS = {
        BREAKOUT_GROUP: "a 15-min breakout", VOL_CONFIRMATION: "a volume surge", RELATIVE_STRENGTH: "beating NIFTY over 20 days",
        MULTITIMEFRAME: "the hourly trend turning up", RSI_MOMENTUM: "RSI in the 55-70 zone", MACD_BULLISH: "a MACD cross",
        BULLISH_CANDLE: "a strong green candle", RISK_REWARD: "a 1:2 risk-reward"
    };

    // --------------------------------------------------------------------------------------
    // Loading
    // --------------------------------------------------------------------------------------
    function load(sym) {
        if (!Flow || !el("verdictCard")) return;
        const next = String(sym || "").toUpperCase().trim();
        if (!next) return;
        if (next !== symbol) {
            symbol = next;
            selectedId = null;
            el("verdictEmpty").hidden = true;
            el("verdictCard").hidden = false;
            el("svSymbol").textContent = symbol;
            el("svMeta").textContent = "Loading the verdict…";
        }
        fetchVerdict();
        if (timer) clearInterval(timer);
        timer = setInterval(() => { if (!document.hidden) fetchVerdict(); }, REFRESH_MS);
    }

    async function fetchVerdict() {
        if (inFlight || !symbol) return;
        inFlight = true;
        const requested = symbol;
        try {
            const res = await fetch(`${apiBaseUrl}/api/signals/verdict?symbol=${encodeURIComponent(requested)}&userId=${userId}`);
            if (!res.ok) throw new Error(`HTTP ${res.status}`);
            const data = await res.json();
            if (requested === symbol) render(data);
        } catch (err) {
            console.error("Verdict load failed:", err);
            if (requested === symbol) el("svMeta").textContent = "Couldn't load the verdict - check that the API is running. Retrying every minute.";
        } finally {
            inFlight = false;
        }
    }

    // --------------------------------------------------------------------------------------
    // Render
    // --------------------------------------------------------------------------------------
    function render(d) {
        const v = VERDICT[d.verdict] || VERDICT.NO_DATA;
        const card = el("verdictCard");
        card.style.setProperty("--sv-color", v.color);

        el("svSymbol").textContent = d.symbol;
        el("svPrice").textContent = d.lastPrice ? money(d.lastPrice) : "";
        const change = el("svChange");
        if (d.dayChangePct != null) {
            change.textContent = `${d.dayChangePct >= 0 ? "+" : ""}${d.dayChangePct.toFixed(2)}% today`;
            change.className = "sv-change " + (d.dayChangePct >= 0 ? "pos" : "neg");
        } else {
            change.textContent = "";
        }
        el("svMeta").textContent = `Updated ${timeIst(d.asOfUtc)} · same engine and candles as the auto-trading bot · refreshes every minute`;

        const h = d.holding;
        el("svTags").innerHTML =
            (h ? `<span class="sv-tag sv-tag-hold">You own ${h.quantity} @ ${money(h.averagePrice)}</span>` : "") +
            (h && h.source === "ZERODHA" ? `<span class="sv-tag sv-tag-warn">Not monitored by the bot</span>` : "") +
            (h && h.source === "BOT" && typeof window.openStockJourney === "function"
                ? `<button type="button" class="btn-journey" onclick="openStockJourney('${esc(d.symbol).replace(/'/g, "")}')">Flow</button>` : "");

        el("svFor").textContent = h ? "Verdict for your shares" : "Verdict for a new buyer";
        el("svVerdict").textContent = d.verdict === "NO_DATA" ? "NO DATA" : d.verdict;
        el("svScore").textContent = d.verdict === "NO_DATA" ? "–" : d.score;
        const arc = el("svRingArc");
        arc.setAttribute("stroke", v.color);
        arc.setAttribute("stroke-dasharray", `${d.verdict === "NO_DATA" ? 0 : d.score} 100`);

        const text = explain(d);
        el("svWhy").textContent = text.why;
        el("svMiss").innerHTML = text.miss;

        renderFlow(d, v);
        renderScoreBar(d);
        renderBot(d);
    }

    // Plain-language reason and "what's missing" for each verdict.
    function explain(d) {
        const trendIssue = !d.emaTrendPassed ? "the daily EMA20 is not above EMA50 with both rising"
            : !d.adxPassed ? `daily ADX is ${num(d.adx1d)}, below 20 (a choppy, trendless chart)` : "";
        const missingFactors = (d.factors || []).filter(f => f.points < f.maxPoints)
            .sort((a, b) => (b.maxPoints - b.points) - (a.maxPoints - a.points)).slice(0, 2)
            .map(f => `${FACTOR_TIPS[f.code] || f.name} (+${f.maxPoints - f.points})`);

        if (d.verdict === "NO_DATA") return { why: d.engineReason, miss: "<b>Not enough history yet.</b> The verdict appears once the daily candles are backfilled." };

        if (d.verdict === "HOLD") {
            const h = d.holding;
            const why = d.trendPassed
                ? "The daily trend is still intact, so the chart gives no reason to sell."
                : `Warning: the daily trend has weakened (${trendIssue}). The bot's stop loss and exit rules decide when to sell.`;
            const miss = h.source === "BOT"
                ? `<b>Exit plan:</b> target ${h.takeProfit ? money(h.takeProfit) : "-"} · stop loss ${h.stopLoss ? money(h.stopLoss) : "-"} (judged on the closing price).`
                : `<b>The bot isn't watching these shares.</b> Use "Set Target" on the Auto Real Trade page to hand them over.`;
            return { why, miss };
        }

        if (d.verdict === "AVOID") {
            return {
                why: `The daily trend fails a must-pass rule: ${trendIssue}. The bot never buys against the daily trend, however strong the shorter charts look.`,
                miss: `<b>Blocked by the daily trend.</b> ${!d.emaTrendPassed ? "Needs the daily EMA20 back above EMA50, both rising." : "Needs daily ADX of 20 or more."}`
            };
        }

        const market = d.marketPenalty > 0 ? ` NIFTY is weak, which costs ${d.marketPenalty} points.` : "";
        if (d.verdict === "BUY") {
            const viaConditions = d.score < d.buyThreshold;
            const why = viaConditions
                ? `It meets ${d.metCount} of ${d.totalConditions} conditions. The bot buys at ${d.minConditionsMatch}+ conditions even below a score of ${d.buyThreshold}.${market}`
                : `The daily trend is healthy${d.setupPassed ? ", the hourly chart confirms it," : ""} and the 15-min timing scores ${d.timingPoints} of ${d.timingMaxPoints}.${market}`;
            const p = d.botPreview;
            const miss = !p ? "<b>Nothing missing.</b>"
                : p.willBuy ? `<b>Nothing missing.</b> The bot can buy ${p.quantity} share${p.quantity === 1 ? "" : "s"} (about ${money(p.estimatedCost)}) at its next scan, if the price hasn't moved more than 2%.`
                : `<b>The bot won't buy it right now:</b> check ${p.failedGuardNumber} (${esc(p.failedGuardName)}) fails.`;
            return { why, miss };
        }

        // WAIT
        const pointsNeeded = Math.max(0, d.buyThreshold - d.score);
        const conditionsNeeded = Math.max(0, d.minConditionsMatch - d.metCount);
        return {
            why: `The daily trend is fine, but the setup isn't complete yet: score ${d.score} (BUY needs ${d.buyThreshold}) with ${d.metCount} of ${d.totalConditions} conditions met.${market}`,
            miss: `<b>Needs +${pointsNeeded} points or ${conditionsNeeded} more condition${conditionsNeeded === 1 ? "" : "s"}.</b>` +
                (missingFactors.length ? ` Biggest gaps: ${esc(missingFactors.join(", "))}.` : "")
        };
    }

    // NIFTY -> 1 day -> 60 min -> 15 min -> verdict (-> bot, only when BUY)
    function renderFlow(d, v) {
        const W = 140, PITCH = 166, X = i => 8 + i * PITCH, Y = 26, H = 56;
        const at = (i, kind, title, sub, info, value) => Flow.node("n" + i, 0, 0, kind, title, sub, info, value, "", { x: X(i), y: Y, w: W, h: H });
        const noData = d.verdict === "NO_DATA";
        const trendFailed = !noData && !d.trendPassed;
        const factor = code => (d.factors || []).find(f => f.code === code);
        const breakout = factor("BREAKOUT_GROUP");

        const nodes = [
            noData ? at(0, "skip", "NIFTY 50", "not checked", "The stock has too little history to be scored, so the market isn't checked either.", "") :
            at(0, d.marketPassed ? "done" : "bad", "NIFTY 50", d.marketPassed ? "uptrend · no penalty" : `weak · −${d.marketPenalty} pts`,
                d.marketPassed ? "NIFTY is above its 50-day average with EMA20 above EMA50, so the market adds no penalty."
                    : `NIFTY is below its 50-day average or its EMA20 is under EMA50. It doesn't block the stock, but takes ${d.marketPenalty} points off the score.`,
                d.marketPassed ? "✓" : `−${d.marketPenalty}`),
            at(1, noData ? "skip" : d.trendPassed ? "done" : "bad", "1 day · trend",
                noData ? "not enough data" : !d.emaTrendPassed ? "EMA trend broken" : !d.adxPassed ? `ADX ${num(d.adx1d)} < 20` : `ADX ${num(d.adx1d)} · EMA ✓`,
                `Must pass. Close above EMA20 above EMA50 (now ${money(d.ema20_1d)} / ${money(d.ema50_1d)}), both rising, and ADX of 20 or more (now ${num(d.adx1d)}).`,
                d.trendPassed ? "must-pass ✓" : "must-pass ✕"),
            trendFailed || noData
                ? at(2, "skip", "60 min · setup", "not checked", "Skipped: once the daily trend fails, the lower timeframes don't matter.", "")
                : at(2, d.setupPassed ? "done" : "bad", "60 min · setup",
                    !d.has60mData ? "no 60m data" : d.setupPassed ? `RSI ${num(d.rsi60m)} · +15` : `below EMA20 · +0`,
                    !d.has60mData ? "Not enough hourly candles, so no confirmation points are given."
                        : `Hourly close above its EMA20 with RSI at least 40 earns 15 points. RSI is ${num(d.rsi60m)}.`,
                    d.setupPassed ? "+15" : "+0"),
            trendFailed || noData
                ? at(3, "skip", "15 min · timing", "not checked", "Skipped: a strong 15-min move can't override a daily downtrend.", "")
                : at(3, breakout && breakout.points > 0 ? "done" : d.timingPoints > 0 ? "warn" : "bad", "15 min · timing",
                    `${d.timingPoints}/${d.timingMaxPoints} pts · vol ${num(d.volumeMultiple)}×`,
                    `Entry timing on the 15-min chart: ${breakout && breakout.points > 0 ? "a breakout" : "no breakout yet"}, volume ${num(d.volumeMultiple)}× average, RSI ${num(d.rsi15m)}. Scores ${d.timingPoints} of ${d.timingMaxPoints} timing points.`,
                    `${d.timingPoints}/${d.timingMaxPoints}`),
            at(4, v.kind, noData ? "NO DATA" : `${d.verdict} · ${d.score}`, d.holding ? "you own it" : "for a new buyer",
                explain(d).why, `${d.score}/100`)
        ];
        const edges = [0, 1, 2, 3].map(i => Flow.edge("n" + i, "n" + (i + 1), "r", "l"));

        const p = d.botPreview;
        if (d.verdict === "BUY" && p) {
            nodes.push(at(5, p.willBuy ? "ok" : "bad", p.willBuy ? "Bot buys" : "Bot won't buy",
                p.willBuy ? `${p.quantity} sh · ${money(p.estimatedCost)}` : `check ${p.failedGuardNumber} of ${p.totalGuards} fails`,
                p.willBuy ? `All checks the bot can see now pass. At the next scan it buys ${p.quantity} share(s) for about ${money(p.estimatedCost)}; the ±2% price check happens at order time.`
                    : `${p.reason}. Nothing is ordered until this check passes.`,
                p.willBuy ? "all checks ✓" : `check ${p.failedGuardNumber} of ${p.totalGuards}`));
            edges.push(Flow.edge("n4", "n5", "r", "l"));
        }

        const state = e => {
            if (e.t === "n4" || e.f === "n4") return "cur";
            const from = nodes.find(n => n.id === e.f), to = nodes.find(n => n.id === e.t);
            return from.kind === "skip" || to.kind === "skip" ? "" : "done";
        };
        nodeMap = Flow.render(el("svSvg"), { nodes, edges }, { nowId: "n4", edgeState: state });
        el("svSvg").setAttribute("aria-label", `${d.symbol} timeframe verdict`);
        Flow.onNodeSelect(el("svSvg"), selectNode);
        if (!selectedId || !nodeMap[selectedId]) selectedId = "n4";
        selectNode(selectedId);
    }

    function selectNode(id) {
        const n = nodeMap[id];
        if (!n) return;
        selectedId = id;
        Flow.markSelected(el("svSvg"), id);
        el("svDetail").innerHTML = `<div><h4>${esc(String(n.title).replace(/\n/g, " "))}</h4><p>${esc(n.info || "")}</p></div><div class="sv-detail-value">${esc(n.value || "")}</div>`;
    }

    function renderScoreBar(d) {
        const factors = d.factors || [];
        const earned = factors.reduce((a, f) => a + f.points, 0);
        let bar = factors.filter(f => f.points > 0).map(f =>
            `<div class="sv-seg" style="width:${f.points}%;background:${FACTOR_COLORS[f.code] || "#94a3b8"}" title="${esc(f.name)} +${f.points}">${f.points >= 7 ? "+" + f.points : ""}</div>`).join("");
        const penalty = Math.min(d.marketPenalty || 0, earned);
        if (penalty > 0) bar += `<div class="sv-seg sv-seg-penalty" style="width:${penalty}%" title="NIFTY penalty">−${penalty}</div>`;
        const missing = Math.max(0, 100 - earned - penalty);
        if (missing > 0) bar += `<div class="sv-seg sv-seg-missing" style="width:${missing}%">${missing >= 14 ? "not earned" : ""}</div>`;
        bar += `<div class="sv-mark" style="left:${d.watchThreshold}%"><span>WAIT ${d.watchThreshold}</span></div><div class="sv-mark" style="left:${d.buyThreshold}%"><span>BUY ${d.buyThreshold}</span></div>`;
        el("svBar").innerHTML = bar;

        el("svScoreNote").textContent = d.verdict === "NO_DATA" ? ""
            : !d.trendPassed ? "Daily trend failed: score set to 0"
            : `${earned} earned${d.marketPenalty ? ` − ${d.marketPenalty} market penalty` : ""} = ${d.score} · ${d.metCount}/${d.totalConditions} conditions`;
        el("svFactors").innerHTML = factors.map(f =>
            `<span class="${f.points ? "" : "off"}"><i style="background:${FACTOR_COLORS[f.code] || "#94a3b8"}"></i>${esc(f.name)} ${f.points}/${f.maxPoints}</span>`).join("");
    }

    // The bot strip appears only for a BUY verdict.
    function renderBot(d) {
        const box = el("svBot");
        const p = d.botPreview;
        if (d.verdict !== "BUY" || !p) { box.hidden = true; return; }
        box.hidden = false;
        box.className = "sv-bot " + (p.willBuy ? "ok" : "bad");
        box.innerHTML = p.willBuy
            ? `<div class="sv-bot-icon">✓</div><div class="sv-bot-text"><b>The bot will buy this at its next scan</b>${p.quantity} share${p.quantity === 1 ? "" : "s"} for about ${money(p.estimatedCost)} (${money(p.tradeAmount)} per trade). The ±2% price check runs at order time.</div>`
            : `<div class="sv-bot-icon">✕</div><div class="sv-bot-text"><b>The bot won't buy this right now</b>Check ${p.failedGuardNumber} of ${p.totalGuards} (${esc(p.failedGuardName)}): ${esc(p.reason)}.</div>`;
        box.innerHTML += `<a class="sv-bot-link" href="/SwingTrading">See all stocks →</a>`;
    }

    return { load };
})();
