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
    const orderQuantity = document.getElementById('orderQuantity');
    const orderSlPct = document.getElementById('orderSlPct');
    const orderTslPct = document.getElementById('orderTslPct');
    const estimatedMargin = document.getElementById('estimatedMargin');
    const estSlPrice = document.getElementById('estSlPrice');
    const estRisk = document.getElementById('estRisk');
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
    let currentLtpTimeMap = {}; // symbol -> stored candle time of currentLtpMap price
    let activeSymbol = orderSymbol ? orderSymbol.value : 'NIFTY';
    let signalRConnection = null;
    let historyPageState = { page: 1, pageSize: 10, symbol: '', side: '', fromDate: '', toDate: '' };
    let ordersPageState = { page: 1, pageSize: 10, symbol: '', side: '', status: '', fromDate: '', toDate: '' };
    let manualRefreshTimer = null; // debounce for Manual-only dashboard reloads
    let lastRenderedPositions = []; // rows shown in Live Open Positions (Edit dialog pre-fill)
    let manualSettings = null; // manual_paper_trade_settings
    let quantityTouched = false; // user typed a Quantity - stop auto-filling it from Amount / LTP
    let quantityDefaultedFor = null; // symbol the Quantity was last auto-filled for (once per symbol)

    // --- 1. Initial Load & Setup ---
    init();

    async function init() {
        bindEvents();
        await loadActiveStocks();
        await loadPortfolio();
        await loadPositions();
        await loadOrders();
        await loadHistory();
        await loadManualTradeDefaults();
        await loadManualLogs();
        initSignalR();
        await refreshQuote(activeSymbol);

        // Keep the displayed LTP (and so Required Capital / SL price / Risk) fresh every 5s, like the
        // Manual Real Trade popup. Display only - the server re-checks the live price at order time.
        setInterval(() => refreshQuote(activeSymbol), 5000);

        // Keep the Manual stat cards / open positions' live P&L current between trade events.
        setInterval(scheduleManualDashboardRefresh, 10000);
    }

    function onSymbolChanged(newSymbol) {
        if (!newSymbol || newSymbol === activeSymbol) {
            updateLtpDisplay();
            validateFormInputs();
            return;
        }

        activeSymbol = newSymbol;
        quantityTouched = false;
        applyDefaultQuantity();
        updateLtpDisplay();
        validateFormInputs();

        refreshQuote(newSymbol);
    }

    // Latest stored 1-minute close from Postgres (market_candles_1m) - the same price a BUY fills at.
    // Manual Trading never asks Zerodha for a live price.
    async function refreshQuote(symbol) {
        if (!symbol) return;
        try {
            const res = await fetch(`${apiBaseUrl}/api/manualpapertrade/quote?symbol=${encodeURIComponent(symbol)}`);
            if (!res.ok) return;
            const q = await res.json();
            if (q && q.success && q.ltp > 0) {
                currentLtpMap[symbol] = Number(q.ltp);
                currentLtpTimeMap[symbol] = q.asOfUtc || null;
                if (symbol === activeSymbol) {
                    if (quantityDefaultedFor !== symbol) applyDefaultQuantity();
                    updateLtpDisplay();
                    validateFormInputs();
                }
            }
        } catch (ex) {
            // Transient failure - keep the last known price.
        }
    }

    // --- Manual Paper Trading settings (manual_paper_trade_settings) ---
    // Pre-fills trade-wise SL%/Trailing SL% from the Manual Trading settings - the same defaults the
    // Manual Real Trade popup takes from Real Trade settings. Fixed defaults stay if unreachable.
    async function loadManualTradeDefaults() {
        try {
            const res = await fetch(`${apiBaseUrl}/api/manualpapertrade/settings`);
            if (!res.ok) return;
            manualSettings = await res.json();
            if (orderSlPct && manualSettings.stopLossPct > 0) orderSlPct.value = manualSettings.stopLossPct;
            if (orderTslPct && manualSettings.trailingSlPct > 0) orderTslPct.value = manualSettings.trailingSlPct;
            updateManualToggleUi(!!manualSettings.isManualTradeEnabled);
            applyDefaultQuantity();
            validateFormInputs();
        } catch (ex) {
            console.error('Failed to load manual trade defaults:', ex);
        }
    }

    // Default Quantity = Default Amount per Trade / LTP (like the Manual Real Trade popup), until the
    // user types their own Quantity for the selected symbol.
    function applyDefaultQuantity() {
        if (quantityTouched || !orderQuantity || !manualSettings) return;
        const ltp = getEffectivePrice();
        if (manualSettings.fixedAmountPerTrade > 0 && ltp > 0) {
            orderQuantity.value = Math.max(1, Math.floor(manualSettings.fixedAmountPerTrade / ltp));
            quantityDefaultedFor = activeSymbol;
        }
    }

    function updateManualToggleUi(enabled) {
        const toggle = document.getElementById('manualTradeToggle');
        const badge = document.getElementById('manualTradeStatusBadge');
        if (toggle) toggle.checked = enabled;
        if (badge) {
            badge.className = enabled ? 'badge bg-success' : 'badge bg-danger';
            badge.innerText = enabled ? 'ON' : 'OFF';
        }
    }

    async function handleManualToggle(e) {
        const enabled = e.target.checked;
        try {
            const res = await fetch(`${apiBaseUrl}/api/manualpapertrade/toggle`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ enabled })
            });
            if (!res.ok) throw new Error(`HTTP ${res.status}`);
            if (manualSettings) manualSettings.isManualTradeEnabled = enabled;
            updateManualToggleUi(enabled);
            showToast('Manual Trading', `Manual Paper Trading switched ${enabled ? 'ON' : 'OFF'}.`, enabled ? 'success' : 'warning');
            await loadManualLogs();
        } catch (ex) {
            console.error('Failed to toggle manual trading:', ex);
            updateManualToggleUi(!enabled);
            showToast('Error', 'Failed to change the Manual Trading switch.', 'danger');
        }
    }

    // Settings modal field id -> settings property
    const manualSettingsFields = {
        msStopLossPct: 'stopLossPct',
        msTrailingSlPct: 'trailingSlPct',
        msFixedAmountPerTrade: 'fixedAmountPerTrade',
        msMaxTradesPerDay: 'maxTradesPerDay',
        msAvailableCapital: 'availableCapital',
        msMaxDailyLossLimit: 'maxDailyLossLimit',
        msTradingWindowStart: 'tradingWindowStart',
        msTradingWindowEnd: 'tradingWindowEnd',
        msEntryDelayMinutes: 'entryDelayMinutes',
        msExitMode: 'exitMode',
        msCloseCheckTime: 'closeCheckTime',
        msMaxDurationDays: 'maxDurationDays',
        msProfitTargetPct: 'profitTargetPct',
        msStopLossAtrMult: 'stopLossAtrMult',
        msTrailAtrMult: 'trailAtrMult',
        msTargetAtrMult: 'targetAtrMult'
    };
    const manualSettingsTextFields = ['tradingWindowStart', 'tradingWindowEnd', 'exitMode', 'closeCheckTime'];

    async function openManualSettingsModal() {
        await loadManualTradeDefaults();
        if (!manualSettings) {
            showToast('Error', 'Could not load Manual Trading settings.', 'danger');
            return;
        }
        Object.entries(manualSettingsFields).forEach(([id, prop]) => {
            const el = document.getElementById(id);
            if (el) el.value = manualSettings[prop] ?? '';
        });
        const modalEl = document.getElementById('manualSettingsModal');
        if (modalEl && window.bootstrap) bootstrap.Modal.getOrCreateInstance(modalEl).show();
    }

    async function handleSaveManualSettings() {
        const payload = { isManualTradeEnabled: manualSettings ? !!manualSettings.isManualTradeEnabled : true };
        Object.entries(manualSettingsFields).forEach(([id, prop]) => {
            const el = document.getElementById(id);
            if (!el) return;
            const raw = (el.value || '').trim();
            if (manualSettingsTextFields.includes(prop)) {
                payload[prop] = raw;
            } else if (prop === 'maxDailyLossLimit') {
                payload[prop] = raw === '' ? null : Number(raw);
            } else {
                payload[prop] = Number(raw);
            }
        });

        try {
            const res = await fetch(`${apiBaseUrl}/api/manualpapertrade/settings`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload)
            });
            if (!res.ok) {
                const err = await res.json().catch(() => ({}));
                const firstError = err.errors ? Object.values(err.errors).flat()[0] : null;
                showToast('Settings Not Saved', firstError || err.title || 'Invalid settings.', 'danger');
                return;
            }
            manualSettings = await res.json();
            const modalEl = document.getElementById('manualSettingsModal');
            if (modalEl && window.bootstrap) bootstrap.Modal.getOrCreateInstance(modalEl).hide();
            if (orderSlPct) orderSlPct.value = manualSettings.stopLossPct;
            if (orderTslPct) orderTslPct.value = manualSettings.trailingSlPct;
            validateFormInputs();
            showToast('Settings Saved', 'Manual Paper Trading settings updated.', 'success');
            await loadManualLogs();
        } catch (ex) {
            console.error('Failed to save manual settings:', ex);
            showToast('Error', 'Failed to save Manual Trading settings.', 'danger');
        }
    }

    // --- Today's Manual Paper Trade logs (manual_paper_trade_execution_logs) ---
    function escapeHtml(value) {
        return String(value ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
    }

    function getLogBadgeClass(actionType) {
        switch (actionType) {
            case 'MANUAL_BUY': return 'bg-success';
            case 'MANUAL_SELL': return 'bg-info text-dark';
            case 'TRADE_SKIPPED': return 'bg-warning text-dark';
            case 'CIRCUIT_BREAKER':
            case 'SYSTEM_ERROR': return 'bg-danger';
            case 'TRAILING_SL_ACTIVATED':
            case 'LEVELS_UPDATED': return 'bg-primary';
            default: return 'bg-secondary';
        }
    }

    async function loadManualLogs() {
        const list = document.getElementById('manualLogsList');
        const countBadge = document.getElementById('manualTradeCountBadge');
        try {
            const res = await fetch(`${apiBaseUrl}/api/manualpapertrade/logs?limit=50`);
            if (!res.ok) return;
            const data = await res.json();
            const logs = data.logs || [];
            if (countBadge) {
                const max = manualSettings ? manualSettings.maxTradesPerDay : '-';
                countBadge.innerText = `${data.todayTradeCount || 0} / ${max} trades`;
            }
            if (!list) return;
            if (logs.length === 0) {
                list.innerHTML = '<div class="text-white-50">No manual trade activity today.</div>';
                return;
            }
            list.innerHTML = logs.map(l => `
                <div class="border-bottom border-secondary border-opacity-25 py-2">
                    <div class="d-flex justify-content-between align-items-center gap-2">
                        <span><span class="badge ${getLogBadgeClass(l.actionType)}">${escapeHtml(l.actionType)}</span>
                            <strong class="text-light ms-1">${escapeHtml(l.symbol)}</strong></span>
                        <span class="text-white-50">${formatISTTime(l.executedAt)}</span>
                    </div>
                    <div class="text-white mt-1">${escapeHtml(l.reason)}</div>
                </div>`).join('');
        } catch (ex) {
            console.error('Failed to load manual trade logs:', ex);
        }
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
                onSymbolChanged(orderSymbol.value);
            });
        } else if (orderSymbol) {
            orderSymbol.addEventListener('change', () => {
                onSymbolChanged(orderSymbol.value);
            });
        }

        if (orderQuantity) orderQuantity.addEventListener('input', () => {
            quantityTouched = true;
            validateFormInputs();
        });

        const manualTradeToggle = document.getElementById('manualTradeToggle');
        if (manualTradeToggle) manualTradeToggle.addEventListener('change', handleManualToggle);

        const btnOpenManualSettings = document.getElementById('btnOpenManualSettings');
        if (btnOpenManualSettings) btnOpenManualSettings.addEventListener('click', openManualSettingsModal);

        const btnSaveManualSettings = document.getElementById('btnSaveManualSettings');
        if (btnSaveManualSettings) btnSaveManualSettings.addEventListener('click', handleSaveManualSettings);

        const btnSaveLevels = document.getElementById('btnSaveLevels');
        if (btnSaveLevels) btnSaveLevels.addEventListener('click', handleSaveLevels);
        ['editStopLoss', 'editTakeProfit'].forEach(id => {
            const el = document.getElementById(id);
            if (el) el.addEventListener('input', updateEditLevelsPreview);
        });
        if (orderSlPct) orderSlPct.addEventListener('input', validateFormInputs);
        if (orderTslPct) orderTslPct.addEventListener('input', validateFormInputs);

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

        const btnFilterOrders = document.getElementById('btnFilterOrders');
        if (btnFilterOrders) {
            btnFilterOrders.addEventListener('click', () => {
                ordersPageState.symbol = document.getElementById('ordersFilterSymbol')?.value || '';
                ordersPageState.side = document.getElementById('ordersFilterSide')?.value || '';
                ordersPageState.status = document.getElementById('ordersFilterStatus')?.value || '';
                ordersPageState.fromDate = document.getElementById('ordersFilterFromDate')?.value || '';
                ordersPageState.toDate = document.getElementById('ordersFilterToDate')?.value || '';
                loadOrders(1);
            });
        }

        const btnResetOrdersFilter = document.getElementById('btnResetOrdersFilter');
        if (btnResetOrdersFilter) {
            btnResetOrdersFilter.addEventListener('click', () => {
                ['ordersFilterSymbol', 'ordersFilterSide', 'ordersFilterStatus', 'ordersFilterFromDate', 'ordersFilterToDate']
                    .forEach(id => { const el = document.getElementById(id); if (el) el.value = ''; });
                ordersPageState = { ...ordersPageState, page: 1, symbol: '', side: '', status: '', fromDate: '', toDate: '' };
                loadOrders(1);
            });
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
    function getEffectivePrice() {
        return currentLtpMap[activeSymbol] || 0;
    }

    // Same client-side checks as the Manual Real Trade popup - the server re-runs every risk gate.
    function validateFormInputs() {
        let isValid = true;
        const price = getEffectivePrice();
        const qty = parseInt(orderQuantity.value, 10) || 0;
        const slPct = parseFloat(orderSlPct.value) || 0;
        const tslPct = parseFloat(orderTslPct.value) || 0;

        // Reset errors
        resetInputStyles();

        // 1. Quantity Check
        if (qty < 1) {
            showFieldError(orderQuantity, 'quantityError', 'Quantity must be a positive whole number.');
            isValid = false;
        }

        // 2. SL% / Trailing SL% - both mandatory
        if (slPct <= 0) {
            showFieldError(orderSlPct, 'slPctError', 'Stop Loss % must be greater than zero.');
            isValid = false;
        }
        if (tslPct <= 0) {
            showFieldError(orderTslPct, 'tslPctError', 'Trailing Stop Loss % must be greater than zero.');
            isValid = false;
        }

        // 3. Capital Check
        const required = qty * price;
        estimatedMargin.innerText = `₹${required.toLocaleString('en-IN', { minimumFractionDigits: 2 })}`;

        if (required > currentAccount.availableMargin) {
            estimatedMargin.className = 'fw-bold text-danger';
            showFieldError(orderQuantity, 'quantityError', `Required capital (₹${required.toFixed(2)}) exceeds available margin (₹${currentAccount.availableMargin.toFixed(2)}).`);
            isValid = false;
        } else {
            estimatedMargin.className = 'fw-bold text-light';
        }

        // 4. Estimated SL price & risk
        const slPrice = (slPct > 0 && price > 0) ? price * (1 - slPct / 100) : 0;
        if (estSlPrice) estSlPrice.textContent = slPrice > 0 ? `₹${slPrice.toFixed(2)}` : '-';
        if (estRisk) estRisk.textContent = (slPrice > 0 && qty > 0) ? `₹${((price - slPrice) * qty).toFixed(2)}` : '-';

        if (!activeSymbol || price <= 0) {
            isValid = false;
        }

        if (placeOrderBtn) {
            placeOrderBtn.disabled = !isValid;
        }

        return isValid;
    }

    function resetInputStyles() {
        [orderQuantity, orderSlPct, orderTslPct].forEach(el => {
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
                // Show the stored candle's time so a stale price (symbol no longer updating) is obvious.
                const asOf = currentLtpTimeMap[activeSymbol];
                const asOfText = asOf ? ` @ ${formatISTTime(asOf)}` : '';
                liveLtpBadge.innerText = `LTP (${activeSymbol}): ₹${ltp.toFixed(2)}${asOfText}`;
                liveLtpBadge.className = 'badge bg-primary bg-opacity-25 text-info ms-auto font-monospace';
            } else {
                liveLtpBadge.innerText = `LTP (${activeSymbol}): No stored price`;
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
                const ordersFilterSymbol = document.getElementById('ordersFilterSymbol');
                if (ordersFilterSymbol) ordersFilterSymbol.innerHTML = '<option value="">All Symbols</option>';

                stocks.forEach(stock => {
                    const sym = stock.symbol || stock.Symbol;
                    if (sym) {
                        const opt = document.createElement('option');
                        opt.value = sym;
                        opt.innerText = sym;
                        orderSymbol.appendChild(opt);

                        [historyFilterSymbol, ordersFilterSymbol].forEach(filterSelect => {
                            if (!filterSelect) return;
                            const filterOpt = document.createElement('option');
                            filterOpt.value = sym;
                            filterOpt.innerText = sym;
                            filterSelect.appendChild(filterOpt);
                        });
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

    // Manual-only stat cards from the paper_* tables (fn_get_manual_paper_dashboard).
    async function loadPortfolio() {
        try {
            const res = await fetch(`${apiBaseUrl}/api/manualpapertrade/dashboard`);
            if (!res.ok) return;
            const data = await res.json();
            updatePortfolioUi(data);
        } catch (err) {
            console.error('Error loading manual paper dashboard:', err);
        }
    }

    function formatInr(value) {
        return `₹${Number(value || 0).toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
    }

    function formatSignedInr(value) {
        const v = Number(value || 0);
        return `${v >= 0 ? '+' : '-'}${formatInr(Math.abs(v))}`;
    }

    function updatePortfolioUi(data) {
        if (!data) return;

        // The BUY capital check (validateFormInputs) uses Manual Trading's own cash, same as the server.
        currentAccount = { ...currentAccount, availableMargin: data.manualAvailableMargin || 0, usedMargin: data.manualUsedMargin || 0 };

        statTotalEquity.innerText = formatInr(data.manualEquity);
        const capitalEl = document.getElementById('statManualCapital');
        if (capitalEl) capitalEl.innerText = `Capital: ${formatInr(data.manualCapital)}`;

        statAvailableMargin.innerText = formatInr(data.manualAvailableMargin);
        statUsedMargin.innerText = `Manual Used Margin: ${formatInr(data.manualUsedMargin)}`;

        const unPnl = data.manualUnrealizedPnl || 0;
        statUnrealizedPnl.innerText = formatSignedInr(unPnl);
        statUnrealizedPnl.className = `fw-bold mb-0 mt-1 ${unPnl >= 0 ? 'text-success' : 'text-danger'}`;
        const openEl = document.getElementById('statOpenPositions');
        if (openEl) openEl.innerText = `${data.openPositionsCount || 0} Open Manual Position${data.openPositionsCount === 1 ? '' : 's'}`;

        const rePnl = data.manualRealizedPnl || 0;
        statRealizedPnl.innerText = formatSignedInr(rePnl);
        statRealizedPnl.className = `fw-bold mb-0 mt-1 ${rePnl >= 0 ? 'text-success' : 'text-danger'}`;
        const realizedSubEl = document.getElementById('statRealizedSub');
        if (realizedSubEl) {
            realizedSubEl.innerText = `Today: ${formatSignedInr(data.manualTodayRealizedPnl)} · Win Rate: ${data.winRatePct || 0}% (${data.winningTrades || 0}/${data.closedTrades || 0})`;
        }

        if (typeof data.isManualTradeEnabled === 'boolean') updateManualToggleUi(data.isManualTradeEnabled);

        validateFormInputs();
    }

    // Shared paper-account broadcasts (ReceivePaperAccountUpdate / ReceivePaperPositionsUpdate) carry
    // Auto + Manual data - on this page they only trigger a Manual-only reload, debounced.

    function scheduleManualDashboardRefresh() {
        if (manualRefreshTimer) clearTimeout(manualRefreshTimer);
        manualRefreshTimer = setTimeout(async () => {
            manualRefreshTimer = null;
            await loadPortfolio();
            await loadPositions();
        }, 500);
    }

    // Manual-only open positions (fn_get_manual_paper_open_positions).
    async function loadPositions() {
        try {
            const res = await fetch(`${apiBaseUrl}/api/manualpapertrade/positions`);
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
            positionsTableBody.innerHTML = '<tr><td colspan="10" class="text-center text-muted py-4">No open positions. Place an order from the ticket to start paper trading.</td></tr>';
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
            const tsl = pos.trailingStopLoss ?? pos.TrailingStopLoss;
            const tslPct = pos.trailingSlPct ?? pos.TrailingSlPct;
            const slText = sl && sl > 0 ? `₹${parseFloat(sl).toFixed(2)}` : '-';
            const tpText = tp && tp > 0 ? `₹${parseFloat(tp).toFixed(2)}` : '-';
            // Trailing SL has no level until the trade has moved in our favour (SWING_CLOSE) - show its %.
            const tslText = tsl && tsl > 0
                ? `₹${parseFloat(tsl).toFixed(2)}`
                : (tslPct > 0 ? `<span class="text-white-50">Not active (${parseFloat(tslPct).toFixed(2)}%)</span>` : '-');
            const ltpVal = pos.currentPrice || pos.averageEntryPrice;
            const toTargetPct = tp > 0 && ltpVal > 0 ? ((tp - ltpVal) / ltpVal) * 100 : null;
            const toTargetText = toTargetPct !== null
                ? `<small class="d-block text-white-50">${toTargetPct >= 0 ? toTargetPct.toFixed(2) + '% away' : 'reached'}</small>`
                : '';

            const tr = document.createElement('tr');
            tr.innerHTML = `
                <td class="fw-bold">${pos.symbol}</td>
                <td>${sideBadge}</td>
                <td>${pos.quantity}</td>
                <td>₹${pos.averageEntryPrice.toFixed(2)}</td>
                <td>₹${(pos.currentPrice || pos.averageEntryPrice).toFixed(2)}</td>
                <td class="text-danger fw-semibold">${slText}</td>
                <td class="text-warning fw-semibold">${tslText}</td>
                <td class="text-success fw-bold">${tpText}${toTargetText}</td>
                <td class="${pnlClass}">${pnl >= 0 ? '+' : ''}₹${pnl.toFixed(2)}</td>
                <td class="text-end text-nowrap">
                    ${tslPct > 0 ? `<button class="btn btn-outline-primary btn-sm rounded-2 me-1 edit-pos-btn" data-id="${pos.id}" title="Edit Stop Loss / Trailing SL / Target">✏️ Edit</button>` : ''}
                    <button class="btn btn-outline-danger btn-sm rounded-2 close-pos-btn" data-id="${pos.id}">Close</button>
                </td>
            `;
            positionsTableBody.appendChild(tr);
        });

        document.querySelectorAll('.close-pos-btn').forEach(btn => {
            btn.addEventListener('click', () => handleClosePosition(btn.dataset.id));
        });

        // Keep the rendered rows so the Edit dialog can pre-fill from the same values the table shows.
        lastRenderedPositions = positions;
        document.querySelectorAll('.edit-pos-btn').forEach(btn => {
            btn.addEventListener('click', () => openEditLevelsModal(parseInt(btn.dataset.id, 10)));
        });
    }

    // --- Edit Stop Loss / Trailing SL % / Target of an open manual position ---
    function openEditLevelsModal(positionId) {
        const pos = (lastRenderedPositions || []).find(p => p.id === positionId);
        if (!pos) return;

        document.getElementById('editLevelsPositionId').value = pos.id;
        document.getElementById('editLevelsTitle').innerText = `${pos.symbol} · ${pos.quantity} @ ₹${Number(pos.averageEntryPrice).toFixed(2)}`;
        document.getElementById('editLevelsLtp').innerText = `₹${Number(pos.currentPrice || pos.averageEntryPrice).toFixed(2)}`;
        document.getElementById('editStopLoss').value = pos.stopLoss > 0 ? Number(pos.stopLoss).toFixed(2) : '';
        document.getElementById('editTrailingSlPct').value = pos.trailingSlPct > 0 ? Number(pos.trailingSlPct).toFixed(2) : '';
        document.getElementById('editTakeProfit').value = pos.takeProfit > 0 ? Number(pos.takeProfit).toFixed(2) : '';
        document.getElementById('editLevelsError').innerText = '';
        updateEditLevelsPreview();

        const modalEl = document.getElementById('editLevelsModal');
        if (modalEl && window.bootstrap) bootstrap.Modal.getOrCreateInstance(modalEl).show();
    }

    // Shows risk / reward vs the average entry price while editing.
    function updateEditLevelsPreview() {
        const pos = (lastRenderedPositions || []).find(p => p.id === parseInt(document.getElementById('editLevelsPositionId').value, 10));
        const preview = document.getElementById('editLevelsPreview');
        if (!pos || !preview) return;
        const sl = parseFloat(document.getElementById('editStopLoss').value) || 0;
        const tp = parseFloat(document.getElementById('editTakeProfit').value) || 0;
        const entry = Number(pos.averageEntryPrice);
        const risk = sl > 0 ? (entry - sl) * pos.quantity : null;
        const reward = tp > 0 ? (tp - entry) * pos.quantity : null;
        preview.innerText = `Risk at SL: ${risk !== null ? formatSignedInr(-risk) : '-'} · Reward at Target: ${reward !== null ? formatSignedInr(reward) : '-'}`;
    }

    async function handleSaveLevels() {
        const positionId = parseInt(document.getElementById('editLevelsPositionId').value, 10);
        const stopLoss = parseFloat(document.getElementById('editStopLoss').value);
        const trailingSlPct = parseFloat(document.getElementById('editTrailingSlPct').value);
        const takeProfit = parseFloat(document.getElementById('editTakeProfit').value);
        const errorEl = document.getElementById('editLevelsError');

        if (!(stopLoss > 0) || !(trailingSlPct > 0) || !(takeProfit > 0)) {
            errorEl.innerText = 'Stop Loss, Trailing SL % and Target must all be greater than zero.';
            return;
        }
        if (stopLoss >= takeProfit) {
            errorEl.innerText = 'Stop Loss must be below Target.';
            return;
        }

        try {
            const res = await fetch(`${apiBaseUrl}/api/manualpapertrade/position/${positionId}/levels`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ stopLoss, trailingSlPct, takeProfit })
            });
            const data = await res.json().catch(() => ({}));
            if (!res.ok || data.success !== true) {
                const firstError = data.errors ? Object.values(data.errors).flat()[0] : null;
                errorEl.innerText = data.message || firstError || data.title || 'Could not update levels.';
                return;
            }

            const modalEl = document.getElementById('editLevelsModal');
            if (modalEl && window.bootstrap) bootstrap.Modal.getOrCreateInstance(modalEl).hide();
            showToast('Levels Updated', data.message, 'success');
            await loadPositions();
            await loadManualLogs();
        } catch (ex) {
            console.error('Failed to update position levels:', ex);
            errorEl.innerText = 'Failed to connect to the server.';
        }
    }

    // Manual (trade_type = 0) paper orders only, filtered & paged server-side (fn_get_manual_paper_orders_paged).
    async function loadOrders(page = ordersPageState.page) {
        try {
            ordersPageState.page = page;
            const query = new URLSearchParams({
                page: ordersPageState.page,
                pageSize: ordersPageState.pageSize,
                symbol: ordersPageState.symbol || '',
                side: ordersPageState.side || '',
                status: ordersPageState.status || '',
                fromDate: ordersPageState.fromDate || '',
                toDate: ordersPageState.toDate || ''
            });

            const res = await fetch(`${apiBaseUrl}/api/manualpapertrade/orders?${query.toString()}`);
            if (!res.ok) return;
            const pagedData = await res.json();
            renderOrders(pagedData);
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

    function renderOrders(pagedData) {
        if (!ordersTableBody) return;
        ordersTableBody.innerHTML = '';

        const orders = pagedData.items || pagedData.Items || [];
        const totalCount = pagedData.totalCount ?? pagedData.TotalCount ?? 0;
        const page = pagedData.page ?? pagedData.Page ?? 1;
        const pageSize = pagedData.pageSize ?? pagedData.PageSize ?? ordersPageState.pageSize;
        const totalPages = pagedData.totalPages ?? pagedData.TotalPages ?? 0;

        if (!orders || orders.length === 0) {
            ordersTableBody.innerHTML = '<tr><td colspan="10" class="text-center text-white py-3">No manual orders for selected criteria.</td></tr>';
            renderPager('ordersPaginationUl', 'ordersPaginationInfo', 'orders', 0, 0, 0, 1, 0, loadOrders);
            return;
        }

        renderPager('ordersPaginationUl', 'ordersPaginationInfo', 'orders',
            (page - 1) * pageSize + 1, Math.min(page * pageSize, totalCount), totalCount, page, totalPages, loadOrders);

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
                <td class="text-danger fw-semibold">${o.stopLoss > 0 ? '₹' + Number(o.stopLoss).toFixed(2) : '-'}</td>
                <td class="text-success fw-bold">${o.takeProfit > 0 ? '₹' + Number(o.takeProfit).toFixed(2) : '-'}</td>
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
        if (status === 4 || status === 'Open') return '<span class="badge bg-info text-dark">Open</span>';
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

            // Manual (trade_type = 0) paper trade history only (fn_get_manual_paper_trade_history_paged).
            const res = await fetch(`${apiBaseUrl}/api/manualpapertrade/history?${query.toString()}`);
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
        renderPager('historyPaginationUl', 'historyPaginationInfo', 'trades', startItem, endItem, totalCount, currentPage, totalPages, loadHistory);
    }

    // Shared pager for the Orders and Execution History tabs: Prev, first, window of +/-2 pages
    // around the current page, last, Next - so long histories don't render hundreds of links.
    function renderPager(ulId, infoId, label, startItem, endItem, totalCount, currentPage, totalPages, onPage) {
        const infoSpan = document.getElementById(infoId);
        const ul = document.getElementById(ulId);

        if (infoSpan) {
            infoSpan.innerText = totalCount === 0
                ? `Showing 0 of 0 ${label}`
                : `Showing ${startItem}-${endItem} of ${totalCount} ${label}`;
        }

        if (!ul) return;
        ul.innerHTML = '';
        if (totalPages <= 1) return;

        const addItem = (text, targetPage, { active = false, disabled = false } = {}) => {
            const li = document.createElement('li');
            li.className = `page-item ${active ? 'active' : ''} ${disabled ? 'disabled' : ''}`;
            li.innerHTML = `<a class="page-link ${active ? 'bg-primary text-white border-primary fw-bold' : 'bg-dark text-light border-secondary'}" href="javascript:void(0)">${text}</a>`;
            if (!active && !disabled && targetPage) {
                li.addEventListener('click', () => onPage(targetPage));
            }
            ul.appendChild(li);
        };

        addItem('« Prev', currentPage - 1, { disabled: currentPage <= 1 });

        const windowStart = Math.max(1, currentPage - 2);
        const windowEnd = Math.min(totalPages, currentPage + 2);
        if (windowStart > 1) {
            addItem('1', 1);
            if (windowStart > 2) addItem('…', null, { disabled: true });
        }
        for (let i = windowStart; i <= windowEnd; i++) {
            addItem(String(i), i, { active: i === currentPage });
        }
        if (windowEnd < totalPages) {
            if (windowEnd < totalPages - 1) addItem('…', null, { disabled: true });
            addItem(String(totalPages), totalPages);
        }

        addItem('Next »', currentPage + 1, { disabled: currentPage >= totalPages });
    }

    // --- 4. User Actions & Handlers ---
    async function handleOrderSubmit(e) {
        e.preventDefault();
        if (!validateFormInputs()) return;

        // Same request as Manual Real Trade (/api/realtrade/manual-buy), routed to the paper account.
        const payload = {
            symbol: activeSymbol,
            entryPrice: getEffectivePrice(),
            quantity: parseInt(orderQuantity.value, 10),
            stopLossPct: parseFloat(orderSlPct.value),
            trailingSlPct: parseFloat(orderTslPct.value)
        };

        try {
            placeOrderBtn.disabled = true;
            placeOrderBtn.innerText = 'Processing Trade...';

            const res = await fetch(`${apiBaseUrl}/api/manualpapertrade/buy`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload)
            });

            const data = await res.json().catch(() => ({}));
            if (!res.ok || data.success !== true) {
                showToast('Paper Trade Not Executed', data.message || data.title || 'Could not place paper trade.', 'danger');
                return;
            }

            showToast('Paper Trade Executed', data.message || `Paper BUY for ${payload.quantity} ${activeSymbol} executed.`, 'success');

            await loadPortfolio();
            await loadPositions();
            await loadOrders();
            await loadHistory();
        } catch (err) {
            console.error('Order submission exception:', err);
            showToast('System Error', 'Failed to connect to order execution server.', 'danger');
        } finally {
            placeOrderBtn.innerText = '📝 Place Paper Trade';
            validateFormInputs();
            loadManualLogs();
        }
    }

    async function handleClosePosition(positionId) {
        try {
            // Manual close: exits at the position's own latest price (server-side), without touching the
            // shared paper account used by Auto Paper Trading.
            const res = await fetch(`${apiBaseUrl}/api/manualpapertrade/position/close/${parseInt(positionId, 10)}`, {
                method: 'POST'
            });

            const data = await res.json().catch(() => ({}));
            if (!res.ok || data.success !== true) {
                showToast('Closure Error', data.message || data.detail || 'Failed to close position.', 'danger');
                return;
            }

            showToast('Position Closed', data.message || 'Open paper position has been closed at market price.', 'success');
            await loadPortfolio();
            await loadPositions();
            await loadOrders();
            await loadHistory();
            await loadManualLogs();
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
        if (!confirm('Reset Manual Paper Trading? All manual open positions, orders, trade history and today\'s manual logs will be cleared and your full Manual Capital becomes available again. Auto Paper Trading is not affected.')) {
            return;
        }

        try {
            // Manual-only reset (fn_reset_manual_paper_trading) - Auto Paper / Auto Real data is untouched.
            const res = await fetch(`${apiBaseUrl}/api/manualpapertrade/reset`, { method: 'POST' });
            const data = await res.json().catch(() => ({}));
            if (res.ok && data.success) {
                showToast('Manual Trading Reset', data.message || 'Manual Paper Trading has been reset.', 'success');
                await loadPortfolio();
                await loadPositions();
                await loadOrders(1);
                await loadHistory(1);
                await loadManualLogs();
            } else {
                showToast('Reset Failed', data.message || 'Could not reset Manual Paper Trading.', 'danger');
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

        signalRConnection.on('ReceivePaperAccountUpdate', () => {
            scheduleManualDashboardRefresh();
        });

        signalRConnection.on('ReceivePaperPositionsUpdate', () => {
            scheduleManualDashboardRefresh();
        });

        signalRConnection.on('ReceivePaperError', (err) => {
            if (err) {
                showToast(err.errorCode || 'Trading Error', err.message || 'An error occurred during trade execution.', 'danger');
            }
        });

        // Manual Paper Trade BUY/SELL executions (incl. automatic Target / SL / Trailing SL exits).
        signalRConnection.on('ReceiveManualPaperTradeAlert', async (alert) => {
            if (alert && alert.message) {
                showToast(alert.side === 'SELL' ? '🎯 Manual Paper SELL' : '📝 Manual Paper BUY', alert.message, 'success');
            }
            await loadPortfolio();
            await loadPositions();
            await loadOrders();
            await loadHistory(historyPageState.page);
        });

        // New manual_paper_trade_execution_logs row (BUY/SELL, skipped trade, trailing SL, switch change).
        signalRConnection.on('ReceiveManualPaperTradeLogEvent', () => {
            loadManualLogs();
        });

        signalRConnection.on('ReceiveAutoTradeAlert', (alert) => {
            if (alert && alert.message) {
                const toastType = alert.mode === 'Live' ? 'danger' : 'success';
                showToast(`⚡ Auto-Trade (${alert.mode})`, alert.message, toastType);
            }
        });

        signalRConnection.start()
            .then(() => {
                console.log('SignalR connected for Manual Trading events.');
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
