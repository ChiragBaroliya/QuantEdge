/**
 * Backtest (Views/Backtest/Index) - Plan Phase 7.
 *
 *   POST   /api/backtest/run                 -> queue a run (blank fields = current settings)
 *   GET    /api/backtest/runs                -> run list with headline KPIs (polled while a run is going)
 *   GET    /api/backtest/runs/{id}           -> full result (KPIs, equity, breakdowns, sweep, factor edge, coverage)
 *   GET    /api/backtest/runs/{id}/trades    -> the trades the simulated portfolio took (+ trades.csv)
 *   DELETE /api/backtest/runs/{id}           -> cancel (queued / running) or delete (finished)
 *
 * The replay runs in the worker on stored candles only - this page never causes a Zerodha call.
 */
(function () {
    "use strict";

    const root = document.getElementById("backtestPage");
    if (!root) return;
    const cfg = window.QuantEdgeConfig || {};
    const apiBaseUrl = root.dataset.api || cfg.apiBaseUrl || "";
    const userId = Number(root.dataset.user || cfg.userId || 1);
    const api = path => `${apiBaseUrl}/api/backtest/${path}`;

    const el = id => document.getElementById(id);
    const esc = s => String(s == null ? "" : s).replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
    const money = v => (v < 0 ? "-" : "") + "₹" + Math.abs(Number(v || 0)).toLocaleString("en-IN", { maximumFractionDigits: 0 });
    const signedMoney = v => (v > 0 ? "+" : "") + money(v);
    const num = (v, d = 1) => Number(v || 0).toLocaleString("en-IN", { minimumFractionDigits: d, maximumFractionDigits: d });
    const r = v => v == null ? "–" : `${v > 0 ? "+" : ""}${Number(v).toFixed(2)}R`;
    // ProfitFactor is stored as 99 when there were no losing trades.
    const pf = v => Number(v) >= 99 ? "∞" : num(v, 2);
    const cls = v => v > 0 ? "bt-pos" : v < 0 ? "bt-neg" : "";
    const timeIst = utc => new Date(utc).toLocaleString("en-IN", { day: "2-digit", month: "short", year: "2-digit", hour: "2-digit", minute: "2-digit", timeZone: "Asia/Kolkata" });
    const isoDate = d => d.toISOString().slice(0, 10);
    // Calendar dates (IST trading days, sent without a time zone) - formatted as dates, never shifted by the browser's zone.
    const ymd = s => s ? new Date(String(s).slice(0, 10) + "T00:00:00Z").toLocaleDateString("en-IN", { day: "2-digit", month: "short", year: "2-digit", timeZone: "UTC" }) : "–";

    const VERDICT = { PASS: "Edge found", FAIL: "No edge - lost money", NOT_PROVEN: "Not proven" };
    const ACTIVE = new Set(["QUEUED", "RUNNING", "CANCEL_REQUESTED"]);

    let runs = [], viewingId = null, pollTimer = null, equityChart = null;

    // ------------------------------------------------------------------ form
    (function initForm() {
        const now = new Date(Date.now() + 330 * 60000);            // IST
        const to = new Date(now); to.setUTCDate(to.getUTCDate() - 1);
        const from = new Date(to); from.setUTCFullYear(from.getUTCFullYear() - 2);
        el("btTo").value = isoDate(to);
        el("btFrom").value = isoDate(from);
        el("btForm").addEventListener("submit", submit);
    })();

    function val(id, number = true) {
        const v = el(id).value.trim();
        if (v === "") return undefined;
        return number ? Number(v) : v;
    }

    async function submit(e) {
        e.preventDefault();
        const body = {
            fromDate: el("btFrom").value, toDate: el("btTo").value,
            gateMode: val("btGate", false), buyScoreThreshold: val("btBuy"), minConditionsMatch: val("btMin"),
            capital: val("btCapital"), amountPerTrade: val("btAmount"), sizingMode: val("btSizing", false), riskPct: val("btRisk"),
            maxPositions: val("btMaxPos"), slippagePct: val("btSlip"),
            exitMode: val("btExit", false), stopLossAtrMult: val("btSl"), targetAtrMult: val("btTgt"), trailAtrMult: val("btTrail"),
            maxDurationDays: val("btDays"), maxTradesPerDay: val("btTradesDay"), dailyLossLimit: val("btLoss"),
            symbols: (val("btSymbols", false) || "").split(/[\s,]+/).filter(Boolean),
            label: val("btLabel", false)
        };
        const msg = el("btMsg"), btn = el("btRun");
        btn.disabled = true; msg.className = "bt-msg"; msg.textContent = "Queueing…";
        try {
            const res = await fetch(`${api("run")}?userId=${userId}`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
            if (!res.ok) throw new Error(await res.text() || `HTTP ${res.status}`);
            const { id } = await res.json();
            msg.textContent = `Run #${id} queued - the worker picks it up within ~10 s. Results appear below when it finishes.`;
            viewingId = id;
            await loadRuns();
        } catch (err) {
            msg.className = "bt-msg err";
            msg.textContent = "Couldn't queue the run: " + err.message;
        } finally {
            btn.disabled = false;
        }
    }

    // ------------------------------------------------------------------ runs list
    async function loadRuns() {
        try {
            const res = await fetch(api("runs?limit=30"));
            if (!res.ok) throw new Error(`HTTP ${res.status}`);
            runs = await res.json();
            renderRuns();
            const active = runs.some(x => ACTIVE.has(x.status));
            clearTimeout(pollTimer);
            if (active) pollTimer = setTimeout(loadRuns, 4000);
            const viewed = runs.find(x => x.id === viewingId);
            if (viewed && viewed.status === "DONE" && el("btResult").dataset.run !== String(viewed.id)) showRun(viewed.id);
            else if (viewed && ACTIVE.has(viewed.status)) renderPending(viewed);
        } catch (err) {
            el("btRuns").innerHTML = `<tr><td colspan="10" class="bt-empty">Couldn't load runs - check the API is running and backtest_runs exists (schema.sql). ${esc(err.message)}</td></tr>`;
        }
    }

    function renderRuns() {
        if (!runs.length) {
            el("btRuns").innerHTML = `<tr><td colspan="10" class="bt-empty">No runs yet. Start one above.</td></tr>`;
            return;
        }
        el("btRuns").innerHTML = runs.map(x => {
            const k = x.summary && x.summary.kpis;
            const verdict = x.summary && x.summary.verdict;
            const status = ACTIVE.has(x.status)
                ? `<span class="bt-badge bt-${x.status}">${esc(x.status === "QUEUED" ? "Queued" : x.status === "RUNNING" ? `Running ${x.progressPct}%` : "Stopping")}</span>
                   <div class="bt-bar"><i style="width:${x.progressPct}%"></i></div><div class="bt-note">${esc(x.message || "")}</div>`
                : `<span class="bt-badge bt-${x.status}">${esc(x.status)}</span>${x.error ? `<div class="bt-note bt-neg">${esc(x.error)}</div>` : ""}`;
            const action = ACTIVE.has(x.status) ? "Stop" : "Delete";
            return `<tr class="${x.status === "DONE" ? "click" : ""} ${x.id === viewingId ? "hl" : ""}" data-id="${x.id}">
                <td>${x.id}</td>
                <td>${esc(x.label || "")}<div class="bt-note">${timeIst(x.createdAt)}</div></td>
                <td>${status}</td>
                <td class="num">${k ? k.trades : "–"}</td>
                <td class="num ${k ? cls(k.expectancyR) : ""}">${k ? r(k.expectancyR) : "–"}</td>
                <td class="num">${k ? num(k.winRatePct) + "%" : "–"}</td>
                <td class="num ${k ? cls(k.netPnl) : ""}">${k ? signedMoney(k.netPnl) : "–"}</td>
                <td class="num">${k ? num(k.maxDrawdownPct) + "%" : "–"}</td>
                <td>${verdict ? `<span class="bt-badge bt-${verdict}">${esc(VERDICT[verdict] || verdict)}</span>` : ""}</td>
                <td><button type="button" class="bt-link" data-del="${x.id}">${action}</button></td>
            </tr>`;
        }).join("");
    }

    el("btRuns").addEventListener("click", async e => {
        const del = e.target.closest("[data-del]");
        if (del) {
            e.stopPropagation();
            const id = Number(del.dataset.del);
            const run = runs.find(x => x.id === id);
            const verb = run && ACTIVE.has(run.status) ? "Stop" : "Delete";
            if (!confirm(`${verb} run #${id}?`)) return;
            await fetch(api(`runs/${id}`), { method: "DELETE" });
            if (viewingId === id && verb === "Delete") { viewingId = null; el("btResult").innerHTML = ""; el("btResult").dataset.run = ""; }
            loadRuns();
            return;
        }
        const row = e.target.closest("tr.click");
        if (row) showRun(Number(row.dataset.id));
    });

    function renderPending(run) {
        el("btResult").dataset.run = "";
        el("btResult").innerHTML = `<section class="bt-card"><div class="bt-head"><div><h2>Run #${run.id} - ${esc(run.label || "")}</h2>
            <p>${esc(run.message || "")} (${run.progressPct}%)</p></div></div><div class="bt-bar"><i style="width:${run.progressPct}%"></i></div></section>`;
    }

    // ------------------------------------------------------------------ result
    async function showRun(id) {
        viewingId = id;
        renderRuns();
        const box = el("btResult");
        box.innerHTML = `<section class="bt-card"><div class="bt-empty">Loading run #${id}…</div></section>`;
        try {
            const [runRes, tradesRes] = await Promise.all([fetch(api(`runs/${id}`)), fetch(api(`runs/${id}/trades`))]);
            if (!runRes.ok) throw new Error(`HTTP ${runRes.status}`);
            const run = await runRes.json();
            const trades = tradesRes.ok ? await tradesRes.json() : [];
            box.dataset.run = String(id);
            renderResult(run, trades);
            box.scrollIntoView({ behavior: "smooth", block: "start" });
        } catch (err) {
            box.innerHTML = `<section class="bt-card"><div class="bt-empty">Couldn't load run #${id}: ${esc(err.message)}</div></section>`;
        }
    }

    function table(headers, rows, emptyText = "No trades.") {
        if (!rows.length) return `<div class="bt-empty">${esc(emptyText)}</div>`;
        return `<div class="bt-wrap"><table class="bt-table"><thead><tr>${headers.map(h => `<th class="${h.num ? "num" : ""}">${h.label}</th>`).join("")}</tr></thead>
            <tbody>${rows.join("")}</tbody></table></div>`;
    }

    const groupHeaders = first => [{ label: first }, { label: "Trades", num: 1 }, { label: "Win rate", num: 1 }, { label: "Expectancy", num: 1 }, { label: "Profit factor", num: 1 }, { label: "Net P&amp;L", num: 1 }];
    const groupRows = rows => rows.map(g => `<tr><td>${esc(g.key)}</td><td class="num">${g.trades}</td><td class="num">${num(g.winRatePct)}%</td>
        <td class="num ${cls(g.expectancyR)}">${r(g.expectancyR)}</td><td class="num">${pf(g.profitFactor)}</td><td class="num ${cls(g.netPnl)}">${signedMoney(g.netPnl)}</td></tr>`);
    const groupCard = (title, note, first, rows) => `<div><h3 class="bt-sub">${title} <small>${note}</small></h3>${table(groupHeaders(first), groupRows(rows || []))}</div>`;

    function renderResult(run, trades) {
        const s = run.summary || {}, k = s.kpis || {}, p = run.params || {}, c = s.coverage || {};
        const kpi = (label, value, note, css = "") => `<div class="bt-kpi"><span>${label}</span><b class="${css}">${value}</b>${note ? `<small>${note}</small>` : ""}</div>`;
        const threshold = p.gateMode === "REGIME" ? null : p.buyScoreThreshold;

        const sweepRows = (s.thresholdSweep || []).map(x => `<tr class="${x.threshold === threshold ? "hl" : ""}">
            <td>score ≥ ${x.threshold}</td><td class="num">${x.trades}</td><td class="num">${num(x.winRatePct)}%</td>
            <td class="num ${cls(x.expectancyR)}">${r(x.expectancyR)}</td><td class="num">${pf(x.profitFactor)}</td>
            <td class="num ${cls(x.netPnl)}">${signedMoney(x.netPnl)}</td><td class="num">${num(x.maxDrawdownPct)}%</td></tr>`);
        const factorRows = (s.factorEdge || []).map(f => {
            const helps = f.tradesWith >= 10 && f.tradesWithout >= 10 ? (f.expectancyWithR > f.expectancyWithoutR ? "✓ helps" : "✗ doesn't help") : "too few trades";
            return `<tr><td>${esc(f.code)}</td><td class="num">${f.tradesWith}</td><td class="num ${cls(f.expectancyWithR)}">${r(f.expectancyWithR)}</td>
                <td class="num">${f.tradesWithout}</td><td class="num ${cls(f.expectancyWithoutR)}">${r(f.expectancyWithoutR)}</td><td>${helps}</td></tr>`;
        });
        const skipped = Object.entries(s.skipped || {});

        el("btResult").innerHTML = `
        <section class="bt-card">
            <div class="bt-head">
                <div>
                    <h2>Run #${run.id} - ${esc(run.label || "")}</h2>
                    <p>${esc(p.gateMode === "REGIME" ? "Market regime gate" : `NIFTY filter · BUY ≥ ${p.buyScoreThreshold} or ≥ ${p.minConditionsMatch}/11 conditions`)}
                       · ${ymd(p.fromDate)} → ${ymd(p.toDate)} · capital ${money(p.capital)} · ${p.sizingMode === "RISK" ? `risk ${p.riskPct}% (max ${money(p.amountPerTrade)})` : `${money(p.amountPerTrade)} per trade`}
                       · max ${p.maxPositions} open · slippage ${p.slippagePct}% / side · exits ${esc(p.exitMode)} (SL ${p.stopLossAtrMult}×, target ${p.targetAtrMult}×, trail ${p.trailAtrMult}× ATR, ${p.maxDurationDays} days)</p>
                </div>
                <a class="btn-trigger" href="${api(`runs/${run.id}/trades.csv`)}">Download trades (CSV)</a>
            </div>
            <div class="bt-verdict ${s.verdict}"><b>${esc(VERDICT[s.verdict] || s.verdict)}</b>${esc(s.verdictReason || "")}</div>
        </section>

        <section class="bt-card">
            <div class="bt-kpis">
                ${kpi("Trades", k.trades, `${num(k.tradesPerMonth)} / month · ${s.liveSignals || 0} signals`)}
                ${kpi("Win rate", num(k.winRatePct) + "%", `avg win ${r(k.avgWinR)} · avg loss ${r(k.avgLossR)}`)}
                ${kpi("Expectancy / trade", r(k.expectancyR), "after charges + slippage · go-live bar ≥ +0.20R", cls(k.expectancyR))}
                ${kpi("Profit factor", pf(k.profitFactor), "₹ won ÷ ₹ lost")}
                ${kpi("Net P&amp;L", signedMoney(k.netPnl), `${num(k.returnPct)}% · CAGR ${num(k.cagrPct)}%`, cls(k.netPnl))}
                ${kpi("Max drawdown", num(k.maxDrawdownPct) + "%", money(k.maxDrawdownRs), k.maxDrawdownPct < -15 ? "bt-neg" : "")}
                ${kpi("Charges paid", money(k.charges), `gross ${signedMoney(k.grossPnl)}`)}
                ${kpi("Avg hold", num(k.avgSessionsHeld) + " days", `exposure ${num(k.exposurePct)}% of equity`)}
                ${kpi("Best / worst", `${r(k.bestTradeR)} / ${r(k.worstTradeR)}`, `max ${k.maxConsecutiveLosses} losses in a row`)}
            </div>
        </section>

        <section class="bt-card">
            <h3 class="bt-sub">Equity curve <small>capital + realised P&amp;L + open positions at each day's close; shaded = drawdown</small></h3>
            <div class="bt-chart"><canvas id="btEquity"></canvas></div>
        </section>

        <section class="bt-card bt-grid2">
            ${groupCard("Stability", "an edge should show in both halves, not just one", "Period", s.byHalf)}
            ${groupCard("By year", "", "Year", s.byYear)}
        </section>

        <section class="bt-card bt-grid2">
            <div>
                <h3 class="bt-sub">Threshold sweep <small>same data, score ≥ T only (hard filters + gate) - is the threshold robust or lucky?</small></h3>
                ${table([{ label: "Rule" }, { label: "Trades", num: 1 }, { label: "Win rate", num: 1 }, { label: "Expectancy", num: 1 }, { label: "PF", num: 1 }, { label: "Net P&amp;L", num: 1 }, { label: "Max DD", num: 1 }], sweepRows, "No sweep rows.")}
            </div>
            <div>
                <h3 class="bt-sub">Factor edge <small>trades that earned a factor's points vs trades that didn't</small></h3>
                ${table([{ label: "Factor" }, { label: "With", num: 1 }, { label: "Exp.", num: 1 }, { label: "Without", num: 1 }, { label: "Exp.", num: 1 }, { label: "" }], factorRows)}
            </div>
        </section>

        <section class="bt-card bt-grid2">
            ${groupCard("By market regime", "regime in force on the entry day", "Regime", s.byRegime)}
            ${groupCard("By exit", "how trades ended", "Exit", s.byExit)}
            ${groupCard("By score", "entry score bucket", "Score", s.byScore)}
            ${groupCard("By sector", "", "Sector", s.bySector)}
        </section>

        <section class="bt-card bt-grid2">
            <div>
                <h3 class="bt-sub">Signals not taken <small>${s.liveSignals || 0} live signals in total</small></h3>
                ${skipped.length ? `<ul class="bt-list">${skipped.map(([k2, v]) => `<li>${esc(k2)}: <b>${v.toLocaleString("en-IN")}</b></li>`).join("")}</ul>` : `<div class="bt-empty">None.</div>`}
                <h3 class="bt-sub" style="margin-top:14px">Data used</h3>
                <ul class="bt-list">
                    <li>${c.symbolsWithData} of ${c.symbolsRequested} stocks had 15m data · ${c.sessions} trading days · ${Number(c.scansEvaluated || 0).toLocaleString("en-IN")} scans</li>
                    <li>15m data from ${ymd(c.firstIntradayDate)} to ${ymd(c.lastIntradayDate)}</li>
                    ${(c.symbolsWithoutData || []).length ? `<li>No data: ${esc(c.symbolsWithoutData.join(", "))}</li>` : ""}
                    ${(c.suspectedSplits || []).length ? `<li>Suspected unadjusted splits/bonuses (excluded around them): ${esc(c.suspectedSplits.join(", "))}</li>` : ""}
                </ul>
            </div>
            <div>
                <h3 class="bt-sub">How this was simulated</h3>
                <ul class="bt-list">${(s.assumptions || []).map(a => `<li>${esc(a)}</li>`).join("")}</ul>
            </div>
        </section>

        <section class="bt-card">
            <h3 class="bt-sub">Trades <small>${trades.length} · times in IST</small></h3>
            <div id="btTrades"></div>
        </section>`;

        renderTrades(trades, 100);
        renderEquity(s.equity || [], p.capital);
    }

    function renderTrades(trades, limit) {
        const rows = trades.slice(0, limit).map(t => `<tr>
            <td><b>${esc(t.symbol)}</b><div class="bt-note">${esc(t.sector || "")}</div></td>
            <td>${timeIst(t.entryTime)}</td><td>${timeIst(t.exitTime)}</td><td class="num">${t.sessionsHeld}</td>
            <td class="num">${num(t.entryPrice, 2)}</td><td class="num">${num(t.exitPrice, 2)}</td><td class="num">${t.quantity}</td>
            <td>${esc(t.exitCategory)}<div class="bt-note">${esc(t.exitReason)}</div></td>
            <td class="num ${cls(t.netPnl)}">${signedMoney(t.netPnl)}<div class="bt-note">charges ${money(t.charges)}</div></td>
            <td class="num ${cls(t.rMultiple)}">${r(t.rMultiple)}</td><td class="num">${t.score}</td><td>${esc(t.regime || "")}</td></tr>`);
        el("btTrades").innerHTML = table([{ label: "Stock" }, { label: "Entry" }, { label: "Exit" }, { label: "Days", num: 1 }, { label: "Entry ₹", num: 1 },
            { label: "Exit ₹", num: 1 }, { label: "Qty", num: 1 }, { label: "Exit" }, { label: "Net", num: 1 }, { label: "R", num: 1 }, { label: "Score", num: 1 }, { label: "Regime" }], rows)
            + (trades.length > limit ? `<div class="bt-actions"><button type="button" class="bt-link" id="btMore">Show all ${trades.length} trades</button></div>` : "");
        const more = el("btMore");
        if (more) more.addEventListener("click", () => renderTrades(trades, trades.length));
    }

    function renderEquity(points, capital) {
        if (equityChart) { equityChart.destroy(); equityChart = null; }
        const canvas = el("btEquity");
        if (!canvas || typeof Chart === "undefined" || !points.length) return;
        equityChart = new Chart(canvas, {
            type: "line",
            data: {
                labels: points.map(x => ymd(x.date)),
                datasets: [
                    { label: "Equity", data: points.map(x => x.equity), borderColor: "#a78bfa", backgroundColor: "transparent", pointRadius: 0, borderWidth: 2, yAxisID: "y", tension: 0.1 },
                    { label: "Capital", data: points.map(() => capital), borderColor: "rgba(148,163,184,.5)", borderDash: [4, 4], pointRadius: 0, borderWidth: 1, yAxisID: "y" },
                    { label: "Drawdown %", data: points.map(x => x.drawdownPct), borderColor: "rgba(248,113,113,.6)", backgroundColor: "rgba(248,113,113,.15)", fill: true, pointRadius: 0, borderWidth: 1, yAxisID: "dd" }
                ]
            },
            options: {
                responsive: true, maintainAspectRatio: false, interaction: { mode: "index", intersect: false },
                plugins: { legend: { labels: { color: "#cbd5e1", boxWidth: 12 } } },
                scales: {
                    x: { ticks: { color: "#94a3b8", maxTicksLimit: 10 }, grid: { color: "rgba(148,163,184,.08)" } },
                    y: { position: "left", ticks: { color: "#94a3b8", callback: v => "₹" + Number(v).toLocaleString("en-IN") }, grid: { color: "rgba(148,163,184,.08)" } },
                    dd: { position: "right", max: 0, ticks: { color: "#f87171", callback: v => Number(v).toFixed(1) + "%" }, grid: { display: false } }
                }
            }
        });
    }

    loadRuns();
})();
