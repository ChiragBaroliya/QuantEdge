// QuantEdge Dashboard Real-time Charting & SignalR JavaScript

const API_BASE_URL = window.QuantEdgeConfig?.apiBaseUrl || "";
let connection = null;

let activeSymbol = "";
let activeTimeframe = "1m";

// Chart definitions
let priceChart = null;
let rsiChart = null;
let macdChart = null;

// Series definitions
let candleSeries = null;
let ema20Series = null;
let ema50Series = null;
let vwapSeries = null;
let rsiSeries = null;
let macdLineSeries = null;
let macdSignalSeries = null;
let macdHistSeries = null;

// Keep local cache of data for real-time appends
let chartDataCache = [];

$(document).ready(async function () {
    // Connect to SignalR early so listeners can be attached before await yields
    connectSignalR();

    await loadStocksDropdown();
    initCharts();
  

    // Initialize Select2 dropdown with search disabled for few assets, styled nicely
    $("#stockSelector").select2({
        minimumResultsForSearch: Infinity
    });

    // Set up select2 dropdown change event
    $("#stockSelector").on('change select2:select', function() {
        const newSymbol = $(this).val();
        if (newSymbol && newSymbol !== activeSymbol) {
            switchSymbol(newSymbol);
        }
    });

    // Set up timeframe button click events
    $(".tab-btn").click(function() {
        const newTimeframe = $(this).data("timeframe");
        if (newTimeframe === activeTimeframe) return;

        $(".tab-btn").removeClass("active");
        $(this).addClass("active");
        
        switchTimeframe(newTimeframe);
    });
});

// Initialize Lightweight Charts (Dark Theme)
function initCharts() {
    const priceEl = document.getElementById('priceChartContainer');
    if (!priceEl) return;
    const chartOptions = {
        layout: {
            background: { color: '#0d111e' },
            textColor: '#8892a4',
        },
        grid: {
            vertLines: { color: 'rgba(255, 255, 255, 0.03)' },
            horzLines: { color: 'rgba(255, 255, 255, 0.03)' },
        },
        timeScale: {
            timeVisible: true,
            secondsVisible: false,
            borderColor: 'rgba(255, 255, 255, 0.06)',
            tickMarkFormatter: (time, tickMarkType, locale) => {
                if (typeof time === 'number') {
                    const date = new Date(time * 1000);
                    const options = { timeZone: 'Asia/Kolkata' };
                    switch (tickMarkType) {
                        case 0: // Year
                            return date.toLocaleDateString('en-IN', { ...options, year: 'numeric' });
                        case 1: // Month
                            return date.toLocaleDateString('en-IN', { ...options, month: 'short' });
                        case 2: // DayOfMonth
                            return date.toLocaleDateString('en-IN', { ...options, day: '2-digit', month: 'short' });
                        case 3: // Time
                            return date.toLocaleTimeString('en-IN', { ...options, hour: '2-digit', minute: '2-digit', hour12: false });
                        case 4: // TimeWithSeconds
                            return date.toLocaleTimeString('en-IN', { ...options, hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false });
                        default:
                            return date.toLocaleTimeString('en-IN', { ...options, hour: '2-digit', minute: '2-digit', hour12: false });
                    }
                }
                return null;
            }
        },
        localization: {
            locale: 'en-IN',
            dateFormat: 'dd MMM yyyy',
            timeFormatter: (time) => {
                if (typeof time === 'number') {
                    const date = new Date(time * 1000);
                    if (activeTimeframe === '1d') {
                        return date.toLocaleDateString('en-IN', {
                            timeZone: 'Asia/Kolkata',
                            day: '2-digit',
                            month: 'short',
                            year: 'numeric'
                        }) + ' (IST)';
                    }
                    return date.toLocaleString('en-IN', {
                        timeZone: 'Asia/Kolkata',
                        day: '2-digit',
                        month: 'short',
                        year: 'numeric',
                        hour: '2-digit',
                        minute: '2-digit',
                        second: '2-digit',
                        hour12: false
                    }) + ' IST';
                }
                return String(time);
            }
        },
        crosshair: {
            mode: LightweightCharts.CrosshairMode.Normal,
        },
    };

    // 1. Price Chart Container
    priceChart = LightweightCharts.createChart(document.getElementById('priceChartContainer'), {
        ...chartOptions,
        rightPriceScale: {
            borderColor: 'rgba(255, 255, 255, 0.06)',
        }
    });

    candleSeries = priceChart.addCandlestickSeries({
        upColor: '#34d399',
        downColor: '#f87171',
        borderVisible: false,
        wickUpColor: '#34d399',
        wickDownColor: '#f87171',
    });

    const currentTheme = localStorage.getItem("theme-color") || "blue";
    const themeColors = {
        blue: '#4f9cf9',
        green: '#34d399',
        red: '#f87171',
        amber: '#fbbf24',
        purple: '#a78bfa'
    };
    const activeThemeColor = themeColors[currentTheme] || themeColors.blue;

    ema20Series = priceChart.addLineSeries({ color: activeThemeColor, lineWidth: 1.5 });
    ema50Series = priceChart.addLineSeries({ color: '#a78bfa', lineWidth: 1.5 });
    vwapSeries = priceChart.addLineSeries({ color: '#fbbf24', lineWidth: 1.2 });

    // 2. RSI Chart Container
    rsiChart = LightweightCharts.createChart(document.getElementById('rsiChartContainer'), {
        ...chartOptions,
        rightPriceScale: {
            borderColor: 'rgba(255, 255, 255, 0.06)',
            visible: true
        }
    });
    rsiSeries = rsiChart.addLineSeries({ color: '#fbbf24', lineWidth: 1.5 });

    // Add RSI bounds
    const rsiScale = rsiChart.priceScale('right');
    rsiSeries.createPriceLine({ price: 70, color: 'rgba(248, 113, 113, 0.25)', lineStyle: LightweightCharts.LineStyle.Dashed, axisLabelVisible: true, title: 'Overbought' });
    rsiSeries.createPriceLine({ price: 30, color: 'rgba(52, 211, 153, 0.25)', lineStyle: LightweightCharts.LineStyle.Dashed, axisLabelVisible: true, title: 'Oversold' });

    // 3. MACD Chart Container
    macdChart = LightweightCharts.createChart(document.getElementById('macdChartContainer'), {
        ...chartOptions,
        rightPriceScale: {
            borderColor: 'rgba(255, 255, 255, 0.06)',
            visible: true
        }
    });
    macdLineSeries = macdChart.addLineSeries({ color: activeThemeColor, lineWidth: 1.5 });
    macdSignalSeries = macdChart.addLineSeries({ color: '#fbbf24', lineWidth: 1.2 });
    macdHistSeries = macdChart.addHistogramSeries({
        upColor: 'rgba(52, 211, 153, 0.4)',
        downColor: 'rgba(248, 113, 113, 0.4)',
    });

    // Synchronize crosshairs & scaling across all three charts
    priceChart.timeScale().subscribeVisibleLogicalRangeChange(range => {
        rsiChart.timeScale().setVisibleLogicalRange(range);
        macdChart.timeScale().setVisibleLogicalRange(range);
    });
    rsiChart.timeScale().subscribeVisibleLogicalRangeChange(range => {
        priceChart.timeScale().setVisibleLogicalRange(range);
        macdChart.timeScale().setVisibleLogicalRange(range);
    });
    macdChart.timeScale().subscribeVisibleLogicalRangeChange(range => {
        priceChart.timeScale().setVisibleLogicalRange(range);
        rsiChart.timeScale().setVisibleLogicalRange(range);
    });
    
    // Adjust resizing
    window.addEventListener('resize', () => {
        const priceEl = document.getElementById('priceChartContainer');
        if (priceEl && priceChart && rsiChart && macdChart) {
            priceChart.resize(priceEl.clientWidth, 320);
            rsiChart.resize(priceEl.clientWidth, 120);
            macdChart.resize(priceEl.clientWidth, 120);
        }
    });
}

// Fetch active stock instruments
async function loadStocksDropdown() {
    try {
        const response = await fetch(`${API_BASE_URL}/api/marketdata/stocks`);
        if (!response.ok) throw new Error("Failed to load active stocks from API.");
        const stocksList = await response.json();
        
        const selector = $("#stockSelector");
        stocksList.forEach(stock => {
            selector.append(`<option value="${stock.symbol}">${stock.symbol}</option>`);
        });

        // Auto-select first stock and load
        if (stocksList.length > 0) {
            selector.val(stocksList[0].symbol);
            switchSymbol(stocksList[0].symbol);
        }
    } catch (ex) {
        console.error("Stocks list load failed:", ex);
    }
}

// Fetch and display single stock master details
async function fetchStockMasterDetails(symbol) {
    if (!symbol) return;
    try {
        const response = await fetch(`${API_BASE_URL}/api/marketdata/stock-details/${symbol}`);
        if (!response.ok) throw new Error("Failed to load stock details.");
        const data = await response.json();
        
        $("#specName").text(data.name || "-");
        $("#specSymbol").text(data.symbol || "-");
        $("#specInstrumentToken").text(data.instrumentToken || "-");
        $("#specExchangeToken").text(data.exchangeToken || "-");
        $("#specExchange").text(data.exchange || "-");
        $("#specSegment").text(data.segment || "-");
        $("#specInstrumentType").text(data.instrumentType || "-");
        
        $("#specLastPrice").text(data.lastPrice !== null && data.lastPrice !== undefined ? "₹" + parseFloat(data.lastPrice).toFixed(2) : "-");
        $("#specLotSize").text(data.lotSize !== null && data.lotSize !== undefined ? data.lotSize : "-");
        $("#specTickSize").text(data.tickSize !== null && data.tickSize !== undefined ? parseFloat(data.tickSize).toFixed(4) : "-");
        $("#specStrike").text(data.strike !== null && data.strike !== undefined && parseFloat(data.strike) !== 0 ? "₹" + parseFloat(data.strike).toFixed(2) : "-");

        if (data.expiry) {
            const expiryDate = new Date(data.expiry);
            if (!isNaN(expiryDate.getTime())) {
                $("#specExpiry").text(expiryDate.toLocaleDateString('en-IN', { timeZone: 'Asia/Kolkata', day: '2-digit', month: 'short', year: 'numeric' }));
            } else {
                $("#specExpiry").text("-");
            }
        } else {
            $("#specExpiry").text("-");
        }

        const statusBadge = $("#specStatus");
        if (data.isActive) {
            statusBadge.text("Active").attr("class", "spec-status-badge active");
        } else {
            statusBadge.text("Inactive").attr("class", "spec-status-badge inactive");
        }
    } catch (ex) {
        console.error("Failed to load stock details:", ex);
    }
}

// Switch viewed stock symbol
async function switchSymbol(symbol) {
    const oldSymbol = activeSymbol;
    activeSymbol = symbol;
    
    $("#chartTitle").text(`${activeSymbol} Candlestick Chart (${activeTimeframe} - IST)`);
    await fetchChartHistory();
    await fetchStockMasterDetails(symbol);

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
    
    $("#chartTitle").text(`${activeSymbol} Candlestick Chart (${activeTimeframe} - IST)`);
    await fetchChartHistory();

    // Re-subscribe to SignalR groups
    if (connection && connection.state === signalR.HubConnectionState.Connected) {
        await connection.invoke("Unsubscribe", activeSymbol, oldTimeframe);
        await connection.invoke("Subscribe", activeSymbol, activeTimeframe);
        console.log(`Subscribed to SignalR group: ${activeSymbol}_${activeTimeframe}`);
    }
}

// Load historical chart data
async function fetchChartHistory() {
    if (!activeSymbol) return;

    try {
        const response = await fetch(`${API_BASE_URL}/api/marketdata/chart-data?symbol=${activeSymbol}&timeframe=${activeTimeframe}&limit=200`);
        if (!response.ok) throw new Error("Failed to load chart history.");
        const data = await response.json();

        chartDataCache = data;

        // Bind series data
        bindChartData(chartDataCache);

        // Fit time scale to display the new dataset
        if (priceChart) {
            priceChart.timeScale().fitContent();
        }

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

function bindChartData(dataList) {
    if (!candleSeries) return;
    const priceData = [];
    const ema20Data = [];
    const ema50Data = [];
    const vwapData = [];
    const rsiData = [];
    const macdData = [];
    const macdSignalData = [];
    const macdHistData = [];

    dataList.forEach(item => {
        const timeSec = item.time / 1000;

        priceData.push({ time: timeSec, open: item.open, high: item.high, low: item.low, close: item.close });
        
        if (item.ema20 !== null) ema20Data.push({ time: timeSec, value: item.ema20 });
        if (item.ema50 !== null) ema50Data.push({ time: timeSec, value: item.ema50 });
        if (item.vwap !== null) vwapData.push({ time: timeSec, value: item.vwap });
        if (item.rsi !== null) rsiData.push({ time: timeSec, value: item.rsi });
        if (item.macd !== null) macdData.push({ time: timeSec, value: item.macd });
        if (item.signalLine !== null) macdSignalData.push({ time: timeSec, value: item.signalLine });
        
        if (item.macd !== null && item.signalLine !== null) {
            const hist = item.macd - item.signalLine;
            macdHistData.push({
                time: timeSec,
                value: hist,
                color: hist >= 0 ? 'rgba(52, 211, 153, 0.4)' : 'rgba(248, 113, 113, 0.4)'
            });
        }
    });

    candleSeries.setData(priceData);
    ema20Series.setData(ema20Data);
    ema50Series.setData(ema50Data);
    vwapSeries.setData(vwapData);
    rsiSeries.setData(rsiData);
    macdLineSeries.setData(macdData);
    macdSignalSeries.setData(macdSignalData);
    macdHistSeries.setData(macdHistData);
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

        const timeSec = candleUpdate.time / 1000;

        // Live price updates current active candle bar on the chart
        if (candleSeries) {
            candleSeries.update({
                time: timeSec,
                open: candleUpdate.open,
                high: candleUpdate.high,
                low: candleUpdate.low,
                close: candleUpdate.close
            });
        }

        // Update widgets with live LTP
        $("#widgetLTP").text(candleUpdate.close.toFixed(2));
        
        // Calculate percentage change compared to the open price of the active candle
        const changePct = ((candleUpdate.close - candleUpdate.open) / candleUpdate.open) * 100;
        const changeEl = $("#widgetChange");
        changeEl.text((changePct >= 0 ? "+" : "") + changePct.toFixed(2) + "%");
        
        if (changePct >= 0) {
            changeEl.attr("class", "w-change bullish");
        } else {
            changeEl.attr("class", "w-change bearish");
        }
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
            if (chartDataCache.length > 200) {
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

    // 2. Radial Progress Circle & Score
    $("#scoreValue").text(score);
    
    // stroke-dasharray maps to circumference (2 * pi * r = 2 * 3.1415 * 15.9155 = 100)
    if (scoreCircle.length) scoreCircle.css("stroke-dasharray", `${score}, 100`);

    // 3. Info labels
    $("#signalStrength").text(strength);
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
    let confidenceColor = "var(--text-secondary)";
    if (type === "HOLD") {
        confidenceText = `Low (Hold) (${score}%)`;
        confidenceColor = "var(--text-muted)";
    } else {
        if (score >= 90) {
            confidenceText = `Very High (${score}%)`;
            confidenceColor = "var(--accent-green)";
        } else if (score >= 70) {
            confidenceText = `High (${score}%)`;
            confidenceColor = "var(--accent-green)";
        } else if (score >= 50) {
            confidenceText = `Moderate (${score}%)`;
            confidenceColor = "var(--accent-amber)";
        } else {
            confidenceText = `Low (${score}%)`;
            confidenceColor = "var(--accent-red)";
        }
    }
    const confidenceEl = $("#signalConfidence");
    if (confidenceEl.length) {
        confidenceEl.text(confidenceText).css("color", confidenceColor);
    }

    $("#signalReasoning").text(reason);

    // 4. Quick indicator widgets
    // LTP
    if (priceVal) {
        $("#widgetLTP").text(parseFloat(priceVal).toFixed(2));
        if (openVal) {
            const changePct = ((priceVal - openVal) / openVal) * 100;
            const changeEl = $("#widgetChange");
            if (changeEl.length) {
                changeEl.text((changePct >= 0 ? "+" : "") + changePct.toFixed(2) + "%");
                changeEl.attr("class", `w-change ${changePct >= 0 ? "bullish" : "bearish"}`);
            }
        }
    }

    // RSI
    const rsi = data.rsi !== undefined ? data.rsi : data.RSI;
    if (rsi !== null && rsi !== undefined) {
        $("#widgetRSI").text(parseFloat(rsi).toFixed(2));
        const rsiEl = $("#widgetRsiStatus");
        if (rsiEl.length) {
            if (rsi > 60) {
                rsiEl.text("Overbought").attr("class", "w-status bearish");
            } else if (rsi < 40) {
                rsiEl.text("Oversold").attr("class", "w-status bullish");
            } else {
                rsiEl.text("Neutral").attr("class", "w-status neutral");
            }
        }
    } else {
        $("#widgetRSI").text("-");
        $("#widgetRsiStatus").text("-").attr("class", "w-status neutral");
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

// Global, responsive hover tooltip handler for elements with data-tooltip
$(document).ready(function () {
    const tooltip = $('<div id="global-tooltip" class="global-tooltip"></div>').appendTo('body');

    $(document).on('mouseenter', '[data-tooltip]', function (e) {
        const target = $(this);
        const text = target.attr('data-tooltip');
        if (!text) return;

        tooltip.text(text);
        tooltip.addClass('visible');

        // Position calculations
        const targetRect = this.getBoundingClientRect();
        
        // Temporarily set position to get actual dimensions
        tooltip.css({ top: '0px', left: '0px' });
        const tooltipWidth = tooltip.outerWidth();
        const tooltipHeight = tooltip.outerHeight();

        // Default position: Centered above the hovered element
        let top = window.scrollY + targetRect.top - tooltipHeight - 8;
        let left = window.scrollX + targetRect.left + (targetRect.width / 2) - (tooltipWidth / 2);

        // Adjust if overflowing the left side of the window
        if (left < 10) {
            left = 10;
        }
        // Adjust if overflowing the right side of the window
        else if (left + tooltipWidth > window.innerWidth - 10) {
            left = window.innerWidth - tooltipWidth - 10;
        }

        // Adjust if overflowing the top of the window (display below target instead)
        if (targetRect.top - tooltipHeight - 8 < 10) {
            top = window.scrollY + targetRect.bottom + 8;
        }

        tooltip.css({
            top: `${top}px`,
            left: `${left}px`
        });
    });

    $(document).on('mouseleave', '[data-tooltip]', function () {
        tooltip.removeClass('visible');
    });

    // Hide tooltip on scroll or window resize to prevent floating artifacts
    $(window).on('scroll resize', function () {
        tooltip.removeClass('visible');
    });

    // Chart Line Highlighting on Indicator Tag Hover
    $(document).on('mouseenter', '.indicator-tag.ema20', function () {
        if (ema20Series) ema20Series.applyOptions({ lineWidth: 3.5 });
    }).on('mouseleave', '.indicator-tag.ema20', function () {
        if (ema20Series) ema20Series.applyOptions({ lineWidth: 1.5 });
    });

    $(document).on('mouseenter', '.indicator-tag.ema50', function () {
        if (ema50Series) ema50Series.applyOptions({ lineWidth: 3.5 });
    }).on('mouseleave', '.indicator-tag.ema50', function () {
        if (ema50Series) ema50Series.applyOptions({ lineWidth: 1.5 });
    });

    $(document).on('mouseenter', '.indicator-tag.vwap', function () {
        if (vwapSeries) vwapSeries.applyOptions({ lineWidth: 3.5 });
    }).on('mouseleave', '.indicator-tag.vwap', function () {
        if (vwapSeries) vwapSeries.applyOptions({ lineWidth: 1.2 });
    });
});

