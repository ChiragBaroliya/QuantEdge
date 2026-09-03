/**
 * QuantEdge Auto Real Trading (Live Broker) JavaScript Module
 */

let apiBaseUrl = "";
let currentUserId = 1;
let currentUserName = "Chirag";
let countdownInterval = null;
let modalSquareOff = null;
let modalKillSwitch = null;

// Smart Polling Manager
let pollingTimer = null;
let pollingIntervalMs = 5000; // 5s ultra-fast live polling by default
let isPollingInFlight = false;

document.addEventListener("DOMContentLoaded", function () {
    const configElem = document.getElementById("realtrade-config");
    if (configElem) {
        apiBaseUrl = configElem.dataset.apiBaseUrl || "";
        currentUserId = parseInt(configElem.dataset.userId || "1", 10);
        currentUserName = configElem.dataset.userName || "Chirag";
    }

    const sqModalEl = document.getElementById('modalSquareOffPosition');
    if (sqModalEl && typeof bootstrap !== 'undefined') {
        modalSquareOff = new bootstrap.Modal(sqModalEl);
    }

    const killModalEl = document.getElementById('modalEmergencyKillSwitch');
    if (killModalEl && typeof bootstrap !== 'undefined') {
        modalKillSwitch = new bootstrap.Modal(killModalEl);
    }

    // Check URL parameters for OAuth return
    const urlParams = new URLSearchParams(window.location.search);
    if (urlParams.get("connected") === "true") {
        const msg = urlParams.get("message") || "⚡ Zerodha Account Connected Successfully!";
        showToastAlert(msg, "success");
        window.history.replaceState({}, document.title, window.location.pathname);
    } else if (urlParams.get("connected") === "false") {
        const msg = urlParams.get("message") || "Failed to connect Zerodha account.";
        showToastAlert(msg, "danger");
        window.history.replaceState({}, document.title, window.location.pathname);
    }

    renderDdpiStatus();
    loadDashboardData();
    setupEventListeners();
    setupSignalRHub();
    startSmartPolling();

    // Page Visibility listener: Pause when tab is minimized, instant refresh when focused
    document.addEventListener("visibilitychange", function () {
        if (document.hidden) {
            stopSmartPolling();
            updateLiveSyncBadge(false, "Tab Inactive");
        } else {
            loadLivePositionsFast();
            startSmartPolling();
        }
    });
});

function startSmartPolling() {
    stopSmartPolling();
    if (pollingIntervalMs <= 0) {
        updateLiveSyncBadge(false, "Paused");
        return;
    }

    updateLiveSyncBadge(true, `Live (${pollingIntervalMs / 1000}s)`);
    pollingTimer = setInterval(() => {
        if (!document.hidden && !isPollingInFlight) {
            loadLivePositionsFast();
        }
    }, pollingIntervalMs);
}

function stopSmartPolling() {
    if (pollingTimer) {
        clearInterval(pollingTimer);
        pollingTimer = null;
    }
}

function updateLiveSyncBadge(isActive, text) {
    const indicator = document.getElementById("liveSyncIndicator");
    const label = document.getElementById("liveSyncText");
    if (indicator && label) {
        if (isActive) {
            indicator.classList.remove("paused");
        } else {
            indicator.classList.add("paused");
        }
        label.innerText = text;
    }
}

async function loadLivePositionsFast() {
    if (isPollingInFlight) return;
    isPollingInFlight = true;

    try {
        const response = await fetch(`${apiBaseUrl}/api/realtrade/live-positions?userId=${currentUserId}`);
        if (!response.ok) return;

        const data = await response.json();
        if (data && data.success) {
            updateFastPositionsUI(data);
        }
    } catch (err) {
        console.debug("Fast positions poll skip:", err);
    } finally {
        isPollingInFlight = false;
    }
}

function updateFastPositionsUI(data) {
    // 1. Margin & P&L Cards
    const availMarginEl = document.getElementById("stat-available-margin");
    if (availMarginEl) availMarginEl.innerText = formatCurrency(data.availableBrokerMargin);

    const usedMarginEl = document.getElementById("stat-used-margin");
    if (usedMarginEl) usedMarginEl.innerText = formatCurrency(data.usedBrokerMargin);

    const unrealPnlEl = document.getElementById("stat-unrealized-pnl");
    if (unrealPnlEl) {
        const unPnl = data.totalUnrealizedPnl || 0;
        const usedMargin = data.usedBrokerMargin || 0;
        const unPct = usedMargin > 0 ? (unPnl / usedMargin) * 100 : 0;
        const unPctSign = unPct > 0 ? "+" : "";
        const unPctStr = `${unPctSign}${unPct.toFixed(2)}%`;
        unrealPnlEl.innerText = `${formatCurrencyWithSign(unPnl)} (${unPctStr})`;
        unrealPnlEl.style.color = unPnl >= 0 ? "#34d399" : "#f87171";
    }

    const zerodhaM2mEl = document.getElementById("stat-zerodha-m2m");
    if (zerodhaM2mEl) {
        zerodhaM2mEl.innerText = formatCurrencyWithSign(data.zerodhaTotalM2M);
        zerodhaM2mEl.style.color = data.zerodhaTotalM2M >= 0 ? "#34d399" : "#f87171";
    }

    const zerodhaRealPnlEl = document.getElementById("stat-zerodha-realized-pnl");
    if (zerodhaRealPnlEl) {
        zerodhaRealPnlEl.innerText = formatCurrencyWithSign(data.zerodhaRealizedPnl);
        zerodhaRealPnlEl.style.color = data.zerodhaRealizedPnl >= 0 ? "#34d399" : "#f87171";
    }

    const zerodhaUnrealPnlEl = document.getElementById("stat-zerodha-unrealized-pnl");
    if (zerodhaUnrealPnlEl) {
        zerodhaUnrealPnlEl.innerText = formatCurrencyWithSign(data.zerodhaUnrealizedPnl);
        zerodhaUnrealPnlEl.style.color = data.zerodhaUnrealizedPnl >= 0 ? "#34d399" : "#f87171";
    }

    const statOpenCount = document.getElementById("stat-open-count");
    if (statOpenCount) statOpenCount.innerText = (data.openPositions || []).length;

    const badgeBotCount = document.getElementById("badge-bot-count");
    if (badgeBotCount) badgeBotCount.innerText = (data.openPositions || []).length;

    // 2. Refresh Tables
    renderOpenPositions(data.openPositions || []);
    renderZerodhaPositions(data.brokerPositions);
    if (data.brokerHoldings && data.brokerHoldings.length > 0) {
        renderZerodhaHoldings(data.brokerHoldings);
    }
}

async function loadDashboardData() {
    try {
        const response = await fetch(`${apiBaseUrl}/api/realtrade/dashboard?userId=${currentUserId}`);
        if (!response.ok) return;

        const data = await response.json();
        updateDashboardUI(data);
    } catch (err) {
        console.error("Failed to load Real Trade Dashboard data:", err);
    }
}

function updateDashboardUI(data) {
    if (!data) return;

    // 0. Prerequisite Banners Control
    const bannerTokenMissing = document.getElementById("banner-token-missing");
    if (bannerTokenMissing) {
        bannerTokenMissing.style.display = data.isBrokerTokenActive ? "none" : "flex";
    }

    const bannerMarketLock = document.getElementById("banner-market-locked");
    if (bannerMarketLock) {
        bannerMarketLock.style.display = data.isMarketOpen ? "flex" : "none";
    }

    // Lock/Unlock Settings Form based on Market Hours
    const btnSave = document.getElementById("btnSaveSettings");
    if (btnSave) {
        if (data.isMarketOpen) {
            btnSave.disabled = true;
            btnSave.classList.add("disabled");
            btnSave.title = "🔒 Settings are locked during market hours (09:15 AM - 03:30 PM IST)";
            btnSave.innerHTML = "🔒 Settings Locked During Market Hours";
        } else {
            btnSave.disabled = false;
            btnSave.classList.remove("disabled");
            btnSave.title = "Save Risk & Strategy Settings";
            btnSave.innerHTML = "💾 Save Risk & Strategy Settings";
        }
    }

    // 1. Status Badges & Counters
    const statusTextEl = document.getElementById("sys-status-text");
    if (statusTextEl) {
        statusTextEl.innerText = data.systemStatus || "IDLE";
        if (data.systemStatus === "LIVE_ACTIVE") {
            statusTextEl.style.color = "#34d399";
        } else if (data.systemStatus === "TOKEN_EXPIRED") {
            statusTextEl.style.color = "#ef4444";
        } else {
            statusTextEl.style.color = "#f59e0b";
        }
    }

    // Client ID Tag in Broker Metrics Header
    const clientTag = document.getElementById("brokerClientIdTag");
    if (clientTag) {
        if (data.clientId) {
            clientTag.innerText = `Client: ${data.clientId}${data.accountHolderName ? ' (' + data.accountHolderName + ')' : ''}`;
            clientTag.style.display = "inline-block";
        } else {
            clientTag.style.display = "none";
        }
    }

    // Token Badge & Connect Button
    const tokenBadge = document.getElementById("broker-token-badge");
    const btnConnectHeader = document.getElementById("btnConnectZerodhaHeader");
    if (tokenBadge) {
        if (data.isBrokerTokenActive) {
            tokenBadge.className = "badge-token active";
            const clientText = data.clientId ? `${data.clientId} • ` : "";
            tokenBadge.innerHTML = `<span class="token-dot"></span> Zerodha: Active (${clientText}${data.brokerTokenCreatedIst || 'Today'})`;
            if (btnConnectHeader) {
                btnConnectHeader.style.display = "none";
            }
        } else {
            tokenBadge.className = "badge-token expired";
            tokenBadge.innerHTML = `<span class="token-dot"></span> Zerodha: Disconnected / Expired`;
            if (btnConnectHeader) {
                btnConnectHeader.style.display = "inline-flex";
            }
        }
    }

    // Trade Counter Badge
    const counterBadge = document.getElementById("trade-counter-badge");
    if (counterBadge) {
        counterBadge.innerText = `${data.todayTradeCount} / ${data.maxTradesPerDay} trades used today`;
    }

    // Master Switch Toggle
    const chkToggle = document.getElementById("chkRealTradeToggle");
    if (chkToggle && data.settings) {
        chkToggle.checked = data.settings.isRealTradeEnabled;
    }

    // 2. Metrics Cards
    const capitalEl = document.getElementById("stat-capital");
    if (capitalEl && data.settings) {
        capitalEl.innerText = formatCurrency(data.settings.availableCapital);
    }

    const availMarginEl = document.getElementById("stat-available-margin");
    if (availMarginEl) {
        availMarginEl.innerText = formatCurrency(data.availableBrokerMargin);
    }

    const usedMarginEl = document.getElementById("stat-used-margin");
    if (usedMarginEl) {
        usedMarginEl.innerText = formatCurrency(data.usedBrokerMargin);
    }

    const openCountEl = document.getElementById("stat-open-count");
    if (openCountEl) {
        openCountEl.innerText = (data.openPositions || []).length;
    }

    const badgeBotCount = document.getElementById("badge-bot-count");
    if (badgeBotCount) {
        badgeBotCount.innerText = (data.openPositions || []).length;
    }

    const unrealPnlEl = document.getElementById("stat-unrealized-pnl");
    if (unrealPnlEl) {
        const unPnl = data.totalUnrealizedPnl || 0;
        const usedMargin = data.usedBrokerMargin || 0;
        const baseCap = usedMargin > 0 ? usedMargin : (data.settings?.availableCapital || 0);
        const unPct = baseCap > 0 ? (unPnl / baseCap) * 100 : 0;
        const unPctSign = unPct > 0 ? "+" : "";
        const unPctStr = `${unPctSign}${unPct.toFixed(2)}%`;
        unrealPnlEl.innerText = `${formatCurrencyWithSign(unPnl)} (${unPctStr})`;
        unrealPnlEl.style.color = data.totalUnrealizedPnl >= 0 ? "#34d399" : "#f87171";
    }

    // Zerodha Live Broker Metrics
    const zerodhaM2mEl = document.getElementById("stat-zerodha-m2m");
    if (zerodhaM2mEl) {
        zerodhaM2mEl.innerText = formatCurrencyWithSign(data.zerodhaTotalM2M);
        zerodhaM2mEl.style.color = data.zerodhaTotalM2M >= 0 ? "#34d399" : "#f87171";
    }

    const zerodhaRealPnlEl = document.getElementById("stat-zerodha-realized-pnl");
    if (zerodhaRealPnlEl) {
        zerodhaRealPnlEl.innerText = formatCurrencyWithSign(data.zerodhaRealizedPnl);
        zerodhaRealPnlEl.style.color = data.zerodhaRealizedPnl >= 0 ? "#34d399" : "#f87171";
    }

    const todayCountEl = document.getElementById("stat-today-trade-count");
    if (todayCountEl) {
        todayCountEl.innerText = `${data.todayTradeCount} / ${data.maxTradesPerDay} Trades`;
    }

    const todayUsedAmtEl = document.getElementById("stat-today-used-amount");
    if (todayUsedAmtEl) {
        todayUsedAmtEl.innerText = formatCurrency(data.todayTradeAmount);
    }

    const todayRealPnlEl = document.getElementById("stat-realized-pnl");
    if (todayRealPnlEl) {
        const todayAmt = data.todayTradeAmount || (data.settings?.fixedAmountPerTrade ? data.todayTradeCount * data.settings.fixedAmountPerTrade : 0);
        const realPnl = data.totalRealizedPnlToday || 0;
        const baseCap = todayAmt > 0 ? todayAmt : (data.settings?.availableCapital || 0);
        const pnlPct = baseCap > 0 ? (realPnl / baseCap) * 100 : 0;
        const pctSign = pnlPct > 0 ? "+" : "";
        const pctStr = `${pctSign}${pnlPct.toFixed(2)}%`;
        todayRealPnlEl.innerText = `${formatCurrencyWithSign(realPnl)} (${pctStr})`;
        todayRealPnlEl.style.color = data.totalRealizedPnlToday >= 0 ? "#34d399" : "#f87171";
    }

    // 3. Populate Settings Form
    if (data.settings) {
        populateSettingsForm(data.settings);
    }

    // 4. Populate Open Real Positions (Bot DB)
    renderOpenPositions(data.openPositions || []);

    // 4b. Populate Zerodha Live Broker Positions & Holdings
    renderZerodhaPositions(data.brokerPositions);
    renderZerodhaHoldings(data.brokerHoldings);

    // 5. Populate Recent Orders
    renderRecentOrders(data.recentOrders || []);

    // 6. Populate Today's Logs
    if (data.todayLogs && data.todayLogs.length > 0) {
        renderLogs(data.todayLogs);
    }

    // 7. Next Scan Countdown
    startNextScanCountdownTimer(data.nextRunTime, data.nextRunFormatted, data.isMarketOpen);
}

function populateSettingsForm(s) {
    const setVal = (id, val) => {
        const el = document.getElementById(id);
        if (el && val !== undefined && val !== null) el.value = val;
    };

    setVal("inpAvailableCapital", s.availableCapital);
    setVal("inpFixedAmount", s.fixedAmountPerTrade);
    setVal("inpProfitTarget", s.profitTargetPct);
    setVal("inpMaxTrades", s.maxTradesPerDay);
    setVal("inpMaxDuration", s.maxDurationDays);
    setVal("selProductType", s.productType || "CNC");
    setVal("inpMinConditions", s.minConditionsMatch);
    setVal("inpWindowStart", s.tradingWindowStart);
    setVal("inpWindowEnd", s.tradingWindowEnd);

    // Optional Stop Loss
    const chkSL = document.getElementById("chkEnableStopLoss");
    const slWrapper = document.getElementById("slInputWrapper");
    const inpSL = document.getElementById("inpStopLoss");
    if (chkSL && slWrapper && inpSL) {
        const hasSL = s.stopLossPct !== null && s.stopLossPct !== undefined && s.stopLossPct > 0;
        chkSL.checked = hasSL;
        slWrapper.style.display = hasSL ? "block" : "none";
        if (hasSL) inpSL.value = s.stopLossPct;
    }

    // Optional Trailing SL
    const chkTSL = document.getElementById("chkEnableTrailingSl");
    const tslWrapper = document.getElementById("tslInputWrapper");
    const inpTSL = document.getElementById("inpTrailingSl");
    if (chkTSL && tslWrapper && inpTSL) {
        chkTSL.checked = s.trailingSlEnabled === true;
        tslWrapper.style.display = s.trailingSlEnabled ? "block" : "none";
        if (s.trailingSlPct) inpTSL.value = s.trailingSlPct;
    }

    // Optional Daily Loss Limit
    const chkLoss = document.getElementById("chkEnableDailyLossLimit");
    const lossWrapper = document.getElementById("dailyLossInputWrapper");
    const inpLoss = document.getElementById("inpMaxDailyLoss");
    if (chkLoss && lossWrapper && inpLoss) {
        const hasLoss = s.maxDailyLossLimit !== null && s.maxDailyLossLimit !== undefined && s.maxDailyLossLimit > 0;
        chkLoss.checked = hasLoss;
        lossWrapper.style.display = hasLoss ? "block" : "none";
        if (hasLoss) inpLoss.value = s.maxDailyLossLimit;
    }
}

let cachedOpenPositions = [];
let cachedRecentOrders = [];
let cachedTodayLogs = [];

function applyPositionsFilter() {
    const searchSymbol = (document.getElementById("inpSearchPositions")?.value || "").trim().toUpperCase();
    const filterSide = document.getElementById("selFilterPosSide")?.value || "";
    const filterPnl = document.getElementById("selFilterPosPnl")?.value || "";

    const filtered = cachedOpenPositions.filter(p => {
        if (searchSymbol && !p.symbol.toUpperCase().includes(searchSymbol)) return false;
        if (filterSide === "BUY" && p.side !== 0) return false;
        if (filterSide === "SELL" && p.side !== 1) return false;
        if (filterPnl === "PROFIT" && (p.unrealizedPnl || 0) < 0) return false;
        if (filterPnl === "LOSS" && (p.unrealizedPnl || 0) >= 0) return false;
        return true;
    });

    renderFilteredOpenPositions(filtered);
}

function applyOrdersFilter() {
    const searchTerm = (document.getElementById("inpSearchOrders")?.value || "").trim().toUpperCase();
    const filterStatus = document.getElementById("selFilterOrderStatus")?.value || "";
    const filterSide = document.getElementById("selFilterOrderSide")?.value || "";

    const filtered = cachedRecentOrders.filter(o => {
        if (searchTerm) {
            const symMatch = o.symbol && o.symbol.toUpperCase().includes(searchTerm);
            const orderIdMatch = o.brokerOrderId && o.brokerOrderId.toUpperCase().includes(searchTerm);
            if (!symMatch && !orderIdMatch) return false;
        }
        if (filterSide === "BUY" && o.side !== 0) return false;
        if (filterSide === "SELL" && o.side !== 1) return false;

        if (filterStatus === "FILLED" && o.status !== 1) return false;
        if (filterStatus === "CANCELLED" && o.status !== 2) return false;
        if (filterStatus === "REJECTED" && o.status !== 3) return false;
        if (filterStatus === "PENDING" && o.status !== 0) return false;

        return true;
    });

    renderFilteredRecentOrders(filtered);
}

function renderOpenPositions(positions) {
    cachedOpenPositions = positions || [];
    applyPositionsFilter();
}

function renderFilteredOpenPositions(positions) {
    const tbody = document.getElementById("openPositionsTableBody");
    if (!tbody) return;

    if (!positions || positions.length === 0) {
        tbody.innerHTML = `<tr><td colspan="10" class="text-center py-4 text-light" style="color: #cbd5e1 !important;">No open real positions matching filters.</td></tr>`;
        return;
    }

    let html = "";
    positions.forEach(p => {
        const pnl = p.unrealizedPnl || 0;
        const pnlClass = pnl >= 0 ? "text-success fw-bold" : "text-danger fw-bold";
        const entryVal = p.quantity * p.averageEntryPrice;
        const pnlPct = entryVal > 0 ? (pnl / entryVal * 100).toFixed(2) : '0.00';
        const pnlPctSign = pnl > 0 ? '+' : '';
        const sideText = p.side === 0 ? '<span class="text-success fw-bold">BUY</span>' : '<span class="text-danger fw-bold">SELL</span>';
        const targetText = p.takeProfit ? `₹${p.takeProfit.toFixed(2)}` : '-';
        const slText = p.stopLoss ? `₹${p.stopLoss.toFixed(2)}` : '-';
        const tslText = p.trailingStopLoss ? `₹${p.trailingStopLoss.toFixed(2)}` : '-';

        html += `
            <tr>
                <td><strong class="text-white">${p.symbol}</strong></td>
                <td>${sideText}</td>
                <td class="text-white">${p.quantity}</td>
                <td class="text-white">₹${p.averageEntryPrice.toFixed(2)}</td>
                <td><strong class="text-white">₹${(p.currentPrice || p.averageEntryPrice).toFixed(2)}</strong></td>
                <td class="text-info fw-semibold">${targetText}</td>
                <td class="text-white">${slText}</td>
                <td class="text-white">${tslText}</td>
                <td class="${pnlClass}">${formatCurrencyWithSign(pnl)} (${pnlPctSign}${pnlPct}%)</td>
                <td>
                    <button class="btn-square-off" onclick="openSquareOffModal(${p.id}, '${p.symbol}')" title="Square off this real position">
                        Exit / Sell
                    </button>
                </td>
            </tr>
        `;
    });

    tbody.innerHTML = html;
}

function renderZerodhaPositions(brokerPositions) {
    const tbody = document.getElementById("zerodhaPositionsTableBody");
    const badgeZerodhaCount = document.getElementById("badge-zerodha-count");
    if (!tbody) return;

    const netPositions = (brokerPositions && brokerPositions.net) ? brokerPositions.net : [];
    if (badgeZerodhaCount) {
        badgeZerodhaCount.innerText = netPositions.length;
    }

    if (!brokerPositions || netPositions.length === 0) {
        tbody.innerHTML = `<tr><td colspan="10" class="text-center py-4 text-light" style="color: #cbd5e1 !important;">No live open positions in Zerodha account.</td></tr>`;
        return;
    }

    let html = "";
    netPositions.forEach(p => {
        const pnl = p.pnl || 0;
        const m2m = p.m2m || 0;
        const pnlClass = pnl >= 0 ? "text-success fw-bold" : "text-danger fw-bold";
        const m2mClass = m2m >= 0 ? "text-success fw-bold" : "text-danger fw-bold";
        const buyPrice = p.buyPrice > 0 ? `₹${p.buyPrice.toFixed(2)}` : "-";
        const sellPrice = p.sellPrice > 0 ? `₹${p.sellPrice.toFixed(2)}` : "-";
        const ltp = p.lastPrice > 0 ? `₹${p.lastPrice.toFixed(2)}` : "-";
        const prodBadge = p.product === "MIS" 
            ? '<span class="badge bg-warning text-dark">MIS (Intraday)</span>' 
            : '<span class="badge bg-info text-dark">CNC (Delivery)</span>';

        html += `
            <tr>
                <td><strong class="text-white">${p.tradingSymbol}</strong> <small style="color: #cbd5e1;">(${p.exchange})</small></td>
                <td>${prodBadge}</td>
                <td><strong class="text-white">${p.quantity}</strong></td>
                <td class="text-white">${buyPrice}</td>
                <td class="text-white">${sellPrice}</td>
                <td><strong class="text-white">${ltp}</strong></td>
                <td class="${m2mClass}">${formatCurrencyWithSign(m2m)}</td>
                <td class="text-white">${formatCurrencyWithSign(p.unrealised)}</td>
                <td class="text-white">${formatCurrencyWithSign(p.realised)}</td>
                <td class="${pnlClass}">${formatCurrencyWithSign(pnl)}</td>
            </tr>
        `;
    });

    tbody.innerHTML = html;
}

function renderZerodhaHoldings(holdings) {
    const tbody = document.getElementById("zerodhaHoldingsTableBody");
    const badgeHoldingsCount = document.getElementById("badge-holdings-count");
    if (!tbody) return;

    const holdingsList = holdings || [];
    if (badgeHoldingsCount) {
        badgeHoldingsCount.innerText = holdingsList.length;
    }

    if (holdingsList.length === 0) {
        tbody.innerHTML = `<tr><td colspan="8" class="text-center py-4 text-light" style="color: #cbd5e1 !important;">No demat equity holdings found in Zerodha.</td></tr>`;
        return;
    }

    let html = "";
    holdingsList.forEach(h => {
        const pnl = h.pnl || 0;
        const pnlClass = pnl >= 0 ? "text-success fw-bold" : "text-danger fw-bold";
        const dayChangeClass = (h.dayChange || 0) >= 0 ? "text-success" : "text-danger";
        const invested = (h.averagePrice * h.quantity) || 0;
        const currVal = h.value > 0 ? h.value : (h.lastPrice * h.quantity);

        html += `
            <tr>
                <td><strong class="text-white">${h.tradingSymbol}</strong> <small style="color: #cbd5e1;">(${h.exchange})</small></td>
                <td><strong class="text-white">${h.quantity}</strong></td>
                <td class="text-white">₹${h.averagePrice.toFixed(2)}</td>
                <td><strong class="text-white">₹${h.lastPrice.toFixed(2)}</strong></td>
                <td class="text-white">₹${invested.toFixed(2)}</td>
                <td class="text-white fw-bold">₹${currVal.toFixed(2)}</td>
                <td class="${dayChangeClass}">${formatCurrencyWithSign(h.dayChange)} (${h.dayChangePercentage.toFixed(2)}%)</td>
                <td class="${pnlClass}">${formatCurrencyWithSign(pnl)}</td>
            </tr>
        `;
    });

    tbody.innerHTML = html;
}

function renderRecentOrders(orders) {
    cachedRecentOrders = orders || [];
    applyOrdersFilter();
}

function renderFilteredRecentOrders(orders) {
    const tbody = document.getElementById("recentOrdersTableBody");
    if (!tbody) return;

    if (!orders || orders.length === 0) {
        tbody.innerHTML = `<tr><td colspan="8" class="text-center py-4 text-light" style="color: #cbd5e1 !important;">No real orders matching filters.</td></tr>`;
        return;
    }

    let html = "";
    orders.forEach(o => {
        const timeStr = new Date(o.createdAt).toLocaleTimeString('en-IN', { hour: '2-digit', minute: '2-digit', second: '2-digit' });
        const sideText = o.side === 0 ? '<span class="text-success fw-bold">BUY</span>' : '<span class="text-danger fw-bold">SELL</span>';

        let statusBadge = '<span class="status-badge-pending">PENDING</span>';
        if (o.status === 1) statusBadge = '<span class="status-badge-filled">FILLED</span>';
        else if (o.status === 2) statusBadge = '<span class="status-badge-rejected">CANCELLED</span>';
        else if (o.status === 3) statusBadge = '<span class="status-badge-rejected">REJECTED</span>';

        html += `
            <tr>
                <td class="text-white">${timeStr}</td>
                <td><code>${o.brokerOrderId || 'N/A'}</code></td>
                <td><strong class="text-white">${o.symbol}</strong></td>
                <td>${sideText}</td>
                <td class="text-white">${o.quantity}</td>
                <td class="text-white">₹${(o.filledPrice || o.price).toFixed(2)}</td>
                <td>${statusBadge}</td>
                <td class="small" style="color: #cbd5e1 !important;">${o.remarks || o.rejectionReason || '-'}</td>
            </tr>
        `;
    });

    tbody.innerHTML = html;
}

function renderLogs(logs) {
    cachedTodayLogs = logs || [];
    const container = document.getElementById("consoleLogContainer");
    if (!container) return;

    const filterText = (document.getElementById("inpSearchLogs")?.value || "").trim().toUpperCase();

    container.innerHTML = "";
    logs.slice(0, 50).forEach(log => {
        if (filterText) {
            const symMatch = log.symbol && log.symbol.toUpperCase().includes(filterText);
            const actMatch = log.actionType && log.actionType.toUpperCase().includes(filterText);
            const reasonMatch = log.reason && log.reason.toUpperCase().includes(filterText);
            if (!symMatch && !actMatch && !reasonMatch) return;
        }
        appendLogEntry(log);
    });
}

function appendLogEntry(log) {
    if (!log) return;
    const container = document.getElementById("consoleLogContainer");
    if (!container) return;

    // Filter strictly by current active user (prevent displaying other users' logs)
    if (log.userId && currentUserId && log.userId !== currentUserId) {
        return;
    }

    const timeStr = new Date(log.executedAt).toLocaleTimeString('en-IN', { hour: '2-digit', minute: '2-digit', second: '2-digit' });
    const div = document.createElement("div");
    div.className = `log-entry ${log.actionType.toLowerCase()}`;

    let badgeClass = "badge bg-secondary";
    if (log.actionType.includes("BUY")) badgeClass = "badge bg-success";
    else if (log.actionType.includes("SELL")) badgeClass = "badge bg-danger";
    else if (log.actionType.includes("KILL") || log.actionType.includes("CIRCUIT")) badgeClass = "badge bg-warning text-dark";

    // Format display tag: replace SYSTEM / ALL / default with active user's name
    let displayTag = (log.symbol || "").trim();
    if (!displayTag || displayTag === "SYSTEM" || displayTag === "ALL" || displayTag === "DEFAULT" || displayTag === "ZERODHA") {
        displayTag = (currentUserName || "CHIRAG").toUpperCase();
    }

    div.innerHTML = `
        <span class="log-time">[${timeStr}]</span>
        <span class="${badgeClass} me-2" style="font-size: 0.7rem;">${log.actionType}</span>
        <span class="fw-bold me-2">${displayTag}:</span>
        <span class="log-msg">${log.reason || ''}</span>
    `;

    container.prepend(div);
}

function setupEventListeners() {
    // Advance Search: Positions Filter
    document.getElementById("inpSearchPositions")?.addEventListener("input", applyPositionsFilter);
    document.getElementById("selFilterPosSide")?.addEventListener("change", applyPositionsFilter);
    document.getElementById("selFilterPosPnl")?.addEventListener("change", applyPositionsFilter);

    // Advance Search: Orders Filter
    document.getElementById("inpSearchOrders")?.addEventListener("input", applyOrdersFilter);
    document.getElementById("selFilterOrderStatus")?.addEventListener("change", applyOrdersFilter);
    document.getElementById("selFilterOrderSide")?.addEventListener("change", applyOrdersFilter);

    // Advance Search: Logs Filter
    document.getElementById("inpSearchLogs")?.addEventListener("input", () => renderLogs(cachedTodayLogs));

    // Optional SL toggle
    const chkSL = document.getElementById("chkEnableStopLoss");
    const slWrapper = document.getElementById("slInputWrapper");
    if (chkSL && slWrapper) {
        chkSL.addEventListener("change", function () {
            slWrapper.style.display = this.checked ? "block" : "none";
        });
    }

    // Optional Trailing SL toggle
    const chkTSL = document.getElementById("chkEnableTrailingSl");
    const tslWrapper = document.getElementById("tslInputWrapper");
    if (chkTSL && tslWrapper) {
        chkTSL.addEventListener("change", function () {
            tslWrapper.style.display = this.checked ? "block" : "none";
        });
    }

    // Optional Daily Loss toggle
    const chkLoss = document.getElementById("chkEnableDailyLossLimit");
    const lossWrapper = document.getElementById("dailyLossInputWrapper");
    if (chkLoss && lossWrapper) {
        chkLoss.addEventListener("change", function () {
            lossWrapper.style.display = this.checked ? "block" : "none";
        });
    }

    // Connect Zerodha Button Handlers
    const handleConnectZerodha = async function () {
        try {
            const returnUrl = `${window.location.origin}/RealTrading`;
            const res = await fetch(`${apiBaseUrl}/api/zerodha/login-url?returnUrl=${encodeURIComponent(returnUrl)}&userId=${currentUserId}`);
            if (res.ok) {
                const data = await res.json();
                if (data.loginUrl) {
                    window.location.href = data.loginUrl;
                    return;
                }
            }
            alert("Failed to retrieve Zerodha login URL from server.");
        } catch (e) {
            console.error("Connect Zerodha error:", e);
            alert("Error initiating Zerodha connection.");
        }
    };

    const btnHeaderConnect = document.getElementById("btnConnectZerodhaHeader");
    if (btnHeaderConnect) {
        btnHeaderConnect.addEventListener("click", handleConnectZerodha);
    }

    const btnBannerConnect = document.getElementById("btnBannerConnectZerodha");
    if (btnBannerConnect) {
        btnBannerConnect.addEventListener("click", handleConnectZerodha);
    }

    // Master Real Trade Switch Toggle
    const chkToggle = document.getElementById("chkRealTradeToggle");
    if (chkToggle) {
        chkToggle.addEventListener("change", async function () {
            const enabled = this.checked;
            try {
                const response = await fetch(`${apiBaseUrl}/api/realtrade/toggle`, {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify({ enabled, userId: currentUserId })
                });

                const resData = await response.json();
                if (!response.ok) {
                    alert(resData.message || "⚠️ Zerodha Account Not Connected: Please click 'Connect Zerodha' before enabling Auto Real Trading.");
                    chkToggle.checked = !enabled;
                } else {
                    loadDashboardData();
                }
            } catch (err) {
                console.error("Toggle error:", err);
                chkToggle.checked = !enabled;
            }
        });
    }

    // Save Settings Form
    const frmSettings = document.getElementById("frmRealTradeSettings");
    if (frmSettings) {
        frmSettings.addEventListener("submit", async function (e) {
            e.preventDefault();
            const btn = document.getElementById("btnSaveSettings");
            if (btn) btn.disabled = true;

            const chkSL = document.getElementById("chkEnableStopLoss");
            const inpSL = document.getElementById("inpStopLoss");
            const stopLossVal = (chkSL && chkSL.checked && inpSL && inpSL.value) ? parseFloat(inpSL.value) : null;

            const chkTSL = document.getElementById("chkEnableTrailingSl");
            const inpTSL = document.getElementById("inpTrailingSl");
            const trailingSlEnabled = chkTSL ? chkTSL.checked : false;
            const trailingSlVal = (trailingSlEnabled && inpTSL && inpTSL.value) ? parseFloat(inpTSL.value) : null;

            const chkLoss = document.getElementById("chkEnableDailyLossLimit");
            const inpLoss = document.getElementById("inpMaxDailyLoss");
            const dailyLossVal = (chkLoss && chkLoss.checked && inpLoss && inpLoss.value) ? parseFloat(inpLoss.value) : null;

            const payload = {
                IsRealTradeEnabled: document.getElementById("chkRealTradeToggle")?.checked || false,
                AvailableCapital: parseFloat(document.getElementById("inpAvailableCapital")?.value || "100000"),
                FixedAmountPerTrade: parseFloat(document.getElementById("inpFixedAmount")?.value || "20000"),
                ProfitTargetPct: parseFloat(document.getElementById("inpProfitTarget")?.value || "5.0"),
                StopLossPct: stopLossVal,
                TrailingSlEnabled: trailingSlEnabled,
                TrailingSlPct: trailingSlVal,
                MaxDailyLossLimit: dailyLossVal,
                MaxTradesPerDay: parseInt(document.getElementById("inpMaxTrades")?.value || "5"),
                MaxDurationDays: parseInt(document.getElementById("inpMaxDuration")?.value || "20"),
                ProductType: document.getElementById("selProductType")?.value || "CNC",
                MinConditionsMatch: parseInt(document.getElementById("inpMinConditions")?.value || "10"),
                TradingWindowStart: document.getElementById("inpWindowStart")?.value || "09:15",
                TradingWindowEnd: document.getElementById("inpWindowEnd")?.value || "15:30"
            };

            try {
                const response = await fetch(`${apiBaseUrl}/api/realtrade/settings?userId=${currentUserId}`, {
                    method: "PUT",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify(payload)
                });

                if (response.ok) {
                    alert("✅ Real Trade Risk & Strategy settings saved successfully!");
                    loadDashboardData();
                } else {
                    const errData = await response.json();
                    alert("Error saving settings: " + (errData.message || JSON.stringify(errData)));
                }
            } catch (err) {
                console.error("Save error:", err);
                alert("Network error while saving settings.");
            } finally {
                if (btn) btn.disabled = false;
            }
        });
    }

    // Emergency Panic Kill Switch Button
    const btnKill = document.getElementById("btnEmergencyKillSwitch");
    if (btnKill) {
        btnKill.addEventListener("click", function () {
            if (modalKillSwitch) modalKillSwitch.show();
        });
    }

    const btnConfirmKill = document.getElementById("btnConfirmKillSwitch");
    if (btnConfirmKill) {
        btnConfirmKill.addEventListener("click", async function () {
            this.disabled = true;
            try {
                const response = await fetch(`${apiBaseUrl}/api/realtrade/kill-switch`, {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify({ Reason: "Manual Panic Kill Switch Pressed in UI", UserId: currentUserId })
                });

                const data = await response.json();
                if (modalKillSwitch) modalKillSwitch.hide();
                alert(data.message || "🚨 Emergency Kill Switch Activated!");
                loadDashboardData();
            } catch (err) {
                console.error("Kill switch error:", err);
            } finally {
                this.disabled = false;
            }
        });
    }

    // Single Position Square Off Confirm
    const btnConfirmSq = document.getElementById("btnConfirmSquareOff");
    if (btnConfirmSq) {
        btnConfirmSq.addEventListener("click", async function () {
            const posId = parseInt(document.getElementById("sqPositionId")?.value || "0");
            if (!posId) return;

            this.disabled = true;
            try {
                const response = await fetch(`${apiBaseUrl}/api/realtrade/square-off`, {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify({ PositionId: posId, Reason: "Manual 1-Click Exit", UserId: currentUserId })
                });

                const data = await response.json();
                if (modalSquareOff) modalSquareOff.hide();
                alert(data.message || "Position squared off");
                loadDashboardData();
            } catch (err) {
                console.error("Square off error:", err);
            } finally {
                this.disabled = false;
            }
        });
    }

    // Clear Logs Button
    const btnClear = document.getElementById("btnClearLogs");
    if (btnClear) {
        btnClear.addEventListener("click", function () {
            const c = document.getElementById("consoleLogContainer");
            if (c) c.innerHTML = '<div class="log-entry system"><span class="log-time">System:</span><span class="log-msg">Logs cleared.</span></div>';
        });
    }

    // Auto-Refresh Interval Selector
    const selInterval = document.getElementById("selAutoRefreshInterval");
    if (selInterval) {
        selInterval.addEventListener("change", function () {
            pollingIntervalMs = parseInt(this.value, 10);
            startSmartPolling();
        });
    }

    // Refresh Positions / Sync Now Button
    const btnRefresh = document.getElementById("btnRefreshPositions");
    if (btnRefresh) {
        btnRefresh.addEventListener("click", async function () {
            const originalText = this.innerHTML;
            this.disabled = true;
            this.innerHTML = "⏳ Syncing...";
            try {
                await Promise.all([loadDashboardData(), loadLivePositionsFast()]);
            } finally {
                this.disabled = false;
                this.innerHTML = originalText;
            }
        });
    }

    // DDPI Confirmation Handlers
    document.getElementById("btnDdpiMarkComplete")?.addEventListener("click", function () {
        setDdpiActive(true);
        showToastAlert("🎉 DDPI Verified: Auto CNC selling is fully enabled for your Zerodha account!", "success");
    });

    document.getElementById("btnModalConfirmDdpi")?.addEventListener("click", function () {
        setDdpiActive(true);
        showToastAlert("🎉 DDPI Verified: Auto CNC selling is fully enabled for your Zerodha account!", "success");
    });

    document.getElementById("broker-ddpi-badge")?.addEventListener("click", function () {
        if (!isDdpiActive()) {
            if (typeof bootstrap !== 'undefined') {
                const modalEl = document.getElementById('modalDdpiGuideline');
                if (modalEl) new bootstrap.Modal(modalEl).show();
            }
        } else {
            showToastAlert("🛡️ DDPI Status: Active & Verified. No daily CDSL TPIN/OTP required for automated exits.", "info");
        }
    });

    // Auto Real Trading Guide Modal Setup
    const btnOpenRealGuide = document.getElementById("btnOpenRealGuide");
    const realModal = document.getElementById("realTradingGuideModal");
    const btnCloseRealGuideModal = document.getElementById("btnCloseRealGuideModal");

    if (btnOpenRealGuide && realModal) {
        btnOpenRealGuide.addEventListener("click", () => {
            realModal.classList.add("active");
            document.body.style.overflow = "hidden";
        });

        const closeGuideModal = () => {
            realModal.classList.remove("active");
            document.body.style.overflow = "";
        };

        if (btnCloseRealGuideModal) {
            btnCloseRealGuideModal.addEventListener("click", closeGuideModal);
        }

        realModal.addEventListener("click", (e) => {
            if (e.target === realModal) {
                closeGuideModal();
            }
        });

        document.addEventListener("keydown", (e) => {
            if (e.key === "Escape" && realModal.classList.contains("active")) {
                closeGuideModal();
            }
        });

        // Tab Navigation
        const tabBtns = realModal.querySelectorAll(".qe-modal-nav-btn");
        const panes = realModal.querySelectorAll(".qe-modal-pane");
        tabBtns.forEach(btn => {
            btn.addEventListener("click", () => {
                tabBtns.forEach(b => b.classList.remove("active"));
                panes.forEach(p => p.classList.remove("active"));
                btn.classList.add("active");
                const targetId = btn.dataset.tab;
                const targetPane = document.getElementById(targetId);
                if (targetPane) targetPane.classList.add("active");
            });
        });
    }
}

window.openSquareOffModal = function (id, symbol) {
    document.getElementById("sqPositionId").value = id;
    document.getElementById("sqSymbol").innerText = symbol;
    if (modalSquareOff) modalSquareOff.show();
};

function setupSignalRHub() {
    if (typeof signalR === 'undefined') return;

    const hubUrl = `${apiBaseUrl}/hubs/marketdata`;
    const connection = new signalR.HubConnectionBuilder()
        .withUrl(hubUrl)
        .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
        .build();

    connection.on("ReceiveRealTradeDashboardUpdate", function (dashboard) {
        updateDashboardUI(dashboard);
    });

    connection.on("ReceiveRealTradeAlert", function (alertData) {
        if (alertData && alertData.userId && currentUserId && alertData.userId !== currentUserId) {
            return;
        }
        showToastAlert(alertData.message || "Real Trade Event");
        loadDashboardData();
    });

    connection.on("ReceiveRealTradeLogEvent", function (log) {
        appendLogEntry(log);
    });

    connection.start()
        .then(() => {
            const wsBadge = document.getElementById("ws-status-badge");
            if (wsBadge) {
                wsBadge.className = "badge-ws connected";
                wsBadge.innerHTML = '<span class="ws-dot"></span> SignalR Live Stream Connected';
            }
        })
        .catch(err => {
            console.warn("SignalR connection error:", err);
            const wsBadge = document.getElementById("ws-status-badge");
            if (wsBadge) {
                wsBadge.className = "badge-ws disconnected";
                wsBadge.innerHTML = '<span class="ws-dot"></span> SignalR Reconnecting...';
            }
        });
}

function showToastAlert(msg) {
    console.log("[REAL TRADE ALERT]", msg);
}

function startNextScanCountdownTimer(nextRunTimeStr, runTextFormatted, isMarketOpen) {
    if (countdownInterval) clearInterval(countdownInterval);

    const badge = document.getElementById("nextScanCountdownBadge");
    if (!badge) return;

    if (!nextRunTimeStr) {
        badge.innerHTML = `⏳ <span style="opacity: 0.75;">Next Scan: Standby</span>`;
        return;
    }

    const targetTimeMs = new Date(nextRunTimeStr).getTime();

    function updateTimer() {
        const nowMs = new Date().getTime();
        const diffMs = targetTimeMs - nowMs;

        const badgeElem = document.getElementById("nextScanCountdownBadge");
        if (!badgeElem) return;

        if (diffMs <= 0) {
            badgeElem.innerHTML = `⏳ <span style="font-weight: 700; color: #f59e0b;">Scanning Now...</span>`;
            clearInterval(countdownInterval);
            setTimeout(() => { loadDashboardData(); }, 3000);
            return;
        }

        const totalSecs = Math.floor(diffMs / 1000);
        const mins = Math.floor(totalSecs / 60);
        const secs = totalSecs % 60;

        if (isMarketOpen) {
            badgeElem.innerHTML = `⏳ Next Scan: <strong style="color: #38bdf8; font-family: monospace;">${mins}m ${secs.toString().padStart(2, '0')}s</strong>`;
        } else {
            badgeElem.innerHTML = `⏳ Next Scan: <strong style="color: #fbbf24;">${runTextFormatted || 'Market Closed'}</strong>`;
        }
    }

    updateTimer();
    countdownInterval = setInterval(updateTimer, 1000);
}

function formatCurrency(val) {
    const num = parseFloat(val || 0);
    return '₹' + num.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}

function formatCurrencyWithSign(val) {
    const num = parseFloat(val || 0);
    const sign = num > 0 ? '+' : '';
    return sign + '₹' + num.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}

// ============================================================================
// DDPI State & UI Manager
// ============================================================================
function isDdpiActive() {
    const key = `quantedge_ddpi_active_${currentUserId}`;
    return localStorage.getItem(key) === "true" || localStorage.getItem("quantedge_ddpi_active") === "true";
}

async function setDdpiActive(active) {
    const key = `quantedge_ddpi_active_${currentUserId}`;
    if (active) {
        localStorage.setItem(key, "true");
        localStorage.setItem("quantedge_ddpi_active", "true");
    } else {
        localStorage.removeItem(key);
        localStorage.removeItem("quantedge_ddpi_active");
    }
    renderDdpiStatus();

    try {
        await fetch(`${apiBaseUrl}/api/zerodha/ddpi-status`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ userId: currentUserId, isDdpiEnabled: active })
        });
    } catch (err) {
        console.warn("Could not sync DDPI status to database:", err);
    }
}

function renderDdpiStatus() {
    const active = isDdpiActive();
    const banner = document.getElementById("banner-ddpi-activation");
    const bannerIcon = document.getElementById("bannerDdpiIcon");
    const bannerText = document.getElementById("bannerDdpiText");
    const bannerActions = document.getElementById("bannerDdpiActions");
    const badge = document.getElementById("broker-ddpi-badge");
    const badgeText = document.getElementById("ddpiBadgeText");

    // Header Badge Update
    if (badge) {
        if (active) {
            badge.className = "badge-ddpi";
            if (badgeText) badgeText.innerText = "DDPI: Active";
            badge.title = "🛡️ DDPI Status: Active & Verified. No daily CDSL TPIN/OTP required for automated exits.";
        } else {
            badge.className = "badge-ddpi inactive";
            if (badgeText) badgeText.innerText = "DDPI: Pending";
            badge.title = "⚠️ DDPI Pending: Click to view 1-time DDPI activation steps.";
        }
    }

    // Prerequisite Banner Update
    if (banner && bannerIcon && bannerText && bannerActions) {
        if (active) {
            banner.className = "prerequisite-banner banner-ddpi-verified";
            bannerIcon.innerText = "🛡️";
            bannerText.innerHTML = `<strong>DDPI Active & Verified:</strong> 1-Click automated CNC Sell & Target exit orders are enabled via Zerodha API without daily TPIN/OTP.`;
            bannerActions.innerHTML = `
                <span class="badge-ddpi-verified-tag">✓ Active (No TPIN Needed)</span>
                <button type="button" class="btn-ddpi-recheck" id="btnDdpiRecheck" title="Click if you need to review guidelines">📖 View Guide</button>
            `;
            document.getElementById("btnDdpiRecheck")?.addEventListener("click", () => {
                if (typeof bootstrap !== 'undefined') {
                    const modalEl = document.getElementById('modalDdpiGuideline');
                    if (modalEl) new bootstrap.Modal(modalEl).show();
                }
            });
        } else {
            banner.className = "prerequisite-banner banner-tpin-notice";
            bannerIcon.innerText = "🚀";
            bannerText.innerHTML = `<strong>Automated Real Trading Prerequisite:</strong> To allow the engine to automatically execute CNC Sell & Target exit orders via Zerodha API, please activate <strong>1-time DDPI</strong> in your Zerodha account.`;
            bannerActions.innerHTML = `
                <button type="button" class="btn-tpin-guide" data-bs-toggle="modal" data-bs-target="#modalDdpiGuideline" title="Step-by-step 1-time DDPI activation guide">
                    📖 DDPI Guide
                </button>
                <a href="https://console.zerodha.com/account/demat" target="_blank" class="btn-ddpi-enable" id="btnDdpiOpenConsole">
                    <span>Enable DDPI Online</span> <span class="ddpi-arrow">↗</span>
                </a>
                <button type="button" class="btn-ddpi-verify-action" id="btnDdpiMarkComplete" title="Click if DDPI status is Completed in Zerodha Demat Console">
                    ✅ I've Enabled DDPI
                </button>
            `;
            document.getElementById("btnDdpiMarkComplete")?.addEventListener("click", () => {
                setDdpiActive(true);
                showToastAlert("🎉 DDPI Verified: Auto CNC selling is active without TPIN!", "success");
            });
        }
    }
}

// Global Toast Notification Helper
function showToastAlert(message, type = "info") {
    let toastContainer = document.getElementById("qeToastContainer");
    if (!toastContainer) {
        toastContainer = document.createElement("div");
        toastContainer.id = "qeToastContainer";
        toastContainer.style.position = "fixed";
        toastContainer.style.top = "20px";
        toastContainer.style.right = "20px";
        toastContainer.style.zIndex = "999999";
        toastContainer.style.display = "flex";
        toastContainer.style.flexDirection = "column";
        toastContainer.style.gap = "10px";
        document.body.appendChild(toastContainer);
    }

    const toast = document.createElement("div");
    toast.className = `qe-toast qe-toast-${type}`;
    toast.style.background = type === "success" 
        ? "linear-gradient(135deg, #065f46 0%, #047857 100%)" 
        : type === "danger" 
            ? "linear-gradient(135deg, #991b1b 0%, #dc2626 100%)" 
            : "linear-gradient(135deg, #1e293b 0%, #0f172a 100%)";
    toast.style.color = "#ffffff";
    toast.style.padding = "12px 20px";
    toast.style.borderRadius = "10px";
    toast.style.border = type === "success" ? "1px solid #34d399" : "1px solid #475569";
    toast.style.boxShadow = "0 10px 25px rgba(0,0,0,0.5)";
    toast.style.fontSize = "0.88rem";
    toast.style.fontWeight = "600";
    toast.style.display = "flex";
    toast.style.alignItems = "center";
    toast.style.gap = "10px";
    toast.style.transition = "all 0.3s ease";
    toast.style.opacity = "0";
    toast.style.transform = "translateY(-10px)";
    toast.innerHTML = `<span>${message}</span>`;

    toastContainer.appendChild(toast);

    requestAnimationFrame(() => {
        toast.style.opacity = "1";
        toast.style.transform = "translateY(0)";
    });

    setTimeout(() => {
        toast.style.opacity = "0";
        toast.style.transform = "translateY(-10px)";
        setTimeout(() => toast.remove(), 350);
    }, 4500);
}
