// ============================================================================
// Zerodha Live Position Chart (modal)
// Candles for the symbol with the position's levels drawn as labelled price lines:
// Entry (the BUY or SELL average price), Target, Stop Loss, Trailing SL and LTP.
// Rows register their levels via setPositionChartRow(); the Chart button opens the modal.
// ============================================================================

const positionChartRows = new Map();
let positionChart = null;
let positionChartSeries = null;
let positionChartModal = null;
let positionChartSymbol = null;
let positionChartTimeframe = "5m";
let positionChartRequestId = 0;

// Lightweight Charts renders timestamps as UTC - shift so the time axis reads in IST.
const IST_OFFSET_SECONDS = 19800;

const PC_COLORS = {
    entry: "#60a5fa",
    target: "#34d399",
    stopLoss: "#f87171",
    trailing: "#fbbf24",
    ltp: "#e2e8f0"
};

function setPositionChartRow(symbol, levels) {
    positionChartRows.set(String(symbol || "").toUpperCase(), levels);
}

function chartButton(symbol) {
    const safe = String(symbol || "").replace(/[^A-Za-z0-9&._-]/g, "");
    return `<button type="button" class="btn-pos-chart" onclick="openPositionChart('${safe}')" title="Chart ${safe} with entry, target and stop loss levels">📈 Chart</button>`;
}

function pcFmt(v) {
    return (v === null || v === undefined || isNaN(v) || v <= 0) ? "-" : `₹${Number(v).toFixed(2)}`;
}

// Signed % distance of a level from the entry price.
function pcPctFromEntry(level, entry) {
    if (!level || !entry) return "";
    const pct = ((level - entry) / entry) * 100;
    return ` <span class="pc-chip-pct">(${pct >= 0 ? "+" : ""}${pct.toFixed(2)}%)</span>`;
}

window.openPositionChart = function (symbol) {
    const key = String(symbol || "").toUpperCase();
    const row = positionChartRows.get(key);
    if (!row) return;

    positionChartSymbol = key;
    renderPositionChartHeader(row);

    const modalEl = document.getElementById("modalPositionChart");
    if (!modalEl || typeof bootstrap === "undefined") return;
    if (!positionChartModal) {
        positionChartModal = new bootstrap.Modal(modalEl);
        // The container has no width until the modal is visible, so the chart is built after it opens.
        modalEl.addEventListener("shown.bs.modal", () => loadPositionChart());
        modalEl.addEventListener("hidden.bs.modal", () => {
            positionChartRequestId++;
            if (positionChart) {
                positionChart.remove();
                positionChart = null;
                positionChartSeries = null;
            }
        });
        window.addEventListener("resize", resizePositionChart);
        document.querySelectorAll("#pcTimeframes [data-tf]").forEach(btn => {
            btn.addEventListener("click", () => {
                positionChartTimeframe = btn.dataset.tf;
                loadPositionChart();
            });
        });
    }
    positionChartModal.show();
};

function renderPositionChartHeader(row) {
    const isShort = row.side === "SELL";
    document.getElementById("pcTitle").innerHTML =
        `${row.symbol} <span class="badge ${isShort ? "bg-danger" : "bg-success"} ms-2">${isShort ? "SHORT (SELL)" : "LONG (BUY)"}</span>` +
        `<span class="badge bg-secondary ms-2">${row.product || ""}</span>` +
        `<span class="pc-qty ms-2">Qty ${row.quantity}</span>`;

    const entryLabel = isShort ? "Sell Price (Entry)" : "Buy Price (Entry)";
    const chips = [
        { cls: "entry", label: entryLabel, value: pcFmt(row.entry), extra: "" },
        { cls: "ltp", label: "LTP", value: pcFmt(row.ltp), extra: pcPctFromEntry(row.ltp, row.entry) },
        { cls: "target", label: "Target", value: pcFmt(row.target), extra: pcPctFromEntry(row.target, row.entry) },
        { cls: "sl", label: "Stop Loss", value: pcFmt(row.stopLoss), extra: pcPctFromEntry(row.stopLoss, row.entry) },
        { cls: "tsl", label: "Trailing SL", value: pcFmt(row.trailingStopLoss), extra: pcPctFromEntry(row.trailingStopLoss, row.entry) }
    ];
    document.getElementById("pcLevels").innerHTML = chips.map(c =>
        `<div class="pc-chip pc-chip-${c.cls}"><span class="pc-chip-label">${c.label}</span><span class="pc-chip-value">${c.value}${c.value !== "-" ? c.extra : ""}</span></div>`
    ).join("");

    const note = document.getElementById("pcNote");
    note.textContent = row.isMonitored
        ? ""
        : "QuantEdge isn't managing this position, so it has no Target / Stop Loss - only the entry price and LTP are drawn.";
    note.style.display = row.isMonitored ? "none" : "block";
}

async function loadPositionChart() {
    const row = positionChartRows.get(positionChartSymbol);
    const container = document.getElementById("pcChartContainer");
    const status = document.getElementById("pcStatus");
    if (!row || !container) return;

    document.querySelectorAll("#pcTimeframes [data-tf]").forEach(btn =>
        btn.classList.toggle("active", btn.dataset.tf === positionChartTimeframe));

    if (typeof LightweightCharts === "undefined") {
        status.textContent = "Chart library failed to load.";
        status.style.display = "flex";
        return;
    }

    const requestId = ++positionChartRequestId;
    status.textContent = "Loading candles…";
    status.style.display = "flex";

    let candles = [];
    try {
        const url = `${apiBaseUrl}/api/marketdata/chart-data?symbol=${encodeURIComponent(row.symbol)}&timeframe=${positionChartTimeframe}&limit=300`;
        const response = await fetch(url);
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        const data = await response.json();
        candles = (data || [])
            .filter(c => c.open > 0 && c.close > 0)
            .map(c => ({
                time: Math.floor(c.time / 1000) + IST_OFFSET_SECONDS,
                open: c.open, high: c.high, low: c.low, close: c.close
            }));
    } catch (err) {
        console.error("Position chart load error:", err);
    }
    if (requestId !== positionChartRequestId) return; // a newer load (or close) superseded this one

    buildPositionChart(container, row, candles);

    if (candles.length === 0) {
        status.textContent = `No ${positionChartTimeframe} candles available for ${row.symbol}. Levels are still shown on the price scale.`;
        status.style.display = "flex";
    } else {
        status.style.display = "none";
    }
}

function buildPositionChart(container, row, candles) {
    if (positionChart) positionChart.remove();

    positionChart = LightweightCharts.createChart(container, {
        width: container.clientWidth,
        height: container.clientHeight || 440,
        layout: { background: { color: "#0d111e" }, textColor: "#cbd5e1", fontSize: 12 },
        grid: { vertLines: { color: "rgba(255,255,255,0.04)" }, horzLines: { color: "rgba(255,255,255,0.05)" } },
        rightPriceScale: { borderColor: "rgba(255,255,255,0.12)", scaleMargins: { top: 0.08, bottom: 0.08 } },
        timeScale: { borderColor: "rgba(255,255,255,0.12)", timeVisible: positionChartTimeframe !== "1d", secondsVisible: false },
        crosshair: { mode: LightweightCharts.CrosshairMode.Normal }
    });

    const levels = [row.entry, row.target, row.stopLoss, row.trailingStopLoss, row.ltp].filter(v => v > 0);

    positionChartSeries = positionChart.addCandlestickSeries({
        upColor: "#22c55e", downColor: "#ef4444",
        borderUpColor: "#22c55e", borderDownColor: "#ef4444",
        wickUpColor: "#22c55e", wickDownColor: "#ef4444",
        // Price lines don't widen the scale on their own - include every level so none sit off-screen.
        autoscaleInfoProvider: original => {
            const res = original();
            if (!levels.length) return res;
            const lo = Math.min(...levels), hi = Math.max(...levels);
            if (!res || !res.priceRange) return { priceRange: { minValue: lo * 0.995, maxValue: hi * 1.005 } };
            return {
                ...res,
                priceRange: {
                    minValue: Math.min(res.priceRange.minValue, lo),
                    maxValue: Math.max(res.priceRange.maxValue, hi)
                }
            };
        }
    });

    // With no candles the series has nothing to scale on - a flat line at the entry keeps the levels visible.
    if (candles.length) {
        positionChartSeries.setData(candles);
    } else if (row.entry > 0) {
        const now = Math.floor(Date.now() / 1000) + IST_OFFSET_SECONDS;
        positionChartSeries.setData([{ time: now, open: row.entry, high: row.entry, low: row.entry, close: row.entry }]);
    }

    const isShort = row.side === "SELL";
    const solid = LightweightCharts.LineStyle.Solid;
    const dashed = LightweightCharts.LineStyle.Dashed;
    const dotted = LightweightCharts.LineStyle.Dotted;
    const addLine = (price, color, title, lineStyle, lineWidth) => {
        if (!(price > 0)) return;
        positionChartSeries.createPriceLine({ price, color, title, lineStyle, lineWidth, axisLabelVisible: true });
    };

    addLine(row.entry, PC_COLORS.entry, isShort ? "SELL ENTRY" : "BUY ENTRY", solid, 2);
    addLine(row.target, PC_COLORS.target, "TARGET", dashed, 2);
    addLine(row.stopLoss, PC_COLORS.stopLoss, "STOP LOSS", dashed, 2);
    addLine(row.trailingStopLoss, PC_COLORS.trailing, "TRAILING SL", dotted, 2);
    addLine(row.ltp, PC_COLORS.ltp, "LTP", dotted, 1);

    positionChart.timeScale().fitContent();
}

function resizePositionChart() {
    const container = document.getElementById("pcChartContainer");
    if (positionChart && container) positionChart.resize(container.clientWidth, container.clientHeight || 440);
}
