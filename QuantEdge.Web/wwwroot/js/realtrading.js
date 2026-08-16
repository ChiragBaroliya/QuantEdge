/**
 * QuantEdge Auto Real Trading (Live Broker) JavaScript Module
 */

let apiBaseUrl = "";
let countdownInterval = null;
let modalSquareOff = null;
let modalKillSwitch = null;

document.addEventListener("DOMContentLoaded", function () {
    const configElem = document.getElementById("realtrade-config");
    if (configElem) {
        apiBaseUrl = configElem.dataset.apiBaseUrl || "";
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
    }

    loadDashboardData();
    setupEventListeners();
    setupSignalRHub();

    // Auto-refresh fallback every 15s
    setInterval(() => {
        loadDashboardData();
    }, 15000);
});

async function loadDashboardData() {
    try {
        const response = await fetch(`${apiBaseUrl}/api/realtrade/dashboard`);
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

    // Token Badge & Connect Button
    const tokenBadge = document.getElementById("broker-token-badge");
    const btnConnectHeader = document.getElementById("btnConnectZerodhaHeader");
    if (tokenBadge) {
        if (data.isBrokerTokenActive) {
            tokenBadge.className = "badge-token active";
            tokenBadge.innerHTML = `<span class="token-dot"></span> Zerodha: Active (${data.brokerTokenCreatedIst || 'Today'})`;
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

    const unrealPnlEl = document.getElementById("stat-unrealized-pnl");
    if (unrealPnlEl) {
        unrealPnlEl.innerText = formatCurrencyWithSign(data.totalUnrealizedPnl);
        unrealPnlEl.style.color = data.totalUnrealizedPnl >= 0 ? "#34d399" : "#f87171";
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
        todayRealPnlEl.innerText = formatCurrencyWithSign(data.totalRealizedPnlToday);
        todayRealPnlEl.style.color = data.totalRealizedPnlToday >= 0 ? "#34d399" : "#f87171";
    }

    // 3. Populate Settings Form
    if (data.settings) {
        populateSettingsForm(data.settings);
    }

    // 4. Populate Open Real Positions
    renderOpenPositions(data.openPositions || []);

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
        const sideText = p.side === 0 ? '<span class="text-success fw-bold">BUY</span>' : '<span class="text-danger fw-bold">SELL</span>';
        const targetText = p.takeProfit ? `₹${p.takeProfit.toFixed(2)}` : '<span style="color: #94a3b8;">None</span>';
        const slText = p.stopLoss ? `₹${p.stopLoss.toFixed(2)}` : '<span style="color: #94a3b8;">None</span>';
        const tslText = p.trailingStopLoss ? `₹${p.trailingStopLoss.toFixed(2)}` : '<span style="color: #94a3b8;">None</span>';

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
                <td class="${pnlClass}">${formatCurrencyWithSign(pnl)}</td>
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
    const container = document.getElementById("consoleLogContainer");
    if (!container) return;

    const timeStr = new Date(log.executedAt).toLocaleTimeString('en-IN', { hour: '2-digit', minute: '2-digit', second: '2-digit' });
    const div = document.createElement("div");
    div.className = `log-entry ${log.actionType.toLowerCase()}`;

    let badgeClass = "badge bg-secondary";
    if (log.actionType.includes("BUY")) badgeClass = "badge bg-success";
    else if (log.actionType.includes("SELL")) badgeClass = "badge bg-danger";
    else if (log.actionType.includes("KILL") || log.actionType.includes("CIRCUIT")) badgeClass = "badge bg-warning text-dark";

    div.innerHTML = `
        <span class="log-time">[${timeStr}]</span>
        <span class="${badgeClass} me-2" style="font-size: 0.7rem;">${log.actionType}</span>
        <span class="fw-bold me-2">${log.symbol}:</span>
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
            const returnUrl = window.location.origin;
            const res = await fetch(`${apiBaseUrl}/api/zerodha/login-url?returnUrl=${encodeURIComponent(returnUrl)}`);
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
                    body: JSON.stringify({ enabled })
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
                const response = await fetch(`${apiBaseUrl}/api/realtrade/settings`, {
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
                    body: JSON.stringify({ Reason: "Manual Panic Kill Switch Pressed in UI" })
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
                    body: JSON.stringify({ PositionId: posId, Reason: "Manual 1-Click Exit" })
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

    // Refresh Positions Button
    const btnRefresh = document.getElementById("btnRefreshPositions");
    if (btnRefresh) {
        btnRefresh.addEventListener("click", function () {
            loadDashboardData();
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
