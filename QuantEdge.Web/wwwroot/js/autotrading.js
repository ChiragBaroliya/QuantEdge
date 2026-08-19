/**
 * QuantEdge Auto Paper Trading Dashboard JavaScript Module
 */

let apiBaseUrl = "";
let countdownInterval = null;

function getTodayDateString() {
    const d = new Date();
    const year = d.getFullYear();
    const month = String(d.getMonth() + 1).padStart(2, '0');
    const day = String(d.getDate()).padStart(2, '0');
    return `${year}-${month}-${day}`;
}

document.addEventListener("DOMContentLoaded", function () {
    const configElem = document.getElementById("autotrade-config");
    if (configElem) {
        apiBaseUrl = configElem.dataset.apiBaseUrl || "";
    }

    const todayStr = getTodayDateString();
    const elFrom = document.getElementById('historyFilterFromDate');
    const elTo = document.getElementById('historyFilterToDate');
    if (elFrom && !elFrom.value) elFrom.value = todayStr;
    if (elTo && !elTo.value) elTo.value = todayStr;
    historyPageState.fromDate = elFrom?.value || todayStr;
    historyPageState.toDate = elTo?.value || todayStr;

    loadDashboardData();
    loadActiveStocks();
    loadOrders();
    loadHistory(1);
    setupEventListeners();
    setupSignalRHub();
    startHistoryAutoRefreshCountdown();

    // Periodic dashboard refresh fallback every 15 seconds
    setInterval(() => {
        loadDashboardData();
    }, 15000);
});

let historyRefreshSecs = 300;
let historyRefreshInterval = null;

function startHistoryAutoRefreshCountdown() {
    if (historyRefreshInterval) clearInterval(historyRefreshInterval);
    historyRefreshSecs = 300;

    const updateBadge = () => {
        const el = document.getElementById("historyCountdownText");
        if (!el) return;
        const mins = Math.floor(historyRefreshSecs / 60);
        const secs = historyRefreshSecs % 60;
        el.innerText = `${mins.toString().padStart(2, '0')}:${secs.toString().padStart(2, '0')}`;

        if (historyRefreshSecs <= 0) {
            historyRefreshSecs = 300;
            loadHistory(historyPageState.page);
        } else {
            historyRefreshSecs--;
        }
    };

    updateBadge();
    historyRefreshInterval = setInterval(updateBadge, 1000);
}

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
            badgeElem.innerHTML = `🔄 <strong style="color: #60a5fa;">Syncing Market Data & Scanning...</strong>`;
            return;
        }

        const totalSecs = Math.floor(diffMs / 1000);
        const hours = Math.floor(totalSecs / 3600);
        const mins = Math.floor((totalSecs % 3600) / 60);
        const secs = totalSecs % 60;

        let timeStr = "";
        if (hours > 0) {
            timeStr = `${hours}h ${mins.toString().padStart(2, '0')}m ${secs.toString().padStart(2, '0')}s`;
        } else {
            timeStr = `${mins.toString().padStart(2, '0')}m ${secs.toString().padStart(2, '0')}s`;
        }

        const targetFormattedTime = new Date(targetTimeMs).toLocaleTimeString("en-IN", { hour: '2-digit', minute: '2-digit' });

        if (isMarketOpen) {
            badgeElem.innerHTML = `⏳ Next Scan in: <strong style="color: #38bdf8;">${timeStr}</strong> <span style="opacity: 0.75;">(${targetFormattedTime})</span>`;
        } else {
            badgeElem.innerHTML = `🌙 Market Closed <span style="opacity: 0.75;">(Next: ${runTextFormatted || targetFormattedTime})</span>`;
        }
    }

    updateTimer();
    countdownInterval = setInterval(updateTimer, 1000);
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

    // Trigger Next Scan countdown timer
    const nextRunTime = data.nextRunTime || data.NextRunTime;
    const nextRunFormatted = data.nextRunFormatted || data.NextRunFormatted;
    const isMarketOpen = data.isMarketOpen !== undefined ? data.isMarketOpen : (data.IsMarketOpen !== undefined ? data.IsMarketOpen : false);

    startNextScanCountdownTimer(nextRunTime, nextRunFormatted, isMarketOpen);

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

    const availableMargin = data.availableMargin ?? data.AvailableMargin ?? 0;
    const usedMargin = data.usedMargin ?? data.UsedMargin ?? 0;

    // 2. Metrics & Stats
    const availMarginElem = document.getElementById("stat-available-margin");
    if (availMarginElem) availMarginElem.innerText = `₹${formatNumber(availableMargin)}`;

    const usedMarginElem = document.getElementById("stat-used-margin");
    if (usedMarginElem) usedMarginElem.innerText = `₹${formatNumber(usedMargin)}`;

    const capElem = document.getElementById("stat-capital");
    if (capElem) capElem.innerText = `₹${formatNumber(availableCap)}`;

    const countElem = document.getElementById("stat-open-count");
    if (countElem) countElem.innerText = openCount;

    const todayTradeAmount = data.todayTradeAmount ?? data.TodayTradeAmount ?? (todayCount * (s.fixedAmountPerTrade || s.FixedAmountPerTrade || 20000));

    const todayCountElem = document.getElementById("stat-today-trade-count");
    if (todayCountElem) todayCountElem.innerText = `${todayCount} / ${maxTrades} Trades`;

    const todayAmountElem = document.getElementById("stat-today-used-amount");
    if (todayAmountElem) todayAmountElem.innerText = `₹${formatNumber(todayTradeAmount)}`;

    const unPnlElem = document.getElementById("stat-unrealized-pnl");
    if (unPnlElem) {
        const baseMargin = usedMargin > 0 ? usedMargin : (availableCap > 0 ? availableCap : 0);
        const unPnlPct = baseMargin > 0 ? (unPnl / baseMargin) * 100 : 0;
        const unPctSign = unPnlPct > 0 ? "+" : "";
        const unPctStr = `${unPctSign}${unPnlPct.toFixed(2)}%`;
        const unSign = unPnl > 0 ? "+" : (unPnl < 0 ? "-" : "");
        unPnlElem.innerText = `${unSign}₹${formatNumber(Math.abs(unPnl))} (${unPctStr})`;
        unPnlElem.className = `stat-value ${unPnl >= 0 ? "positive" : "negative"}`;
    }

    const realPnlElem = document.getElementById("stat-realized-pnl");
    if (realPnlElem) {
        const baseCap = todayTradeAmount > 0 ? todayTradeAmount : (availableCap > 0 ? availableCap : 0);
        const pnlPct = baseCap > 0 ? (realPnl / baseCap) * 100 : 0;
        const pctSign = pnlPct > 0 ? "+" : "";
        const pctStr = `${pctSign}${pnlPct.toFixed(2)}%`;
        const pnlSign = realPnl > 0 ? "+" : (realPnl < 0 ? "-" : "");
        realPnlElem.innerText = `${pnlSign}₹${formatNumber(Math.abs(realPnl))} (${pctStr})`;
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
    const sl = s.stopLossPct ?? s.StopLossPct;
    const maxDur = s.maxDurationDays ?? s.MaxDurationDays ?? 20;
    const maxTrd = s.maxTradesPerDay ?? s.MaxTradesPerDay ?? 5;
    const fixedAmt = s.fixedAmountPerTrade ?? s.FixedAmountPerTrade ?? 20000;
    const minCond = s.minConditionsMatch ?? s.MinConditionsMatch ?? 10;

    const elCap = document.getElementById("txtCapital"); if (elCap) elCap.value = cap;
    const elTarget = document.getElementById("txtTargetPct"); if (elTarget) elTarget.value = target;

    const hasSL = sl !== null && sl !== undefined && Number(sl) > 0;
    const chkSL = document.getElementById("chkEnableStopLoss");
    const elSl = document.getElementById("txtStopLossPct");
    if (chkSL) chkSL.checked = hasSL;
    if (elSl) {
        elSl.disabled = !hasSL;
        elSl.value = hasSL ? sl : '';
        elSl.placeholder = hasSL ? "e.g. 3.0" : "Disabled (No Stop Loss)";
    }

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
        const unPnlPct = entryVal > 0 ? (unPnl / entryVal * 100).toFixed(2) : '0.00';
        const unPnlPctSign = unPnl > 0 ? '+' : '';
        const unPnlSign = unPnl > 0 ? '+' : (unPnl < 0 ? '-' : '');
        const tpPctText = tp && avgPrice > 0 ? ((tp - avgPrice) / avgPrice * 100).toFixed(2).replace(/\.?0+$/, '') : '';
        const slPctText = sl && avgPrice > 0 ? ((avgPrice - sl) / avgPrice * 100).toFixed(2).replace(/\.?0+$/, '') : '';
        const tpText = tp && tp > 0 ? `₹${formatNumber(tp)}${tpPctText ? ' (+' + tpPctText + '%)' : ''}` : '-';
        const slText = sl && sl > 0 ? `₹${formatNumber(sl)}${slPctText ? ' (-' + slPctText + '%)' : ''}` : '-';

        html += `
            <tr>
                <td><strong>${symbol}</strong> <span class="badge-tag badge-auto">AUTO</span></td>
                <td>₹${formatNumber(avgPrice)}</td>
                <td>₹${formatNumber(curPrice)}</td>
                <td>${qty} (₹${formatNumber(entryVal)})</td>
                <td>${tpText}</td>
                <td>${slText}</td>
                <td class="${pnlClass} font-weight-bold">${unPnlSign}₹${formatNumber(Math.abs(unPnl))} (${unPnlPctSign}${unPnlPct}%)</td>
            </tr>
        `;
    });

    tbody.innerHTML = html;
}


function formatISTTime(dateInput) {
    if (!dateInput) return '-';
    let rawStr = String(dateInput);
    if (!rawStr.endsWith('Z') && !rawStr.includes('+')) rawStr += 'Z';
    let d = new Date(rawStr);
    if (isNaN(d.getTime())) d = new Date(dateInput);
    return d.toLocaleString('en-IN', {
        timeZone: 'Asia/Kolkata',
        day: '2-digit',
        month: 'short',
        year: 'numeric',
        hour: '2-digit',
        minute: '2-digit',
        second: '2-digit',
        hour12: true
    });
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

        const timeStr = formatISTTime(execTime);
        html += `<div class="log-entry ${typeClass}"><span class="time">[${timeStr} IST]</span> <strong>[${actionType}]</strong> ${symbol ? symbol + ': ' : ''}${reason}</div>`;
    });

    consoleBox.innerHTML = html;
}

let historyPageState = { page: 1, pageSize: 10, symbol: '', side: '', fromDate: getTodayDateString(), toDate: getTodayDateString() };

function formatIST(dateInput) {
    if (!dateInput) return '-';
    let rawStr = String(dateInput);
    if (!rawStr.endsWith('Z') && !rawStr.includes('+')) rawStr += 'Z';
    let d = new Date(rawStr);
    if (isNaN(d.getTime())) d = new Date(dateInput);
    return d.toLocaleString('en-IN', {
        timeZone: 'Asia/Kolkata',
        day: '2-digit',
        month: '2-digit',
        year: 'numeric',
        hour: '2-digit',
        minute: '2-digit',
        second: '2-digit',
        hour12: true
    }) + ' IST';
}

async function loadActiveStocks() {
    try {
        const res = await fetch(`${apiBaseUrl}/api/marketdata/stocks`);
        if (!res.ok) return;
        const stocks = await res.json();
        const historyFilterSymbol = document.getElementById('historyFilterSymbol');
        if (historyFilterSymbol && Array.isArray(stocks) && stocks.length > 0) {
            historyFilterSymbol.innerHTML = '<option value="">All Symbols</option>';
            stocks.forEach(stock => {
                const sym = stock.symbol || stock.Symbol;
                if (sym) {
                    const opt = document.createElement('option');
                    opt.value = sym;
                    opt.innerText = sym;
                    historyFilterSymbol.appendChild(opt);
                }
            });
        }
        initSelect2Filters();
    } catch (err) {
        console.error('Failed to load active stocks for filter:', err);
    }
}

function initSelect2Filters() {
    if (window.jQuery && $.fn.select2) {
        const $sym = $('#historyFilterSymbol');
        const $side = $('#historyFilterSide');

        if ($sym.length) {
            if ($sym.data('select2')) {
                $sym.trigger('change.select2');
            } else {
                $sym.select2({
                    placeholder: 'All Symbols',
                    allowClear: true,
                    width: '100%',
                    dropdownParent: $sym.parent()
                });
            }
        }

        if ($side.length) {
            if (!$side.data('select2')) {
                $side.select2({
                    placeholder: 'All Sides',
                    minimumResultsForSearch: Infinity,
                    width: '100%',
                    dropdownParent: $side.parent()
                });
            }
        }
    }
}

async function loadOrders() {
    try {
        const ordersTableBody = document.getElementById('ordersTableBody');
        if (!ordersTableBody) return;
        const res = await fetch(`${apiBaseUrl}/api/papertrading/orders`);
        if (!res.ok) return;
        const orders = await res.json();
        renderOrders(orders);
    } catch (err) {
        console.error('Error loading orders:', err);
    }
}

function renderOrders(orders) {
    const ordersTableBody = document.getElementById('ordersTableBody');
    if (!ordersTableBody) return;
    ordersTableBody.innerHTML = '';

    if (!orders || orders.length === 0) {
        ordersTableBody.innerHTML = '<tr><td colspan="8" class="text-center text-white py-3">No active or pending orders.</td></tr>';
        return;
    }

    orders.forEach(o => {
        const sideBadge = o.side === 0 || o.side === 'BUY' ? '<span class="badge bg-success bg-opacity-25 text-success">BUY</span>' : '<span class="badge bg-danger bg-opacity-25 text-danger">SELL</span>';
        const statusBadge = getStatusBadge(o.status);

        const tr = document.createElement('tr');
        tr.innerHTML = `
            <td><small class="text-white fw-medium">${formatISTTime(o.createdAt)} IST</small></td>
            <td class="fw-bold">${o.symbol}</td>
            <td>${sideBadge}</td>
            <td>${o.orderType === 0 || o.orderType === 'Market' ? 'Market' : 'Limit'}</td>
            <td>${o.quantity}</td>
            <td>${o.price > 0 ? '₹' + o.price.toFixed(2) : 'MKT'}</td>
            <td>${statusBadge}</td>
            <td class="text-end">
                ${(o.status === 0 || o.status === 'Pending') ? `<button class="btn btn-outline-secondary btn-sm cancel-order-btn" data-id="${o.id}">Cancel</button>` : '-'}
            </td>
        `;
        ordersTableBody.appendChild(tr);
    });

    document.querySelectorAll('.cancel-order-btn').forEach(btn => {
        btn.addEventListener('click', () => handleCancelOrder(btn.dataset.id));
    });
}

function getStatusBadge(status) {
    if (status === 0 || status === 'Pending') return '<span class="badge bg-warning text-dark">Pending</span>';
    if (status === 1 || status === 'Filled') return '<span class="badge bg-success">Filled</span>';
    if (status === 2 || status === 'Cancelled') return '<span class="badge bg-secondary">Cancelled</span>';
    return '<span class="badge bg-danger">Rejected</span>';
}

async function handleCancelOrder(orderId) {
    try {
        const res = await fetch(`${apiBaseUrl}/api/papertrading/order/${orderId}`, { method: 'DELETE' });
        if (res.ok) {
            showToast('Order cancelled successfully', 'info');
            loadOrders();
        }
    } catch (err) {
        console.error('Failed to cancel order:', err);
    }
}

async function loadHistory(page = 1) {
    try {
        const historyTableBody = document.getElementById('historyTableBody');
        if (!historyTableBody) return;
        historyPageState.page = page;
        const query = new URLSearchParams({
            page: historyPageState.page,
            pageSize: 10,
            symbol: historyPageState.symbol || '',
            side: historyPageState.side || '',
            fromDate: historyPageState.fromDate || '',
            toDate: historyPageState.toDate || ''
        });

        const res = await fetch(`${apiBaseUrl}/api/papertrading/history/paged?${query.toString()}`);
        if (!res.ok) return;
        const pagedData = await res.json();
        renderHistory(pagedData);
    } catch (err) {
        console.error('Error loading paged history:', err);
    }
}

function renderHistory(pagedData) {
    const historyTableBody = document.getElementById('historyTableBody');
    if (!historyTableBody) return;
    historyTableBody.innerHTML = '';

    const items = pagedData.items || pagedData.Items || [];
    const totalCount = pagedData.totalCount ?? pagedData.TotalCount ?? 0;
    const page = pagedData.page ?? pagedData.Page ?? 1;
    const pageSize = pagedData.pageSize ?? pagedData.PageSize ?? 10;
    const totalPages = pagedData.totalPages ?? pagedData.TotalPages ?? 0;

    if (!items || items.length === 0) {
        historyTableBody.innerHTML = '<tr><td colspan="8" class="text-center text-white py-3">No execution history logged for selected criteria.</td></tr>';
        renderPaginationControls(0, 0, 0, 1, 0);
        return;
    }

    items.forEach(h => {
        const sideBadge = h.side === 0 || h.side === 'BUY' ? '<span class="badge bg-success bg-opacity-25 text-success">BUY</span>' : '<span class="badge bg-danger bg-opacity-25 text-danger">SELL</span>';
        const pnl = h.realizedPnl || 0;
        const pnlClass = pnl > 0 ? 'text-success' : (pnl < 0 ? 'text-danger' : 'text-white');
        const entryPriceVal = h.entryPrice ?? h.EntryPrice ?? 0;

        const tr = document.createElement('tr');
        tr.innerHTML = `
            <td style="color:#ffffff !important;"><small class="text-white fw-medium">${formatIST(h.executedAt)}</small></td>
            <td class="fw-bold">${h.symbol}</td>
            <td>${sideBadge}</td>
            <td>${h.quantity}</td>
            <td>₹${entryPriceVal > 0 ? entryPriceVal.toFixed(2) : '-'}</td>
            <td>₹${h.executedPrice ? h.executedPrice.toFixed(2) : '0.00'}</td>
            <td class="${pnlClass}">${pnl >= 0 ? '+' : ''}₹${pnl.toFixed(2)}</td>
            <td style="color:#ffffff !important;"><small class="text-white fw-medium">${h.remarks || '-'}</small></td>
        `;
        historyTableBody.appendChild(tr);
    });

    const startItem = (page - 1) * pageSize + 1;
    const endItem = Math.min(page * pageSize, totalCount);
    renderPaginationControls(startItem, endItem, totalCount, page, totalPages);
}

function renderPaginationControls(startItem, endItem, totalCount, currentPage, totalPages) {
    const infoSpan = document.getElementById('historyPaginationInfo');
    const ul = document.getElementById('historyPaginationUl');

    if (infoSpan) {
        if (totalCount === 0) {
            infoSpan.innerText = 'Showing 0 of 0 trades';
        } else {
            infoSpan.innerText = `Showing ${startItem}-${endItem} of ${totalCount} trades`;
        }
    }

    if (!ul) return;
    ul.innerHTML = '';

    if (totalPages <= 1) return;

    // Previous Button
    const prevLi = document.createElement('li');
    prevLi.className = `page-item ${currentPage <= 1 ? 'disabled' : ''}`;
    prevLi.innerHTML = `<a class="page-link bg-dark text-light border-secondary" href="javascript:void(0)" aria-label="Previous">« Prev</a>`;
    if (currentPage > 1) {
        prevLi.addEventListener('click', () => loadHistory(currentPage - 1));
    }
    ul.appendChild(prevLi);

    // Page Numbers
    for (let i = 1; i <= totalPages; i++) {
        const li = document.createElement('li');
        const isActive = i === currentPage;
        li.className = `page-item ${isActive ? 'active' : ''}`;
        li.innerHTML = `<a class="page-link ${isActive ? 'bg-primary text-white border-primary fw-bold' : 'bg-dark text-light border-secondary'}" href="javascript:void(0)">${i}</a>`;
        if (!isActive) {
            const pNum = i;
            li.addEventListener('click', () => loadHistory(pNum));
        }
        ul.appendChild(li);
    }

    // Next Button
    const nextLi = document.createElement('li');
    nextLi.className = `page-item ${currentPage >= totalPages ? 'disabled' : ''}`;
    nextLi.innerHTML = `<a class="page-link bg-dark text-light border-secondary" href="javascript:void(0)" aria-label="Next">Next »</a>`;
    if (currentPage < totalPages) {
        nextLi.addEventListener('click', () => loadHistory(currentPage + 1));
    }
    ul.appendChild(nextLi);
}


function setupEventListeners() {
    const btnFilterHistory = document.getElementById('btnFilterHistory');
    const btnResetHistoryFilter = document.getElementById('btnResetHistoryFilter');

    if (btnFilterHistory) {
        btnFilterHistory.addEventListener('click', () => {
            historyPageState.symbol = document.getElementById('historyFilterSymbol')?.value || '';
            historyPageState.side = document.getElementById('historyFilterSide')?.value || '';
            historyPageState.fromDate = document.getElementById('historyFilterFromDate')?.value || '';
            historyPageState.toDate = document.getElementById('historyFilterToDate')?.value || '';
            loadHistory(1);
        });
    }

    if (btnResetHistoryFilter) {
        btnResetHistoryFilter.addEventListener('click', () => {
            const symEl = document.getElementById('historyFilterSymbol');
            const sideEl = document.getElementById('historyFilterSide');
            if (symEl) {
                symEl.value = '';
                if (window.jQuery && $.fn.select2 && $(symEl).data('select2')) $(symEl).trigger('change.select2');
            }
            if (sideEl) {
                sideEl.value = '';
                if (window.jQuery && $.fn.select2 && $(sideEl).data('select2')) $(sideEl).trigger('change.select2');
            }
            const todayStr = getTodayDateString();
            if (document.getElementById('historyFilterFromDate')) document.getElementById('historyFilterFromDate').value = todayStr;
            if (document.getElementById('historyFilterToDate')) document.getElementById('historyFilterToDate').value = todayStr;
            historyPageState = { page: 1, pageSize: 10, symbol: '', side: '', fromDate: todayStr, toDate: todayStr };
            loadHistory(1);
        });
    }
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

    // Enable/Disable Stop Loss Toggle Handler
    const chkSL = document.getElementById("chkEnableStopLoss");
    const inpSL = document.getElementById("txtStopLossPct");
    if (chkSL && inpSL) {
        chkSL.addEventListener("change", function () {
            inpSL.disabled = !this.checked;
            if (this.checked) {
                inpSL.placeholder = "e.g. 3.0";
                if (!inpSL.value) inpSL.value = "3.0";
                inpSL.focus();
            } else {
                inpSL.value = "";
                inpSL.placeholder = "Disabled (No Stop Loss)";
            }
        });
    }

    // Save Settings Form Handler
    const btnSave = document.getElementById("btnSaveSettings");
    if (btnSave) {
        btnSave.addEventListener("click", async function (e) {
            e.preventDefault();

            const chkSLElem = document.getElementById("chkEnableStopLoss");
            const rawSl = document.getElementById("txtStopLossPct")?.value;
            const parsedSl = (chkSLElem && chkSLElem.checked && rawSl !== "" && rawSl !== null && rawSl !== undefined && !isNaN(rawSl) && parseFloat(rawSl) > 0)
                ? parseFloat(rawSl)
                : null;

            const dto = {
                isAutoTradeEnabled: document.getElementById("chkAutoTradeToggle").checked,
                availableCapital: parseFloat(document.getElementById("txtCapital").value) || 100000,
                profitTargetPct: parseFloat(document.getElementById("txtTargetPct").value) || 5.0,
                stopLossPct: parsedSl,
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
                    let errMsg = "Settings update failed. Please check validation limits.";
                    try {
                        const errObj = await res.json();
                        if (errObj && errObj.errors) {
                            const firstErr = Object.values(errObj.errors).flat()[0];
                            if (firstErr) errMsg = firstErr;
                        } else if (errObj && (errObj.message || errObj.title)) {
                            errMsg = errObj.message || errObj.title;
                        }
                    } catch (_) { }
                    showToast(`⚠️ ${errMsg}`, "error");
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
