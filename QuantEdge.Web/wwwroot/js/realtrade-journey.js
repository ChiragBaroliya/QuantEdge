/**
 * Stock Journey popup (Auto Real Trade page).
 *
 * A "Flow" button on each position / holding row (and "Why?" on skipped audit entries) opens a flow
 * diagram of that one stock, built from GET /api/realtrade/symbol-journey:
 *   - OPEN        how the bot got in -> where the live price is now -> each exit rule with its ₹ result
 *   - NOT_MANAGED a Zerodha position/holding the bot isn't watching, and how to hand it over
 *   - skip        which of the 13 pre-trade guards stopped today's BUY, and what would unblock it
 * The path the bot is on right now is highlighted, and the popup refreshes every 5 s while open.
 * Exit levels come from the server (SwingTradeRules), so this file never does trading math of its own.
 */
window.QeStockJourney = (function () {
    "use strict";

    const REFRESH_MS = 5000;
    const Flow = window.QeFlow;

    let apiBaseUrl = "", userId = 1;
    let modal = null, svg = null;
    let symbol = "", mode = "position", timer = null, inFlight = false;
    let nodeMap = {}, selectedId = null, isFirstRender = true;

    const el = id => document.getElementById(id);
    const esc = s => Flow.esc(s == null ? "" : s);

    // --------------------------------------------------------------------------------------
    // Formatting
    // --------------------------------------------------------------------------------------
    const money = v => "₹" + Math.abs(Number(v) || 0).toLocaleString("en-IN", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
    const signed = v => ((Number(v) || 0) >= 0 ? "+" : "−") + money(v);
    // Inside diagram boxes, large amounts drop the paise so they fit (₹1,26,200 rather than ₹1,26,200.00).
    const boxMoney = v => Math.abs(Number(v) || 0) >= 10000
        ? "₹" + Math.round(Math.abs(Number(v))).toLocaleString("en-IN")
        : money(v);
    const boxSigned = v => ((Number(v) || 0) >= 0 ? "+" : "−") + boxMoney(v);
    const pctOf = (v, base) => base ? (((Number(v) || 0) >= 0 ? "+" : "−") + Math.abs(v / base * 100).toFixed(2) + "%") : "";
    const cls = v => (Number(v) || 0) >= 0 ? "sj-pos" : "sj-neg";
    const timeIst = utc => utc ? new Date(utc).toLocaleTimeString("en-IN", { hour: "2-digit", minute: "2-digit", timeZone: "Asia/Kolkata" }) : "";
    const dateTimeIst = utc => utc ? new Date(utc).toLocaleString("en-IN", { day: "2-digit", month: "short", hour: "2-digit", minute: "2-digit", timeZone: "Asia/Kolkata" }) : "";
    const shortText = (s, max) => { s = String(s || ""); return s.length > max ? s.slice(0, max - 1) + "…" : s; };
    const plural = (n, word) => `${n} ${word}${n === 1 ? "" : "s"}`;

    const SOURCE_TEXT = { WS: "live tick", REST: "Zerodha quote", ZERODHA: "Zerodha last price", NONE: "no live price" };

    // --------------------------------------------------------------------------------------
    // Open / refresh
    // --------------------------------------------------------------------------------------
    function init() {
        const config = el("realtrade-config");
        if (config) {
            apiBaseUrl = config.dataset.apiBaseUrl || "";
            userId = parseInt(config.dataset.userId || "1", 10);
        }
        svg = el("sjSvg");
        const modalEl = el("modalStockJourney");
        if (modalEl && typeof bootstrap !== "undefined") {
            modal = new bootstrap.Modal(modalEl);
            modalEl.addEventListener("hidden.bs.modal", stopRefresh);
        }
        document.addEventListener("visibilitychange", () => {
            if (document.hidden) stopRefresh();
            else if (symbol && modalEl && modalEl.classList.contains("show")) startRefresh();
        });
    }

    function open(sym, openMode) {
        if (!modal || !Flow) return;
        symbol = String(sym || "").toUpperCase().trim();
        mode = openMode === "skip" ? "skip" : "position";
        selectedId = null;
        isFirstRender = true;
        el("sjTitle").textContent = symbol;
        el("sjSub").textContent = "";
        el("sjLive").innerHTML = "";
        showMessage("Loading…");
        modal.show();
        load();
        startRefresh();
    }

    function startRefresh() {
        stopRefresh();
        timer = setInterval(load, REFRESH_MS);
    }

    function stopRefresh() {
        if (timer) { clearInterval(timer); timer = null; }
    }

    async function load() {
        if (inFlight || !symbol) return;
        inFlight = true;
        const requested = symbol;
        try {
            const res = await fetch(`${apiBaseUrl}/api/realtrade/symbol-journey?symbol=${encodeURIComponent(requested)}&userId=${userId}`);
            if (!res.ok) throw new Error(`HTTP ${res.status}`);
            const data = await res.json();
            if (requested === symbol) render(data);
        } catch (err) {
            console.error("Stock journey load failed:", err);
            if (isFirstRender) showMessage(`Couldn't load the journey for ${symbol}. Check that the API is running, then reopen this popup.`, true);
        } finally {
            inFlight = false;
        }
    }

    function showMessage(text, isError) {
        const msg = el("sjMessage");
        msg.textContent = text;
        msg.classList.toggle("error", !!isError);
        msg.hidden = false;
        el("sjContent").hidden = true;
    }

    // --------------------------------------------------------------------------------------
    // Render
    // --------------------------------------------------------------------------------------
    function render(d) {
        let view;
        if (mode === "skip") view = d.lastSkip ? buildSkipView(d) : null;
        else if (d.status === "OPEN") view = buildOpenView(d);
        else if (d.status === "NOT_MANAGED") view = buildNotManagedView(d);
        else if (d.lastSkip) view = buildSkipView(d);

        if (!view) {
            stopRefresh();
            showMessage(mode === "skip"
                ? `${symbol} has no skipped BUY signal today.`
                : `${symbol} has no open position or Zerodha holding right now. It may have just been sold - check Trade History.`);
            return;
        }

        el("sjMessage").hidden = true;
        el("sjContent").hidden = false;
        el("sjTitle").innerHTML = `${esc(d.symbol)} ${view.pills}`;
        el("sjSub").textContent = view.subtitle;
        el("sjLive").innerHTML = liveBadge(d);
        el("sjStats").innerHTML = view.stats.map(([label, value, note, valueCls]) =>
            `<div class="sj-stat"><span>${esc(label)}</span><b class="${valueCls || ""}">${esc(value)}</b>${note ? `<small>${esc(note)}</small>` : ""}</div>`).join("");

        el("sjStripWrap").hidden = !view.strip;
        if (view.strip) renderStrip(view.strip);

        el("sjDiagramTitle").textContent = view.diagramTitle;
        const decision = el("sjDecision");
        decision.textContent = view.decision ? view.decision.text : "";
        decision.className = "sj-decision" + (view.decision ? " " + view.decision.kind : "");
        decision.hidden = !view.decision;

        const done = new Set(view.done.map(([a, b]) => a + ">" + b));
        const cur = new Set(view.cur.map(([a, b]) => a + ">" + b));
        nodeMap = Flow.render(svg, view.diagram, {
            nowId: view.nowId,
            edgeState: e => cur.has(e.f + ">" + e.t) ? "cur" : done.has(e.f + ">" + e.t) ? "done" : ""
        });
        svg.setAttribute("aria-label", `${d.symbol} journey`);
        Flow.onNodeSelect(svg, selectNode);

        if (!selectedId || !nodeMap[selectedId]) selectedId = view.nowId;
        selectNode(selectedId);
        isFirstRender = false;
    }

    function liveBadge(d) {
        if (!d.ltp) return `<i class="stale"></i>No live price yet`;
        const stale = d.ltpSource !== "WS";
        const market = d.isMarketOpen ? "" : " · market closed";
        return `<i class="${stale ? "stale" : ""}"></i>${esc(SOURCE_TEXT[d.ltpSource] || "live price")} · ${timeIst(d.asOfUtc)}${market}`;
    }

    function selectNode(id) {
        const n = nodeMap[id];
        if (!n) return;
        selectedId = id;
        Flow.markSelected(svg, id);
        const k = Flow.KIND[n.kind] || Flow.KIND.proc;
        el("sjDetail").innerHTML =
            `<div class="sj-detail-icon" style="border-color:${k.stroke};color:${k.text};background:${k.fill}">${k.icon}</div>` +
            `<div><h6>${esc(String(n.title).replace(/\n/g, " "))}</h6><p>${esc(n.info || "")}</p></div>` +
            `<div class="sj-detail-value ${n.valueCls || ""}">${esc(n.value || "")}</div>`;
    }

    // Levels on one rail, labels alternating above/below so neighbours never overlap.
    function renderStrip(strip) {
        const levels = strip.levels.filter(l => l.price > 0).sort((a, b) => a.price - b.price);
        const prices = levels.map(l => l.price).concat(strip.ltp ? [strip.ltp] : []);
        if (!prices.length) { el("sjStripWrap").hidden = true; return; }
        const lo = Math.min(...prices), hi = Math.max(...prices), pad = (hi - lo) * 0.06 || lo * 0.02;
        const X = v => ((v - (lo - pad)) / ((hi + pad) - (lo - pad)) * 100);
        const entryX = X(strip.entry);
        const rail = `<div class="sj-rail" style="background:linear-gradient(90deg,rgba(248,113,113,.45),rgba(248,113,113,.15) ${entryX}%,rgba(52,211,153,.12) ${entryX}%,rgba(52,211,153,.45))"></div>`;
        const marks = levels.map((l, i) =>
            `<div class="sj-mk ${i % 2 ? "up" : "dn"}" style="left:${X(l.price)}%;--c:${l.color}"><div class="tk"></div><div class="lb">${esc(l.name)}<b>${money(l.price)}</b></div></div>`).join("");
        const ltp = strip.ltp
            ? `<div class="sj-ltp" style="left:${X(strip.ltp)}%"><div class="lb">LTP ${money(strip.ltp)}</div><div class="ln"></div><div class="dot"></div></div>`
            : "";
        el("sjStrip").innerHTML = rail + marks + ltp;
    }

    // --------------------------------------------------------------------------------------
    // View: bot-managed OPEN position
    // --------------------------------------------------------------------------------------
    function buildOpenView(d) {
        const N = Flow.node, E = Flow.edge;
        const p = d.position, L = d.levels;
        const qty = p.quantity, entry = p.averageEntryPrice, invested = L.invested;
        const ltp = d.ltp || null;
        const pnl = L.unrealizedPnl;
        const shareWord = qty === 1 ? "share" : "shares";
        const closeAt = d.closeCheckTime;

        // --- How it got here (top row) ---
        const o = d.entryOrder;
        let past;
        if (d.entrySource === "HOLDING") {
            past = [
                N("p0", 0, 0, "ext", "Bought in Zerodha", "outside QuantEdge", "These shares were bought directly in Zerodha, not by the bot."),
                N("p1", 1, 0, "done", "Holding listed", "Zerodha Holdings tab", "They showed up in the Zerodha Holdings tab."),
                N("p2", 2, 0, "done", "Set Target", money(L.takeProfit), `You set a target of ${money(L.takeProfit)} with "Set Target".`, money(L.takeProfit)),
                N("p3", 3, 0, "done", "Monitoring ON", dateTimeIst(p.openedAt), "From here the bot watches these shares exactly like one of its own trades.", timeIst(p.openedAt)),
                N("p4", 4, 0, "done", "Position OPEN", `${money(invested)} tracked`, `Stop loss ${money(L.stopLoss)} and target ${money(L.takeProfit)} are checked every ${d.monitorIntervalSeconds} seconds.`, money(invested))
            ];
        } else {
            const met = o && o.remarks ? (o.remarks.match(/Met (\d+)\/11/) || [])[1] : null;
            const manual = d.entrySource === "MANUAL";
            const orderNo = o && o.brokerOrderId ? o.brokerOrderId : "";
            const placedAt = o ? o.createdAt : p.openedAt;
            const filledAt = o ? (o.filledAt || o.createdAt) : p.openedAt;
            past = [
                manual
                    ? N("p0", 0, 0, "done", "Manual trade", "placed by you", "You placed this trade from the Manual Real Trade popup, with your own quantity and stop-loss %.")
                    : N("p0", 0, 0, "done", "Signal found", met ? `scan · met ${met}/11` : "15-min scan", met ? `The scan scored ${d.symbol} and it met ${met} of the 11 checklist items.` : `The 15-minute scan picked ${d.symbol} as a BUY.`, met ? `${met}/11` : ""),
                N("p1", 1, 0, "done", "13 guards passed", dateTimeIst(placedAt), "Trading window, daily caps, loss limit, margin and the ±2% price-drift check all passed.", timeIst(placedAt)),
                N("p2", 2, 0, "done", "LIMIT BUY sent", `${qty} × ${money(o ? o.price : entry)}`, `A LIMIT buy for ${plural(qty, "share")} went to Zerodha with a small protection buffer.`, orderNo ? "#" + orderNo : ""),
                N("p3", 3, 0, "done", "Filled", orderNo ? `#…${orderNo.slice(-6)} · ${timeIst(filledAt)}` : timeIst(filledAt), `Zerodha filled the order at ${money(entry)}. The stop loss and target were set from this fill price.`, money(entry)),
                N("p4", 4, 0, "done", "Position OPEN", `${money(invested)} invested`, `Stop loss ${money(L.stopLoss)} and target ${money(L.takeProfit)} are checked every ${d.monitorIntervalSeconds} seconds.`, money(invested))
            ];
        }

        const nowNode = N("now", 4, 1, "now",
            ltp ? `Now · ${money(ltp)}` : "Now · no price",
            ltp ? `${boxSigned(pnl)} (${pctOf(pnl, invested)})` : "waiting for price",
            ltp ? `Live price ${money(ltp)} (${SOURCE_TEXT[d.ltpSource] || "live"}). Unrealized P&L ${signed(pnl)} on ${money(invested)} invested.${d.isMarketOpen ? "" : " The market is closed, so nothing is checked until it opens."}`
                : "The bot has no live price for this stock yet, so it skips the exit check this cycle and tries again.",
            ltp ? signed(pnl) : "", "", { valueCls: cls(pnl) });

        const edges = [E("p0", "p1", "r", "l"), E("p1", "p2", "r", "l"), E("p2", "p3", "r", "l"), E("p3", "p4", "r", "l"),
            E("p4", "now", "b", "t", `checked every ${d.monitorIntervalSeconds} s`)];
        const done = [["p0", "p1"], ["p1", "p2"], ["p2", "p3"], ["p3", "p4"], ["p4", "now"]];
        let exitNodes, cur = [], strip;

        if (d.isSwingClose) {
            const trailOn = L.isTrailActive;
            const closeLine1 = trailOn
                ? `TSL ${boxMoney(L.trailingStopLoss)} → ${boxSigned(L.pnlAtTrailingStopLoss)}`
                : `SL ${boxMoney(L.stopLoss)} → ${boxSigned(L.pnlAtStopLoss)}`;
            const closeLine2 = trailOn ? `SL ${boxMoney(L.stopLoss)} → ${boxSigned(L.pnlAtStopLoss)}`
                : d.isEntryDay ? "no trail on entry day"
                : ltp && ltp >= L.trailActivationPrice ? "trail can start today"
                : `trail starts ${boxMoney(L.trailActivationPrice)}`;
            const closeLine3 = d.maxDurationDays > 0 ? `day ${d.tradingDaysHeld} of ${d.maxDurationDays}` : "no max hold";

            exitNodes = [
                N("tgt", 3, 1, "dec", `LTP ≥ target\n${money(L.takeProfit)}?`, "",
                    ltp ? `Needs ${money(Math.max(0, L.takeProfit - ltp))} more per share (${((L.takeProfit / ltp - 1) * 100).toFixed(2)}%). Checked live, all day.` : "Checked live, all day.", money(L.takeProfit)),
                N("em", 2, 1, "dec", `LTP ≤ emergency\n${money(L.emergencyStop)}?`, "",
                    `The emergency stop sells at once on a sharp fall, any time of day. It sits ${money(entry - L.emergencyStop)} below your entry.`, money(L.emergencyStop)),
                N("win", 1, 1, "dec", `Closing window\n(≥ ${closeAt})?`, "",
                    `Before ${closeAt} only the target and emergency stop can sell. The stop loss, trailing stop and holding time are judged on the closing price.`, d.isClosingWindow ? "in window" : closeAt),
                N("hold", 0, 1, "end", "HOLD", `stops wait for ${closeAt}`,
                    `No exit now. Even if the price dips below the stop loss during the day, the bot waits for the closing price before deciding.`),
                N("tgtS", 3, 2, "win", "SELL · Target", `${boxSigned(L.pnlAtTarget)} (${pctOf(L.pnlAtTarget, invested)})\nat ${boxMoney(L.takeProfit)}`,
                    `If the price reaches ${money(L.takeProfit)}, the bot sells ${plural(qty, "share")} for about ${signed(L.pnlAtTarget)}.`, signed(L.pnlAtTarget), "", { money: true, valueCls: cls(L.pnlAtTarget) }),
                N("emS", 2, 2, "loss", "SELL · Emergency", `${boxSigned(L.pnlAtEmergencyStop)} (${pctOf(L.pnlAtEmergencyStop, invested)})\nat ${boxMoney(L.emergencyStop)}`,
                    `A sharp fall to ${money(L.emergencyStop)} sells straight away, for about ${signed(L.pnlAtEmergencyStop)}.`, signed(L.pnlAtEmergencyStop), "", { money: true, valueCls: cls(L.pnlAtEmergencyStop) }),
                N("close", 1, 2, "later", `${closeAt} close check`, `${closeLine1}\n${closeLine2}\n${closeLine3}`,
                    `At the close the bot sells if the price is still at or below the ${trailOn ? `trailing stop ${money(L.trailingStopLoss)} (about ${signed(L.pnlAtTrailingStopLoss)}) or the ` : ""}stop loss ${money(L.stopLoss)} (about ${signed(L.pnlAtStopLoss)}). ` +
                    (trailOn ? "The trailing stop only moves up." : `The trailing stop starts once the price passes ${money(L.trailActivationPrice)}, never on the entry day.`) +
                    (d.maxDurationDays > 0 ? ` It also sells after ${d.maxDurationDays} trading days (now day ${d.tradingDaysHeld}).` : ""),
                    trailOn ? signed(L.pnlAtTrailingStopLoss) : signed(L.pnlAtStopLoss), "", { w: 170, x: Flow.GRID.OX + Flow.GRID.PX - 10, h: 66, y: Flow.GRID.OY + 2 * Flow.GRID.PY - 8, money: true, valueCls: cls(trailOn ? L.pnlAtTrailingStopLoss : L.pnlAtStopLoss) })
            ];
            edges.push(E("now", "tgt", "l", "r"), E("tgt", "em", "l", "r", "no"), E("tgt", "tgtS", "b", "t", "yes"),
                E("em", "win", "l", "r", "no"), E("em", "emS", "b", "t", "yes"), E("win", "hold", "l", "r", "no"), E("win", "close", "b", "t", "yes"));

            if (ltp) {
                if (ltp >= L.takeProfit) cur = [["now", "tgt"], ["tgt", "tgtS"]];
                else if (ltp <= L.emergencyStop) cur = [["now", "tgt"], ["tgt", "em"], ["em", "emS"]];
                else if (d.isClosingWindow) cur = [["now", "tgt"], ["tgt", "em"], ["em", "win"], ["win", "close"]];
                else cur = [["now", "tgt"], ["tgt", "em"], ["em", "win"], ["win", "hold"]];
            }

            strip = {
                entry, ltp,
                levels: [
                    { name: "Emergency", price: L.emergencyStop, color: "#f87171" },
                    { name: "Stop loss", price: L.stopLoss, color: "#fbbf24" },
                    { name: "Entry", price: entry, color: "#94a3b8" },
                    trailOn ? { name: "Trailing SL", price: L.trailingStopLoss, color: "#22d3ee" } : { name: "Trail starts", price: L.trailActivationPrice, color: "#22d3ee" },
                    { name: "Target", price: L.takeProfit, color: "#34d399" }
                ]
            };
        } else {
            // INTRADAY: every rule is checked live on every price.
            const tsl = L.trailingStopLoss;
            const tslPnl = tsl ? (tsl - entry) * qty : null;
            exitNodes = [
                N("tgt", 3, 1, "dec", `LTP ≥ target\n${money(L.takeProfit)}?`, "", "Checked live on every price.", money(L.takeProfit)),
                N("tsl", 2, 1, "dec", `LTP ≤ trail SL\n${tsl ? money(tsl) : "-"}?`, "", "The trailing stop follows the price up on every cycle and never moves down.", tsl ? money(tsl) : ""),
                N("sl", 1, 1, "dec", `LTP ≤ stop loss\n${money(L.stopLoss)}?`, "", "Checked live on every price.", money(L.stopLoss)),
                N("hold", 0, 1, "end", "HOLD", d.maxDurationDays > 0 ? `day ${d.tradingDaysHeld} of ${d.maxDurationDays}` : "trail moves up",
                    `No exit now. The trailing stop moves up with the price${d.maxDurationDays > 0 ? `, and the bot sells after ${d.maxDurationDays} trading days` : ""}.`),
                N("tgtS", 3, 2, "win", "SELL · Target", `${signed(L.pnlAtTarget)}\nat ${money(L.takeProfit)}`, `Selling at the target makes about ${signed(L.pnlAtTarget)}.`, signed(L.pnlAtTarget), "", { money: true, valueCls: cls(L.pnlAtTarget) }),
                N("tslS", 2, 2, tslPnl != null && tslPnl >= 0 ? "win" : "loss", "SELL · Trailing SL", tsl ? `${signed(tslPnl)}\nat ${money(tsl)}` : "not set", tsl ? `Falling back to the trailing stop sells for about ${signed(tslPnl)}.` : "", tsl ? signed(tslPnl) : "", "", { money: !!tsl, valueCls: cls(tslPnl) }),
                N("slS", 1, 2, "loss", "SELL · Stop Loss", `${signed(L.pnlAtStopLoss)}\nat ${money(L.stopLoss)}`, `Hitting the stop loss sells for about ${signed(L.pnlAtStopLoss)}.`, signed(L.pnlAtStopLoss), "", { money: true, valueCls: cls(L.pnlAtStopLoss) })
            ];
            edges.push(E("now", "tgt", "l", "r"), E("tgt", "tsl", "l", "r", "no"), E("tgt", "tgtS", "b", "t", "yes"),
                E("tsl", "sl", "l", "r", "no"), E("tsl", "tslS", "b", "t", "yes"), E("sl", "hold", "l", "r", "no"), E("sl", "slS", "b", "t", "yes"));

            if (ltp) {
                if (ltp >= L.takeProfit) cur = [["now", "tgt"], ["tgt", "tgtS"]];
                else if (tsl && ltp <= tsl) cur = [["now", "tgt"], ["tgt", "tsl"], ["tsl", "tslS"]];
                else if (ltp <= L.stopLoss) cur = [["now", "tgt"], ["tgt", "tsl"], ["tsl", "sl"], ["sl", "slS"]];
                else cur = [["now", "tgt"], ["tgt", "tsl"], ["tsl", "sl"], ["sl", "hold"]];
            }

            strip = {
                entry, ltp,
                levels: [
                    { name: "Stop loss", price: L.stopLoss, color: "#fbbf24" },
                    { name: "Trailing SL", price: tsl, color: "#22d3ee" },
                    { name: "Entry", price: entry, color: "#94a3b8" },
                    { name: "Target", price: L.takeProfit, color: "#34d399" }
                ]
            };
        }

        const sourcePill = d.entrySource === "HOLDING" ? "Enrolled holding" : d.entrySource === "MANUAL" ? "Manual trade" : "Auto trade";
        const decision = !ltp ? { kind: "", text: "Waiting for a live price" }
            : !d.isMarketOpen ? { kind: "", text: "Market closed · checks resume at the open" }
            : d.wouldSellNow ? { kind: "sell", text: `Selling now: ${d.decisionReason}` }
            : { kind: "hold", text: "Holding · no exit rule is hit" };

        return {
            pills: `<span class="sj-pill sj-pill-open">OPEN</span><span class="sj-pill sj-pill-accent">${esc(d.exitMode)} · ${esc(d.productType)}</span><span class="sj-pill sj-pill-muted">${sourcePill}</span>`,
            subtitle: `${plural(qty, "share")} × ${money(entry)} · opened ${dateTimeIst(p.openedAt)}`,
            stats: [
                ["Qty", String(qty), shareWord],
                ["Avg entry", money(entry), ""],
                ["Invested", money(invested), ""],
                ["Live price", ltp ? money(ltp) : "-", SOURCE_TEXT[d.ltpSource] || ""],
                ["Unrealized", ltp ? signed(pnl) : "-", ltp ? pctOf(pnl, invested) : "", ltp ? cls(pnl) : ""],
                ["If stop loss hits", signed(L.pnlAtStopLoss), "at " + money(L.stopLoss), "sj-neg"],
                ["If target hits", signed(L.pnlAtTarget), "at " + money(L.takeProfit), "sj-pos"]
            ],
            strip,
            diagramTitle: "Journey and possible exits",
            decision,
            diagram: { nodes: past.concat([nowNode], exitNodes), edges },
            done, cur, nowId: "now"
        };
    }

    // --------------------------------------------------------------------------------------
    // View: in the Zerodha account but not managed by the bot
    // --------------------------------------------------------------------------------------
    function buildNotManagedView(d) {
        const N = Flow.node, E = Flow.edge;
        const h = d.brokerHolding, bp = d.brokerPosition;
        const qty = h ? (h.quantity || 0) + (h.t1Quantity || 0) : (bp ? bp.quantity : 0);
        const avg = h ? h.averagePrice : (bp ? bp.buyPrice : 0);
        const ltp = d.ltp || (h ? h.lastPrice : bp ? bp.lastPrice : 0);
        const invested = qty * avg;
        const pnl = ltp ? (ltp - avg) * qty : 0;
        const where = h ? "Zerodha Holdings" : "Zerodha Live Positions";

        const nodes = [
            N("p0", 0, 0, "ext", "Bought in Zerodha", "outside QuantEdge", "These shares were bought directly in Zerodha, not by the bot."),
            N("p1", 1, 0, "done", "In your account", `${qty} × ${money(avg)}`, `They are listed in the ${where} tab.`, money(invested)),
            N("p2", 2, 0, "now", "Not monitored", "the bot won't sell it", `The bot doesn't watch these shares, so no target or stop loss will sell them automatically. Current P&L ${signed(pnl)}.`, signed(pnl), "", { valueCls: cls(pnl) }),
            N("p3", 3, 0, "later", "Set Target", "Zerodha Holdings tab", "Press \"Set Target\" on this stock in the Zerodha Holdings tab and choose a target price above your average buy price."),
            N("p4", 4, 0, "later", "Bot monitors it", "3% SL + your target", `From then on the bot checks it every ${d.monitorIntervalSeconds} seconds and sells at your target, or at a stop loss 3% below your average price.`)
        ];
        const edges = [E("p0", "p1", "r", "l"), E("p1", "p2", "r", "l"), E("p2", "p3", "r", "l", "you"), E("p3", "p4", "r", "l")];

        return {
            pills: `<span class="sj-pill sj-pill-warn">NOT MONITORED</span><span class="sj-pill sj-pill-muted">${h ? "Zerodha holding" : "Zerodha position"}</span>`,
            subtitle: `${plural(qty, "share")} × ${money(avg)} in your Zerodha account`,
            stats: [
                ["Qty", String(qty), ""],
                ["Avg price", money(avg), ""],
                ["Invested", money(invested), ""],
                ["Live price", ltp ? money(ltp) : "-", SOURCE_TEXT[d.ltpSource] || ""],
                ["P&L", ltp ? signed(pnl) : "-", ltp ? pctOf(pnl, invested) : "", ltp ? cls(pnl) : ""],
                ["Managed by bot", "No", "set a target to hand it over"]
            ],
            strip: null,
            diagramTitle: "What happens to these shares",
            decision: { kind: "", text: "The bot is not watching this stock" },
            diagram: { nodes, edges },
            done: [["p0", "p1"], ["p1", "p2"]], cur: [], nowId: "p2"
        };
    }

    // --------------------------------------------------------------------------------------
    // View: today's BUY signal that a risk guard skipped
    // --------------------------------------------------------------------------------------
    // [short text for the box, full explanation for the detail panel] per failed guard number.
    const GUARD_FIX = {
        2: () => ["log in to Zerodha", "Log in to Zerodha from Token Manager. The bot trades again once today's login is active."],
        3: () => ["wait for market hours", "The bot only buys inside the trading window. It will look at this stock again in the next scan during market hours."],
        4: () => ["wait for opening delay", "New buys wait for the opening delay after the market opens. The next scan after the delay can buy it."],
        5: () => ["needs a stronger signal", "The signal didn't meet enough checklist items. It may qualify in a later scan if the setup improves."],
        6: () => ["cap resets tomorrow", "Today's trade cap is used up. It resets tomorrow, or you can raise Max Trades/Day after market hours."],
        7: () => ["review, then switch on", "Today's loss limit was hit, so the bot switched itself off. Review the day, then switch Live Trading back on."],
        8: () => ["wait for a free slot", "The portfolio already has the maximum number of open positions. A slot frees up when one is sold."],
        9: () => ["already holding it", "The bot already holds this stock, and it never buys the same stock twice."],
        12: () => ["price moved over 2%", "The price moved more than 2% between the scan and the order, so buying would have meant chasing. A later scan can try again."],
        13: d => ["share over " + boxMoney(d.fixedAmountPerTrade), `One share costs more than the ${money(d.fixedAmountPerTrade)} trade amount. Raise "Trade amount per stock" after market hours to include it.`]
    };

    function buildSkipView(d) {
        const N = Flow.node, E = Flow.edge;
        const s = d.lastSkip;
        const n = s.guardNumber || 0;
        const guardName = s.guardName || "A risk check";
        const at = timeIst(s.atUtc);
        const isBreaker = s.actionType === "CIRCUIT_BREAKER";

        let fixShort, fixText, fixValue = "", shortBy = null;
        if (n === 11) {
            const margin = d.availableMargin;
            shortBy = margin != null ? d.fixedAmountPerTrade - margin : null;
            fixText = shortBy != null && shortBy > 0
                ? `Your Zerodha margin is ${money(margin)} but each trade needs ${money(d.fixedAmountPerTrade)}. Add ${money(shortBy)}, or lower "Trade amount per stock" below ${money(margin)} after market hours.`
                : margin != null
                    ? `Your margin is now ${money(margin)}, which covers the ${money(d.fixedAmountPerTrade)} trade amount. The next scan can buy it if the signal is still there.`
                    : `Each trade needs ${money(d.fixedAmountPerTrade)} of available margin in Zerodha.`;
            fixValue = shortBy != null && shortBy > 0 ? "short " + money(shortBy) : "";
            fixShort = shortBy != null && shortBy > 0 ? `add ${boxMoney(shortBy)} margin` : "margin looks OK now";
        } else {
            [fixShort, fixText] = (GUARD_FIX[n] || (() => ["see the reason", "Check the reason above; the stock is looked at again in the next scan."]))(d);
        }
        const marginAtSkip = (s.reason || "").match(/₹([\d,.]+) < Trade Amount ₹([\d,.]+)/);

        const passedText = n > 2 ? `Guards 1–${n - 1}` : n === 2 ? "Guard 1" : "Earlier checks";
        const nodes = [
            N("s0", 0, 0, "done", "Signal found", `${at} scan`, `The ${at} scan picked ${d.symbol} as a BUY candidate.`, s.price ? money(s.price) : ""),
            N("s1", 1, 0, "done", `${passedText} passed`, n > 1 ? "earlier checks OK" : "", "Every check before the failing one passed."),
            N("s2", 2, 0, "bad", n ? `Check ${n} failed\n${guardName}` : guardName,
                marginAtSkip ? shortText(`₹${marginAtSkip[1]} < ₹${marginAtSkip[2]}`, 23) : "", s.reason, n ? `check ${n} of ${s.totalGuards}` : "", "", { w: 170, x: Flow.GRID.OX + 2 * Flow.GRID.PX - 10, h: 58, y: Flow.GRID.OY - 4 }),
            N("s3", 3, 0, "bad", "Skipped", `at ${at}`, `Nothing was bought. It is logged as ${isBreaker ? "CIRCUIT_BREAKER" : "REAL_SIGNAL_SKIPPED"} in the Live Real Trade Audit Stream.`),
            isBreaker
                ? N("s4", 4, 0, "bad", "Bot switched OFF", "until you turn it on", "The daily loss limit pauses the whole bot, not just this stock.")
                : N("s4", 4, 0, "later", "Next scan", "tries again in 15 min", "The stock is scored again in the next 15-minute scan and can be bought if every check passes."),
            N("fix", 2, 1, "info", "How to unblock", fixShort, fixText, fixValue, "", { valueCls: shortBy > 0 ? "sj-neg" : "" })
        ];
        const edges = [E("s0", "s1", "r", "l"), E("s1", "s2", "r", "l"), E("s2", "s3", "r", "l", "fail", { c: "r" }), E("s3", "s4", "r", "l"), E("s2", "fix", "b", "t", "why")];

        const stats = [["Skipped at", at, ""], ["Failed check", n ? `${n} of ${s.totalGuards}` : "-", guardName]];
        if (n === 11) {
            stats.push(["Zerodha margin", d.availableMargin != null ? money(d.availableMargin) : "-", "available now"]);
            stats.push(["Trade amount", money(d.fixedAmountPerTrade), "per stock"]);
            if (shortBy != null) stats.push(["Short by", shortBy > 0 ? money(shortBy) : "₹0.00", shortBy > 0 ? "to place one trade" : "margin is enough now", shortBy > 0 ? "sj-neg" : "sj-pos"]);
        } else if (s.price) {
            stats.push(["Price at scan", money(s.price), ""]);
        }

        return {
            pills: `<span class="sj-pill sj-pill-skip">SKIPPED TODAY</span>`,
            subtitle: "Why the bot didn't buy it, and what would unblock it.",
            stats,
            strip: null,
            diagramTitle: "Journey through the risk guards",
            decision: null,
            diagram: { nodes, edges },
            done: [["s0", "s1"], ["s1", "s2"]], cur: [["s2", "s3"], ["s3", "s4"]], nowId: "s3"
        };
    }

    document.addEventListener("DOMContentLoaded", init);
    return { open };
})();

/** Row buttons call this: mode "position" (default) or "skip" (audit stream "Why?"). */
window.openStockJourney = function (symbol, mode) {
    window.QeStockJourney.open(symbol, mode);
};
