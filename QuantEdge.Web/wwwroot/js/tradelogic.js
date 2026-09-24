/**
 * Real Trade Flow (admin) - renders flow diagrams of the Auto Real Trade background jobs.
 *
 * Each diagram is plain data (nodes on a column/row grid + edges between node sides), drawn as SVG
 * by renderDiagram(). Values shown in the boxes come from the server-rendered rules snapshot
 * (TradeLogicRulesDto in #tradelogic-rules), so they always match what the workers run with.
 * When the backend logic changes, update the matching diagram definition below.
 */
(function () {
    "use strict";

    const rulesEl = document.getElementById("tradelogic-rules");
    const svg = document.getElementById("tlSvg");
    if (!rulesEl || !svg || !window.QeFlow) return;

    const R = JSON.parse(rulesEl.textContent || "{}");

    // ------------------------------------------------------------------------------------------
    // Formatting helpers
    // ------------------------------------------------------------------------------------------
    const num = v => Number(v).toLocaleString("en-IN", { maximumFractionDigits: 2 });
    const money = v => "₹" + num(v);
    const fracPct = f => num(Number(f) * 100) + "%";
    const addMinutes = (hhmm, mins) => {
        const [h, m] = String(hhmm).split(":").map(Number);
        const t = (h * 60 + m + Number(mins || 0)) % 1440;
        return String(Math.floor(t / 60)).padStart(2, "0") + ":" + String(t % 60).padStart(2, "0");
    };
    const esc = s => String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");

    const window_ = `${R.tradingWindowStart} – ${R.tradingWindowEnd}`;
    const firstEntry = addMinutes(R.tradingWindowStart, R.entryDelayMinutes);
    const emergencyMult = Number(R.stopLossAtrMult) + Number(R.emergencyExtraAtrMult);
    const buffer = fracPct(R.marketProtectionBufferPct);
    const gapBuffer = fracPct(R.gapExitProtectionBufferPct);
    const lossLimit = money(R.effectiveDailyLossLimit) + (R.isDailyLossLimitDefault ? " (10% of capital)" : "");
    const staleAfterSec = R.restFallbackMissThreshold * R.monitorIntervalSeconds;
    const CHECKLIST_TOTAL = 11; // SwingDecisionEngine checklist: market filter + 2 hard filters + 8 factors

    // ------------------------------------------------------------------------------------------
    // Diagram building blocks - shared renderer in flow-diagram.js
    // ------------------------------------------------------------------------------------------
    const Flow = window.QeFlow;
    // N(id, col, row, kind, title, sub, info, value, src, overrides) / E(from, to, fromSide, toSide, label, options)
    const N = Flow.node, E = Flow.edge, KIND = Flow.KIND;

    // ------------------------------------------------------------------------------------------
    // 1. Big picture
    // ------------------------------------------------------------------------------------------
    const bigPicture = {
        name: "Big picture",
        title: "How the background jobs fit together",
        runs: "QuantEdge.Worker · several jobs",
        desc: `Separate jobs keep prices and the Zerodha login up to date. Two trading workers read that data: one looks for buys every ${R.scanIntervalMinutes} minutes, the other watches open positions every ${R.monitorIntervalSeconds} seconds.`,
        nodes: [
            N("kite", 0, 1, "ext", "Zerodha Kite", "broker API · WebSocket", "The broker. It supplies the daily login, live price ticks and historical candles, and it receives the buy and sell orders."),
            N("token", 1, 0, "proc", "Token Worker", "daily login · 06:00", "Logs in to Zerodha every morning between 06:00 and 08:30 IST. Without today's token no trade is placed.", "06:00 – 08:30", "ActiveZerodhaTokenWorker"),
            N("feed", 1, 1, "proc", "Market Data Feed", "live ticks → candles", "Streams live ticks from the Kite WebSocket, builds 1m to 1d candles and keeps the latest price of each stock in memory.", "WebSocket", "MarketDataProcessor"),
            N("sync", 1, 2, "proc", "Historical Sync", "fills candle gaps", "Downloads missing historical candles so the indicators always have enough history.", "", "HistoricalDataSyncWorker"),
            N("store", 2, 1, "info", "DB + RAM cache", "candles · live prices", "PostgreSQL holds candles, orders, positions and the audit log. A RAM cache holds settings, open positions and live prices for speed.", "", "RealTradeCacheService"),
            N("scan", 3, 0, "start", "Signal Scan", `every ${R.scanIntervalMinutes} min · buys`, "Scores every stock and places a BUY order when one qualifies and passes all risk guards. See the Signal scan tab.", `${R.scanIntervalMinutes} min`, "AutoRealTradeSignalScanWorker"),
            N("mon", 3, 2, "start", "Position Monitor", `every ${R.monitorIntervalSeconds} s · sells`, "Checks every open position against the exit rules and sells when one is hit. See the Position monitor tab.", `${R.monitorIntervalSeconds} s`, "AutoRealPositionMonitorWorker"),
            N("orders", 4, 1, "ext", "Zerodha Orders", "LIMIT buy / sell", `Orders are always LIMIT orders with a ${buffer} price buffer, so the fill price is protected.`, `±${buffer}`, "ZerodhaKiteBrokerService"),
            N("dash", 4, 3, "info", "Live Dashboard", "SignalR alerts + audit", "Each fill, skip and alert is saved to the audit log and pushed live to the Auto Real Trade page.", "SignalR", "/hubs/marketdata")
        ],
        edges: [
            E("kite", "token", "r", "l", "login"), E("kite", "feed", "r", "l"), E("kite", "sync", "r", "l", "history"),
            E("token", "store", "r", "t", "token"), E("feed", "store", "r", "l", "prices"), E("sync", "store", "r", "b", "candles"),
            E("store", "scan", "r", "l"), E("store", "mon", "r", "l"),
            E("scan", "orders", "r", "t", "BUY order"), E("mon", "orders", "r", "b", "SELL order", { to: -25 }),
            E("orders", "dash", "b", "t", "fill", { fo: 25, to: 25 }), E("mon", "dash", "b", "l", "alerts")
        ],
        scenarios: [
            { name: "Morning start", path: ["kite", "token", "store"] },
            { name: "A buy happens", path: ["kite", "feed", "store", "scan", "orders", "dash"] },
            { name: "A sell happens", path: ["kite", "feed", "store", "mon", "orders", "dash"] }
        ]
    };

    // ------------------------------------------------------------------------------------------
    // 2. Signal scan
    // ------------------------------------------------------------------------------------------
    const signalScan = {
        name: "Signal scan",
        title: "Finding a stock to buy",
        runs: `AutoRealTradeSignalScanWorker · every ${R.scanIntervalMinutes} min`,
        desc: `Every ${R.scanIntervalMinutes} minutes during market hours, each active stock is scored. Only a strong signal that also passes all 13 risk guards becomes a real order.`,
        nodes: [
            N("wake", 0, 0, "start", "Timer fires", `every ${R.scanIntervalMinutes} min`, `The worker wakes every ${R.scanIntervalMinutes} minutes, counted from when the process started.`, `${R.scanIntervalMinutes} min`, "AutoRealTradeSignalScanWorker"),
            N("mkt", 1, 0, "dec", "Market\nopen?", "", "Checks NSE hours, 09:15 to 15:30 IST on weekdays, and skips holidays from the holiday table.", "09:15 – 15:30", "MarketHoursService"),
            N("sleep", 1, 1, "end", "Sleep", `try again in ${R.scanIntervalMinutes} min`, "Outside market hours the worker does nothing and waits for the next cycle."),
            N("users", 2, 0, "proc", "Load active users", "bot switch ON", "Loads every user who has Auto Real Trade switched on, with their own settings."),
            N("stock", 3, 0, "proc", "Next stock", "1d · 15m · 60m candles", `Loads the last ${R.candleHistoryCount} candles on three timeframes plus NIFTY 50. Stocks with fewer than ${R.minDailyCandles} daily candles are skipped.`, `${R.candleHistoryCount} candles`),
            N("hard", 3, 1, "dec", "Hard filters\npass?", "", "Two must-pass rules on the daily chart: uptrend (Close > EMA20 > EMA50, both rising) and trend strength ADX ≥ 20.", "ADX ≥ 20", "SwingDecisionEngine"),
            N("reject", 4, 1, "bad", "REJECT", "score = 0", "The stock fails a hard filter, so it gets no score and is not traded in this scan."),
            N("score", 3, 2, "proc", "Score 8 factors", "max 100 pts", `Breakout 20, volume 15, relative strength 15, 60-min trend 15, RSI 10, MACD 10, candle 8, risk:reward 7. A weak NIFTY subtracts ${R.marketContextScorePenalty}.`, "100 pts", "SwingDecisionEngine"),
            N("dec", 2, 2, "dec", `BUY ≥ ${R.buyScoreThreshold} or\n≥ ${R.minConditionsMatch}/${CHECKLIST_TOTAL} met?`, "", `The stock goes forward if its score is ${R.buyScoreThreshold} or more (BUY), or if it meets at least ${R.minConditionsMatch} of the ${CHECKLIST_TOTAL} checklist items.`, `${R.buyScoreThreshold} · ${R.minConditionsMatch}/${CHECKLIST_TOTAL}`),
            N("nosig", 2, 3, "end", "No trade", "WATCH / NO SIGNAL", `A score from ${R.watchScoreThreshold} to ${R.buyScoreThreshold - 1} is WATCH, and under ${R.watchScoreThreshold} is NO SIGNAL. Nothing is bought.`),
            N("guards", 1, 2, "warn", "Risk guards", "13 checks in order", "Safety checks such as the trading window, daily trade cap, loss limit, open-position cap, margin and price drift. See the Risk guards tab.", "13 checks"),
            N("skip", 1, 3, "bad", "Skipped", "logged with reason", "The first failing guard stops the trade. It is written to the audit log as REAL_SIGNAL_SKIPPED."),
            N("order", 0, 2, "ok", "Place LIMIT BUY", `qty = ${money(R.fixedAmountPerTrade)} ÷ price`, `Quantity = ${money(R.fixedAmountPerTrade)} ÷ live price (rounded down). The order goes to Zerodha as a LIMIT at price + ${buffer}.`, `${money(R.fixedAmountPerTrade)} / trade`),
            N("life", 0, 3, "info", "Order lifecycle", "see next tabs", "What happens after the order is sent: filled, resting or rejected. See the Order lifecycle tab.")
        ],
        edges: [
            E("wake", "mkt", "r", "l"), E("mkt", "users", "r", "l", "yes"), E("mkt", "sleep", "b", "t", "no"), E("users", "stock", "r", "l"),
            E("stock", "hard", "b", "t"), E("hard", "reject", "r", "l", "no"), E("hard", "score", "b", "t", "yes"), E("score", "dec", "l", "r"),
            E("dec", "nosig", "b", "t", "no"), E("dec", "guards", "l", "r", "yes"), E("guards", "skip", "b", "t", "fail"), E("guards", "order", "l", "r", "pass"),
            E("order", "life", "b", "t")
        ],
        scenarios: [
            { name: "Stock gets bought", path: ["wake", "mkt", "users", "stock", "hard", "score", "dec", "guards", "order", "life"] },
            { name: "Weak trend", path: ["wake", "mkt", "users", "stock", "hard", "reject"] },
            { name: "Blocked by a guard", path: ["wake", "mkt", "users", "stock", "hard", "score", "dec", "guards", "skip"] },
            { name: "Market closed", path: ["wake", "mkt", "sleep"] }
        ]
    };

    // ------------------------------------------------------------------------------------------
    // 3. Risk guards (vertical ladder, free-form layout)
    // ------------------------------------------------------------------------------------------
    const GUARDS = [
        ["Bot switched on?", "Auto Real Trade master switch", "If the switch is off, the bot stops without logging anything."],
        ["Zerodha login valid?", "token created today after 06:00", "An old or missing token means no order can be placed."],
        ["Inside trading window?", `${window_}, Mon–Fri`, "TradingWindowStart to TradingWindowEnd from the settings."],
        ["Opening delay passed?", `no entries before ${firstEntry}`, `EntryDelayMinutes (${R.entryDelayMinutes}) avoids the volatile opening minutes. Exits are never delayed.`],
        ["Signal strong enough?", `BUY, or ≥ ${R.minConditionsMatch} of ${CHECKLIST_TOTAL} conditions`, `A BUY signal always passes. Otherwise at least ${R.minConditionsMatch} checklist items must be met.`],
        ["Under daily trade cap?", `fewer than ${R.maxTradesPerDay} filled BUYs today`, "MaxTradesPerDay counts only filled BUY orders."],
        ["Under daily loss limit?", `loss < ${lossLimit}`, "If today's loss reaches the limit, the bot also switches itself OFF and logs CIRCUIT_BREAKER."],
        ["Under portfolio cap?", `fewer than ${R.maxConcurrentPositions} open positions`, `A fixed limit of ${R.maxConcurrentPositions} positions open at the same time.`],
        ["Not already holding it?", "no open position in this stock", "The bot never buys the same stock twice."],
        ["No pending BUY?", "no resting BUY for this stock", "Stops a second BUY while the first one is still waiting at the broker."],
        ["Enough margin?", `available margin ≥ ${money(R.fixedAmountPerTrade)}`, "Live Zerodha margin, or AvailableCapital if the margin can't be read."],
        ["Price still close?", `within ±${num(R.maxSignalDriftPct)}% of the scan price`, `The live price is fetched again. Below −${num(R.maxSignalDriftPct)}% the signal is stale; above +${num(R.maxSignalDriftPct)}% it's too late to chase.`],
        ["At least one share?", `floor(${money(R.fixedAmountPerTrade)} ÷ price) ≥ 1`, "If one share costs more than the trade amount, the trade is skipped."]
    ];
    const G_TOP = 90, G_PITCH = 58, G_H = 40, SINK_H = (GUARDS.length - 1) * G_PITCH + G_H;
    const guardNodes = [N("cand", 0, 0, "start", "Candidate from scan", "passed the signal engine", "A stock that passed the Signal scan and now has to clear every guard in this order.", "", "AutoRealTradeService", { x: 300, y: 20, w: 300, h: 44 })];
    const guardEdges = [E("cand", "g0", "b", "t")];
    GUARDS.forEach((g, i) => {
        guardNodes.push(N("g" + i, 0, 0, "dec", `${i + 1} · ${g[0]}`, g[1], g[2], "", "", { x: 300, y: G_TOP + i * G_PITCH, w: 300, h: G_H }));
        guardEdges.push(E("g" + i, i < GUARDS.length - 1 ? "g" + (i + 1) : "buy", "b", "t", i === 0 ? "pass" : ""));
        const guardCy = G_TOP + i * G_PITCH + G_H / 2, sinkCy = G_TOP + SINK_H / 2;
        const label = i === 0 ? "not logged" : i === 1 ? "fail" : i === 6 ? "bot OFF" : i === 9 ? "log only" : "";
        guardEdges.push(E("g" + i, "sink", "r", "l", label, { to: guardCy - sinkCy, c: "r" }));
    });
    guardNodes.push(N("buy", 0, 0, "ok", "Place LIMIT BUY", "all 13 guards passed", "Every check passed. The order goes to Zerodha (see the Order lifecycle tab).", "", "", { x: 300, y: G_TOP + GUARDS.length * G_PITCH + 8, w: 300, h: 44 }));
    guardNodes.push(N("sink", 0, 0, "bad", "Trade skipped", "written to the audit log\nas REAL_SIGNAL_SKIPPED\nwith the reason", `The first failing guard stops the trade. The reason is logged, and the stock is looked at again in the next ${R.scanIntervalMinutes}-minute scan.`, "", "real_trade_execution_logs", { x: 700, y: G_TOP, w: 260, h: SINK_H }));

    const riskGuards = {
        name: "Risk guards",
        title: "13 safety checks before any BUY",
        runs: "AutoRealTradeService · per candidate",
        desc: "The checks run top to bottom. The first one that fails stops the trade, so a stock only reaches Zerodha after passing all 13.",
        nodes: guardNodes,
        edges: guardEdges,
        groups: [[0, 3, "Session & timing"], [4, 4, "Signal strength"], [5, 7, "Daily risk limits"], [8, 9, "No duplicates"], [10, 12, "Money & price"]],
        stepMs: 380,
        scenarios: [
            { name: "All guards pass", path: ["cand"].concat(GUARDS.map((g, i) => "g" + i), ["buy"]) },
            { name: "Loss limit hit", path: ["cand", "g0", "g1", "g2", "g3", "g4", "g5", "g6", "sink"] },
            { name: "Price ran away", path: ["cand"].concat(GUARDS.slice(0, 12).map((g, i) => "g" + i), ["sink"]) }
        ]
    };

    // ------------------------------------------------------------------------------------------
    // 4. Order lifecycle
    // ------------------------------------------------------------------------------------------
    const orderLifecycle = {
        name: "Order lifecycle",
        title: "From accepted signal to closed position",
        runs: "Scan worker places · Monitor worker reconciles",
        desc: `An order can fill at once, wait at the broker, or be rejected. Waiting orders are checked with Zerodha again every ${R.monitorIntervalSeconds} seconds until they settle.`,
        nodes: [
            N("sig", 0, 1, "start", "Signal accepted", "all guards passed", "A stock passed the signal engine and all 13 risk guards."),
            N("lim", 1, 1, "proc", "LIMIT order sent", `price +${buffer} buffer`, `Sent to Zerodha as a LIMIT order at live price + ${buffer}, rounded to the ₹0.05 tick. Product is ${R.productType}.`, `+${buffer}`, "ZerodhaKiteBrokerService.PlaceLiveOrderAsync"),
            N("filled", 2, 0, "ok", "Filled", "broker: COMPLETE", "Zerodha filled the order. Stop loss and target are recalculated from the real fill price."),
            N("open", 2, 1, "warn", "Open (resting)", "waiting at the broker", "The order is on Zerodha's book but not filled yet. No position exists yet."),
            N("rej", 2, 2, "bad", "Rejected", "logged, no position", "Zerodha rejected or cancelled the order. It is logged as ORDER_REJECTED and nothing is held."),
            N("pos", 3, 0, "info", "Position OPEN", "SL · target saved", `The position is tracked in memory and in the database. The monitor checks it every ${R.monitorIntervalSeconds} seconds.`, "", "real_positions"),
            N("sell", 4, 0, "proc", "SELL order", `LTP −${buffer} (gap −${gapBuffer})`, `An exit rule was hit. A LIMIT sell is sent at live price − ${buffer}, or − ${gapBuffer} after a big gap down.`, `−${buffer} / −${gapBuffer}`, "ExecuteRealSellOrderCoreAsync"),
            N("closed", 4, 1, "end", "Position CLOSED", "P&L + exit reason", "The SELL filled. P&L = (fill − entry) × qty is saved with the exit reason, and an alert goes to the dashboard.", "", "real_trade_history")
        ],
        edges: [
            E("sig", "lim", "r", "l"), E("lim", "filled", "t", "l", "COMPLETE"), E("lim", "open", "r", "l", "OPEN"), E("lim", "rej", "b", "l", "REJECTED"),
            E("open", "filled", "t", "b", `reconcile · ${R.monitorIntervalSeconds} s`), E("open", "rej", "b", "t", "broker rejects"),
            E("filled", "pos", "r", "l", "fill"), E("pos", "sell", "r", "l", "exit", { fo: -10, to: -10 }),
            E("sell", "pos", "l", "r", "retry", { fo: 10, to: 10, lp: "b" }), E("sell", "closed", "b", "t", "SELL fill")
        ],
        scenarios: [
            { name: "Fills at once", path: ["sig", "lim", "filled", "pos", "sell", "closed"] },
            { name: "Rests, then fills", path: ["sig", "lim", "open", "filled", "pos"] },
            { name: "Broker rejects", path: ["sig", "lim", "rej"] }
        ]
    };

    // ------------------------------------------------------------------------------------------
    // 5. Position monitor
    // ------------------------------------------------------------------------------------------
    const positionMonitor = {
        name: "Position monitor",
        title: `Watching open positions every ${R.monitorIntervalSeconds} seconds`,
        runs: `AutoRealPositionMonitorWorker · every ${R.monitorIntervalSeconds} s`,
        desc: "The monitor first settles any waiting orders, then checks each open position with a fresh price. If the live feed is quiet, it falls back to a REST price quote.",
        nodes: [
            N("wake", 2, 0, "start", "Timer fires", `every ${R.monitorIntervalSeconds} s`, `The monitor wakes every ${R.monitorIntervalSeconds} seconds.`, `${R.monitorIntervalSeconds} s`, "AutoRealPositionMonitorWorker"),
            N("mkt", 2, 1, "dec", "Market\nopen?", "", "Only runs during market hours."),
            N("sleep", 1, 1, "end", "Release cache", "sleep till market open", "Outside market hours the RAM cache is released and the worker waits."),
            N("rec", 2, 2, "proc", "Reconcile orders", "settle waiting orders", "Asks Zerodha about every order still resting. Filled BUYs become positions and filled SELLs close them.", "", "ReconcilePendingRealOrdersAsync"),
            N("pos", 2, 3, "proc", "Next open position", "from RAM cache", "Takes each open position in turn. If the stock dropped off the live feed, it is re-subscribed (WS_RESUBSCRIBED)."),
            N("fresh", 2, 4, "dec", `Live tick\n< ${R.ltpFreshnessSeconds} s old?`, "", `Uses the WebSocket price only if it is less than ${R.ltpFreshnessSeconds} seconds old.`, `${R.ltpFreshnessSeconds} s`),
            N("miss", 3, 4, "dec", `Missed\n≥ ${R.restFallbackMissThreshold} times?`, "", "Counts how many cycles in a row had no fresh price.", `${R.restFallbackMissThreshold} misses`),
            N("wait", 4, 4, "warn", "Skip this cycle", "logs LTP_UNAVAILABLE", `No usable price yet. The position is checked again in ${R.monitorIntervalSeconds} seconds.`),
            N("rest", 3, 5, "info", "REST price quote", "one batch call per user", `After about ${staleAfterSec} seconds without ticks, one batched REST quote call fetches prices for all stale stocks (LTP_REST_FALLBACK).`, `~${staleAfterSec} s`),
            N("eval", 2, 5, "proc", "Check exit rules", "target · stops · days", "Runs the exit rules for this position. See the Exit decision tab.", "", "EvaluateAndExecuteRealSellAsync"),
            N("hit", 2, 6, "dec", "Exit rule\nhit?", "", "If any rule says sell, the position is sold. Otherwise it is held."),
            N("hold", 3, 6, "end", "Hold", "trail may move up", "No exit this time. The trailing stop may move up, and the next position is checked."),
            N("sellN", 2, 7, "bad", "Place SELL", "see Order lifecycle", "A LIMIT sell is sent to Zerodha, and the position closes when it fills.")
        ],
        edges: [
            E("wake", "mkt", "b", "t"), E("mkt", "sleep", "l", "r", "no"), E("mkt", "rec", "b", "t", "yes"), E("rec", "pos", "b", "t"), E("pos", "fresh", "b", "t"),
            E("fresh", "miss", "r", "l", "no"), E("fresh", "eval", "b", "t", "yes"), E("miss", "wait", "r", "l", "no"), E("miss", "rest", "b", "t", "yes"),
            E("rest", "eval", "l", "r", "price"), E("eval", "hit", "b", "t"), E("hit", "hold", "r", "l", "no"), E("hit", "sellN", "b", "t", "yes")
        ],
        scenarios: [
            { name: "Normal check", path: ["wake", "mkt", "rec", "pos", "fresh", "eval", "hit", "hold"] },
            { name: "Live feed quiet", path: ["wake", "mkt", "rec", "pos", "fresh", "miss", "rest", "eval", "hit", "sellN"] },
            { name: "Market closed", path: ["wake", "mkt", "sleep"] }
        ]
    };

    // ------------------------------------------------------------------------------------------
    // 6. Exit decision - mirrors SwingTradeRules.EvaluateExit for the user's ExitMode
    // ------------------------------------------------------------------------------------------
    const exitSwing = {
        name: "Exit decision",
        title: "When a position is sold (Swing close mode)",
        runs: "SwingTradeRules.EvaluateExit · each price",
        desc: `Target and emergency stop are checked all day. Stop loss, trailing stop and holding time are checked only in the closing window from ${R.closeCheckTime}, so normal intraday dips don't sell the stock.`,
        nodes: [
            N("ltp", 0, 1, "start", "New live price", "for one position", "Called by the monitor with the latest price of one open position."),
            N("tgt", 1, 1, "dec", "LTP ≥\ntarget?", "", `Target = entry + ${num(R.targetAtrMult)} × daily ATR. Checked live, any time of day.`, `${num(R.targetAtrMult)} × ATR`),
            N("tgtS", 1, 0, "ok", "SELL · Target", `entry + ${num(R.targetAtrMult)} × ATR`, `Profit target reached. The reason is marked "(Gap)" if the price is already ${num(R.gapBreachThresholdPct)}% past it.`),
            N("em", 2, 1, "dec", "LTP ≤\nemergency stop?", "", `Emergency stop = entry − ${num(emergencyMult)} × ATR, never more than ${num(R.maxEmergencyStopPct)}% below entry. Checked live, any time.`, `${num(emergencyMult)} × ATR`),
            N("emS", 2, 0, "bad", "SELL · Emergency", `entry − ${num(emergencyMult)} × ATR`, `A sharp fall. After a gap of ${num(R.gapBreachThresholdPct)}% or more, the sell uses the wider ${gapBuffer} price buffer so it fills.`),
            N("win", 3, 1, "dec", `Closing window\n(≥ ${R.closeCheckTime})?`, "", `Before ${R.closeCheckTime} only target and emergency stop can sell. The remaining rules wait for the close.`, R.closeCheckTime),
            N("holdA", 3, 0, "warn", "HOLD", `stops wait for ${R.closeCheckTime}`, "The price may be below the stop loss, but the bot waits for the closing price before deciding."),
            N("tsl", 3, 2, "dec", "LTP ≤\ntrailing SL?", "", "Only once the trailing stop is at or above the entry price."),
            N("tslS", 4, 2, "ok", "SELL · Trailing SL", "profit locked in", "The price fell back to the trailing stop, which locks in profit."),
            N("sl", 3, 3, "dec", "LTP ≤\nstop loss?", "", `Stop loss = entry − ${num(R.stopLossAtrMult)} × ATR, never more than ${num(R.maxStopLossPct)}% below entry.`, `${num(R.stopLossAtrMult)} × ATR`),
            N("slS", 4, 3, "bad", "SELL · Stop Loss", "closing basis", "The stock closed below its stop loss."),
            N("days", 3, 4, "dec", `Held ≥ ${R.maxDurationDays}\ntrading days?`, "", "Counts trading days only, skipping weekends and holidays.", `${R.maxDurationDays} days`),
            N("daysS", 4, 4, "warn", "SELL · Max Days", "time limit reached", "The position has been held for MaxDurationDays."),
            N("trail", 2, 4, "info", "Raise trailing SL", `from +${num(R.trailActivationAtrMult)} ATR · ≥ entry`, `Starts once the price is ${num(R.trailActivationAtrMult)} ATR above entry, trails ${num(R.trailAtrMult)} ATR below the price, never drops below entry, only moves up, and never on the entry day.`, `${num(R.trailAtrMult)} × ATR`),
            N("holdB", 1, 4, "end", "HOLD", `check again in ${R.monitorIntervalSeconds} s`, `Nothing to do. The monitor checks again in ${R.monitorIntervalSeconds} seconds.`)
        ],
        edges: [
            E("ltp", "tgt", "r", "l"), E("tgt", "tgtS", "t", "b", "yes"), E("tgt", "em", "r", "l", "no"), E("em", "emS", "t", "b", "yes"), E("em", "win", "r", "l", "no"),
            E("win", "holdA", "t", "b", "no"), E("win", "tsl", "b", "t", "yes"), E("tsl", "tslS", "r", "l", "yes"), E("tsl", "sl", "b", "t", "no"),
            E("sl", "slS", "r", "l", "yes"), E("sl", "days", "b", "t", "no"), E("days", "daysS", "r", "l", "yes"), E("days", "trail", "l", "r", "no"), E("trail", "holdB", "l", "r")
        ],
        scenarios: [
            { name: "Target hit", path: ["ltp", "tgt", "tgtS"] },
            { name: "Dip during the day", path: ["ltp", "tgt", "em", "win", "holdA"] },
            { name: "Closes below stop", path: ["ltp", "tgt", "em", "win", "tsl", "sl", "slS"] },
            { name: "Normal day, trail rises", path: ["ltp", "tgt", "em", "win", "tsl", "sl", "days", "trail", "holdB"] }
        ]
    };

    const exitIntraday = {
        name: "Exit decision",
        title: "When a position is sold (Intraday mode)",
        runs: "SwingTradeRules.EvaluateExit · each price",
        desc: `In Intraday mode every rule is checked live on every price, and the trailing stop follows ${num(R.defaultTrailingSlPct)}% below the price.`,
        nodes: [
            N("ltp", 0, 1, "start", "New live price", "for one position", "Called by the monitor with the latest price of one open position."),
            N("tgt", 1, 1, "dec", "LTP ≥\ntarget?", "", "Target = the signal engine's Target 1 (2 × risk on the 15-min ATR)."),
            N("tgtS", 1, 0, "ok", "SELL · Target", "engine target (2R)", "Profit target reached."),
            N("tsl", 2, 1, "dec", "LTP ≤\ntrailing SL?", "", `Trailing stop = price − ${num(R.defaultTrailingSlPct)}%, raised on every cycle.`, `${num(R.defaultTrailingSlPct)}%`),
            N("tslS", 2, 0, "ok", "SELL · Trailing SL", `${num(R.defaultTrailingSlPct)}% below the peak`, "The price fell back from its high to the trailing stop."),
            N("sl", 3, 1, "dec", "LTP ≤\nstop loss?", "", "Stop loss = entry − 1.5 × the 15-min ATR."),
            N("slS", 3, 0, "bad", "SELL · Stop Loss", "live", "The price is at or below the stop loss."),
            N("days", 4, 1, "dec", `Held ≥ ${R.maxDurationDays}\ntrading days?`, "", "Counts trading days only, skipping weekends and holidays.", `${R.maxDurationDays} days`),
            N("daysS", 4, 0, "warn", "SELL · Max Days", "time limit reached", "The position has been held for MaxDurationDays."),
            N("hold", 4, 2, "end", "HOLD", "trail moves up", `Nothing to do. The trailing stop is raised and the monitor checks again in ${R.monitorIntervalSeconds} seconds.`)
        ],
        edges: [
            E("ltp", "tgt", "r", "l"), E("tgt", "tgtS", "t", "b", "yes"), E("tgt", "tsl", "r", "l", "no"), E("tsl", "tslS", "t", "b", "yes"), E("tsl", "sl", "r", "l", "no"),
            E("sl", "slS", "t", "b", "yes"), E("sl", "days", "r", "l", "no"), E("days", "daysS", "t", "b", "yes"), E("days", "hold", "b", "t", "no")
        ],
        scenarios: [
            { name: "Target hit", path: ["ltp", "tgt", "tgtS"] },
            { name: "Falls back from high", path: ["ltp", "tgt", "tsl", "tslS"] },
            { name: "Nothing hit", path: ["ltp", "tgt", "tsl", "sl", "days", "hold"] }
        ]
    };

    const isIntraday = String(R.exitMode).toUpperCase() === "INTRADAY";
    const DIAGRAMS = [bigPicture, signalScan, riskGuards, orderLifecycle, positionMonitor, isIntraday ? exitIntraday : exitSwing];

    // ------------------------------------------------------------------------------------------
    // Risk-guard group brackets (drawn beneath the diagram)
    // ------------------------------------------------------------------------------------------
    function groupsSvg(groups) {
        return (groups || []).map(([a, b, name]) => {
            const y1 = G_TOP + a * G_PITCH + 4, y2 = G_TOP + b * G_PITCH + G_H - 4;
            return `<g class="deco"><path d="M282,${y1}H276V${y2}H282" fill="none" stroke="#34425f" stroke-width="1.5"/>` +
                `<text x="266" y="${(y1 + y2) / 2 + 4}" text-anchor="end" font-size="11.5" font-weight="600" fill="#94a3b8">${esc(name)}</text></g>`;
        }).join("");
    }

    // ------------------------------------------------------------------------------------------
    // Page wiring: tabs, node details, scenario playback
    // ------------------------------------------------------------------------------------------
    const tabsEl = document.getElementById("tlTabs");
    const playEl = document.getElementById("tlPlay");
    const detailEl = document.getElementById("tlDetail");
    const reduceMotion = window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches;

    let current = 0, nodeMap = {}, timer = null;

    function stopPlayback() {
        if (timer) { clearTimeout(timer); timer = null; }
    }

    function clearHighlights() {
        svg.classList.remove("dim");
        svg.querySelectorAll(".on").forEach(el => el.classList.remove("on"));
        svg.querySelectorAll(".e path").forEach(p => p.setAttribute("marker-end", `url(#${p.dataset.mk})`));
        playEl.querySelectorAll("[data-s]").forEach(b => b.setAttribute("aria-pressed", "false"));
        const clear = document.getElementById("tlClear");
        if (clear) clear.hidden = true;
    }

    function showIdleDetail() {
        detailEl.innerHTML = `<div class="tl-detail-icon" style="border-color:#34425f;color:#94a3b8">i</div>` +
            `<div><h4>Click any box</h4><p>It shows what that step does and the setting behind it. Or play a scenario above to watch one case flow through.</p></div><div></div>`;
    }

    function showNodeDetail(id, stepText) {
        const n = nodeMap[id];
        if (!n) return;
        Flow.markSelected(svg, id);
        const k = KIND[n.kind];
        detailEl.innerHTML =
            `<div class="tl-detail-icon" style="border-color:${k.stroke};color:${k.text};background:${k.fill}">${k.icon}</div>` +
            `<div>${stepText ? `<div class="tl-detail-step">${esc(stepText)}</div>` : ""}<h4>${esc(n.title.replace(/\n/g, " "))}</h4><p>${esc(n.info || n.sub || "")}</p></div>` +
            `<div class="tl-detail-meta">${n.value ? `<span class="tl-detail-value">${esc(n.value)}</span>` : ""}${n.src ? `<span class="tl-detail-src">${esc(n.src)}</span>` : ""}</div>`;
    }

    function playScenario(index) {
        stopPlayback();
        clearHighlights();
        const diagram = DIAGRAMS[current], scenario = diagram.scenarios[index], path = scenario.path;
        playEl.querySelectorAll("[data-s]").forEach(b => b.setAttribute("aria-pressed", String(+b.dataset.s === index)));
        document.getElementById("tlClear").hidden = false;
        svg.classList.add("dim");

        const light = i => {
            const node = svg.querySelector(`.n[data-id="${path[i]}"]`);
            if (node) node.classList.add("on");
            if (i > 0) {
                const edge = svg.querySelector(`.e[data-k="${path[i - 1]}>${path[i]}"]`);
                if (edge) {
                    edge.classList.add("on");
                    edge.querySelector("path").setAttribute("marker-end", `url(#${Flow.MARKER.active})`);
                }
            }
            showNodeDetail(path[i], `${scenario.name} · step ${i + 1} of ${path.length}`);
        };

        if (reduceMotion) { path.forEach((_, i) => light(i)); return; }
        let i = 0;
        const tick = () => {
            light(i++);
            if (i < path.length) timer = setTimeout(tick, diagram.stepMs || 750);
        };
        tick();
    }

    function renderDiagram(index) {
        stopPlayback();
        current = index;
        const d = DIAGRAMS[index];
        nodeMap = Flow.render(svg, d, { extraSvg: groupsSvg(d.groups) });
        svg.setAttribute("aria-label", d.title);

        document.getElementById("tlDiagramTitle").textContent = d.title;
        document.getElementById("tlDiagramDesc").textContent = d.desc;
        document.getElementById("tlDiagramRuns").textContent = "Runs in: " + d.runs;

        playEl.innerHTML = `<span class="tl-play-label">Play a scenario:</span>` +
            d.scenarios.map((s, j) => `<button type="button" class="tl-chip" data-s="${j}" aria-pressed="false"><svg viewBox="0 0 10 10"><path d="M1,0 L10,5 L1,10z"/></svg>${esc(s.name)}</button>`).join("") +
            `<button type="button" class="tl-chip tl-chip-clear" id="tlClear" hidden>Clear</button>`;
        playEl.querySelectorAll("[data-s]").forEach(b => b.addEventListener("click", () => playScenario(+b.dataset.s)));
        document.getElementById("tlClear").addEventListener("click", () => { stopPlayback(); clearHighlights(); });

        Flow.onNodeSelect(svg, id => showNodeDetail(id));

        tabsEl.querySelectorAll(".tl-tab").forEach((t, j) => t.setAttribute("aria-selected", String(j === index)));
        showIdleDetail();
        try { localStorage.setItem("tl-diagram", String(index)); } catch (e) { /* storage unavailable */ }
    }

    tabsEl.innerHTML = DIAGRAMS.map((d, i) =>
        `<button type="button" class="tl-tab" role="tab" aria-selected="false" data-i="${i}"><span class="tl-tab-no">${i + 1}</span>${esc(d.name)}</button>`).join("");
    tabsEl.querySelectorAll(".tl-tab").forEach(t => t.addEventListener("click", () => renderDiagram(+t.dataset.i)));

    let start = 0;
    try {
        const saved = parseInt(localStorage.getItem("tl-diagram"), 10);
        if (saved >= 0 && saved < DIAGRAMS.length) start = saved;
    } catch (e) { /* storage unavailable */ }
    renderDiagram(start);
})();
