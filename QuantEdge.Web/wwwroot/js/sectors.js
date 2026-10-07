/**
 * Sector Dashboard - Sector Overview (Views/Sector/Index) and Sector Detail (Views/Sector/Detail).
 *
 *   Overview: GET /api/sectors/overview   -> ranked sector cards + "Top strong sectors"
 *   Detail:   GET /api/sectors/{id}       -> today's trade candidates + every stock's BUY / WATCH / NO TRADE,
 *                                            with a modal listing exactly which conditions passed or failed.
 *
 * Stock signals come from the same SwingDecisionEngine evaluation as the Signal Dashboard verdict and the
 * auto-trading bot; the sector is one extra gate (a WEAK sector holds a bot-ready stock at WATCH).
 * Both pages refresh every 60 s while visible; the API caches one snapshot per minute.
 */
(function () {
    "use strict";

    const REFRESH_MS = 60000;
    const cfg = window.QuantEdgeConfig || {};
    const apiBaseUrl = cfg.apiBaseUrl || "";
    const userId = cfg.userId || 1;

    const el = id => document.getElementById(id);
    const esc = s => String(s == null ? "" : s).replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
    const money = v => "₹" + Number(v || 0).toLocaleString("en-IN", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
    const num = (v, d = 1) => Number(v || 0).toLocaleString("en-IN", { minimumFractionDigits: d, maximumFractionDigits: d });
    const timeIst = utc => new Date(utc).toLocaleTimeString("en-IN", { hour: "2-digit", minute: "2-digit", timeZone: "Asia/Kolkata" });
    const pct = v => v == null ? "–" : `${v > 0 ? "+" : ""}${Number(v).toFixed(2)}%`;
    const pctClass = v => v == null || Number(v) === 0 ? "sx-flat" : v > 0 ? "sx-pos" : "sx-neg";
    // Where the price / day change came from: live feed tick or stored candles, and when.
    const priceTitle = x => x.priceAsOfUtc
        ? `${x.priceSource === "LIVE" ? "Live" : "Stored candles"} · as of ${timeIst(x.priceAsOfUtc)} IST${x.prevClose ? ` · prev close ${money(x.prevClose)}` : ""}`
        : "";

    const STRENGTH = {
        STRONG: { cls: "sx-strong", label: "Strong", rank: 3 },
        NEUTRAL: { cls: "sx-neutral", label: "Neutral", rank: 2 },
        WEAK: { cls: "sx-weak", label: "Weak", rank: 1 },
        NO_DATA: { cls: "sx-nodata", label: "No data", rank: 0 }
    };
    const SIGNAL = {
        BUY: { cls: "sx-buy", label: "BUY", rank: 3 },
        WATCH: { cls: "sx-watch", label: "WATCH", rank: 2 },
        NO_TRADE: { cls: "sx-notrade", label: "NO TRADE", rank: 1 },
        NO_DATA: { cls: "sx-nodata", label: "NO DATA", rank: 0 }
    };
    const strengthOf = s => STRENGTH[s] || STRENGTH.NO_DATA;
    const signalOf = s => SIGNAL[s] || SIGNAL.NO_DATA;
    const chip = (meta, extra = "") => `<span class="sx-chip ${meta.cls} ${extra}">${esc(meta.label)}</span>`;

    function marketBanner(d) {
        if (d.marketGateMode === "REGIME") {
            const reason = esc(d.marketReason || "Market regime not computed yet.");
            return d.marketPassed
                ? `<div class="sx-banner ${d.marketReason && d.marketReason.startsWith("BULLISH (") ? "sx-pass" : "sx-watch"}"><span><b>Market regime gate:</b> ${reason} Stocks must clear this regime's bar to be BUY.</span></div>`
                : `<div class="sx-banner sx-fail"><span><b>Market regime blocks new buys:</b> ${reason} Every stock shows NO TRADE; sector strength still shows who leads.</span></div>`;
        }
        if (d.marketPassed) {
            return `<div class="sx-banner sx-pass"><span><b>NIFTY 50 trend filter passes.</b> The market allows new buys - sector and stock conditions decide.</span></div>`;
        }
        if (d.marketRequired) {
            return `<div class="sx-banner sx-fail"><span><b>NIFTY 50 is below its trend filter.</b> The bot buys nothing while the market is weak, so every stock shows NO TRADE. Sector strength is still shown so you can see who leads when NIFTY recovers.</span></div>`;
        }
        return `<div class="sx-banner sx-watch"><span><b>NIFTY 50 is below its trend filter.</b> Soft mode: stocks lose score points and size is reduced, but buys are not blocked.</span></div>`;
    }

    // Market regime card (Plan Phase 1) - GET /api/market/regime, database only.
    const REGIME = {
        BULLISH: { cls: "sx-pass", label: "Bullish" },
        BULLISH_WEAKENING: { cls: "sx-watch", label: "Bullish, weakening" },
        SIDEWAYS: { cls: "sx-watch", label: "Sideways" },
        BEARISH: { cls: "sx-fail", label: "Bearish" },
        STRONG_BEARISH: { cls: "sx-fail", label: "Strong bearish" }
    };
    const regimeOf = r => REGIME[r] || { cls: "sx-nodata", label: r || "Unknown" };

    function sparkline(history) {
        const pts = (history || []).slice().sort((a, b) => new Date(a.tradeDate) - new Date(b.tradeDate));
        if (pts.length < 2) return "";
        const w = 220, h = 44;
        const xy = pts.map((p, i) => `${(i / (pts.length - 1) * w).toFixed(1)},${(h - p.score / 100 * h).toFixed(1)}`).join(" ");
        // Dashed guides at the regime boundaries 70 / 40 / 25.
        const guide = v => `<line x1="0" x2="${w}" y1="${h - v / 100 * h}" y2="${h - v / 100 * h}" stroke="currentColor" stroke-opacity=".2" stroke-dasharray="3 3"/>`;
        return `<svg class="sx-spark" viewBox="0 0 ${w} ${h}" width="${w}" height="${h}" aria-label="Regime score, last ${pts.length} days">
            ${guide(70)}${guide(40)}${guide(25)}
            <polyline points="${xy}" fill="none" stroke="var(--sx-c)" stroke-width="2"/></svg>`;
    }

    function regimeCard(r) {
        const x = r.latest;
        if (!x) return `<div class="sx-panel sx-empty">No market regime yet - it is computed after the evening NSE bhavcopy (needs NIFTY 50 daily candles).</div>`;
        const meta = regimeOf(x.regime), p = r.policy;
        const bar = (label, pts, max) => `<div class="sx-rg-part"><span>${label}</span><div class="sx-rg-track"><i style="width:${Math.max(0, Math.min(100, pts / max * 100))}%"></i></div><b class="sx-num">${pts}/${max}</b></div>`;
        const policy = p
            ? (p.maxPositions <= 0 ? "No new entries." : `Stock score ≥ ${p.minStockScore}${p.requireRelativeStrength ? ", must beat NIFTY" : ""}, max ${p.maxPositions} positions, risk ${num(p.riskPct, 2)}% per trade.`)
            : "";
        const mode = r.botUsesRegime
            ? `<b>The bot uses this regime</b> as its market gate.`
            : `Shown for information - the bot still uses the NIFTY trend filter. Switch "Market gate" to Regime in Swing settings to use it.`;
        return `<div class="sx-panel sx-regime ${meta.cls}">
            <div class="sx-rg-head">
                <div><div class="sx-rg-label">Market regime · ${esc(new Date(x.tradeDate).toLocaleDateString("en-IN", { day: "2-digit", month: "short" }))}</div>
                    <div class="sx-rg-title">${chip(meta, "sx-chip-lg")} <span class="sx-rg-score sx-num">${x.score}<small>/100</small></span>
                    ${x.rawRegime !== x.regime ? `<span class="sx-rg-note" title="A regime change needs to hold for a few days (hysteresis)">raw: ${esc(regimeOf(x.rawRegime).label)}</span>` : ""}</div></div>
                <div style="color:var(--sx-c)">${sparkline(r.history)}</div>
            </div>
            <div class="sx-rg-parts">
                ${bar("Trend", x.trendPts, 40)}${bar("Breadth", x.breadthPts, 35)}${bar("Volatility", x.volPts, 15)}${bar("Drawdown", x.drawdownPts, 10)}
            </div>
            <div class="sx-rg-facts">NIFTY ${num(x.niftyClose, 2)} · EMA50 ${num(x.ema50, 0)} · EMA200 ${num(x.ema200, 0)} · ${num(x.drawdownPct)}% off high ·
                ${num(x.pctAboveEma50, 0)}% of ${x.breadthStocks} stocks above EMA50${x.vix != null ? ` · VIX ${num(x.vix)}` : ""}</div>
            <div class="sx-rg-policy"><b>Policy:</b> ${esc(policy)} ${mode}</div>
        </div>`;
    }

    async function loadRegime() {
        const box = el("sxRegime");
        if (!box) return;
        try {
            box.innerHTML = regimeCard(await getJson("market/regime?days=60", false));
        } catch (err) {
            console.error("Market regime load failed:", err);
            box.innerHTML = `<div class="sx-panel sx-empty">Market regime unavailable - apply market_regime_daily / regime_policy from schema.sql.</div>`;
        }
    }

    function startPolling(load) {
        load(false);
        setInterval(() => { if (!document.hidden) load(false); }, REFRESH_MS);
        const btn = el("sxRefresh");
        if (btn) btn.addEventListener("click", () => load(true));
    }

    function bindPills(containerId, onChange) {
        const box = el(containerId);
        if (!box) return;
        box.addEventListener("click", e => {
            const pill = e.target.closest(".sx-pill");
            if (!pill) return;
            box.querySelectorAll(".sx-pill").forEach(p => p.classList.toggle("active", p === pill));
            onChange(pill.dataset);
        });
    }

    async function getJson(path, refresh) {
        const sep = path.includes("?") ? "&" : "?";
        const res = await fetch(`${apiBaseUrl}/api/${path}${sep}userId=${userId}${refresh ? "&refresh=true" : ""}`);
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return res.json();
    }

    // ======================================================================================
    // Sector Overview
    // ======================================================================================
    function initOverview() {
        let data = null, sortKey = "strength", filter = "ALL", inFlight = false;

        const SORTS = {
            strength: (a, b) => strengthOf(b.strength).rank - strengthOf(a.strength).rank || b.strengthScore - a.strengthScore,
            change: (a, b) => (b.changePct ?? -999) - (a.changePct ?? -999),
            buy: (a, b) => b.buyCount - a.buyCount || b.watchCount - a.watchCount || b.strengthScore - a.strengthScore,
            momentum: (a, b) => b.momentumPct - a.momentumPct || b.strengthScore - a.strengthScore
        };

        async function load(refresh) {
            if (inFlight) return;
            inFlight = true;
            if (refresh) el("sxMeta").textContent = "Re-scoring every sector…";
            try {
                data = await getJson("sectors/overview", refresh);
                render();
            } catch (err) {
                console.error("Sector overview load failed:", err);
                el("sxMeta").textContent = "Couldn't load sectors - check that the API is running. Retrying every minute.";
            } finally {
                inFlight = false;
            }
        }

        function render() {
            const sectors = data.sectors || [];
            el("sxMeta").textContent = `Updated ${timeIst(data.asOfUtc)} IST · ${sectors.length} sectors · same engine as the auto-trading bot · refreshes every minute`;
            el("sxMarket").innerHTML = marketBanner(data);
            loadRegime();
            renderTop(sectors);
            renderGrid(sectors);
        }

        function renderTop(sectors) {
            const strong = sectors.filter(s => s.strength === "STRONG").sort(SORTS.strength).slice(0, 3);
            if (!strong.length) {
                const best = sectors.filter(s => s.strength !== "NO_DATA").sort(SORTS.strength).slice(0, 3);
                el("sxTop").innerHTML = `<div class="sx-panel sx-empty" style="grid-column: 1 / -1;">No sector is strong right now.${best.length ? " Strongest so far: " + best.map(s => `<a href="${detailHref(s)}">${esc(s.name)}</a> (${pct(s.changePct)})`).join(", ") + "." : ""}</div>`;
                return;
            }
            el("sxTop").innerHTML = strong.map((s, i) => `
                <a class="sx-top-item ${strengthOf(s.strength).cls}" href="${detailHref(s)}">
                    <div>
                        <div class="sx-top-rank">#${i + 1}</div>
                        <div class="sx-top-name">${esc(s.name)}</div>
                        <div class="sx-top-sub">${s.buyCount} BUY · ${s.watchCount} WATCH</div>
                    </div>
                    <div class="sx-top-change sx-num ${pctClass(s.changePct)}">${pct(s.changePct)}</div>
                </a>`).join("");
        }

        function renderGrid(sectors) {
            const list = sectors.filter(s => filter === "ALL" || s.strength === filter).sort(SORTS[sortKey]);
            if (!list.length) {
                el("sxGrid").innerHTML = `<div class="sx-panel sx-empty" style="grid-column: 1 / -1;">${sectors.length ? "No sectors match this filter." : "No sectors found - run schema.sql and stock_sector_data.sql."}</div>`;
                return;
            }
            el("sxGrid").innerHTML = list.map(card).join("");
        }

        function card(s) {
            const st = strengthOf(s.strength);
            const moving = s.advancingStocks + s.decliningStocks;
            const upPct = moving ? Math.round(100 * s.advancingStocks / moving) : 0;
            const downPct = moving ? 100 - upPct : 0;
            const count = (n, label, cls) => `<div class="sx-count ${cls} ${n ? "" : "zero"}"><b>${n}</b><span>${label}</span></div>`;
            const changeTitle = s.changeSource === "INDEX" ? "Sector index change" : "Average change of the sector's stocks (index candles not stored)";
            return `
                <a class="sx-card ${st.cls}" href="${detailHref(s)}">
                    <div class="sx-card-head">
                        <div>
                            <div class="sx-card-name">${esc(s.name)}</div>
                            <div class="sx-card-index">${s.indexValue != null ? "Index " + num(s.indexValue, 2) : `${s.totalStocks} stocks`}</div>
                        </div>
                        ${chip(st)}
                    </div>
                    <div class="sx-card-change sx-num ${pctClass(s.changePct)}" title="${changeTitle}">${pct(s.changePct)}<small>${s.changeSource === "INDEX" ? "index" : "avg of stocks"}</small></div>
                    <div class="sx-breadth">
                        <div class="sx-breadth-bar"><span style="width:${upPct}%"></span><span style="width:${downPct}%"></span></div>
                        <div class="sx-breadth-text"><span class="sx-pos">▲ ${s.advancingStocks} up</span><span class="sx-neg">${s.decliningStocks} down ▼</span></div>
                    </div>
                    <div class="sx-counts">
                        ${count(s.buyCount, "BUY", "sx-buy")}
                        ${count(s.watchCount, "Watch", "sx-watch")}
                        ${count(s.noTradeCount, "No trade", "sx-notrade")}
                    </div>
                    <div class="sx-card-foot">
                        <span title="Share of scored stocks passing the bot's daily trend filters (EMA trend + ADX)">Momentum ${s.momentumPct}% in uptrend</span>
                        <span title="0-100: half day change, half breadth">Strength ${s.strengthScore}</span>
                    </div>
                    ${s.noDataCount ? `<div class="sx-card-foot"><span>${s.noDataCount} of ${s.totalStocks} stocks have no candle data</span></div>` : ""}
                </a>`;
        }

        const detailHref = s => `${cfg.detailUrl || "/Sector/Detail"}/${s.sectorId}`;

        bindPills("sxSort", d => { sortKey = d.sort; if (data) renderGrid(data.sectors || []); });
        bindPills("sxFilter", d => { filter = d.filter; if (data) renderGrid(data.sectors || []); });
        startPolling(load);
    }

    // ======================================================================================
    // Sector Detail
    // ======================================================================================
    function initDetail(root) {
        const sectorId = Number(root.dataset.sectorId);
        let data = null, candSort = "score", inFlight = false, openSymbol = null;
        let modal = null;

        const bySignalThen = f => (a, b) => signalOf(b.signal).rank - signalOf(a.signal).rank || f(a, b);
        const CAND_SORTS = {
            score: bySignalThen((a, b) => b.score - a.score),
            momentum: (a, b) => b.timingPoints - a.timingPoints || b.score - a.score,
            volume: (a, b) => b.volumeMultiple - a.volumeMultiple,
            rr: (a, b) => b.riskReward - a.riskReward,
            change: (a, b) => (b.dayChangePct ?? -999) - (a.dayChangePct ?? -999)
        };

        async function load(refresh) {
            if (inFlight) return;
            inFlight = true;
            if (refresh) el("sxMeta").textContent = "Re-scoring…";
            try {
                data = await getJson(`sectors/${sectorId}`, refresh);
                render();
            } catch (err) {
                console.error("Sector detail load failed:", err);
                el("sxMeta").textContent = String(err.message).includes("404")
                    ? "This sector doesn't exist or is inactive."
                    : "Couldn't load the sector - check that the API is running. Retrying every minute.";
            } finally {
                inFlight = false;
            }
        }

        function render() {
            const s = data.sector;
            document.title = `${s.name} - Sector Detail`;
            const pageTitle = document.querySelector(".page-title");
            if (pageTitle) pageTitle.textContent = s.name;
            el("sxMeta").textContent = `Updated ${timeIst(data.asOfUtc)} IST · refreshes every minute`;
            el("sxMarket").innerHTML = data.marketPassed ? "" : marketBanner(data);
            renderHero(s);
            renderCandidates();
            renderTable();
            if (openSymbol) {
                const stock = data.stocks.find(x => x.symbol === openSymbol);
                if (stock) fillModal(stock);
            }
        }

        function renderHero(s) {
            const st = strengthOf(s.strength);
            const hero = el("sxHero");
            hero.className = `sx-hero ${st.cls}`;
            const count = (n, label, cls) => `<div class="sx-count ${cls} ${n ? "" : "zero"}"><b>${n}</b><span>${label}</span></div>`;
            hero.innerHTML = `
                <div>
                    <h2>${esc(s.name)}</h2>
                    <div class="sx-hero-line">
                        <span class="sx-hero-change sx-num ${pctClass(s.changePct)}">${pct(s.changePct)}</span>
                        ${chip(st, "sx-chip-lg")}
                        ${s.indexValue != null ? `<span>Index ${num(s.indexValue, 2)}</span>` : `<span>${s.changeSource === "STOCKS" ? "Average of its stocks" : ""}</span>`}
                        <span>▲ ${s.advancingStocks} up · ${s.decliningStocks} down ▼</span>
                        <span>Momentum ${s.momentumPct}% in uptrend</span>
                    </div>
                </div>
                <div class="sx-hero-counts">
                    ${count(s.buyCount, "BUY", "sx-buy")}
                    ${count(s.watchCount, "Watch", "sx-watch")}
                    ${count(s.noTradeCount, "No trade", "sx-notrade")}
                    ${count(s.noDataCount, "No data", "sx-nodata")}
                </div>`;
        }

        function renderCandidates() {
            const list = data.stocks.filter(x => x.signal === "BUY" || x.signal === "WATCH").sort(CAND_SORTS[candSort]);
            if (!list.length) {
                el("sxCands").innerHTML = `<div class="sx-empty">No stock in ${esc(data.sector.name)} has a BUY or WATCH setup right now.${data.sector.strength === "WEAK" ? " The sector is weak today." : ""}</div>`;
                return;
            }
            el("sxCands").innerHTML = list.map((x, i) => {
                const sig = signalOf(x.signal);
                return `
                    <div class="sx-cand ${sig.cls}" data-symbol="${esc(x.symbol)}" role="button" tabindex="0">
                        <span class="sx-cand-rank">${i + 1}</span>
                        <span class="sx-cand-sym">${esc(x.symbol)}</span>
                        ${chip(sig)}
                        <span class="sx-cand-why" title="${esc(x.summary)}">${esc(x.highlight)}</span>
                        <span class="sx-cand-stat sx-num"><span>Score</span>${x.score}</span>
                        <span class="sx-cand-stat sx-num"><span>Volume</span>${num(x.volumeMultiple)}x</span>
                        <span class="sx-cand-stat sx-num"><span>R:R</span>${x.riskReward ? "1:" + num(x.riskReward) : "–"}</span>
                        <span class="sx-cand-stat sx-num ${pctClass(x.dayChangePct)}"><span>Change</span>${pct(x.dayChangePct)}</span>
                    </div>`;
            }).join("");
        }

        function renderTable() {
            const list = data.stocks.slice().sort(bySignalThen((a, b) => b.score - a.score || a.symbol.localeCompare(b.symbol)));
            if (!list.length) {
                el("sxStocks").innerHTML = `<tr><td colspan="9" class="sx-empty">No stocks are linked to this sector yet - add them in stock_sector_data.sql.</td></tr>`;
                return;
            }
            el("sxStocks").innerHTML = list.map(x => {
                const sig = signalOf(x.signal);
                const scored = x.signal !== "NO_DATA";
                return `
                    <tr data-symbol="${esc(x.symbol)}">
                        <td><div class="sx-sym">${esc(x.symbol)}</div>${x.name ? `<div class="sx-name">${esc(x.name)}</div>` : ""}</td>
                        <td>${chip(sig)}</td>
                        <td class="num sx-num" title="${esc(priceTitle(x))}">${x.lastPrice ? money(x.lastPrice) : "–"}</td>
                        <td class="num sx-num ${pctClass(x.dayChangePct)}">${pct(x.dayChangePct)}</td>
                        <td class="num sx-num">${scored && x.volumeMultiple ? num(x.volumeMultiple) + "x" : "–"}</td>
                        <td class="num sx-num">${scored && x.rsi15m ? num(x.rsi15m) : "–"}</td>
                        <td>${scored ? `<span class="sx-score ${sig.cls}"><span class="sx-score-bar"><i style="width:${x.score}%"></i></span><span class="sx-num">${x.score}</span></span>` : "–"}</td>
                        <td class="num sx-num">${scored ? `${x.metCount}/${x.totalConditions}` : "–"}</td>
                        <td><div class="sx-why">${esc(x.highlight)}</div></td>
                    </tr>`;
            }).join("");
        }

        // ---------------------------------------------------------------- stock modal
        function openStock(symbol) {
            const stock = data && data.stocks.find(x => x.symbol === symbol);
            if (!stock) return;
            openSymbol = symbol;
            fillModal(stock);
            if (!modal && window.bootstrap) {
                const node = el("sxStockModal");
                modal = new bootstrap.Modal(node);
                node.addEventListener("hidden.bs.modal", () => { openSymbol = null; });
            }
            if (modal) modal.show();
        }

        function fillModal(x) {
            const sig = signalOf(x.signal);
            const scored = x.signal !== "NO_DATA";
            el("sxStockTitle").innerHTML = `
                <h3>${esc(x.symbol)}</h3>
                ${chip(sig, "sx-chip-lg")}
                <span class="sx-num" title="${esc(priceTitle(x))}">${x.lastPrice ? money(x.lastPrice) : ""}</span>
                <span class="sx-num ${pctClass(x.dayChangePct)}">${pct(x.dayChangePct)}</span>
                ${x.name ? `<span class="sx-meta">${esc(x.name)}</span>` : ""}`;

            if (!scored) {
                el("sxStockBody").innerHTML = `<p class="sx-m-summary sx-nodata">${esc(x.summary || "This stock can't be scored yet.")}</p>`;
                return;
            }

            const pillars = x.pillars.map(p => {
                const cls = p.state === "PASS" ? "sx-pass" : p.state === "FAIL" ? "sx-fail" : "sx-na";
                return `<div class="sx-pillar ${cls}"><span>${esc(p.label)}</span><b>${esc(p.value)}</b></div>`;
            }).join("");

            const showLevels = (x.signal === "BUY" || x.signal === "WATCH") && x.stopLoss > 0;
            const levels = showLevels ? `
                <div class="sx-m-box" style="margin-top:14px;">
                    <h4>Trade levels</h4>
                    <div class="sx-levels">
                        <div class="sx-level"><span>Entry</span><b>${money(x.entry)}</b></div>
                        <div class="sx-level sl"><span>Stop loss</span><b>${money(x.stopLoss)}</b></div>
                        <div class="sx-level tg"><span>Target 1</span><b>${money(x.target1)}</b></div>
                        <div class="sx-level"><span>Risk/Reward</span><b>1:${num(x.riskReward)}</b></div>
                    </div>
                    <div class="sx-m-note">Signal levels from the 15-min ATR. The bot sizes its own SL / target from the daily ATR when it buys.</div>
                </div>` : "";

            const conds = x.conditions.map(c => {
                const state = !c.isChecked ? "sx-na" : c.isMet ? "sx-pass" : "sx-fail";
                const mark = !c.isChecked ? "–" : c.isMet ? "✓" : "✗";
                return `
                    <li class="sx-cond ${state}">
                        <span class="sx-cond-mark">${mark}</span>
                        <span class="sx-cond-name">${esc(c.name)}${c.isGate ? `<span class="sx-gate" title="Blocks a BUY on its own">MUST PASS</span>` : ""}<small>${esc(c.detail)}</small></span>
                        <span class="sx-cond-val">${c.isChecked ? esc(c.value) : "Not checked"}</span>
                    </li>`;
            }).join("");

            const met = x.conditions.filter(c => c.isChecked && c.isMet).length;
            const botNote = x.botVerdict === "BUY" && x.signal !== "BUY"
                ? `<div class="sx-m-note">The Signal Dashboard and the bot rate this stock BUY - it is held at WATCH here only because the sector is weak.</div>` : "";
            const skipped = x.conditions.some(c => !c.isChecked)
                ? `<div class="sx-m-note">"Not checked" rules were skipped because a must-pass filter failed first.</div>` : "";

            el("sxStockBody").innerHTML = `
                <p class="sx-m-summary ${sig.cls}">${esc(x.summary)}</p>
                <div class="sx-m-grid">
                    <div>
                        <div class="sx-m-box">
                            <h4>At a glance</h4>
                            <div class="sx-pillars">${pillars}</div>
                        </div>
                        ${levels}
                        <div class="sx-m-box" style="margin-top:14px;">
                            <h4>Score</h4>
                            <div class="sx-pillars">
                                <div class="sx-pillar ${x.score >= data.buyThreshold ? "sx-pass" : x.score >= data.watchThreshold ? "sx-watch" : "sx-fail"}"><span>Engine score</span><b>${x.score}/100</b></div>
                                <div class="sx-pillar sx-na"><span>BUY at / WATCH at</span><b>${data.buyThreshold} / ${data.watchThreshold}</b></div>
                                <div class="sx-pillar ${x.metCount >= data.minConditionsMatch ? "sx-pass" : "sx-fail"}"><span>Engine conditions met</span><b>${x.metCount}/${x.totalConditions} (bot needs ${data.minConditionsMatch})</b></div>
                            </div>
                        </div>
                    </div>
                    <div class="sx-m-box">
                        <h4>Conditions</h4>
                        <ul class="sx-conds">${conds}</ul>
                        <div class="sx-m-result ${sig.cls}">${met} of ${x.conditions.length} passed · Result: <b>${sig.label}</b></div>
                        ${botNote}
                        ${skipped}
                    </div>
                </div>`;
        }

        function onPick(e) {
            const row = e.target.closest("[data-symbol]");
            if (row) openStock(row.dataset.symbol);
        }
        el("sxCands").addEventListener("click", onPick);
        el("sxCands").addEventListener("keydown", e => { if (e.key === "Enter") onPick(e); });
        el("sxStocks").addEventListener("click", onPick);

        bindPills("sxCandSort", d => { candSort = d.sort; if (data) renderCandidates(); });
        startPolling(load);
    }

    document.addEventListener("DOMContentLoaded", () => {
        if (el("sectorOverview")) initOverview();
        const detail = el("sectorDetail");
        if (detail) initDetail(detail);
    });
})();
