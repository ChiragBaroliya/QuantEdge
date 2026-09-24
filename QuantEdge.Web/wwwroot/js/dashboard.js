// QuantEdge Dashboard Real-time Charting & SignalR JavaScript

const API_BASE_URL = window.QuantEdgeConfig?.apiBaseUrl || "";
let connection = null;

let activeSymbol = "";
let activeTimeframe = "15m"; // swing timing timeframe; 1m/5m tabs are chart-only

// Chart definitions
let weeklyPnlChart = null;
let weeklyPnlOffset = 0; // 0 = current week, -1 = previous week, etc.

// Trading Indicators mini-charts (compact cards - see initIndicatorMiniCharts)
let emaMiniChart = null, emaMiniPriceSeries = null, emaMiniEma9Series = null, emaMiniEma20Series = null;
let rsiMiniChart = null, rsiMiniSeries = null;
let macdMiniChart = null, macdMiniLineSeries = null, macdMiniSignalSeries = null, macdMiniHistSeries = null;
let vwapMiniChart = null, vwapMiniPriceSeries = null, vwapMiniVwapSeries = null;
let volumeMiniChart = null, volumeMiniSeries = null;
const MINI_CHART_VISIBLE_CANDLES = 60; // Compact cards only need a short recent window, not full history

// Overall Signal (from the backend strategy engine) + last computed per-indicator signals,
// tracked so the Trading Indicators confirmation summary can be recomputed from either side
// (whichever arrives second: the historical/live-candle indicator recompute, or the backend
// signal evaluation) without the two code paths needing to know about each other.
let lastOverallSignalType = "HOLD";
let lastIndicatorSignals = { ema: "NEUTRAL", rsi: "NEUTRAL", macd: "NEUTRAL", vwap: "NEUTRAL", volume: "NO CONFIRMATION", adx: "NO CONFIRMATION" };

// Keep local cache of data for real-time appends
let chartDataCache = [];

// Real-time stock price (LTP) & active timeframe candle open price tracking
let currentLivePrice = null;
let currentCandleOpenPrice = null;

// Auto Refresh (1m / 60 seconds) Tracking
let autoRefreshTicker = null;
let autoRefreshRemainingSeconds = 60;
let isAutoRefreshEnabled = true;
const AUTO_REFRESH_INTERVAL_SECONDS = 60;

$(document).ready(async function () {
    // Connect to SignalR early so listeners can be attached before await yields
    connectSignalR();

    // Initialize Select2 dropdown with search enabled early
    $("#stockSelector").select2({
        placeholder: "Select Stock Symbol...",
        allowClear: false,
        width: '100%'
    });

    $('#stockSelector').on('select2:open', function() {
        setTimeout(function() {
            const searchField = document.querySelector('.select2-container--open .select2-search__field');
            if (searchField) {
                searchField.setAttribute('placeholder', 'Search stock symbol (e.g. CIPLA, RELIANCE)...');
            }
        }, 10);
    });

    // Set up select2 dropdown change event
    $("#stockSelector").on('change select2:select', function() {
        const newSymbol = $(this).val();
        if (newSymbol && newSymbol !== activeSymbol) {
            switchSymbol(newSymbol);
        }
    });

    await loadStocksDropdown();
    initCharts();

    // Set up timeframe button click events
    $(".tab-btn").click(function() {
        const newTimeframe = $(this).data("timeframe");
        if (newTimeframe === activeTimeframe) return;

        $(".tab-btn").removeClass("active");
        $(this).addClass("active");
        
        switchTimeframe(newTimeframe);
    });

    // Auto Refresh toggle & manual refresh click handlers
    $("#btnManualRefresh").on("click", function() {
        triggerDashboardRefresh(true);
    });

    $("#toggleAutoRefresh").on("change", function() {
        isAutoRefreshEnabled = $(this).is(":checked");
        if (isAutoRefreshEnabled) {
            startAutoRefresh();
        } else {
            stopAutoRefresh();
        }
    });

    // Weekly P/L % chart week-navigation click handlers
    $("#weeklyPnlPrevBtn").on("click", function() {
        fetchWeeklyPnl(activeSymbol, weeklyPnlOffset - 1);
    });
    $("#weeklyPnlNextBtn").on("click", function() {
        if (weeklyPnlOffset >= 0) return;
        fetchWeeklyPnl(activeSymbol, weeklyPnlOffset + 1);
    });
    $("#weeklyPnlTodayBtn").on("click", function() {
        fetchWeeklyPnl(activeSymbol, 0);
    });

    // Start 1m Auto Refresh timer
    startAutoRefresh();
});

// Start 1m Auto Refresh Countdown & Periodic Fetch
function startAutoRefresh() {
    stopAutoRefresh();
    isAutoRefreshEnabled = true;
    autoRefreshRemainingSeconds = AUTO_REFRESH_INTERVAL_SECONDS;
    updateAutoRefreshBadge(autoRefreshRemainingSeconds);

    autoRefreshTicker = setInterval(async () => {
        if (!isAutoRefreshEnabled) return;

        autoRefreshRemainingSeconds--;

        if (autoRefreshRemainingSeconds <= 0) {
            autoRefreshRemainingSeconds = AUTO_REFRESH_INTERVAL_SECONDS;
            updateAutoRefreshBadge(autoRefreshRemainingSeconds);
            await triggerDashboardRefresh(false);
        } else {
            updateAutoRefreshBadge(autoRefreshRemainingSeconds);
        }
    }, 1000);
}

// Stop/Pause Auto Refresh
function stopAutoRefresh() {
    if (autoRefreshTicker) {
        clearInterval(autoRefreshTicker);
        autoRefreshTicker = null;
    }
    isAutoRefreshEnabled = false;
    $("#autoRefreshBadge").text("OFF").addClass("disabled");
}

// Update Auto Refresh Countdown Badge
function updateAutoRefreshBadge(seconds) {
    const badge = $("#autoRefreshBadge");
    if (!badge.length) return;
    badge.removeClass("disabled").text(`${seconds}s`);
}

// Trigger Manual/Auto Dashboard Refresh
async function triggerDashboardRefresh(isManual = true) {
    const refreshBtnIcon = $(".refresh-spin-icon");
    refreshBtnIcon.addClass("spin");

    try {
        if (activeSymbol) {
            await fetchChartHistory();
            await fetchInitialLastPrice(activeSymbol);
            fetchWeeklyPnl(activeSymbol);
        }
    } catch (ex) {
        console.error("Dashboard refresh error:", ex);
    } finally {
        setTimeout(() => refreshBtnIcon.removeClass("spin"), 600);
    }

    if (isManual && isAutoRefreshEnabled) {
        autoRefreshRemainingSeconds = AUTO_REFRESH_INTERVAL_SECONDS;
        updateAutoRefreshBadge(autoRefreshRemainingSeconds);
    }
}

// Initialize the 5 compact Trading Indicator mini-charts (Lightweight Charts, dark theme).
// These are intentionally minimal (no time axis, no scroll/zoom) - they exist purely for
// at-a-glance shape recognition next to each indicator's signal badge, not for analysis.
function initCharts() {
    if (typeof LightweightCharts === 'undefined') return;

    const currentTheme = localStorage.getItem("theme-color") || "blue";
    const themeColors = {
        blue: '#4f9cf9',
        green: '#34d399',
        red: '#f87171',
        amber: '#fbbf24',
        purple: '#a78bfa'
    };
    const activeThemeColor = themeColors[currentTheme] || themeColors.blue;

    const miniChartBaseOptions = {
        layout: { background: { color: '#0d111e' }, textColor: '#8892a4', fontSize: 10 },
        grid: { vertLines: { visible: false }, horzLines: { color: 'rgba(255, 255, 255, 0.04)' } },
        timeScale: { visible: false },
        rightPriceScale: { borderVisible: false, textColor: '#8892a4' },
        crosshair: { mode: LightweightCharts.CrosshairMode.Magnet },
        handleScroll: false,
        handleScale: false,
    };

    function createMiniChart(containerId) {
        const el = document.getElementById(containerId);
        if (!el) return null;
        return LightweightCharts.createChart(el, {
            ...miniChartBaseOptions,
            width: el.clientWidth,
            height: el.clientHeight || 120
        });
    }

    // 1. EMA 9/20 mini chart: dim price line for context + EMA9/EMA20 crossover lines
    emaMiniChart = createMiniChart('emaMiniChartContainer');
    if (emaMiniChart) {
        emaMiniPriceSeries = emaMiniChart.addLineSeries({ color: 'rgba(136, 146, 164, 0.45)', lineWidth: 1 });
        emaMiniEma9Series = emaMiniChart.addLineSeries({ color: activeThemeColor, lineWidth: 1.5 });
        emaMiniEma20Series = emaMiniChart.addLineSeries({ color: '#a78bfa', lineWidth: 1.5 });
    }

    // 2. RSI mini chart: RSI line with 70/30 reference levels
    rsiMiniChart = createMiniChart('rsiMiniChartContainer');
    if (rsiMiniChart) {
        rsiMiniSeries = rsiMiniChart.addLineSeries({ color: '#fbbf24', lineWidth: 1.5 });
        rsiMiniSeries.createPriceLine({ price: 70, color: 'rgba(248, 113, 113, 0.35)', lineStyle: LightweightCharts.LineStyle.Dashed, axisLabelVisible: true, title: '70' });
        rsiMiniSeries.createPriceLine({ price: 30, color: 'rgba(52, 211, 153, 0.35)', lineStyle: LightweightCharts.LineStyle.Dashed, axisLabelVisible: true, title: '30' });
    }

    // 3. MACD mini chart: MACD line + Signal line + histogram
    macdMiniChart = createMiniChart('macdMiniChartContainer');
    if (macdMiniChart) {
        macdMiniHistSeries = macdMiniChart.addHistogramSeries({ upColor: 'rgba(52, 211, 153, 0.5)', downColor: 'rgba(248, 113, 113, 0.5)' });
        macdMiniLineSeries = macdMiniChart.addLineSeries({ color: activeThemeColor, lineWidth: 1.3 });
        macdMiniSignalSeries = macdMiniChart.addLineSeries({ color: '#fbbf24', lineWidth: 1.1 });
    }

    // 4. VWAP mini chart: dim price line + VWAP line
    vwapMiniChart = createMiniChart('vwapMiniChartContainer');
    if (vwapMiniChart) {
        vwapMiniPriceSeries = vwapMiniChart.addLineSeries({ color: 'rgba(136, 146, 164, 0.45)', lineWidth: 1 });
        vwapMiniVwapSeries = vwapMiniChart.addLineSeries({ color: '#fbbf24', lineWidth: 1.5 });
    }

    // 5. Volume mini chart: recent volume bars
    volumeMiniChart = createMiniChart('volumeMiniChartContainer');
    if (volumeMiniChart) {
        volumeMiniSeries = volumeMiniChart.addHistogramSeries({ color: activeThemeColor });
    }

    // Resize all mini charts on window resize
    window.addEventListener('resize', () => {
        [
            ['emaMiniChartContainer', emaMiniChart],
            ['rsiMiniChartContainer', rsiMiniChart],
            ['macdMiniChartContainer', macdMiniChart],
            ['vwapMiniChartContainer', vwapMiniChart],
            ['volumeMiniChartContainer', volumeMiniChart]
        ].forEach(([id, chart]) => {
            const el = document.getElementById(id);
            if (el && chart) chart.resize(el.clientWidth, el.clientHeight || 120);
        });
    });
}

// Client-side JavaScript Memory Cache
const jsMemoryCache = new Map();

// Fetch active stock instruments
async function loadStocksDropdown() {
    try {
        let stocksList;
        if (jsMemoryCache.has('stocks_dropdown')) {
            stocksList = jsMemoryCache.get('stocks_dropdown');
            console.log("[JS MemoryCache] Loaded stocks dropdown from client cache.");
        } else {
            const response = await fetch(`${API_BASE_URL}/api/marketdata/stocks`);
            if (!response.ok) throw new Error("Failed to load active stocks from API.");
            stocksList = await response.json();
            jsMemoryCache.set('stocks_dropdown', stocksList);
        }
        
        const selector = $("#stockSelector");
        selector.empty();
        stocksList.forEach(stock => {
            selector.append(`<option value="${stock.symbol}">${stock.symbol}</option>`);
        });

        // Re-initialize Select2 to ensure options are refreshed
        if ($.fn.select2) {
            selector.select2({
                placeholder: "Select Stock Symbol...",
                allowClear: false,
                width: '100%'
            });
        }

        // Auto-select first stock and load
        if (stocksList.length > 0) {
            const defaultSym = stocksList[0].symbol;
            selector.val(defaultSym).trigger('change.select2').trigger('change');
            switchSymbol(defaultSym);
        }
    } catch (ex) {
        console.error("Stocks list load failed:", ex);
    }
}

// Seed the live price header from the stock master's last price until the first live tick arrives.
async function fetchInitialLastPrice(symbol) {
    if (!symbol) return;
    try {
        let data;
        const cacheKey = `stock_details_${symbol}`;
        if (jsMemoryCache.has(cacheKey)) {
            data = jsMemoryCache.get(cacheKey);
        } else {
            const response = await fetch(`${API_BASE_URL}/api/marketdata/stock-details/${symbol}`);
            if (!response.ok) throw new Error("Failed to load stock details.");
            data = await response.json();
            jsMemoryCache.set(cacheKey, data);
        }

        if (data.lastPrice !== null && data.lastPrice !== undefined && parseFloat(data.lastPrice) > 0) {
            currentLivePrice = parseFloat(data.lastPrice);
            refreshLivePriceHeader();
        }
    } catch (ex) {
        console.error("Failed to load stock details:", ex);
    }
}

// Fetch day-wise Weekly P/L % data for the active symbol and render the bar chart.
// offset: 0 = current week, -1 = previous week, etc. Omit to keep the currently browsed week.
async function fetchWeeklyPnl(symbol, offset) {
    if (!symbol) return;
    if (offset === undefined || offset === null) offset = weeklyPnlOffset;
    weeklyPnlOffset = offset;

    try {
        const response = await fetch(`${API_BASE_URL}/api/marketdata/weekly-pnl?symbol=${symbol}&weekOffset=${offset}`);
        if (!response.ok) throw new Error("Failed to load Weekly P/L data.");
        const data = await response.json();
        renderWeeklyPnlChart(data);
    } catch (ex) {
        console.error("Weekly P/L fetch error:", ex);
        renderWeeklyPnlChart(null);
    }
}

// Update the week navigation controls (Prev/Next/This Week, range label, title/subtitle)
function updateWeeklyPnlNav(data) {
    const rangeLabel = document.getElementById('weeklyPnlRangeLabel');
    const nextBtn = document.getElementById('weeklyPnlNextBtn');
    const todayBtn = document.getElementById('weeklyPnlTodayBtn');
    const titleEl = document.getElementById('weeklyPnlTitle');
    const subtitleEl = document.getElementById('weeklyPnlSubtitle');

    const isCurrentWeek = !data || data.isCurrentWeek !== false;

    if (nextBtn) nextBtn.disabled = isCurrentWeek;
    if (todayBtn) todayBtn.style.display = isCurrentWeek ? "none" : "inline-block";
    if (titleEl) titleEl.textContent = isCurrentWeek ? "Current Week P/L %" : "Weekly P/L %";
    if (subtitleEl) {
        subtitleEl.textContent = isCurrentWeek
            ? "Daily price performance for the current trading week"
            : "Daily price performance for the selected trading week";
    }

    if (rangeLabel) {
        if (data && data.weekStart && data.weekEnd) {
            const fmt = (s) => new Date(s + 'T00:00:00').toLocaleDateString('en-IN', { day: '2-digit', month: 'short' });
            rangeLabel.textContent = `${fmt(data.weekStart)} - ${fmt(data.weekEnd)}`;
        } else {
            rangeLabel.textContent = "-";
        }
    }
}

// Render the "Weekly P/L %" bar chart (Chart.js) - green bars for positive days, red for negative
function renderWeeklyPnlChart(data) {
    const canvas = document.getElementById('weeklyPnlChartCanvas');
    const emptyState = document.getElementById('weeklyPnlEmptyState');
    if (!canvas) return;

    updateWeeklyPnlNav(data);

    if (weeklyPnlChart) {
        weeklyPnlChart.destroy();
        weeklyPnlChart = null;
    }

    const days = (data && Array.isArray(data.days)) ? data.days : [];

    if (!days.length) {
        canvas.style.display = "none";
        if (emptyState) {
            emptyState.textContent = (data && data.message) || "No trading data available for this week.";
            emptyState.style.display = "flex";
        }
        return;
    }

    canvas.style.display = "block";
    if (emptyState) emptyState.style.display = "none";

    const labels = days.map(d => d.day);
    const pnlData = days.map(d => d.pnlPercent);

    weeklyPnlChart = new Chart(canvas, {
        type: 'bar',
        data: {
            labels: labels,
            datasets: [{
                label: 'Daily P/L %',
                data: pnlData,
                backgroundColor: pnlData.map(v => v >= 0 ? 'rgba(52, 211, 153, 0.85)' : 'rgba(248, 113, 113, 0.85)'),
                borderRadius: 6,
                maxBarThickness: 56
            }]
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            plugins: {
                legend: { display: false },
                tooltip: {
                    backgroundColor: 'rgba(15, 23, 42, 0.95)',
                    borderColor: 'rgba(255, 255, 255, 0.1)',
                    borderWidth: 1,
                    padding: 10,
                    titleColor: '#e2e8f0',
                    bodyColor: '#cbd5e1',
                    displayColors: false,
                    callbacks: {
                        title: (items) => {
                            const d = days[items[0].dataIndex];
                            const dt = new Date(d.date + 'T00:00:00');
                            return `Date: ${dt.toLocaleDateString('en-IN', { day: '2-digit', month: 'short' })}`;
                        },
                        label: (item) => {
                            const d = days[item.dataIndex];
                            const sign = d.pnlPercent >= 0 ? "+" : "";
                            return [
                                `Previous Close: ₹${d.previousClose.toFixed(2)}`,
                                `Close: ₹${d.close.toFixed(2)}`,
                                `Daily P/L: ${sign}${d.pnlPercent.toFixed(2)}%`
                            ];
                        }
                    }
                }
            },
            scales: {
                x: {
                    grid: { color: 'rgba(255, 255, 255, 0.04)' },
                    ticks: { color: '#94a3b8', font: { size: 11 } }
                },
                y: {
                    grid: { color: 'rgba(255, 255, 255, 0.04)' },
                    ticks: {
                        color: '#94a3b8',
                        font: { size: 10 },
                        callback: v => (v >= 0 ? "+" : "") + v.toFixed(1) + "%"
                    }
                }
            }
        }
    });
}

// Switch viewed stock symbol
async function switchSymbol(symbol) {
    currentLivePrice = null;
    currentCandleOpenPrice = null;
    const oldSymbol = activeSymbol;
    activeSymbol = symbol;
    
    if (isAutoRefreshEnabled) {
        autoRefreshRemainingSeconds = AUTO_REFRESH_INTERVAL_SECONDS;
        updateAutoRefreshBadge(autoRefreshRemainingSeconds);
    }

    if (window.QeStockVerdict) window.QeStockVerdict.load(symbol);

    await fetchChartHistory();
    await fetchInitialLastPrice(symbol);
    fetchWeeklyPnl(symbol, 0); // reset to the current week whenever a different stock is selected

    // Re-subscribe to SignalR groups
    if (connection && connection.state === signalR.HubConnectionState.Connected) {
        if (oldSymbol) {
            await connection.invoke("Unsubscribe", oldSymbol, activeTimeframe);
        }
        await connection.invoke("Subscribe", activeSymbol, activeTimeframe);
        console.log(`Subscribed to SignalR group: ${activeSymbol}_${activeTimeframe}`);
    }
}

// Switch timeframe
async function switchTimeframe(timeframe) {
    const oldTimeframe = activeTimeframe;
    activeTimeframe = timeframe;
    
    if (isAutoRefreshEnabled) {
        autoRefreshRemainingSeconds = AUTO_REFRESH_INTERVAL_SECONDS;
        updateAutoRefreshBadge(autoRefreshRemainingSeconds);
    }

    await fetchChartHistory();

    // Re-subscribe to SignalR groups
    if (connection && connection.state === signalR.HubConnectionState.Connected) {
        await connection.invoke("Unsubscribe", activeSymbol, oldTimeframe);
        await connection.invoke("Subscribe", activeSymbol, activeTimeframe);
        console.log(`Subscribed to SignalR group: ${activeSymbol}_${activeTimeframe}`);
    }
}

// Load historical chart data (500 candles - the Trading Indicators mini-charts only render the
// most recent window, but the full history is kept so EMA/RSI/MACD have a proper warm-up period).
async function fetchChartHistory() {
    if (!activeSymbol) return;

    try {
        const response = await fetch(`${API_BASE_URL}/api/marketdata/chart-data?symbol=${activeSymbol}&timeframe=${activeTimeframe}&limit=500`);
        if (!response.ok) throw new Error("Failed to load chart history.");
        const data = await response.json();

        chartDataCache = data;

        // Bind series data
        bindChartData(chartDataCache);

        // Fetch live signal evaluation to get correct score & justification details
        fetchLiveSignalEvaluation();
    } catch (ex) {
        console.error("History fetch error:", ex);
    }
}

// Fetch live signal evaluation dynamically
async function fetchLiveSignalEvaluation() {
    if (!activeSymbol) return;
    try {
        const response = await fetch(`${API_BASE_URL}/api/signals/evaluate?symbol=${activeSymbol}&timeframe=${activeTimeframe}`);
        if (!response.ok) throw new Error("Failed to fetch live signal evaluation.");
        const data = await response.json();
        updateSignalUi(data);
    } catch (ex) {
        console.error("Failed to load live signal evaluation:", ex);
    }
}

function refreshLivePriceHeader() {
    if (currentLivePrice === null || currentLivePrice === undefined || isNaN(currentLivePrice) || currentLivePrice === 0) return;

    // Real-time stock price (LTP) - independent of timeframe
    $("#widgetLTP").text(parseFloat(currentLivePrice).toFixed(2));

    // Percentage change - calculated based on selected timeframe's open price
    if (currentCandleOpenPrice && currentCandleOpenPrice > 0) {
        const changePct = ((currentLivePrice - currentCandleOpenPrice) / currentCandleOpenPrice) * 100;
        const changeStr = (changePct >= 0 ? "+" : "") + changePct.toFixed(2) + "%";
        const badgeClass = changePct >= 0 ? "bullish" : "bearish";

        const widgetChangeEl = $("#widgetChange");
        if (widgetChangeEl.length) {
            widgetChangeEl.text(changeStr);
            widgetChangeEl.attr("class", `w-change ${badgeClass}`);
        }
    }
}

function bindChartData(dataList) {
    if (!dataList || dataList.length === 0) return;

    const latest = dataList[dataList.length - 1];
    currentCandleOpenPrice = latest.open;
    if (currentLivePrice === null || currentLivePrice === undefined || currentLivePrice === 0) {
        currentLivePrice = latest.close;
    }
    refreshLivePriceHeader();

    updateIndicatorPanel(dataList);
}

// ---- Trading Indicators panel: mini-charts + per-indicator signal badges ----

// Standard EMA (SMA-seeded). Backend doesn't compute EMA9 (only EMA20/50 for its own strategy
// scoring), so it's derived here from the same close-price series already loaded for the chart.
function calculateEma(values, period) {
    const result = new Array(values.length).fill(null);
    if (values.length < period) return result;

    let sum = 0;
    for (let i = 0; i < period; i++) sum += values[i];
    let emaPrev = sum / period;
    result[period - 1] = emaPrev;

    const k = 2 / (period + 1);
    for (let i = period; i < values.length; i++) {
        emaPrev = values[i] * k + emaPrev * (1 - k);
        result[i] = emaPrev;
    }
    return result;
}

function fmtPrice(v) {
    return (v === null || v === undefined || isNaN(v)) ? "-" : parseFloat(v).toFixed(2);
}

function fmtVolume(v) {
    if (v === null || v === undefined || isNaN(v)) return "-";
    if (v >= 10000000) return (v / 10000000).toFixed(2) + "Cr";
    if (v >= 100000) return (v / 100000).toFixed(2) + "L";
    if (v >= 1000) return (v / 1000).toFixed(1) + "K";
    return String(v);
}

function setBadge(el, signal, labelOverride) {
    const cls = signal === "BUY" || signal === "CONFIRMED" ? "bullish"
        : signal === "SELL" ? "bearish"
        : signal === "WEAK" ? "amber"
        : "neutral";
    el.attr("class", `mini-signal-badge ${cls}`).text(labelOverride || signal);
}

// ADX gauge needle: points the SVG needle at the current ADX value on a fixed 0-60 scale
// (ADX rarely runs past the high 50s; values above are just clamped to full-scale).
const ADX_GAUGE_MAX_SCALE = 60;
function updateAdxGauge(adxValue) {
    const needle = document.getElementById("adxGaugeNeedle");
    if (!needle) return;

    const clamped = Math.max(0, Math.min(adxValue ?? 0, ADX_GAUGE_MAX_SCALE));
    const fraction = clamped / ADX_GAUGE_MAX_SCALE;
    const thetaRad = (180 - fraction * 180) * (Math.PI / 180); // 180deg (left, value=0) -> 0deg (right, value=max)
    const needleLength = 68; // slightly inside the 80-radius track

    const x2 = 100 + needleLength * Math.cos(thetaRad);
    const y2 = 100 - needleLength * Math.sin(thetaRad);
    needle.setAttribute("x2", x2.toFixed(2));
    needle.setAttribute("y2", y2.toFixed(2));
}

function updateIndicatorPanel(dataList) {
    if (!dataList || dataList.length === 0) return;

    const closes = dataList.map(d => d.close);
    const ema9Full = calculateEma(closes, 9);
    const visible = dataList.slice(-MINI_CHART_VISIBLE_CANDLES);
    const visibleOffset = dataList.length - visible.length;

    const emaPriceData = [], ema9Data = [], ema20Data = [];
    const rsiData = [];
    const macdLineData = [], macdSignalData = [], macdHistData = [];
    const vwapPriceData = [], vwapData = [];
    const volumeData = [];

    visible.forEach((item, idx) => {
        const timeSec = item.time / 1000;
        const globalIdx = visibleOffset + idx;

        emaPriceData.push({ time: timeSec, value: item.close });
        if (ema9Full[globalIdx] !== null) ema9Data.push({ time: timeSec, value: ema9Full[globalIdx] });
        if (item.ema20 !== null && item.ema20 !== undefined) ema20Data.push({ time: timeSec, value: item.ema20 });

        if (item.rsi !== null && item.rsi !== undefined) rsiData.push({ time: timeSec, value: item.rsi });

        if (item.macd !== null && item.macd !== undefined) macdLineData.push({ time: timeSec, value: item.macd });
        if (item.signalLine !== null && item.signalLine !== undefined) macdSignalData.push({ time: timeSec, value: item.signalLine });
        if (item.macd !== null && item.macd !== undefined && item.signalLine !== null && item.signalLine !== undefined) {
            const hist = item.macd - item.signalLine;
            macdHistData.push({ time: timeSec, value: hist, color: hist >= 0 ? 'rgba(52, 211, 153, 0.5)' : 'rgba(248, 113, 113, 0.5)' });
        }

        vwapPriceData.push({ time: timeSec, value: item.close });
        if (item.vwap !== null && item.vwap !== undefined) vwapData.push({ time: timeSec, value: item.vwap });

        if (item.volume !== null && item.volume !== undefined) {
            volumeData.push({ time: timeSec, value: item.volume, color: item.close >= item.open ? 'rgba(52, 211, 153, 0.6)' : 'rgba(248, 113, 113, 0.6)' });
        }
    });

    if (emaMiniChart) { emaMiniPriceSeries.setData(emaPriceData); emaMiniEma9Series.setData(ema9Data); emaMiniEma20Series.setData(ema20Data); emaMiniChart.timeScale().fitContent(); }
    if (rsiMiniChart) { rsiMiniSeries.setData(rsiData); rsiMiniChart.timeScale().fitContent(); }
    if (macdMiniChart) { macdMiniLineSeries.setData(macdLineData); macdMiniSignalSeries.setData(macdSignalData); macdMiniHistSeries.setData(macdHistData); macdMiniChart.timeScale().fitContent(); }
    if (vwapMiniChart) { vwapMiniPriceSeries.setData(vwapPriceData); vwapMiniVwapSeries.setData(vwapData); vwapMiniChart.timeScale().fitContent(); }
    if (volumeMiniChart) { volumeMiniSeries.setData(volumeData); volumeMiniChart.timeScale().fitContent(); }

    // ---- Per-indicator signal, from the latest completed candle ----
    const n = dataList.length;
    const latest = dataList[n - 1];
    const prev = n > 1 ? dataList[n - 2] : null;

    // 1. EMA 9/20
    const ema9Latest = ema9Full[n - 1];
    const ema20Latest = latest.ema20;
    let emaSignal = "NEUTRAL", emaStatus = "Flat";
    if (ema9Latest !== null && ema20Latest !== null && ema20Latest !== undefined) {
        if (ema9Latest > ema20Latest) { emaSignal = "BUY"; emaStatus = "Bullish"; }
        else if (ema9Latest < ema20Latest) { emaSignal = "SELL"; emaStatus = "Bearish"; }
    }
    $("#valEma9").text(fmtPrice(ema9Latest));
    $("#valEma20").text(fmtPrice(ema20Latest));
    setBadge($("#badgeEma"), emaSignal);
    $("#statusEma").text(emaStatus).attr("class", `indicator-card-status ${emaSignal === "BUY" ? "bullish" : emaSignal === "SELL" ? "bearish" : "neutral"}`);

    // 2. RSI (14): oversold = potential BUY, overbought = potential SELL
    const rsiLatest = (latest.rsi !== null && latest.rsi !== undefined) ? parseFloat(latest.rsi) : null;
    let rsiSignal = "NEUTRAL", rsiStatus = "Neutral";
    if (rsiLatest !== null) {
        if (rsiLatest >= 70) { rsiSignal = "SELL"; rsiStatus = "Overbought"; }
        else if (rsiLatest <= 30) { rsiSignal = "BUY"; rsiStatus = "Oversold"; }
    }
    $("#valRsi").text(rsiLatest !== null ? rsiLatest.toFixed(2) : "-");
    setBadge($("#badgeRsi"), rsiSignal);
    $("#statusRsi").text(rsiStatus).attr("class", `indicator-card-status ${rsiSignal === "BUY" ? "bullish" : rsiSignal === "SELL" ? "bearish" : "neutral"}`);

    // 3. MACD (12,26,9): crossover against the Signal line
    const macdLatest = (latest.macd !== null && latest.macd !== undefined) ? latest.macd : null;
    const sigLatest = (latest.signalLine !== null && latest.signalLine !== undefined) ? latest.signalLine : null;
    const macdPrev = prev && prev.macd !== null && prev.macd !== undefined ? prev.macd : null;
    const sigPrev = prev && prev.signalLine !== null && prev.signalLine !== undefined ? prev.signalLine : null;
    let macdSignal = "NEUTRAL", macdStatus = "Flat";
    if (macdLatest !== null && sigLatest !== null) {
        const crossUp = macdPrev !== null && sigPrev !== null && macdPrev <= sigPrev && macdLatest > sigLatest;
        const crossDown = macdPrev !== null && sigPrev !== null && macdPrev >= sigPrev && macdLatest < sigLatest;
        if (crossUp) { macdSignal = "BUY"; macdStatus = "Bullish Crossover"; }
        else if (crossDown) { macdSignal = "SELL"; macdStatus = "Bearish Crossover"; }
        else if (macdLatest > sigLatest) { macdSignal = "BUY"; macdStatus = "Bullish"; }
        else if (macdLatest < sigLatest) { macdSignal = "SELL"; macdStatus = "Bearish"; }
    }
    $("#valMacd").text(macdLatest !== null ? macdLatest.toFixed(2) : "-");
    setBadge($("#badgeMacd"), macdSignal);
    $("#statusMacd").text(macdStatus).attr("class", `indicator-card-status ${macdSignal === "BUY" ? "bullish" : macdSignal === "SELL" ? "bearish" : "neutral"}`);

    // 4. VWAP
    const vwapLatest = (latest.vwap !== null && latest.vwap !== undefined && latest.vwap > 0) ? latest.vwap : null;
    let vwapSignal = "NEUTRAL", vwapStatus = "-";
    if (vwapLatest !== null) {
        const diffPct = ((latest.close - vwapLatest) / vwapLatest) * 100;
        if (latest.close > vwapLatest) { vwapSignal = "BUY"; vwapStatus = `Price > VWAP (${diffPct >= 0 ? "+" : ""}${diffPct.toFixed(2)}%)`; }
        else if (latest.close < vwapLatest) { vwapSignal = "SELL"; vwapStatus = `Price < VWAP (${diffPct.toFixed(2)}%)`; }
        else { vwapStatus = "Price = VWAP"; }
    }
    $("#valVwapPrice").text(fmtPrice(latest.close));
    $("#valVwap").text(fmtPrice(vwapLatest));
    setBadge($("#badgeVwap"), vwapSignal);
    $("#statusVwap").text(vwapStatus).attr("class", `indicator-card-status ${vwapSignal === "BUY" ? "bullish" : vwapSignal === "SELL" ? "bearish" : "neutral"}`);

    // 5. Volume - confirms strength of the existing move, never generates BUY/SELL on its own
    const volumeWindow = dataList.slice(Math.max(0, n - 21), n - 1); // last 20 candles, excluding current
    const avgVolume = volumeWindow.length > 0 ? volumeWindow.reduce((s, d) => s + (d.volume || 0), 0) / volumeWindow.length : null;
    const latestVolume = latest.volume || 0;
    const relativeVolume = (avgVolume && avgVolume > 0) ? latestVolume / avgVolume : null;
    let volumeSignal = "NO CONFIRMATION", volumeStatus = "Low Volume";
    if (relativeVolume !== null) {
        if (relativeVolume >= 1.5) { volumeSignal = "CONFIRMED"; volumeStatus = "High Volume"; }
        else if (relativeVolume >= 0.8) { volumeSignal = "WEAK"; volumeStatus = "Average Volume"; }
    }
    $("#valVolume").text(fmtVolume(latestVolume));
    $("#valRelVolume").text(relativeVolume !== null ? relativeVolume.toFixed(2) + "x" : "-");
    setBadge($("#badgeVolume"), volumeSignal);
    $("#statusVolume").text(volumeStatus).attr("class", `indicator-card-status ${volumeSignal === "CONFIRMED" ? "bullish" : volumeSignal === "WEAK" ? "amber" : "neutral"}`);

    // 6. ADX (14) - trend STRENGTH only, never direction, so (like Volume) it never drives its
    // own BUY/SELL and instead "confirms" whatever move is already happening. Thresholds reuse the
    // same two cutoffs already coded for the Swing Trading strategy rather than inventing new ones:
    // SwingDecisionEngine requires ADX >= 20 before it will trade at all (below that it logs the
    // signal as "Weak/Choppy"), and the legacy swing service's own "Strong Trend" bar is ADX > 25.
    const adxLatest = (latest.adx !== null && latest.adx !== undefined) ? parseFloat(latest.adx) : null;
    const adxPrev = (prev && prev.adx !== null && prev.adx !== undefined) ? parseFloat(prev.adx) : null;
    let adxSignal = "NO CONFIRMATION", adxLabel = "WEAK", adxStatus = "Weak / Choppy Trend";
    if (adxLatest !== null) {
        if (adxLatest >= 25) { adxSignal = "CONFIRMED"; adxLabel = "STRONG"; adxStatus = "Strong Trend"; }
        else if (adxLatest >= 20) { adxSignal = "WEAK"; adxLabel = "AVERAGE"; adxStatus = "Average Trend"; }
    }
    const adxStatusCls = adxSignal === "CONFIRMED" ? "bullish" : adxSignal === "WEAK" ? "amber" : "neutral";
    $("#valAdx").text(adxLatest !== null ? `ADX ${adxLatest.toFixed(1)}` : "ADX -");
    $("#valAdxCurrent").text(adxLatest !== null ? adxLatest.toFixed(1) : "-");
    $("#valAdxPrev").text(adxPrev !== null ? adxPrev.toFixed(1) : "-");
    $("#lblAdxStrength").text(adxLabel).attr("class", `adx-gauge-strength ${adxStatusCls}`);
    setBadge($("#badgeAdx"), adxSignal, adxLabel);
    $("#statusAdx").text(adxStatus).attr("class", `indicator-card-status ${adxStatusCls}`);
    updateAdxGauge(adxLatest);

    lastIndicatorSignals = { ema: emaSignal, rsi: rsiSignal, macd: macdSignal, vwap: vwapSignal, volume: volumeSignal, adx: adxSignal };
    updateOverallSignalSummary();
}

// The Overall Signal itself is the actual configured-strategy recommendation (same backend
// scoring engine that drives the AI Recommendation card above - see updateSignalUi/lastOverallSignalType),
// never a naive re-count here. The 5 chips below it just show how many of the mini-indicators
// currently agree with that real signal, for a quick "is this move broadly confirmed?" read.
function updateOverallSignalSummary() {
    const type = (lastOverallSignalType || "HOLD").toUpperCase();
    const badge = $("#overallSignalBadge");
    badge.attr("class", `recommendation-badge ${type.toLowerCase()}`).text(type);

    const chips = [
        { id: "#chipEma", signal: lastIndicatorSignals.ema },
        { id: "#chipRsi", signal: lastIndicatorSignals.rsi },
        { id: "#chipMacd", signal: lastIndicatorSignals.macd },
        { id: "#chipVwap", signal: lastIndicatorSignals.vwap },
        { id: "#chipVolume", signal: lastIndicatorSignals.volume },
        { id: "#chipAdx", signal: lastIndicatorSignals.adx }
    ];

    let confirmations = 0;
    chips.forEach(c => {
        let agrees, cls;
        if (c.signal === "CONFIRMED" || c.signal === "WEAK" || c.signal === "NO CONFIRMATION") {
            // Volume never drives direction - it "agrees" with a live BUY/SELL move when it CONFIRMS,
            // and with a HOLD stance when there simply isn't a strong move to confirm.
            agrees = type === "HOLD" ? c.signal !== "CONFIRMED" : c.signal === "CONFIRMED";
            cls = c.signal === "CONFIRMED" ? "bullish" : c.signal === "WEAK" ? "amber" : "neutral";
        } else {
            agrees = (type === "BUY" || type === "SELL") ? c.signal === type : c.signal === "NEUTRAL";
            cls = c.signal === "BUY" ? "bullish" : c.signal === "SELL" ? "bearish" : "neutral";
        }
        if (agrees) confirmations++;
        $(c.id).attr("class", `chip-value ${cls}`).text(c.signal);
    });

    $("#overallConfirmationCount").text(`${confirmations} / 6`);
}

// SignalR Connection
function connectSignalR() {
    connection = new signalR.HubConnectionBuilder()
        .withUrl(`${API_BASE_URL}/api/hubs/marketdata`)
        .withAutomaticReconnect()
        .build();

    connection.on("ReceiveActiveCandle", function(candleUpdate) {
        // Ignore ticks from other symbols/timeframes
        if (!activeSymbol) return;

        // Real-time stock price is the latest live tick price; timeframe candle open is for timeframe % change.
        // The Trading Indicators mini-charts/signals only update on candle close (ReceiveClosedCandle) -
        // they're meant to reflect the latest COMPLETED candle, not every intra-candle tick.
        currentLivePrice = candleUpdate.close;
        currentCandleOpenPrice = candleUpdate.open;
        refreshLivePriceHeader();
    });

    connection.on("ReceiveClosedCandle", function(closedCandle) {
        console.log("New closed candle received via SignalR:", closedCandle);

        // Add to cache or overwrite last element if timestamps match
        const timeSec = closedCandle.time / 1000;
        
        const index = chartDataCache.findIndex(d => d.time === closedCandle.time);
        const chartItem = {
            time: closedCandle.time,
            open: closedCandle.open,
            high: closedCandle.high,
            low: closedCandle.low,
            close: closedCandle.close,
            volume: closedCandle.volume,
            rsi: closedCandle.rsi,
            ema20: closedCandle.ema20,
            ema50: closedCandle.ema50,
            macd: closedCandle.macd,
            signalLine: closedCandle.signalLine,
            vwap: closedCandle.vwap,
            signalType: closedCandle.signalType,
            signalScore: closedCandle.signalScore,
            signalReason: closedCandle.signalReason
        };

        if (index !== -1) {
            chartDataCache[index] = chartItem;
        } else {
            chartDataCache.push(chartItem);
            if (chartDataCache.length > 2000) {
                chartDataCache.shift();
            }
        }

        // Update all charts
        bindChartData(chartDataCache);

        // Update the dashboard widgets and glowing signal card!
        updateSignalUi(chartItem);
    });

    connection.onreconnecting(error => {
        updateConnectionStatus(false, "Reconnecting...");
    });

    connection.onreconnected(connectionId => {
        updateConnectionStatus(true, "Connected");
        // Restore group subscriptions
        if (activeSymbol) {
            connection.invoke("Subscribe", activeSymbol, activeTimeframe);
        }
    });

    connection.onclose(error => {
        updateConnectionStatus(false, "Disconnected");
    });

    // Start connection
    connection.start()
        .then(() => {
            updateConnectionStatus(true, "Connected");
            if (activeSymbol) {
                connection.invoke("Subscribe", activeSymbol, activeTimeframe);
                console.log(`Subscribed to SignalR group: ${activeSymbol}_${activeTimeframe}`);
            }
        })
        .catch(err => {
            console.error("SignalR connection error:", err);
            updateConnectionStatus(false, "Error Connecting");
            setTimeout(connectSignalR, 5000);
        });
}

function updateConnectionStatus(isOnline, text) {
    const badge = $("#connectionStatus");
    if (badge.length) {
        badge.attr("class", `connection-badge ${isOnline ? "online" : "offline"}`);
        badge.find(".status-text").text(text);
    }
}

// Update Dashboard widgets
function updateSignalUi(data) {
    if (!data) return;

    // Support both SignalEvaluationResult property names and historical/SignalR payload property names
    const type = (data.signalType || data.SignalType || "HOLD").toUpperCase();
    const score = parseInt(data.signalScore !== undefined ? data.signalScore : (data.score !== undefined ? data.score : 0));
    const strength = data.signalStrength || data.strength || "Neutral";
    const reason = data.signalReason || data.reason || "No signal generated for current active candle.";
    
    const priceVal = data.close !== undefined ? data.close : (data.latestPrice !== undefined ? data.latestPrice : 0);
    const openVal = data.open !== undefined ? data.open : (data.latestOpen !== undefined ? data.latestOpen : priceVal);
    const timeVal = data.time || data.evaluatedAt || new Date();

    // 1. Update glowing signal card class and badge
    const card = $("#signalCard");
    const badge = $("#signalBadge");
    const scoreCircle = $("#scoreCircle");

    if (card.length) card.attr("class", `card signal-card ${type.toLowerCase()}`);
    if (badge.length) badge.attr("class", `recommendation-badge ${type.toLowerCase()}`).text(type);

    // Keep the Trading Indicators "Overall Signal" in sync with the same strategy-engine result
    lastOverallSignalType = type;
    updateOverallSignalSummary();

    // 2. Radial Progress Circle & Score
    $("#scoreValue").text(score);
    
    // stroke-dasharray maps to circumference (2 * pi * r = 2 * 3.1415 * 15.9155 = 100)
    if (scoreCircle.length) scoreCircle.css("stroke-dasharray", `${score}, 100`);

    // 3. Info labels
    const strengthEl = $("#signalStrength");
    if (strengthEl.length) {
        strengthEl.text(strength);
        if (type === "BUY") strengthEl.attr("class", "m-val-badge bullish");
        else if (type === "SELL") strengthEl.attr("class", "m-val-badge bearish");
        else strengthEl.attr("class", "m-val-badge neutral");
    }

    $("#signalPrice").text(priceVal ? "₹" + parseFloat(priceVal).toFixed(2) : "-");
    
    let formattedTime = "-";
    if (timeVal) {
        const d = new Date(timeVal);
        if (!isNaN(d.getTime())) {
            formattedTime = d.toLocaleTimeString('en-IN', {
                timeZone: 'Asia/Kolkata',
                hour: '2-digit',
                minute: '2-digit',
                second: '2-digit',
                hour12: true
            }) + " IST";
        }
    }
    $("#signalTime").text(formattedTime);

    // Confidence Level evaluation
    let confidenceText = "-";
    let confidenceClass = "m-val-badge neutral";
    if (type === "HOLD") {
        confidenceText = `Hold (${score}%)`;
        confidenceClass = "m-val-badge neutral";
    } else {
        if (score >= 90) {
            confidenceText = `Very High (${score}%)`;
            confidenceClass = "m-val-badge bullish";
        } else if (score >= 70) {
            confidenceText = `High (${score}%)`;
            confidenceClass = "m-val-badge bullish";
        } else if (score >= 50) {
            confidenceText = `Moderate (${score}%)`;
            confidenceClass = "m-val-badge neutral";
        } else {
            confidenceText = `Low (${score}%)`;
            confidenceClass = "m-val-badge bearish";
        }
    }
    const confidenceEl = $("#signalConfidence");
    if (confidenceEl.length) {
        confidenceEl.text(confidenceText).attr("class", confidenceClass);
    }

    $("#signalReasoning").text(reason);

    // 4. Quick indicator widgets
    // LTP
    if (priceVal) {
        if (currentLivePrice === null || currentLivePrice === undefined || currentLivePrice === 0) {
            currentLivePrice = priceVal;
        }
        if (openVal && (currentCandleOpenPrice === null || currentCandleOpenPrice === undefined || currentCandleOpenPrice === 0)) {
            currentCandleOpenPrice = openVal;
        }
        refreshLivePriceHeader();
    }

    // RSI
    const rsi = data.rsi !== undefined ? data.rsi : data.RSI;
    if (rsi !== null && rsi !== undefined) {
        const rsiVal = parseFloat(rsi);
        $("#widgetRSI").text(rsiVal.toFixed(2));
        
        // Update RSI mini fill bar
        if (!isNaN(rsiVal)) {
            const clampedPct = Math.min(100, Math.max(0, rsiVal));
            $("#rsiBarFill").css("width", `${clampedPct}%`);
        }

        const rsiEl = $("#widgetRsiStatus");
        if (rsiEl.length) {
            if (rsiVal > 60) {
                rsiEl.text("Overbought").attr("class", "w-status bearish");
            } else if (rsiVal < 40) {
                rsiEl.text("Oversold").attr("class", "w-status bullish");
            } else {
                rsiEl.text("Neutral").attr("class", "w-status neutral");
            }
        }
    } else {
        $("#widgetRSI").text("-");
        $("#widgetRsiStatus").text("-").attr("class", "w-status neutral");
        $("#rsiBarFill").css("width", "50%");
    }

    // VWAP Difference
    const vwap = data.vwap !== undefined ? data.vwap : data.VWAP;
    if (vwap !== null && vwap !== undefined && vwap > 0) {
        const diff = priceVal - vwap;
        const pct = (diff / vwap) * 100;
        $("#widgetVWAP").text((pct >= 0 ? "+" : "") + pct.toFixed(2) + "%");
        const vwapEl = $("#widgetVwapStatus");
        if (vwapEl.length) {
            if (pct >= 0) {
                vwapEl.text("Price > VWAP").attr("class", "w-status bullish");
            } else {
                vwapEl.text("Price < VWAP").attr("class", "w-status bearish");
            }
        }
    } else {
        $("#widgetVWAP").text("-");
        $("#widgetVwapStatus").text("-").attr("class", "w-status neutral");
    }

    // MACD Cross
    const macd = data.macd !== undefined ? data.macd : data.MACD;
    const sigLine = data.signalLine !== undefined ? data.signalLine : (data.MACDSignal !== undefined ? data.MACDSignal : data.macdSignal);
    if (macd !== null && sigLine !== null && macd !== undefined && sigLine !== undefined) {
        $("#widgetMACD").text(parseFloat(macd).toFixed(2));
        const macdEl = $("#widgetMacdStatus");
        if (macdEl.length) {
            const diff = macd - sigLine;
            if (diff > 0) {
                macdEl.text("Bullish").attr("class", "w-status bullish");
            } else if (diff < 0) {
                macdEl.text("Bearish").attr("class", "w-status bearish");
            } else {
                macdEl.text("No Cross").attr("class", "w-status neutral");
            }
        }
    } else {
        $("#widgetMACD").text("-");
        $("#widgetMacdStatus").text("-").attr("class", "w-status neutral");
    }
}



