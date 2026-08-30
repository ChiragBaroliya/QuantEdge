/**
 * QuantEdge - Trading Reports & Performance Analytics Client
 * Handles multi-timeframe navigation, Chart.js visualizations, database-side pagination (PageSize: 10), and table filtering.
 */

$(document).ready(function () {
    let currentPeriod = 'daily';
    let currentMode = 'all';
    
    // Pagination & Filter States for Periodic Table
    let periodsPageState = {
        page: 1,
        pageSize: 10,
        totalPages: 0,
        totalCount: 0,
        pnlFilter: 'all'
    };

    // Pagination & Filter States for Trades Log Table
    let tradesPageState = {
        page: 1,
        pageSize: 10,
        totalPages: 0,
        totalCount: 0,
        symbol: '',
        tradeType: 'all',
        pnlFilter: 'all'
    };

    let chartEquity = null;
    let chartPeriodBar = null;
    let chartWinLoss = null;
    let searchDebounceTimer = null;

    // Initialize date pickers with last 30 days default
    const now = new Date();
    const thirtyDaysAgo = new Date();
    thirtyDaysAgo.setDate(now.getDate() - 30);

    $('#reportStartDate').val(formatDateInput(thirtyDaysAgo));
    $('#reportEndDate').val(formatDateInput(now));

    // Bind Event Listeners
    bindEvents();

    // Initial Full Dashboard Load
    loadFullReport();

    function bindEvents() {
        // Period Pill buttons click
        $('.period-pill-btn').on('click', function () {
            $('.period-pill-btn').removeClass('active');
            $(this).addClass('active');
            currentPeriod = $(this).data('period') || 'daily';
            periodsPageState.page = 1;
            loadFullReport();
        });

        // Mode select change
        $('#reportModeSelect').on('change', function () {
            currentMode = $(this).val();
            periodsPageState.page = 1;
            tradesPageState.page = 1;
            loadFullReport();
        });

        // User select change (if Admin)
        $('#reportUserSelect').on('change', function () {
            periodsPageState.page = 1;
            tradesPageState.page = 1;
            loadFullReport();
        });

        // Date inputs change
        $('#reportStartDate, #reportEndDate').on('change', function () {
            periodsPageState.page = 1;
            tradesPageState.page = 1;
            loadFullReport();
        });

        // Quick Date presets
        $('#btnPreset30D').on('click', function () {
            const d = new Date();
            d.setDate(d.getDate() - 30);
            $('#reportStartDate').val(formatDateInput(d));
            $('#reportEndDate').val(formatDateInput(new Date()));
            loadFullReport();
        });

        $('#btnPreset90D').on('click', function () {
            const d = new Date();
            d.setDate(d.getDate() - 90);
            $('#reportStartDate').val(formatDateInput(d));
            $('#reportEndDate').val(formatDateInput(new Date()));
            loadFullReport();
        });

        $('#btnPresetYTD').on('click', function () {
            const d = new Date(new Date().getFullYear(), 0, 1);
            $('#reportStartDate').val(formatDateInput(d));
            $('#reportEndDate').val(formatDateInput(new Date()));
            loadFullReport();
        });

        $('#btnPresetAll').on('click', function () {
            $('#reportStartDate').val('');
            $('#reportEndDate').val('');
            loadFullReport();
        });

        // Refresh button
        $('#btnRefreshReport').on('click', function () {
            loadFullReport();
        });

        // Export CSV button
        $('#btnExportCsv').on('click', function () {
            exportCsv();
        });

        // --- Periodic Table Inline Filters ---
        $('#periodsPnlFilter').on('change', function () {
            periodsPageState.pnlFilter = $(this).val();
            periodsPageState.page = 1;
            fetchPeriodsPaged();
        });

        // --- Trades Table Inline Filters ---
        $('#tradesSearchInput').on('input', function () {
            clearTimeout(searchDebounceTimer);
            const val = $(this).val().trim();
            searchDebounceTimer = setTimeout(function () {
                tradesPageState.symbol = val;
                tradesPageState.page = 1;
                fetchTradesPaged();
            }, 300);
        });

        $('#tradesTypeFilter').on('change', function () {
            tradesPageState.tradeType = $(this).val();
            tradesPageState.page = 1;
            fetchTradesPaged();
        });

        $('#tradesPnlFilter').on('change', function () {
            tradesPageState.pnlFilter = $(this).val();
            tradesPageState.page = 1;
            fetchTradesPaged();
        });
    }

    function getBaseFilterParams() {
        return {
            periodType: currentPeriod,
            tradeMode: currentMode,
            userId: $('#reportUserSelect').val() || null,
            startDate: $('#reportStartDate').val() || null,
            endDate: $('#reportEndDate').val() || null
        };
    }

    function loadFullReport() {
        const base = getBaseFilterParams();
        const params = {
            ...base,
            periodsPage: periodsPageState.page,
            periodsPageSize: periodsPageState.pageSize,
            periodsPnlFilter: periodsPageState.pnlFilter,
            tradesPage: tradesPageState.page,
            tradesPageSize: tradesPageState.pageSize,
            tradesType: tradesPageState.tradeType,
            tradesPnlFilter: tradesPageState.pnlFilter,
            symbol: tradesPageState.symbol || null
        };

        $.ajax({
            url: '/reports/performance',
            method: 'GET',
            data: params,
            success: function (res) {
                renderSummaryKPIs(res.summary);
                renderCharts(res);
                
                if (res.periods) {
                    periodsPageState.totalPages = res.periods.totalPages;
                    periodsPageState.totalCount = res.periods.totalCount;
                    renderPeriodicTable(res.periods.items);
                    renderPagination('periods', periodsPageState, fetchPeriodsPaged);
                }

                if (res.recentTrades) {
                    tradesPageState.totalPages = res.recentTrades.totalPages;
                    tradesPageState.totalCount = res.recentTrades.totalCount;
                    renderTradesTable(res.recentTrades.items);
                    renderPagination('trades', tradesPageState, fetchTradesPaged);
                }
            },
            error: function (xhr) {
                console.error("Failed to load report data:", xhr);
                if (typeof Swal !== 'undefined') {
                    Swal.fire({
                        icon: 'error',
                        title: 'Report Load Failed',
                        text: 'Unable to retrieve trading performance data.',
                        background: 'var(--bg-card, #121826)',
                        color: '#f8fafc'
                    });
                }
            }
        });
    }

    function fetchPeriodsPaged() {
        const base = getBaseFilterParams();
        const params = {
            ...base,
            pnlFilter: periodsPageState.pnlFilter,
            page: periodsPageState.page,
            pageSize: periodsPageState.pageSize
        };

        $('#tablePeriodsBody').html(`<tr><td colspan="9" class="text-center py-4 text-muted">Loading page ${periodsPageState.page}...</td></tr>`);

        $.ajax({
            url: '/reports/periods/paged',
            method: 'GET',
            data: params,
            success: function (res) {
                periodsPageState.totalPages = res.totalPages;
                periodsPageState.totalCount = res.totalCount;
                renderPeriodicTable(res.items);
                renderPagination('periods', periodsPageState, fetchPeriodsPaged);
            },
            error: function (xhr) {
                console.error("Failed to fetch paged periods:", xhr);
            }
        });
    }

    function fetchTradesPaged() {
        const base = getBaseFilterParams();
        const params = {
            ...base,
            symbol: tradesPageState.symbol || null,
            tradeType: tradesPageState.tradeType,
            pnlFilter: tradesPageState.pnlFilter,
            page: tradesPageState.page,
            pageSize: tradesPageState.pageSize
        };

        $('#tableRecentTradesBody').html(`<tr><td colspan="10" class="text-center py-4 text-muted">Loading page ${tradesPageState.page}...</td></tr>`);

        $.ajax({
            url: '/reports/trades/paged',
            method: 'GET',
            data: params,
            success: function (res) {
                tradesPageState.totalPages = res.totalPages;
                tradesPageState.totalCount = res.totalCount;
                renderTradesTable(res.items);
                renderPagination('trades', tradesPageState, fetchTradesPaged);
            },
            error: function (xhr) {
                console.error("Failed to fetch paged trades:", xhr);
            }
        });
    }

    function renderSummaryKPIs(summary) {
        if (!summary) return;

        // 1. Invested Capital
        $('#kpiInvestedCapital').text(formatCurrency(summary.totalInvestedCapital));

        // 2. Net Realized PnL
        const netPnlEl = $('#kpiNetRealizedPnl');
        const pnlBadgeEl = $('#kpiPnlBadge');
        netPnlEl.text(formatCurrency(summary.netRealizedPnl));
        
        if (summary.netRealizedPnl > 0) {
            netPnlEl.css('color', '#34d399');
            pnlBadgeEl.removeClass('negative').addClass('positive').text('PROFIT');
        } else if (summary.netRealizedPnl < 0) {
            netPnlEl.css('color', '#f87171');
            pnlBadgeEl.removeClass('positive').addClass('negative').text('LOSS');
        } else {
            netPnlEl.css('color', '#ffffff');
            pnlBadgeEl.removeClass('positive negative').text('BREAKEVEN');
        }

        // 3. Total ROI %
        const roiEl = $('#kpiTotalRoi');
        const roiSign = summary.totalRoiPct > 0 ? '+' : '';
        roiEl.text(`${roiSign}${summary.totalRoiPct.toFixed(2)}%`);
        if (summary.totalRoiPct > 0) {
            roiEl.css('color', '#34d399');
        } else if (summary.totalRoiPct < 0) {
            roiEl.css('color', '#f87171');
        } else {
            roiEl.css('color', '#ffffff');
        }

        // 4. Win Rate & Trade counts
        $('#kpiWinRate').text(`${summary.winRatePct.toFixed(1)}%`);
        $('#kpiWinLossCount').text(`${summary.winningTrades}W • ${summary.losingTrades}L of ${summary.totalTrades} Trades`);

        // 5. Profit Factor & Avg Trade
        $('#kpiProfitFactor').text(summary.profitFactor > 0 ? summary.profitFactor.toFixed(2) : '0.00');
        const avgPnlSign = summary.avgTradePnl > 0 ? '+' : '';
        $('#kpiAvgTrade').text(`Avg: ${avgPnlSign}${formatCurrency(summary.avgTradePnl)} (${summary.avgTradeRoiPct.toFixed(2)}%)`);
    }

    function renderCharts(data) {
        renderEquityCurveChart(data.equityCurve);
        if (data.periods && data.periods.items) {
            renderPeriodBarChart(data.periods.items);
        }
        renderWinLossChart(data.summary);
    }

    function renderEquityCurveChart(equityData) {
        const ctx = document.getElementById('chartEquityCurve');
        if (!ctx) return;

        if (chartEquity) chartEquity.destroy();

        if (!equityData || equityData.length === 0) {
            chartEquity = new Chart(ctx, {
                type: 'line',
                data: {
                    labels: ['No Data'],
                    datasets: [{ label: 'Cumulative P&L (₹)', data: [0], borderColor: '#94a3b8' }]
                },
                options: { responsive: true, maintainAspectRatio: false }
            });
            return;
        }

        const labels = equityData.map(d => d.label);
        const pnlData = equityData.map(d => d.cumulativePnl);

        const gradient = ctx.getContext('2d').createLinearGradient(0, 0, 0, 300);
        gradient.addColorStop(0, 'rgba(52, 211, 153, 0.35)');
        gradient.addColorStop(1, 'rgba(52, 211, 153, 0.0)');

        chartEquity = new Chart(ctx, {
            type: 'line',
            data: {
                labels: labels,
                datasets: [{
                    label: 'Cumulative P&L (₹)',
                    data: pnlData,
                    borderColor: '#34d399',
                    backgroundColor: gradient,
                    borderWidth: 2.5,
                    fill: true,
                    tension: 0.3,
                    pointRadius: equityData.length > 30 ? 0 : 3,
                    pointHoverRadius: 6
                }]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                interaction: { intersect: false, mode: 'index' },
                plugins: {
                    legend: { display: false },
                    tooltip: {
                        backgroundColor: 'rgba(15, 23, 42, 0.95)',
                        titleColor: '#ffffff',
                        bodyColor: '#34d399',
                        borderColor: 'rgba(255, 255, 255, 0.1)',
                        borderWidth: 1,
                        padding: 12,
                        callbacks: {
                            label: function (context) {
                                return ` Cumulative P&L: ₹${context.parsed.y.toLocaleString('en-IN', { minimumFractionDigits: 2 })}`;
                            }
                        }
                    }
                },
                scales: {
                    x: {
                        grid: { color: 'rgba(255, 255, 255, 0.05)' },
                        ticks: { color: '#94a3b8', maxTicksLimit: 8, font: { size: 11 } }
                    },
                    y: {
                        grid: { color: 'rgba(255, 255, 255, 0.05)' },
                        ticks: {
                            color: '#94a3b8',
                            font: { size: 11 },
                            callback: value => '₹' + value.toLocaleString('en-IN')
                        }
                    }
                }
            }
        });
    }

    function renderPeriodBarChart(periods) {
        const ctx = document.getElementById('chartPeriodBar');
        if (!ctx) return;

        if (chartPeriodBar) chartPeriodBar.destroy();

        if (!periods || periods.length === 0) return;

        const chronPeriods = [...periods].reverse().slice(-12);
        const labels = chronPeriods.map(p => p.periodKey);
        const investedData = chronPeriods.map(p => p.investedCapital);
        const pnlData = chronPeriods.map(p => p.netPnl);

        chartPeriodBar = new Chart(ctx, {
            type: 'bar',
            data: {
                labels: labels,
                datasets: [
                    {
                        label: 'Invested Capital (₹)',
                        data: investedData,
                        backgroundColor: 'rgba(56, 189, 248, 0.65)',
                        borderRadius: 6,
                        yAxisID: 'yInvested'
                    },
                    {
                        label: 'Net P&L (₹)',
                        data: pnlData,
                        backgroundColor: pnlData.map(v => v >= 0 ? 'rgba(52, 211, 153, 0.85)' : 'rgba(248, 113, 113, 0.85)'),
                        borderRadius: 6,
                        yAxisID: 'yPnl'
                    }
                ]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                plugins: {
                    legend: {
                        position: 'top',
                        labels: { color: '#94a3b8', boxWidth: 12, font: { size: 11 } }
                    },
                    tooltip: {
                        backgroundColor: 'rgba(15, 23, 42, 0.95)',
                        borderColor: 'rgba(255, 255, 255, 0.1)',
                        borderWidth: 1
                    }
                },
                scales: {
                    x: {
                        grid: { color: 'rgba(255, 255, 255, 0.04)' },
                        ticks: { color: '#94a3b8', font: { size: 10 } }
                    },
                    yInvested: {
                        type: 'linear',
                        position: 'left',
                        grid: { color: 'rgba(255, 255, 255, 0.04)' },
                        ticks: {
                            color: '#38bdf8',
                            font: { size: 10 },
                            callback: v => '₹' + (v / 1000).toFixed(0) + 'k'
                        }
                    },
                    yPnl: {
                        type: 'linear',
                        position: 'right',
                        grid: { drawOnChartArea: false },
                        ticks: {
                            color: '#34d399',
                            font: { size: 10 },
                            callback: v => '₹' + v.toLocaleString('en-IN')
                        }
                    }
                }
            }
        });
    }

    function renderWinLossChart(summary) {
        const ctx = document.getElementById('chartWinLoss');
        if (!ctx) return;

        if (chartWinLoss) chartWinLoss.destroy();

        const wins = summary ? summary.winningTrades : 0;
        const losses = summary ? summary.losingTrades : 0;

        chartWinLoss = new Chart(ctx, {
            type: 'doughnut',
            data: {
                labels: ['Winning Trades', 'Losing Trades'],
                datasets: [{
                    data: [wins, losses],
                    backgroundColor: ['#34d399', '#f87171'],
                    borderWidth: 0,
                    hoverOffset: 4
                }]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                cutout: '72%',
                plugins: {
                    legend: {
                        position: 'bottom',
                        labels: { color: '#94a3b8', font: { size: 11 }, padding: 12 }
                    }
                }
            }
        });
    }

    function renderPeriodicTable(periods) {
        const tbody = $('#tablePeriodsBody');
        tbody.empty();

        if (!periods || periods.length === 0) {
            tbody.html(`
                <tr>
                    <td colspan="9" class="text-center py-4 text-muted">
                        No periodic performance data found matching current filters.
                    </td>
                </tr>
            `);
            return;
        }

        periods.forEach(p => {
            const pnlClass = p.netPnl > 0 ? 'pnl-text-pos' : (p.netPnl < 0 ? 'pnl-text-neg' : '');
            const roiClass = p.roiPct > 0 ? 'pos' : (p.roiPct < 0 ? 'neg' : 'zero');
            const roiSign = p.roiPct > 0 ? '+' : '';

            const tr = $(`
                <tr>
                    <td><strong>${escapeHtml(p.periodLabel)}</strong></td>
                    <td class="text-center"><span class="badge bg-secondary text-white">${p.totalTrades}</span></td>
                    <td class="text-center" style="font-size:12px;">
                        <span class="pnl-text-pos">${p.winTrades}W</span> / <span class="pnl-text-neg">${p.lossTrades}L</span>
                        <span class="text-muted small">(${p.winRatePct.toFixed(0)}%)</span>
                    </td>
                    <td class="text-end" style="font-family: monospace;">${formatCurrency(p.investedCapital)}</td>
                    <td class="text-end pnl-text-pos" style="font-family: monospace;">+${formatCurrency(p.grossProfit)}</td>
                    <td class="text-end pnl-text-neg" style="font-family: monospace;">-${formatCurrency(p.grossLoss)}</td>
                    <td class="text-end ${pnlClass}" style="font-family: monospace; font-weight:700;">
                        ${p.netPnl > 0 ? '+' : ''}${formatCurrency(p.netPnl)}
                    </td>
                    <td class="text-end">
                        <span class="pnl-badge ${roiClass}">${roiSign}${p.roiPct.toFixed(2)}%</span>
                    </td>
                    <td class="text-end" style="font-family: monospace;">${formatCurrency(p.cumulativePnl)}</td>
                </tr>
            `);
            tbody.append(tr);
        });
    }

    function renderTradesTable(trades) {
        const tbody = $('#tableRecentTradesBody');
        tbody.empty();

        if (!trades || trades.length === 0) {
            tbody.html(`
                <tr>
                    <td colspan="10" class="text-center py-4 text-muted">
                        No closed trade records found matching current filters.
                    </td>
                </tr>
            `);
            return;
        }

        trades.forEach(t => {
            const pnlClass = t.realizedPnl > 0 ? 'pnl-text-pos' : (t.realizedPnl < 0 ? 'pnl-text-neg' : '');
            const roiClass = t.returnPct > 0 ? 'pos' : (t.returnPct < 0 ? 'neg' : 'zero');
            const roiSign = t.returnPct > 0 ? '+' : '';
            const execDate = new Date(t.executedAt).toLocaleString('en-IN', {
                day: '2-digit', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit'
            });

            const modeBadge = t.mode === 'Real' 
                ? '<span class="badge bg-danger text-white" style="font-size:10px;">REAL</span>' 
                : '<span class="badge bg-primary text-white" style="font-size:10px;">PAPER</span>';

            const tr = $(`
                <tr>
                    <td><strong>${escapeHtml(t.symbol)}</strong> ${modeBadge}</td>
                    <td><span class="badge bg-dark border border-secondary">${escapeHtml(t.tradeType)}</span></td>
                    <td class="text-center">${t.quantity}</td>
                    <td class="text-end" style="font-family: monospace;">₹${t.entryPrice.toFixed(2)}</td>
                    <td class="text-end" style="font-family: monospace;">₹${t.executedPrice.toFixed(2)}</td>
                    <td class="text-end" style="font-family: monospace;">${formatCurrency(t.investedAmount)}</td>
                    <td class="text-end ${pnlClass}" style="font-family: monospace; font-weight:700;">
                        ${t.realizedPnl > 0 ? '+' : ''}${formatCurrency(t.realizedPnl)}
                    </td>
                    <td class="text-end">
                        <span class="pnl-badge ${roiClass}">${roiSign}${t.returnPct.toFixed(2)}%</span>
                    </td>
                    <td><span class="small text-muted">${escapeHtml(t.exitReason)}</span></td>
                    <td><span class="small text-white">${execDate}</span></td>
                </tr>
            `);
            tbody.append(tr);
        });
    }

    function renderPagination(targetPrefix, state, fetchCallback) {
        const infoEl = $(`#${targetPrefix}PaginationInfo`);
        const controlsEl = $(`#${targetPrefix}PaginationControls`);

        if (state.totalCount === 0) {
            infoEl.text('Showing 0 to 0 of 0 entries');
            controlsEl.empty();
            return;
        }

        const startItem = (state.page - 1) * state.pageSize + 1;
        const endItem = Math.min(state.page * state.pageSize, state.totalCount);
        infoEl.text(`Showing ${startItem} to ${endItem} of ${state.totalCount} entries`);

        controlsEl.empty();

        // Prev Button
        const prevBtn = $(`<button type="button" class="page-btn" ${state.page <= 1 ? 'disabled' : ''}>&laquo;</button>`);
        prevBtn.on('click', function () {
            if (state.page > 1) {
                state.page--;
                fetchCallback();
            }
        });
        controlsEl.append(prevBtn);

        // Page Numbers
        let startPage = Math.max(1, state.page - 2);
        let endPage = Math.min(state.totalPages, startPage + 4);
        if (endPage - startPage < 4) {
            startPage = Math.max(1, endPage - 4);
        }

        for (let i = startPage; i <= endPage; i++) {
            const pageNumBtn = $(`<button type="button" class="page-btn ${i === state.page ? 'active' : ''}">${i}</button>`);
            const targetPage = i;
            pageNumBtn.on('click', function () {
                if (state.page !== targetPage) {
                    state.page = targetPage;
                    fetchCallback();
                }
            });
            controlsEl.append(pageNumBtn);
        }

        // Next Button
        const nextBtn = $(`<button type="button" class="page-btn" ${state.page >= state.totalPages ? 'disabled' : ''}>&raquo;</button>`);
        nextBtn.on('click', function () {
            if (state.page < state.totalPages) {
                state.page++;
                fetchCallback();
            }
        });
        controlsEl.append(nextBtn);
    }

    function exportCsv() {
        const params = getBaseFilterParams();
        const queryStr = $.param(params);
        window.location.href = `/reports/export?${queryStr}`;
    }

    function formatCurrency(val) {
        if (val == null || isNaN(val)) return '₹0.00';
        return '₹' + Number(val).toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
    }

    function formatDateInput(date) {
        if (!date) return '';
        const y = date.getFullYear();
        const m = String(date.getMonth() + 1).padStart(2, '0');
        const d = String(date.getDate()).padStart(2, '0');
        return `${y}-${m}-${d}`;
    }

    function escapeHtml(text) {
        if (!text) return '';
        return String(text)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#039;');
    }
});
