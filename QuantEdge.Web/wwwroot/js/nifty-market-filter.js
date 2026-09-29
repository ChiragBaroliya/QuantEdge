/**
 * NIFTY Market Filter banner (Signal Dashboard).
 *
 * The filter itself (Close > 50 DMA and EMA20 > EMA50 on daily candles) is what the trading bots
 * check before buying anything - it only changes when the daily candles do, so it's polled from
 * /api/swing/nifty-status. The NIFTY 50 price updates continuously:
 *   - live ticks via its own SignalR connection on the "NIFTY 50_1m" group (kept separate from
 *     dashboard.js, whose ReceiveActiveCandle handler assumes every tick is the selected stock), and
 *   - the Zerodha LTP quote returned by the same poll, as a fallback when no ticks arrive.
 */
(function () {
    const NIFTY_SYMBOL = "NIFTY 50";
    const TICK_TIMEFRAME = "1m";
    const POLL_INTERVAL_MS = 15000;
    const LIVE_STALE_MS = 60000; // price counts as "live" if a tick/quote arrived within this window

    let eodClose = null;
    let lastPrice = null;
    let lastLiveAt = 0;

    const fmt = (n) => Number(n).toLocaleString("en-IN", { minimumFractionDigits: 2, maximumFractionDigits: 2 });

    function apiBase() {
        return (window.QuantEdgeConfig?.apiBaseUrl || "").replace(/\/+$/, "");
    }

    function setCondition(selector, cond) {
        $(selector).removeClass("positive negative").addClass(cond ? "positive" : "negative").text(cond ? "Yes" : "No");
    }

    function renderStatus(ns) {
        const hasData = ns && Number(ns.sma50) > 0;
        const badge = $("#niftyBadge").removeClass("passed failed");

        if (!hasData) {
            badge.addClass("failed").text("No Data");
            $("#niftySma50, #niftyEma20, #niftyEma50").text("-");
            setCondition("#niftyCond1", false);
            setCondition("#niftyCond2", false);
            return;
        }

        eodClose = Number(ns.close);
        $("#niftySma50").text(fmt(ns.sma50));
        $("#niftyEma20").text(fmt(ns.ema20));
        $("#niftyEma50").text(fmt(ns.ema50));
        badge.addClass(ns.isMarketFilterPassed ? "passed" : "failed").text(ns.isMarketFilterPassed ? "Passed" : "Failed");
        setCondition("#niftyCond1", ns.isAboveSma50);
        setCondition("#niftyCond2", ns.isEmaBullish);
    }

    function renderPrice(price, isLive) {
        if (!price || isNaN(price) || price <= 0) return;

        const priceEl = $("#niftyPrice");
        if (lastPrice !== null && price !== lastPrice) {
            const cls = price > lastPrice ? "tick-up" : "tick-down";
            priceEl.removeClass("tick-up tick-down").addClass(cls);
            clearTimeout(renderPrice._flash);
            renderPrice._flash = setTimeout(() => priceEl.removeClass("tick-up tick-down"), 700);
        }
        lastPrice = price;
        if (isLive) lastLiveAt = Date.now();
        priceEl.text(fmt(price));

        const changeEl = $("#niftyChange").removeClass("positive negative");
        if (eodClose && eodClose > 0) {
            const diff = price - eodClose;
            const pct = (diff / eodClose) * 100;
            const sign = diff >= 0 ? "+" : "";
            changeEl.addClass(diff >= 0 ? "positive" : "negative").text(`${sign}${fmt(diff)} (${sign}${pct.toFixed(2)}%)`);
        } else {
            changeEl.text("");
        }

        refreshSourceLabel();
    }

    function refreshSourceLabel() {
        const live = Date.now() - lastLiveAt < LIVE_STALE_MS;
        $("#niftyPriceSource").toggleClass("live", live).text(live ? "Live" : "EOD Close");
    }

    async function poll() {
        if (document.hidden) return;
        try {
            const response = await fetch(`${apiBase()}/api/swing/nifty-status`);
            if (!response.ok) throw new Error(`HTTP ${response.status}`);
            const data = await response.json();

            renderStatus(data);
            if (data.liveLtp && Number(data.liveLtp) > 0) {
                // Only let the quote move the price when ticks have gone quiet - ticks are fresher.
                if (Date.now() - lastLiveAt > POLL_INTERVAL_MS) renderPrice(Number(data.liveLtp), true);
            } else if (lastPrice === null && Number(data.close) > 0) {
                renderPrice(Number(data.close), false);
            }
        } catch (ex) {
            console.error("NIFTY market filter refresh failed:", ex);
        }
        refreshSourceLabel();
    }

    function connectTicks() {
        if (typeof signalR === "undefined") return;

        const conn = new signalR.HubConnectionBuilder()
            .withUrl(`${apiBase()}/api/hubs/marketdata`)
            .withAutomaticReconnect()
            .build();

        const subscribe = () => conn.invoke("Subscribe", NIFTY_SYMBOL, TICK_TIMEFRAME)
            .catch(err => console.error("NIFTY tick subscribe failed:", err));

        conn.on("ReceiveActiveCandle", (candle) => {
            if (candle && candle.close) renderPrice(Number(candle.close), true);
        });
        conn.onreconnected(subscribe);

        conn.start()
            .then(subscribe)
            .catch(err => {
                console.error("NIFTY SignalR connection error:", err);
                setTimeout(connectTicks, 5000);
            });
    }

    $(function () {
        if (!document.getElementById("niftyStatusBanner")) return;
        poll();
        setInterval(poll, POLL_INTERVAL_MS);
        document.addEventListener("visibilitychange", () => { if (!document.hidden) poll(); });
        connectTicks();
    });
})();
