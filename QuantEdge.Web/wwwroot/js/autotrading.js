/**
 * QuantEdge Auto Paper Trading Dashboard JavaScript Module
 */

let apiBaseUrl = "";

document.addEventListener("DOMContentLoaded", function () {
    const configElem = document.getElementById("autotrade-config");
    if (configElem) {
        apiBaseUrl = configElem.dataset.apiBaseUrl || "";
    }

    loadDashboardData();
    setupEventListeners();
    setupSignalRHub();

    // Periodic refresh fallback every 15 seconds
    setInterval(loadDashboardData, 15000);
});

async function loadDashboardData() {
    try {
        const response = await fetch(`${apiBaseUrl}/api/autotrade/dashboard`);
        if (!response.ok) return;

        const data = await response.json();
        updateDashboardUI(data);
    } catch (err) {
        console.error("Failed to load Auto Trade Dashboard data:", err);
    }
}

function updateDashboardUI(data) {
    if (!data) return;

    const s = data.settings || data.Settings || {};
    const isEnabled = s.isAutoTradeEnabled ?? s.IsAutoTradeEnabled ?? false;
    const sysStatus = data.systemStatus || data.SystemStatus || (isEnabled ? "ACTIVE" : "PAUSED");
    const todayCount = data.todayTradeCount ?? data.TodayTradeCount ?? 0;
    const maxTrades = data.maxTradesPerDay ?? data.MaxTradesPerDay ?? (s.maxTradesPerDay || s.MaxTradesPerDay || 5);
    const availableCap = s.availableCapital ?? s.AvailableCapital ?? 100000;
    const openCount = data.activePositionsCount ?? data.ActivePositionsCount ?? 0;
    const unPnl = data.totalUnrealizedPnl ?? data.TotalUnrealizedPnl ?? 0;
    const realPnl = data.totalRealizedPnlToday ?? data.TotalRealizedPnlToday ?? 0;
    const positions = data.openPositions || data.OpenPositions || [];
    const logs = data.todayLogs || data.TodayLogs || [];

    // 1. Toggle Switch & Status Badges
    const toggleSwitch = document.getElementById("chkAutoTradeToggle");
    if (toggleSwitch) {
        toggleSwitch.checked = isEnabled;
    }

    const sysStatusElem = document.getElementById("sys-status-text");
    if (sysStatusElem) {
        sysStatusElem.innerText = sysStatus;
        sysStatusElem.style.color = isEnabled ? "#4ade80" : "#f87171";
    }

    const tradeCounterElem = document.getElementById("trade-counter-badge");
    if (tradeCounterElem) {
        tradeCounterElem.innerText = `${todayCount} / ${maxTrades} trades used today`;
    }

    // 2. Metrics & Stats
    const capElem = document.getElementById("stat-capital");
    if (capElem) capElem.innerText = `₹${formatNumber(availableCap)}`;
    
    const countElem = document.getElementById("stat-open-count");
    if (countElem) countElem.innerText = openCount;
    
    const unPnlElem = document.getElementById("stat-unrealized-pnl");
    if (unPnlElem) {
        unPnlElem.innerText = `₹${formatNumber(unPnl)}`;
        unPnlElem.className = `stat-value ${unPnl >= 0 ? "positive" : "negative"}`;
    }

    const realPnlElem = document.getElementById("stat-realized-pnl");
    if (realPnlElem) {
        realPnlElem.innerText = `₹${formatNumber(realPnl)}`;
        realPnlElem.className = `stat-value ${realPnl >= 0 ? "positive" : "negative"}`;
    }

    // 3. Settings Form Fields
    populateSettingsForm(s);

    // 4. Render Tables
    renderOpenPositionsTable(positions);
    renderLogsConsole(logs);
}

function populateSettingsForm(s) {
    if (!s) return;
    const cap = s.availableCapital ?? s.AvailableCapital ?? 100000;
    const target = s.profitTargetPct ?? s.ProfitTargetPct ?? 5.0;
    const sl = s.stopLossPct ?? s.StopLossPct ?? 3.0;
    const maxDur = s.maxDurationDays ?? s.MaxDurationDays ?? 20;
    const maxTrd = s.maxTradesPerDay ?? s.MaxTradesPerDay ?? 5;
    const fixedAmt = s.fixedAmountPerTrade ?? s.FixedAmountPerTrade ?? 20000;
    const minCond = s.minConditionsMatch ?? s.MinConditionsMatch ?? 12;

    const elCap = document.getElementById("txtCapital"); if (elCap) elCap.value = cap;
    const elTarget = document.getElementById("txtTargetPct"); if (elTarget) elTarget.value = target;
    const elSl = document.getElementById("txtStopLossPct"); if (elSl) elSl.value = sl;
    const elDur = document.getElementById("txtMaxDuration"); if (elDur) elDur.value = maxDur;
    const elTrd = document.getElementById("txtMaxTrades"); if (elTrd) elTrd.value = maxTrd;
    const elFixed = document.getElementById("txtFixedAmount"); if (elFixed) elFixed.value = fixedAmt;
    const elCond = document.getElementById("txtMinConditions"); if (elCond) elCond.value = minCond;
}

function renderOpenPositionsTable(positions) {
    const tbody = document.getElementById("tbl-open-positions-body");
    if (!tbody) return;

    if (!positions || positions.length === 0) {
        tbody.innerHTML = `<tr><td colspan="7" class="text-center" style="padding:24px; text-align:center; color:#ffffff; font-weight:500;">No active OPEN auto positions right now. Scanner will place paper BUY orders when criteria match.</td></tr>`;
        return;
    }


    let html = "";
    positions.forEach(p => {
        const symbol = p.symbol || p.Symbol || "";
        const avgPrice = p.averageEntryPrice ?? p.AverageEntryPrice ?? 0;
        const curPrice = p.currentPrice ?? p.CurrentPrice ?? avgPrice;
        const qty = p.quantity ?? p.Quantity ?? 0;
        const tp = p.takeProfit ?? p.TakeProfit;
        const sl = p.stopLoss ?? p.StopLoss;
        const unPnl = p.unrealizedPnl ?? p.UnrealizedPnl ?? 0;
        const pnlClass = unPnl >= 0 ? "text-success" : "text-danger";
        const entryVal = qty * avgPrice;

        html += `
            <tr>
                <td><strong>${symbol}</strong> <span class="badge-tag badge-auto">AUTO</span></td>
                <td>₹${formatNumber(avgPrice)}</td>
                <td>₹${formatNumber(curPrice)}</td>
                <td>${qty} (₹${formatNumber(entryVal)})</td>
                <td>₹${formatNumber(tp || 0)} (+${tp && avgPrice > 0 ? Math.round((tp - avgPrice)/avgPrice * 100) : 5}%)</td>
                <td>₹${formatNumber(sl || 0)} (-${sl && avgPrice > 0 ? Math.round((avgPrice - sl)/avgPrice * 100) : 3}%)</td>
                <td class="${pnlClass} font-weight-bold">₹${formatNumber(unPnl)}</td>
            </tr>
        `;
    });

    tbody.innerHTML = html;
}


function renderLogsConsole(logs) {
    const consoleBox = document.getElementById("log-console-box");
    if (!consoleBox) return;

    if (!logs || logs.length === 0) {
        consoleBox.innerHTML = `<div class="log-entry"><span class="time">[System]</span> Listening for auto trading signal scan events...</div>`;
        return;
    }

    let html = "";
    logs.forEach(l => {
        const actionType = l.actionType || l.ActionType || "INFO";
        const symbol = l.symbol || l.Symbol || "";
        const reason = l.reason || l.Reason || "";
        const execTime = l.executedAt || l.ExecutedAt || new Date();

        let typeClass = "buy";
        if (actionType.includes("SELL")) typeClass = "sell";
        else if (actionType.includes("SKIPPED")) typeClass = "skip";
        else if (actionType.includes("ALERT") || actionType.includes("EXPIRED")) typeClass = "alert";

        const timeStr = new Date(execTime).toLocaleTimeString();
        html += `<div class="log-entry ${typeClass}"><span class="time">[${timeStr}]</span> <strong>[${actionType}]</strong> ${symbol ? symbol + ': ' : ''}${reason}</div>`;
    });

    consoleBox.innerHTML = html;
}


function setupEventListeners() {
    // Master Toggle Handler
    const chkToggle = document.getElementById("chkAutoTradeToggle");
    if (chkToggle) {
        chkToggle.addEventListener("change", async function () {
            const enabled = chkToggle.checked;
            try {
                const res = await fetch(`${apiBaseUrl}/api/autotrade/toggle`, {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify({ enabled })
                });

                if (res.ok) {
                    showToast(enabled ? "⚡ Auto Trading Started!" : "⏸️ Auto Trading Stopped.", enabled ? "success" : "info");
                    loadDashboardData();
                }
            } catch (err) {
                console.error("Toggle Auto Trade failed:", err);
            }
        });
    }

    // Save Settings Form Handler
    const btnSave = document.getElementById("btnSaveSettings");
    if (btnSave) {
        btnSave.addEventListener("click", async function (e) {
            e.preventDefault();

            const dto = {
                isAutoTradeEnabled: document.getElementById("chkAutoTradeToggle").checked,
                availableCapital: parseFloat(document.getElementById("txtCapital").value) || 100000,
                profitTargetPct: parseFloat(document.getElementById("txtTargetPct").value) || 5.0,
                stopLossPct: parseFloat(document.getElementById("txtStopLossPct").value) || 3.0,
                maxDurationDays: parseInt(document.getElementById("txtMaxDuration").value) || 20,
                maxTradesPerDay: parseInt(document.getElementById("txtMaxTrades").value) || 5,
                fixedAmountPerTrade: parseFloat(document.getElementById("txtFixedAmount").value) || 20000,
                minConditionsMatch: parseInt(document.getElementById("txtMinConditions").value) || 12,
                tradingWindowStart: "09:15",
                tradingWindowEnd: "15:30"
            };

            try {
                const res = await fetch(`${apiBaseUrl}/api/autotrade/settings`, {
                    method: "PUT",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify(dto)
                });

                if (res.ok) {
                    showToast("✅ Auto Trade Settings updated successfully!", "success");
                    loadDashboardData();
                } else {
                    showToast("⚠️ Settings update failed. Please check validation limits.", "error");
                }
            } catch (err) {
                console.error("Settings update failed:", err);
            }
        });
    }
}

function setupSignalRHub() {
    if (typeof signalR === "undefined") return;

    const connection = new signalR.HubConnectionBuilder()
        .withUrl("/marketdatahub")
        .withAutomaticReconnect()
        .build();

    connection.on("ReceiveAutoTradeAlert", function (data) {
        if (data && data.message) {
            showToast(data.message, data.side === "BUY" ? "success" : "warning");
        }
        loadDashboardData();
    });

    connection.on("ReceiveAutoTradeLogEvent", function (log) {
        loadDashboardData();
    });

    connection.on("ReceiveAutoTradeDashboardUpdate", function (dashData) {
        updateDashboardUI(dashData);
    });

    connection.start()
        .then(() => console.log("SignalR MarketDataHub connected for Auto Trading."))
        .catch(err => console.error("SignalR connection error:", err));
}

function formatNumber(num) {
    if (num === null || num === undefined) return "0.00";
    return Number(num).toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}

function showToast(msg, type = "info") {
    if (window.toastr) {
        if (type === "success") toastr.success(msg);
        else if (type === "warning") toastr.warning(msg);
        else if (type === "error") toastr.error(msg);
        else toastr.info(msg);
    } else {
        alert(msg);
    }
}
