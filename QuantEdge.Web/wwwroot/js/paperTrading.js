/**
 * QuantEdge Paper Trading Module Frontend Controller
 * Handles real-time SignalR ticks, interactive order validation, portfolio state updates,
 * and user-friendly glassmorphism toast error notifications.
 */

document.addEventListener('DOMContentLoaded', () => {
    const apiBaseUrl = window.API_BASE_URL || 'https://localhost:44370';

    // DOM Elements
    const statTotalEquity = document.getElementById('statTotalEquity');
    const statAvailableMargin = document.getElementById('statAvailableMargin');
    const statUsedMargin = document.getElementById('statUsedMargin');
    const statUnrealizedPnl = document.getElementById('statUnrealizedPnl');
    const statRealizedPnl = document.getElementById('statRealizedPnl');
    const autoTradeToggle = document.getElementById('autoTradeToggle');
    const resetAccountBtn = document.getElementById('resetAccountBtn');

    const paperOrderForm = document.getElementById('paperOrderForm');
    const orderSymbol = document.getElementById('orderSymbol');
    const orderType = document.getElementById('orderType');
    const limitPriceGroup = document.getElementById('limitPriceGroup');
    const orderLimitPrice = document.getElementById('orderLimitPrice');
    const orderQuantity = document.getElementById('orderQuantity');
    const orderStopLoss = document.getElementById('orderStopLoss');
    const orderTakeProfit = document.getElementById('orderTakeProfit');
    const estimatedMargin = document.getElementById('estimatedMargin');
    const placeOrderBtn = document.getElementById('placeOrderBtn');
    const liveLtpBadge = document.getElementById('liveLtpBadge');

    const positionsTableBody = document.getElementById('positionsTableBody');
    const positionsCountBadge = document.getElementById('positionsCountBadge');
    const ordersTableBody = document.getElementById('ordersTableBody');
    const historyTableBody = document.getElementById('historyTableBody');
    const toastContainer = document.getElementById('toastContainer');

    // Local State
    let currentAccount = { currentBalance: 100000, availableMargin: 100000, usedMargin: 0, realizedPnl: 0 };
    let currentLtpMap = {};
    let activeSymbol = orderSymbol ? orderSymbol.value : 'NIFTY';
    let signalRConnection = null;
    let historyPageState = { page: 1, pageSize: 10, symbol: '', side: '', fromDate: '', toDate: '' };

    // --- 1. Initial Load & Setup ---
    init();

    async function init() {
        bindEvents();
        await loadActiveStocks();
        await loadPortfolio();
        await loadPositions();
        await loadOrders();
        await loadHistory();
        await loadAutoTradeSettings();
        initSignalR();
    }

    async function loadAutoTradeSettings() {
        try {
            const res = await fetch(`${apiBaseUrl}/api/papertrading/settings`);
            if (res.ok) {
                const cfg = await res.json();
                if (autoTradeToggle) autoTradeToggle.checked = !!cfg.isAutoTradeEnabled;
                updateTradingModeBadge(cfg.tradingMode || 'Paper');
            }
        } catch (ex) {
            console.error('Failed to load initial AutoTrade settings:', ex);
        }
    }

    function bindEvents() {
        if (orderSymbol && window.jQuery && $.fn.select2) {
            const $symbol = $(orderSymbol);
            $symbol.select2({
                placeholder: 'Search & Select Stock...',
                width: '100%',
                dropdownParent: $symbol.parent()
            });

            $symbol.on('select2:open', function() {
                const searchField = document.querySelector('.select2-container--open .select2-search__field');
                if (searchField) {
                    searchField.focus();
                }
            });

            $symbol.on('change select2:select', function() {
                activeSymbol = orderSymbol.value;
                updateLtpDisplay();
                validateFormInputs();
            });
        } else if (orderSymbol) {
            orderSymbol.addEventListener('change', () => {
                activeSymbol = orderSymbol.value;
                updateLtpDisplay();
                validateFormInputs();
            });
        }

        if (orderType) {
            orderType.addEventListener('change', () => {
                if (orderType.value === 'Limit') {
                    limitPriceGroup.classList.remove('d-none');
                } else {
                    limitPriceGroup.classList.add('d-none');
                }
                validateFormInputs();
            });
        }

        document.querySelectorAll('input[name="orderSide"]').forEach(radio => {
            radio.addEventListener('change', () => {
                const side = getSelectedSide();
                if (placeOrderBtn) {
                    if (side === 'BUY') {
                        placeOrderBtn.className = 'btn btn-success w-100 fw-bold py-2 rounded-3 shadow';
                        placeOrderBtn.innerText = 'Place BUY Paper Order';
                    } else {
                        placeOrderBtn.className = 'btn btn-danger w-100 fw-bold py-2 rounded-3 shadow';
                        placeOrderBtn.innerText = 'Place SELL Paper Order';
                    }
                }
                validateFormInputs();
            });
        });

        if (orderQuantity) orderQuantity.addEventListener('input', validateFormInputs);
        if (orderLimitPrice) orderLimitPrice.addEventListener('input', validateFormInputs);
        if (orderStopLoss) orderStopLoss.addEventListener('input', validateFormInputs);
        if (orderTakeProfit) orderTakeProfit.addEventListener('input', validateFormInputs);

        if (paperOrderForm) {
            paperOrderForm.addEventListener('submit', handleOrderSubmit);
        }

        if (resetAccountBtn) {
            resetAccountBtn.addEventListener('click', handleResetAccount);
        }

        if (autoTradeToggle) {
            autoTradeToggle.addEventListener('change', handleToggleAutoTrade);
        }

        const btnOpenAutoTradeSettings = document.getElementById('btnOpenAutoTradeSettings');
        if (btnOpenAutoTradeSettings) {
            btnOpenAutoTradeSettings.addEventListener('click', openAutoTradeSettingsModal);
        }

        const btnSaveAutoTradeSettings = document.getElementById('btnSaveAutoTradeSettings');
        if (btnSaveAutoTradeSettings) {
            btnSaveAutoTradeSettings.addEventListener('click', handleSaveAutoTradeSettings);
        }

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
                if (document.getElementById('historyFilterSymbol')) document.getElementById('historyFilterSymbol').value = '';
                if (document.getElementById('historyFilterSide')) document.getElementById('historyFilterSide').value = '';
                if (document.getElementById('historyFilterFromDate')) document.getElementById('historyFilterFromDate').value = '';
                if (document.getElementById('historyFilterToDate')) document.getElementById('historyFilterToDate').value = '';
                historyPageState = { page: 1, pageSize: 10, symbol: '', side: '', fromDate: '', toDate: '' };
                loadHistory(1);
            });
        }
    }

    async function openAutoTradeSettingsModal() {
        try {
            const res = await fetch(`${apiBaseUrl}/api/papertrading/settings`);
            if (res.ok) {
                const cfg = await res.json();
                document.getElementById('cfgTradingMode').value = cfg.tradingMode || 'Paper';
                document.getElementById('cfgAutoTradeTimeframe').value = cfg.autoTradeTimeframe || '1m';
                document.getElementById('cfgMinSignalStrength').value = cfg.autoTradeMinSignalStrength || 70;
                document.getElementById('cfgAutoTradeQuantity').value = cfg.autoTradeQuantity || 25;
                document.getElementById('cfgStopLossPercent').value = cfg.autoTradeStopLossPercent || 1.0;
                document.getElementById('cfgTakeProfitPercent').value = cfg.autoTradeTakeProfitPercent || 2.0;
                document.getElementById('cfgMaxOpenPositions').value = cfg.maxOpenPositions || 5;
                document.getElementById('cfgDailyMaxLossLimit').value = cfg.dailyMaxLossLimit || 2000;
            }
        } catch (ex) {
            console.error('Failed to load AutoTrade settings:', ex);
        }

        const modalEl = document.getElementById('autoTradeSettingsModal');
        if (modalEl && window.bootstrap) {
            const modal = new bootstrap.Modal(modalEl);
            modal.show();
        }
    }

    async function handleSaveAutoTradeSettings() {
        const payload = {
            isAutoTradeEnabled: autoTradeToggle ? autoTradeToggle.checked : false,
            tradingMode: document.getElementById('cfgTradingMode').value,
            autoTradeTimeframe: document.getElementById('cfgAutoTradeTimeframe').value,
            autoTradeMinSignalStrength: parseFloat(document.getElementById('cfgMinSignalStrength').value),
            autoTradeQuantity: parseInt(document.getElementById('cfgAutoTradeQuantity').value),
            autoTradeStopLossPercent: parseFloat(document.getElementById('cfgStopLossPercent').value),
            autoTradeTakeProfitPercent: parseFloat(document.getElementById('cfgTakeProfitPercent').value),
            maxOpenPositions: parseInt(document.getElementById('cfgMaxOpenPositions').value),
            dailyMaxLossLimit: parseFloat(document.getElementById('cfgDailyMaxLossLimit').value)
        };

        try {
            const res = await fetch(`${apiBaseUrl}/api/papertrading/settings`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload)
            });

            if (res.ok) {
                showToast('Settings Saved', 'Auto-Trade strategy & risk settings updated successfully.', 'success');
                updateTradingModeBadge(payload.tradingMode);
                const modalEl = document.getElementById('autoTradeSettingsModal');
                if (modalEl && window.bootstrap) {
                    const modalInstance = bootstrap.Modal.getInstance(modalEl);
                    if (modalInstance) modalInstance.hide();
                }
            } else {
                showToast('Error', 'Failed to save strategy settings.', 'danger');
            }
        } catch (ex) {
            console.error('Error saving settings:', ex);
            showToast('Error', 'Failed to save strategy settings.', 'danger');
        }
    }

    function updateTradingModeBadge(mode) {
        const badge = document.getElementById('tradingModeBadge');
        if (!badge) return;
        if (mode === 'Live') {
            badge.className = 'badge bg-danger ms-1 text-uppercase';
            badge.innerText = '🚀 LIVE (Zerodha)';
        } else {
            badge.className = 'badge bg-primary ms-1 text-uppercase';
            badge.innerText = '🧪 PAPER';
        }
    }

    // --- 2. Live Validation & Helper Calculation ---
    function getSelectedSide() {
        const checked = document.querySelector('input[name="orderSide"]:checked');
        return checked ? checked.value : 'BUY';
    }

    function getEffectivePrice() {
        if (orderType && orderType.value === 'Limit') {
            return parseFloat(orderLimitPrice.value) || 0;
        }
        return currentLtpMap[activeSymbol] || 0;
    }

    function validateFormInputs() {
        let isValid = true;
        const side = getSelectedSide();
        const price = getEffectivePrice();
        const qty = parseInt(orderQuantity.value) || 0;
        const sl = parseFloat(orderStopLoss.value) || 0;
        const tp = parseFloat(orderTakeProfit.value) || 0;

        // Reset errors
        resetInputStyles();

        // 1. Quantity Check
        if (qty <= 0) {
            showFieldError(orderQuantity, 'quantityError', 'Quantity must be a positive number greater than 0.');
            isValid = false;
        }

        // 2. Margin Check
        const required = qty * price;
        estimatedMargin.innerText = `₹${required.toLocaleString('en-IN', { minimumFractionDigits: 2 })}`;

        if (required > currentAccount.availableMargin) {
            estimatedMargin.className = 'fw-bold text-danger';
            showFieldError(orderQuantity, 'quantityError', `Required margin (₹${required.toFixed(2)}) exceeds available margin (₹${currentAccount.availableMargin.toFixed(2)}).`);
            isValid = false;
        } else {
            estimatedMargin.className = 'fw-bold text-light';
        }

        // 3. Stop-Loss Bounds Check
        if (sl > 0 && price > 0) {
            if (side === 'BUY' && sl >= price) {
                showFieldError(orderStopLoss, 'stopLossError', `For a BUY trade, Stop-Loss (₹${sl}) must be less than entry price (₹${price.toFixed(2)}).`);
                isValid = false;
            } else if (side === 'SELL' && sl <= price) {
                showFieldError(orderStopLoss, 'stopLossError', `For a SELL trade, Stop-Loss (₹${sl}) must be higher than entry price (₹${price.toFixed(2)}).`);
                isValid = false;
            }
        }

        // 4. Take-Profit Bounds Check
        if (tp > 0 && price > 0) {
            if (side === 'BUY' && tp <= price) {
                showFieldError(orderTakeProfit, 'takeProfitError', `For a BUY trade, Target (₹${tp}) must be higher than entry price (₹${price.toFixed(2)}).`);
                isValid = false;
            } else if (side === 'SELL' && tp >= price) {
                showFieldError(orderTakeProfit, 'takeProfitError', `For a SELL trade, Target (₹${tp}) must be lower than entry price (₹${price.toFixed(2)}).`);
                isValid = false;
            }
        }

        if (placeOrderBtn) {
            placeOrderBtn.disabled = !isValid;
        }

        return isValid;
    }

    function resetInputStyles() {
        [orderQuantity, orderLimitPrice, orderStopLoss, orderTakeProfit].forEach(el => {
            if (el) {
                el.classList.remove('is-invalid');
            }
        });
        document.querySelectorAll('.invalid-feedback').forEach(el => el.innerText = '');
    }

    function showFieldError(inputElement, errorElementId, message) {
        if (inputElement) inputElement.classList.add('is-invalid');
        const errEl = document.getElementById(errorElementId);
        if (errEl) errEl.innerText = message;
    }

    function updateLtpDisplay() {
        const ltp = currentLtpMap[activeSymbol];
        if (liveLtpBadge) {
            if (ltp > 0) {
                liveLtpBadge.innerText = `LTP (${activeSymbol}): ₹${ltp.toFixed(2)}`;
                liveLtpBadge.className = 'badge bg-primary bg-opacity-25 text-info ms-auto font-monospace';
            } else {
                liveLtpBadge.innerText = `LTP (${activeSymbol}): Fetching...`;
            }
        }
    }

    // --- 3. REST API Interaction ---
    async function loadActiveStocks() {
        try {
            const res = await fetch(`${apiBaseUrl}/api/marketdata/stocks`);
            if (!res.ok) return;
            const stocks = await res.json();

            if (orderSymbol && Array.isArray(stocks) && stocks.length > 0) {
                orderSymbol.innerHTML = '';
                const historyFilterSymbol = document.getElementById('historyFilterSymbol');
                if (historyFilterSymbol) historyFilterSymbol.innerHTML = '<option value="">All Symbols</option>';

                stocks.forEach(stock => {
                    const sym = stock.symbol || stock.Symbol;
                    if (sym) {
                        const opt = document.createElement('option');
                        opt.value = sym;
                        opt.innerText = sym;
                        orderSymbol.appendChild(opt);

                        if (historyFilterSymbol) {
                            const opt2 = document.createElement('option');
                            opt2.value = sym;
                            opt2.innerText = sym;
                            historyFilterSymbol.appendChild(opt2);
                        }
                    }
                });
                activeSymbol = orderSymbol.value;

                if (window.jQuery && $.fn.select2) {
                    const $symbol = $(orderSymbol);
                    if ($symbol.data('select2')) {
                        $symbol.trigger('change.select2');
                    } else {
                        $symbol.select2({
                            placeholder: 'Search & Select Stock...',
                            width: '100%',
                            dropdownParent: $symbol.parent()
                        });
                    }
                }

                updateLtpDisplay();
                validateFormInputs();
            }
        } catch (err) {
            console.error('Error fetching active stocks list:', err);
        }
    }

    async function loadPortfolio() {
        try {
            const res = await fetch(`${apiBaseUrl}/api/papertrading/account`);
            if (!res.ok) return;
            const data = await res.json();
            updatePortfolioUi(data);
        } catch (err) {
            console.error('Error loading paper portfolio:', err);
        }
    }

    function updatePortfolioUi(data) {
        if (!data || !data.account) return;
        const acc = data.account;
        currentAccount = acc;

        statTotalEquity.innerText = `₹${(data.totalEquity || acc.currentBalance).toLocaleString('en-IN', { minimumFractionDigits: 2 })}`;
        statAvailableMargin.innerText = `₹${(acc.availableMargin || (acc.currentBalance - acc.usedMargin)).toLocaleString('en-IN', { minimumFractionDigits: 2 })}`;
        statUsedMargin.innerText = `Used Margin: ₹${acc.usedMargin.toLocaleString('en-IN', { minimumFractionDigits: 2 })}`;

        const unPnl = data.totalUnrealizedPnl || 0;
        statUnrealizedPnl.innerText = `${unPnl >= 0 ? '+' : ''}₹${unPnl.toLocaleString('en-IN', { minimumFractionDigits: 2 })}`;
        statUnrealizedPnl.className = `fw-bold mb-0 mt-1 ${unPnl >= 0 ? 'text-success' : 'text-danger'}`;

        const rePnl = acc.realizedPnl || 0;
        statRealizedPnl.innerText = `${rePnl >= 0 ? '+' : ''}₹${rePnl.toLocaleString('en-IN', { minimumFractionDigits: 2 })}`;
        statRealizedPnl.className = `fw-bold mb-0 mt-1 ${rePnl >= 0 ? 'text-success' : 'text-danger'}`;

        if (autoTradeToggle) {
            autoTradeToggle.checked = !!data.autoTradeEnabled;
        }

        validateFormInputs();
    }

    async function loadPositions() {
        try {
            const res = await fetch(`${apiBaseUrl}/api/papertrading/positions`);
            if (!res.ok) return;
            const positions = await res.json();
            renderPositions(positions);
        } catch (err) {
            console.error('Error loading open positions:', err);
        }
    }

    function renderPositions(positions) {
        if (!positionsTableBody) return;
        positionsTableBody.innerHTML = '';

        if (!positions || positions.length === 0) {
            positionsTableBody.innerHTML = '<tr><td colspan="8" class="text-center text-muted py-4">No open positions. Place an order from the ticket to start paper trading.</td></tr>';
            if (positionsCountBadge) positionsCountBadge.innerText = '0 Active';
            return;
        }

        if (positionsCountBadge) positionsCountBadge.innerText = `${positions.length} Active`;

        positions.forEach(pos => {
            const pnl = pos.unrealizedPnl || 0;
            const pnlClass = pnl >= 0 ? 'text-success fw-bold' : 'text-danger fw-bold';
            const sideBadge = pos.side === 0 || pos.side === 'BUY'
                ? '<span class="badge bg-success bg-opacity-25 text-success">BUY</span>'
                : '<span class="badge bg-danger bg-opacity-25 text-danger">SELL</span>';

            const sl = pos.stopLoss ?? pos.StopLoss;
            const tp = pos.takeProfit ?? pos.TakeProfit;
            const slText = sl && sl > 0 ? `₹${parseFloat(sl).toFixed(2)}` : '-';
            const tpText = tp && tp > 0 ? `₹${parseFloat(tp).toFixed(2)}` : '-';

            const tr = document.createElement('tr');
            tr.innerHTML = `
                <td class="fw-bold">${pos.symbol}</td>
                <td>${sideBadge}</td>
                <td>${pos.quantity}</td>
                <td>₹${pos.averageEntryPrice.toFixed(2)}</td>
                <td>₹${(pos.currentPrice || pos.averageEntryPrice).toFixed(2)}</td>
                <td style="color:#ffffff !important;"><small class="text-white fw-bold">SL: ${slText} | TP: ${tpText}</small></td>
                <td class="${pnlClass}">${pnl >= 0 ? '+' : ''}₹${pnl.toFixed(2)}</td>
                <td class="text-end">
                    <button class="btn btn-outline-danger btn-sm rounded-2 close-pos-btn" data-id="${pos.id}">Close</button>
                </td>
            `;
            positionsTableBody.appendChild(tr);
        });

        document.querySelectorAll('.close-pos-btn').forEach(btn => {
            btn.addEventListener('click', () => handleClosePosition(btn.dataset.id));
        });
    }

    async function loadOrders() {
        try {
            const res = await fetch(`${apiBaseUrl}/api/papertrading/orders`);
            if (!res.ok) return;
            const orders = await res.json();
            renderOrders(orders);
        } catch (err) {
            console.error('Error loading orders:', err);
        }
    }

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

    function formatISTTime(dateInput) {
        if (!dateInput) return '-';
        let rawStr = String(dateInput);
        if (!rawStr.endsWith('Z') && !rawStr.includes('+')) rawStr += 'Z';
        let d = new Date(rawStr);
        if (isNaN(d.getTime())) d = new Date(dateInput);
        return d.toLocaleTimeString('en-IN', {
            timeZone: 'Asia/Kolkata',
            hour: '2-digit',
            minute: '2-digit',
            second: '2-digit',
            hour12: true
        });
    }

    function renderOrders(orders) {
        if (!ordersTableBody) return;
        ordersTableBody.innerHTML = '';

        if (!orders || orders.length === 0) {
            ordersTableBody.innerHTML = '<tr><td colspan="8" class="text-center text-muted py-3">No active pending orders.</td></tr>';
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

    async function loadHistory(page = 1) {
        try {
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
        if (!historyTableBody) return;
        historyTableBody.innerHTML = '';

        const items = pagedData.items || pagedData.Items || [];
        const totalCount = pagedData.totalCount ?? pagedData.TotalCount ?? 0;
        const page = pagedData.page ?? pagedData.Page ?? 1;
        const pageSize = pagedData.pageSize ?? pagedData.PageSize ?? 10;
        const totalPages = pagedData.totalPages ?? pagedData.TotalPages ?? 0;

        if (!items || items.length === 0) {
            historyTableBody.innerHTML = '<tr><td colspan="7" class="text-center text-white py-3">No execution history logged for selected criteria.</td></tr>';
            renderPaginationControls(0, 0, 0, 1, 0);
            return;
        }

        items.forEach(h => {
            const sideBadge = h.side === 0 || h.side === 'BUY' ? '<span class="badge bg-success bg-opacity-25 text-success">BUY</span>' : '<span class="badge bg-danger bg-opacity-25 text-danger">SELL</span>';
            const pnl = h.realizedPnl || 0;
            const pnlClass = pnl > 0 ? 'text-success' : (pnl < 0 ? 'text-danger' : 'text-white');

            const tr = document.createElement('tr');
            tr.innerHTML = `
                <td style="color:#ffffff !important;"><small class="text-white fw-medium">${formatIST(h.executedAt)}</small></td>
                <td class="fw-bold">${h.symbol}</td>
                <td>${sideBadge}</td>
                <td>${h.quantity}</td>
                <td>₹${h.executedPrice.toFixed(2)}</td>
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

    // --- 4. User Actions & Handlers ---
    async function handleOrderSubmit(e) {
        e.preventDefault();
        if (!validateFormInputs()) return;

        const payload = {
            symbol: activeSymbol,
            side: getSelectedSide() === 'BUY' ? 0 : 1,
            orderType: orderType.value === 'Market' ? 0 : 1,
            quantity: parseInt(orderQuantity.value),
            price: getEffectivePrice(),
            stopLoss: parseFloat(orderStopLoss.value) || null,
            takeProfit: parseFloat(orderTakeProfit.value) || null
        };

        try {
            placeOrderBtn.disabled = true;
            placeOrderBtn.innerText = 'Processing Trade...';

            const res = await fetch(`${apiBaseUrl}/api/papertrading/order`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload)
            });

            if (!res.ok) {
                const errData = await res.json();
                showToast(errData.title || 'Order Rejection Alert', errData.detail || 'Could not place paper order.', 'danger');
                return;
            }

            const data = await res.json();
            showToast('Order Placed Successfully', `Paper trade for ${payload.quantity} ${activeSymbol} executed.`, 'success');
            
            // Clear optional fields
            orderStopLoss.value = '';
            orderTakeProfit.value = '';

            await loadPortfolio();
            await loadPositions();
            await loadOrders();
            await loadHistory();
        } catch (err) {
            console.error('Order submission exception:', err);
            showToast('System Error', 'Failed to connect to order execution server.', 'danger');
        } finally {
            validateFormInputs();
        }
    }

    async function handleClosePosition(positionId) {
        try {
            const res = await fetch(`${apiBaseUrl}/api/papertrading/position/close`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ positionId: parseInt(positionId), exitPrice: currentLtpMap[activeSymbol] || 0 })
            });

            if (!res.ok) {
                const errData = await res.json();
                showToast('Closure Error', errData.detail || 'Failed to close position.', 'danger');
                return;
            }

            showToast('Position Closed', 'Open paper position has been closed at market price.', 'success');
            await loadPortfolio();
            await loadPositions();
            await loadHistory();
        } catch (err) {
            console.error('Error closing position:', err);
            showToast('System Error', 'Could not execute position closure.', 'danger');
        }
    }

    async function handleCancelOrder(orderId) {
        try {
            const res = await fetch(`${apiBaseUrl}/api/papertrading/order/cancel/${orderId}`, {
                method: 'POST'
            });

            if (!res.ok) {
                const errData = await res.json();
                showToast('Cancel Failed', errData.detail || 'Could not cancel order.', 'danger');
                return;
            }

            showToast('Order Cancelled', `Pending order #${orderId} cancelled.`, 'warning');
            await loadPortfolio();
            await loadOrders();
        } catch (err) {
            console.error('Error cancelling order:', err);
        }
    }

    async function handleResetAccount() {
        if (!confirm('Are you sure you want to reset your virtual account balance to ₹1,00,000? All active open positions and orders will be cleared.')) {
            return;
        }

        try {
            const res = await fetch(`${apiBaseUrl}/api/papertrading/account/reset`, { method: 'POST' });
            if (res.ok) {
                showToast('Account Reset', 'Virtual account balance reset to ₹1,00,000.', 'success');
                await loadPortfolio();
                await loadPositions();
                await loadOrders();
                await loadHistory();
            }
        } catch (err) {
            console.error('Error resetting account:', err);
        }
    }

    async function handleToggleAutoTrade() {
        const enabled = autoTradeToggle.checked;
        try {
            await fetch(`${apiBaseUrl}/api/papertrading/settings/autotrade`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ enabled })
            });
            showToast('Auto-Trade Setting', `Auto-Trade on AI Signals ${enabled ? 'ENABLED ⚡' : 'DISABLED'}.`, enabled ? 'info' : 'warning');
        } catch (err) {
            console.error('Error toggling auto trade:', err);
        }
    }

    // --- 5. SignalR Real-Time Streams ---
    function initSignalR() {
        if (typeof signalR === 'undefined') return;

        signalRConnection = new signalR.HubConnectionBuilder()
            .withUrl(`${apiBaseUrl}/hubs/marketdata`)
            .withAutomaticReconnect()
            .build();

        signalRConnection.on('ReceiveActiveCandle', (data) => {
            if (data && data.symbol && data.close) {
                currentLtpMap[data.symbol] = data.close;
                if (data.symbol === activeSymbol) {
                    updateLtpDisplay();
                    validateFormInputs();
                }
            }
        });

        signalRConnection.on('ReceivePaperAccountUpdate', (portfolio) => {
            updatePortfolioUi(portfolio);
        });

        signalRConnection.on('ReceivePaperPositionsUpdate', (positions) => {
            renderPositions(positions);
        });

        signalRConnection.on('ReceivePaperError', (err) => {
            if (err) {
                showToast(err.errorCode || 'Trading Error', err.message || 'An error occurred during trade execution.', 'danger');
            }
        });

        signalRConnection.on('ReceiveAutoTradeAlert', (alert) => {
            if (alert && alert.message) {
                const toastType = alert.mode === 'Live' ? 'danger' : 'success';
                showToast(`⚡ Auto-Trade (${alert.mode})`, alert.message, toastType);
            }
        });

        signalRConnection.start()
            .then(() => {
                console.log('SignalR connected for Paper Trading ticks.');
                signalRConnection.invoke('Subscribe', activeSymbol, '1m').catch(console.error);
            })
            .catch(err => console.error('SignalR Connection Error:', err));
    }

    // --- 6. Toast UI Helper ---
    function showToast(title, message, type = 'info') {
        if (!toastContainer) return;

        const icon = type === 'success' ? '✅' : (type === 'danger' ? '❌' : (type === 'warning' ? '⚠️' : 'ℹ️'));
        const toast = document.createElement('div');
        toast.className = `toast align-items-center text-white bg-${type} bg-opacity-90 border-0 shadow-lg mb-2 show`;
        toast.setAttribute('role', 'alert');
        toast.style.backdropFilter = 'blur(10px)';

        toast.innerHTML = `
            <div class="d-flex">
                <div class="toast-body d-flex align-items-start gap-2">
                    <span class="fs-5">${icon}</span>
                    <div>
                        <strong class="d-block fw-bold">${title}</strong>
                        <span class="small">${message}</span>
                    </div>
                </div>
                <button type="button" class="btn-close btn-close-white me-2 m-auto" data-bs-dismiss="toast"></button>
            </div>
        `;

        toastContainer.appendChild(toast);
        setTimeout(() => {
            toast.classList.remove('show');
            setTimeout(() => toast.remove(), 300);
        }, 5000);
    }
});
