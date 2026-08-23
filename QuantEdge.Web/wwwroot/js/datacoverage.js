/**
 * Data Coverage Manager JavaScript Module
 */
$(document).ready(function () {
    const apiBaseUrl = $('.coverage-container').data('api-base-url') || 'https://localhost:44370';

    const Toast = Swal.mixin({
        toast: true,
        position: 'top-end',
        showConfirmButton: false,
        timer: 3000,
        timerProgressBar: true,
        background: 'var(--bg-card, #1e293b)',
        color: '#f8fafc'
    });

    let state = {
        currentPage: 1,
        pageSize: 25,
        searchQuery: '',
        statusFilter: 'all',
        historyFilter: 'all',
        alphabetFilter: 'all',
        totalCount: 0,
        totalPages: 0,
        selectedStockId: null,
        stocksMap: {},
        savedState: {}, // id -> { isActive, history1M, history5M, history15M, history60M, history1D } (values: null, 0, 1)
        draftState: {}  // id -> { isActive, history1M, history5M, history15M, history60M, history1D } (values: null, 0, 1)
    };

    let searchTimer = null;

    // Helper: Normalize URL to avoid missing or duplicate /api prefixes
    function getEndpointUrl(endpoint) {
        let base = (apiBaseUrl || '').replace(/\/+$/, '');
        let path = endpoint.startsWith('/') ? endpoint : '/' + endpoint;

        if (base.endsWith('/api') && path.startsWith('/api/')) {
            path = path.substring(4);
        }
        if (!base.endsWith('/api') && !path.startsWith('/api/')) {
            path = '/api' + path;
        }
        return base + path;
    }

    // Helper to parse history flag into null, 0, or 1
    function getRawTfVal(val) {
        if (val === 1 || val === '1') return 1;
        if (val === 0 || val === '0') return 0;
        return null;
    }

    // Initialize Dashboard Data
    initDataCoverage();

    function initDataCoverage() {
        loadSummary();
        loadPaginatedList();
        bindEvents();
    }

    // 1. Fetch & Render Summary KPI Cards
    function loadSummary() {
        $.ajax({
            url: getEndpointUrl('/datacoverage/summary'),
            type: 'GET',
            success: function (data) {
                if (data) {
                    $('#kpiTotalStocks').text(formatNumber(data.totalStocks || data.total_stocks || 0));
                    $('#kpiActiveCount').text(formatNumber(data.activeCount || data.active_count || 0));
                    $('#kpiInactiveCount').text(formatNumber(data.inactiveCount || data.inactive_count || 0));
                    $('#kpiMissingCount').text(formatNumber(data.historyMissingCount || data.history_missing_count || 0));
                }
            },
            error: function (xhr, status, err) {
                console.error('Failed to load coverage summary:', err);
            }
        });
    }

    // 2. Fetch & Render Paginated Stock List
    function loadPaginatedList() {
        showTableLoading();

        const params = {
            search: state.searchQuery,
            status: state.statusFilter,
            historyFilter: state.historyFilter,
            alphabet: state.alphabetFilter,
            page: state.currentPage,
            pageSize: state.pageSize
        };

        $.ajax({
            url: getEndpointUrl('/datacoverage/list'),
            type: 'GET',
            data: params,
            success: function (res) {
                if (!res) {
                    renderEmptyTable('No response from server');
                    return;
                }

                const items = res.items || res.Items || [];
                state.totalCount = res.totalCount ?? res.TotalCount ?? 0;
                state.totalPages = res.totalPages ?? res.TotalPages ?? Math.ceil(state.totalCount / state.pageSize);

                state.stocksMap = {};
                items.forEach(stock => {
                    const id = stock.id || stock.Id;
                    state.stocksMap[id] = stock;
                });

                renderTableRows(items);
                renderPaginationInfo();
                renderPaginationButtons();
            },
            error: function (xhr, status, err) {
                console.error('Failed to load paginated stock list:', err);
                renderEmptyTable('Failed to load stock list from API');
            }
        });
    }

    // Render Table Rows
    function renderTableRows(items) {
        const $tbody = $('#coverageTableBody');
        $tbody.empty();

        if (items.length === 0) {
            renderEmptyTable('No stocks matching the selected filters');
            return;
        }

        items.forEach(stock => {
            const id = stock.id || stock.Id;
            const symbol = stock.symbol || stock.Symbol || '';
            const name = stock.name || stock.Name || symbol;
            const exchange = stock.exchange || stock.Exchange || 'NSE';
            const token = stock.instrumentToken || stock.InstrumentToken || '';
            const isActive = Boolean(stock.isActive ?? stock.IsActive ?? false);
            const lastCandleDate = stock.lastCandleDate || stock.LastCandleDate;

            const is1m = getRawTfVal(stock.isHistryStored1m ?? stock.IsHistryStored1m ?? stock.history1M ?? stock.History1M);
            const is5m = getRawTfVal(stock.isHistryStored5m ?? stock.IsHistryStored5m ?? stock.history5M ?? stock.History5M);
            const is15m = getRawTfVal(stock.isHistryStored15m ?? stock.IsHistryStored15m ?? stock.history15M ?? stock.History15M);
            const is60m = getRawTfVal(stock.isHistryStored60m ?? stock.IsHistryStored60m ?? stock.history60M ?? stock.History60M);
            const is1d = getRawTfVal(stock.isHistryStored1d ?? stock.IsHistryStored1d ?? stock.history1D ?? stock.History1D);

            // Record Saved State (null, 0, or 1)
            state.savedState[id] = {
                isActive: isActive,
                history1M: is1m,
                history5M: is5m,
                history15M: is15m,
                history60M: is60m,
                history1D: is1d
            };

            // Initialize Draft State if not modified yet
            if (!state.draftState[id] || !isRowDirty(id)) {
                state.draftState[id] = { ...state.savedState[id] };
            }

            const draft = state.draftState[id];
            const isSelected = state.selectedStockId === id;
            const dirty = isRowDirty(id);

            const statusCell = `
                <div class="inline-status-toggle">
                    <label class="switch switch-sm" title="Toggle Active Status">
                        <input type="checkbox" class="row-status-checkbox" data-id="${id}" ${draft.isActive ? 'checked' : ''} />
                        <span class="slider"></span>
                    </label>
                    <span class="status-label ${draft.isActive ? 'active-color' : 'inactive-color'}" id="statusLabel_${id}">
                        ${draft.isActive ? 'Active' : 'Inactive'}
                    </span>
                </div>
            `;

            const tfCheckboxes = renderTfGroupHtml(id);
            const historyStatusBadges = renderHistoryStatusGroupHtml(id);
            const lastSyncText = lastCandleDate ? formatDate(lastCandleDate) : '<span style="color:var(--text-muted);font-style:italic;">No Candles</span>';

            const actionsCell = `
                <div class="row-actions-group">
                    <button type="button" class="btn-row-save ${dirty ? '' : 'btn-disabled'}" data-id="${id}" ${dirty ? '' : 'disabled'} title="Save changes for this stock">
                        <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5"><path d="M19 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11l5 5v11a2 2 0 0 1-2 2z"></path><polyline points="17 21 17 13 7 13 7 21"></polyline><polyline points="7 3 7 8 15 8"></polyline></svg>
                        Save
                    </button>
                    <button type="button" class="btn-row-delete" data-id="${id}" title="Delete stock record">
                        <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5"><polyline points="3 6 5 6 21 6"></polyline><path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"></path></svg>
                        Delete
                    </button>
                </div>
            `;

            const rowHtml = `
                <tr class="stock-row ${isSelected ? 'selected-row' : ''}" data-id="${id}">
                    <td>
                        <div class="symbol-cell">
                            <span class="symbol-code">${escapeHtml(symbol)}</span>
                            <span class="exchange-tag">${escapeHtml(exchange)}</span>
                            ${token ? `<span class="token-tag">${token}</span>` : ''}
                            <span class="unsaved-tag" id="unsavedTag_${id}" style="${dirty ? '' : 'display:none;'}">● Unsaved</span>
                        </div>
                    </td>
                    <td>
                        <div class="company-name-cell" title="${escapeHtml(name)}">${escapeHtml(name)}</div>
                    </td>
                    <td>${tfCheckboxes}</td>
                    <td>${historyStatusBadges}</td>
                    <td style="font-size: 12.5px;">${lastSyncText}</td>
                    <td>${statusCell}</td>
                    <td style="text-align: center;">${actionsCell}</td>
                </tr>
            `;

            $tbody.append(rowHtml);
        });
    }

    // Render Editable Timeframe Checkboxes
    function getTfCheckboxHtml(id, tfKey, label) {
        const val = state.draftState[id] ? state.draftState[id][tfKey] : null;
        const isChecked = (val === 0 || val === 1);

        let stateClass = '';
        let title = `${label} History Disabled (Missing)`;
        if (val === 1) {
            stateClass = 'is-stored';
            title = `${label}: Stored by Worker Job (Value = 1)`;
        } else if (val === 0) {
            stateClass = 'is-pending';
            title = `${label}: Enabled via Web UI / Pending Worker Job (Value = 0)`;
        }

        return `
            <label class="tf-checkbox-item ${isChecked ? 'is-checked' : ''} ${stateClass}" title="${title}">
                <input type="checkbox" class="tf-checkbox" data-id="${id}" data-tf="${tfKey}" ${isChecked ? 'checked' : ''} />
                <span class="tf-label">${label}</span>
            </label>
        `;
    }

    function renderTfGroupHtml(id) {
        const draft = state.draftState[id] || {};
        const isAllChecked = Boolean(
            (draft.history1M === 0 || draft.history1M === 1) &&
            (draft.history5M === 0 || draft.history5M === 1) &&
            (draft.history15M === 0 || draft.history15M === 1) &&
            (draft.history60M === 0 || draft.history60M === 1) &&
            (draft.history1D === 0 || draft.history1D === 1)
        );

        return `
            <div class="tf-checkboxes-group">
                <label class="tf-checkbox-item tf-all-item ${isAllChecked ? 'is-checked' : ''}" title="Select / Deselect All Timeframes">
                    <input type="checkbox" class="tf-all-checkbox" data-id="${id}" ${isAllChecked ? 'checked' : ''} />
                    <span class="tf-label">All</span>
                </label>
                ${getTfCheckboxHtml(id, 'history1M', '1M')}
                ${getTfCheckboxHtml(id, 'history5M', '5M')}
                ${getTfCheckboxHtml(id, 'history15M', '15M')}
                ${getTfCheckboxHtml(id, 'history60M', '60M')}
                ${getTfCheckboxHtml(id, 'history1D', '1D')}
            </div>
        `;
    }

    // Render Read-Only History Status Badges
    function getSingleHsBadgeHtml(label, val) {
        let cssClass = 'hs-disabled';
        let icon = '○';
        let text = 'Disabled';

        if (val === 1) {
            cssClass = 'hs-stored';
            icon = '✓';
            text = 'Stored';
        } else if (val === 0) {
            cssClass = 'hs-pending';
            icon = '⏳';
            text = 'Pending';
        }

        return `
            <span class="hs-badge ${cssClass}" title="${label}: ${text}">
                <span class="hs-label">${label}</span>
                <span class="hs-icon">${icon}</span>
                <span class="hs-text">${text}</span>
            </span>
        `;
    }

    function renderHistoryStatusGroupHtml(id) {
        const saved = state.savedState[id] || {};
        return `
            <div class="history-status-group" id="hsGroup_${id}">
                ${getSingleHsBadgeHtml('1M', saved.history1M)}
                ${getSingleHsBadgeHtml('5M', saved.history5M)}
                ${getSingleHsBadgeHtml('15M', saved.history15M)}
                ${getSingleHsBadgeHtml('60M', saved.history60M)}
                ${getSingleHsBadgeHtml('1D', saved.history1D)}
            </div>
        `;
    }

    // Check Dirty State for Row
    function isRowDirty(id) {
        const saved = state.savedState[id];
        const draft = state.draftState[id];
        if (!saved || !draft) return false;

        if (saved.isActive !== draft.isActive) return true;

        const keys = ['history1M', 'history5M', 'history15M', 'history60M', 'history1D'];
        for (let k of keys) {
            const sVal = saved[k] ?? null;
            const dVal = draft[k] ?? null;
            if (sVal !== dVal) return true;
        }
        return false;
    }

    function checkRowDirtyState(id) {
        const dirty = isRowDirty(id);
        const $btnSave = $(`.btn-row-save[data-id="${id}"]`);
        const $unsavedBadge = $(`#unsavedTag_${id}`);

        if (dirty) {
            $btnSave.prop('disabled', false).removeClass('btn-disabled');
            $unsavedBadge.show();
        } else {
            $btnSave.prop('disabled', true).addClass('btn-disabled');
            $unsavedBadge.hide();
        }
    }

    // Save Row Changes
    function saveRow(id) {
        if (!isRowDirty(id)) return;

        const draft = state.draftState[id];
        if (!draft) return;

        const $btnSave = $(`.btn-row-save[data-id="${id}"]`);
        const $btnDelete = $(`.btn-row-delete[data-id="${id}"]`);

        const payload = {
            id: id,
            isActive: draft.isActive,
            history1M: draft.history1M,
            history5M: draft.history5M,
            history15M: draft.history15M,
            history60M: draft.history60M,
            history1D: draft.history1D
        };

        const originalHtml = $btnSave.html();
        $btnSave.addClass('is-saving').prop('disabled', true).html(`
            <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" class="spin-icon"><line x1="12" y1="2" x2="12" y2="6"></line><line x1="12" y1="18" x2="12" y2="22"></line><line x1="4.93" y1="4.93" x2="7.76" y2="7.76"></line><line x1="16.24" y1="16.24" x2="19.07" y2="19.07"></line><line x1="2" y1="12" x2="6" y2="12"></line><line x1="18" y1="12" x2="22" y2="12"></line></svg>
            Saving...
        `);
        $btnDelete.prop('disabled', true);

        const url = getEndpointUrl('/datacoverage/' + id);

        $.ajax({
            url: url,
            type: 'PUT',
            contentType: 'application/json',
            data: JSON.stringify(payload),
            success: function (res) {
                // Synchronize savedState to match saved draft
                state.savedState[id] = { ...draft };

                $btnSave.removeClass('is-saving').html(originalHtml);
                $btnDelete.prop('disabled', false);

                // Update History Status badges for this row automatically
                $(`#hsGroup_${id}`).replaceWith(renderHistoryStatusGroupHtml(id));

                Toast.fire({
                    icon: 'success',
                    title: 'Stock updated successfully.'
                });

                checkRowDirtyState(id);
                loadSummary();
            },
            error: function (xhr, status, err) {
                $btnSave.removeClass('is-saving').html(originalHtml);
                $btnDelete.prop('disabled', false);
                checkRowDirtyState(id);

                Toast.fire({
                    icon: 'error',
                    title: 'Unable to update stock.'
                });
            }
        });
    }

    // Delete Single Stock Record
    function deleteStockRow(id) {
        const stock = state.stocksMap[id];
        const symbol = stock ? (stock.symbol || stock.Symbol || '') : 'Stock';
        const name = stock ? (stock.name || stock.Name || symbol) : symbol;

        Swal.fire({
            title: 'Delete Stock',
            html: `
                <div style="text-align: left; font-size: 13.5px; line-height: 1.6; color: var(--text-secondary, #cbd5e1);">
                    <p style="margin-bottom: 12px; font-weight: 500; color: var(--text-primary, #f8fafc);">Are you sure you want to delete this stock?</p>
                    <div style="background: rgba(255,255,255,0.04); border: 1px solid var(--border-subtle, #334155); border-radius: 8px; padding: 12px; margin-bottom: 12px;">
                        <div style="margin-bottom: 6px;"><span style="color: #94a3b8; font-weight: 600; font-size: 11px; text-transform: uppercase;">Company:</span><br><strong style="color: #f8fafc;">${escapeHtml(name)}</strong></div>
                        <div><span style="color: #94a3b8; font-weight: 600; font-size: 11px; text-transform: uppercase;">Symbol:</span><br><strong style="color: var(--theme-accent, #60a5fa); font-family: monospace;">${escapeHtml(symbol)}</strong></div>
                    </div>
                    <p style="color: #f87171; font-size: 12px; font-weight: 600; margin: 0;">This action cannot be undone.</p>
                </div>
            `,
            icon: 'warning',
            showCancelButton: true,
            confirmButtonColor: '#ef4444',
            cancelButtonColor: '#475569',
            confirmButtonText: 'Delete',
            cancelButtonText: 'Cancel',
            background: 'var(--bg-card, #1e293b)',
            color: '#f8fafc'
        }).then((result) => {
            if (result.isConfirmed) {
                const $btnDelete = $(`.btn-row-delete[data-id="${id}"]`);
                const $btnSave = $(`.btn-row-save[data-id="${id}"]`);
                $btnDelete.prop('disabled', true).html(`
                    <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" class="spin-icon"><line x1="12" y1="2" x2="12" y2="6"></line><line x1="12" y1="18" x2="12" y2="22"></line><line x1="4.93" y1="4.93" x2="7.76" y2="7.76"></line><line x1="16.24" y1="16.24" x2="19.07" y2="19.07"></line><line x1="2" y1="12" x2="6" y2="12"></line><line x1="18" y1="12" x2="22" y2="12"></line></svg>
                    Deleting...
                `);
                $btnSave.prop('disabled', true);

                const url = getEndpointUrl('/datacoverage/' + id);

                $.ajax({
                    url: url,
                    type: 'DELETE',
                    success: function (res) {
                        Toast.fire({
                            icon: 'success',
                            title: 'Stock deleted successfully.'
                        });

                        delete state.savedState[id];
                        delete state.draftState[id];
                        delete state.stocksMap[id];

                        if (state.selectedStockId === id) {
                            $('#detailCard').slideUp(200);
                            state.selectedStockId = null;
                        }

                        const $row = $(`.stock-row[data-id="${id}"]`);
                        $row.fadeOut(250, function () {
                            $(this).remove();
                        });

                        loadSummary();
                    },
                    error: function (xhr, status, err) {
                        $btnDelete.prop('disabled', false).html(`
                            <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5"><polyline points="3 6 5 6 21 6"></polyline><path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"></path></svg>
                            Delete
                        `);
                        checkRowDirtyState(id);

                        Toast.fire({
                            icon: 'error',
                            title: 'Unable to delete stock.'
                        });
                    }
                });
            }
        });
    }

    function renderEmptyTable(message) {
        $('#coverageTableBody').html(`
            <tr>
                <td colspan="7" style="text-align: center; padding: 50px 20px; color: var(--text-muted);">
                    <div style="font-size: 14px; font-weight: 500;">${escapeHtml(message)}</div>
                </td>
            </tr>
        `);
        $('#paginationInfo').text('Showing 0 to 0 of 0 stocks');
        $('#paginationControls').empty();
    }

    function showTableLoading() {
        $('#coverageTableBody').html(`
            <tr>
                <td colspan="7" style="text-align: center; padding: 50px 20px; color: var(--text-muted);">
                    <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" class="spin-icon" style="vertical-align: middle; margin-right: 8px;">
                        <line x1="12" y1="2" x2="12" y2="6"></line>
                        <line x1="12" y1="18" x2="12" y2="22"></line>
                        <line x1="4.93" y1="4.93" x2="7.76" y2="7.76"></line>
                        <line x1="16.24" y1="16.24" x2="19.07" y2="19.07"></line>
                        <line x1="2" y1="12" x2="6" y2="12"></line>
                        <line x1="18" y1="12" x2="22" y2="12"></line>
                        <line x1="4.93" y1="19.07" x2="7.76" y2="16.24"></line>
                        <line x1="16.24" y1="7.76" x2="19.07" y2="4.93"></line>
                    </svg>
                    Loading stock records...
                </td>
            </tr>
        `);
    }

    // Render Pagination Controls & Info
    function renderPaginationInfo() {
        if (state.totalCount === 0) {
            $('#paginationInfo').text('Showing 0 to 0 of 0 stocks');
            return;
        }

        const start = ((state.currentPage - 1) * state.pageSize) + 1;
        const end = Math.min(state.currentPage * state.pageSize, state.totalCount);
        $('#paginationInfo').text(`Showing ${formatNumber(start)} to ${formatNumber(end)} of ${formatNumber(state.totalCount)} stocks`);
    }

    function renderPaginationButtons() {
        const $container = $('#paginationControls');
        $container.empty();

        if (state.totalPages <= 1) return;

        // First & Prev buttons
        $container.append(`
            <button class="btn-page btn-nav-page" data-page="1" title="First Page" ${state.currentPage === 1 ? 'disabled' : ''}>« <span class="btn-nav-text">First</span></button>
            <button class="btn-page btn-nav-page" data-page="${state.currentPage - 1}" title="Previous Page" ${state.currentPage === 1 ? 'disabled' : ''}>‹ <span class="btn-nav-text">Prev</span></button>
        `);

        // Visible Page Numbers
        let startPage = Math.max(1, state.currentPage - 2);
        let endPage = Math.min(state.totalPages, startPage + 4);
        if (endPage - startPage < 4) {
            startPage = Math.max(1, endPage - 4);
        }

        for (let p = startPage; p <= endPage; p++) {
            $container.append(`
                <button class="btn-page btn-nav-page ${p === state.currentPage ? 'active' : ''}" data-page="${p}">${p}</button>
            `);
        }

        // Next & Last buttons
        $container.append(`
            <button class="btn-page btn-nav-page" data-page="${state.currentPage + 1}" title="Next Page" ${state.currentPage === state.totalPages ? 'disabled' : ''}><span class="btn-nav-text">Next</span> ›</button>
            <button class="btn-page btn-nav-page" data-page="${state.totalPages}" title="Last Page" ${state.currentPage === state.totalPages ? 'disabled' : ''}><span class="btn-nav-text">Last</span> »</button>
        `);
    }

    // Select Stock & Open Extended Specification Modal
    function selectStock(stockId) {
        state.selectedStockId = stockId;
        const stock = state.stocksMap[stockId];
        if (!stock) return;

        $('.stock-row').removeClass('selected-row');
        $(`.stock-row[data-id="${stockId}"]`).addClass('selected-row');

        const symbol = stock.symbol || stock.Symbol || '';
        const name = stock.name || stock.Name || symbol;
        const exchange = stock.exchange || stock.Exchange || 'NSE';
        const token = stock.instrumentToken || stock.InstrumentToken || 'N/A';
        const isActive = stock.isActive ?? stock.IsActive ?? false;
        const lastCandleDate = stock.lastCandleDate || stock.LastCandleDate;
        const lastPrice = stock.lastPrice || stock.LastPrice || stock.ltp || stock.Ltp;
        const lotSize = stock.lotSize || stock.LotSize || 1;
        const tickSize = stock.tickSize || stock.TickSize || 0.05;
        const segment = stock.segment || stock.Segment || (exchange + '_EQ');
        const instrumentType = stock.instrumentType || stock.InstrumentType || 'EQ';

        $('#modalSymbol').text(symbol);
        $('#modalCompanyName').text(name);
        $('#modalSymbolName').text(symbol);
        $('#modalExchangeBadge').text(exchange);
        $('#modalExchangeSegment').text(segment);
        $('#modalInstrumentToken').text(token);
        $('#modalInstrumentType').text(instrumentType);
        $('#modalLotSize').text(lotSize);
        $('#modalTickSize').text('₹' + parseFloat(tickSize).toFixed(2));
        $('#modalLastPrice').text(lastPrice ? ('₹' + parseFloat(lastPrice).toFixed(2)) : 'N/A');

        $('#modalStatusBadge').html(isActive ? '<span class="badge badge-active">Active</span>' : '<span class="badge badge-inactive">Inactive</span>');
        $('#modalLastCandleDate').text(lastCandleDate ? formatDate(lastCandleDate) : 'No data available');

        // Render Timeframe Badges
        const draft = state.draftState[stockId] || {};
        const tfConfig = [
            { key: 'history1M', label: '1M' },
            { key: 'history5M', label: '5M' },
            { key: 'history15M', label: '15M' },
            { key: 'history60M', label: '60M' },
            { key: 'history1D', label: '1D' }
        ];

        let badgesHtml = tfConfig.map(tf => {
            const val = draft[tf.key];
            const isStored = (val === 1 || val === '1');
            return isStored 
                ? `<span class="modal-tf-badge stored">✓ ${tf.label} Stored</span>`
                : `<span class="modal-tf-badge missing">✕ ${tf.label} Missing</span>`;
        }).join('');

        $('#modalTfBadges').html(badgesHtml);

        openModal();
    }

    function openModal() {
        const $modal = $('#stockDetailModal');
        $modal.css('display', 'flex');
        setTimeout(() => $modal.addClass('show'), 10);
    }

    function closeModal() {
        const $modal = $('#stockDetailModal');
        $modal.removeClass('show');
        setTimeout(() => $modal.hide(), 250);
        state.selectedStockId = null;
        $('.stock-row').removeClass('selected-row');
    }

    // Bind Event Handlers
    function bindEvents() {
        // Filter dropdown changes
        $('#statusFilter').on('change', function () {
            state.statusFilter = $(this).val();
            state.currentPage = 1;
            loadPaginatedList();
        });

        $('#historyFilter').on('change', function () {
            state.historyFilter = $(this).val();
            state.currentPage = 1;
            loadPaginatedList();
        });

        $('#alphabetFilter').on('change', function () {
            state.alphabetFilter = $(this).val();
            state.currentPage = 1;
            loadPaginatedList();
        });

        // Page Size change
        $('#pageSizeSelect').on('change', function () {
            state.pageSize = parseInt($(this).val(), 10) || 25;
            state.currentPage = 1;
            loadPaginatedList();
        });

        // Refresh Button
        $('#btnRefreshCoverage').on('click', function () {
            const $icon = $(this).find('svg');
            $icon.addClass('spin-icon');
            loadSummary();
            loadPaginatedList();
            setTimeout(() => $icon.removeClass('spin-icon'), 750);
        });

        // Export Excel Button
        $('#btnExportExcel').on('click', function () {
            const $btn = $(this);
            const originalHtml = $btn.html();
            $btn.prop('disabled', true).html(`
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" class="spin-icon">
                    <line x1="12" y1="2" x2="12" y2="6"></line>
                    <line x1="12" y1="18" x2="12" y2="22"></line>
                    <line x1="4.93" y1="4.93" x2="7.76" y2="7.76"></line>
                    <line x1="16.24" y1="16.24" x2="19.07" y2="19.07"></line>
                    <line x1="2" y1="12" x2="6" y2="12"></line>
                    <line x1="18" y1="12" x2="22" y2="12"></line>
                </svg>
                Exporting...
            `);

            const params = new URLSearchParams({
                search: state.searchQuery || '',
                status: state.statusFilter || 'all',
                historyFilter: state.historyFilter || 'all',
                alphabet: state.alphabetFilter || 'all'
            });

            const exportUrl = getEndpointUrl('/datacoverage/export-excel?' + params.toString());

            Toast.fire({
                icon: 'info',
                title: 'Generating Stock Excel report...'
            });

            window.location.href = exportUrl;

            setTimeout(() => {
                $btn.prop('disabled', false).html(originalHtml);
            }, 2500);
        });

        // Close Stock Specification Modal
        $('#btnCloseStockModal, #btnModalCloseFooter').on('click', function () {
            closeModal();
        });

        $('#stockDetailModal').on('click', function (e) {
            if ($(e.target).is('#stockDetailModal')) {
                closeModal();
            }
        });

        $(document).on('keydown', function (e) {
            if (e.key === 'Escape' && $('#stockDetailModal').is(':visible')) {
                closeModal();
            }
        });

        // Navigate to Manage History from Modal
        $('#btnModalManageHistory').on('click', function () {
            const stock = state.stocksMap[state.selectedStockId];
            const symbol = stock ? (stock.symbol || stock.Symbol || '') : '';
            if (symbol) {
                window.location.href = '/ManageHistory?symbol=' + encodeURIComponent(symbol);
            } else {
                window.location.href = '/ManageHistory';
            }
        });

        // Search Input with Debounce
        $('#searchInput').on('input', function () {
            const query = $(this).val();
            clearTimeout(searchTimer);
            searchTimer = setTimeout(function () {
                state.searchQuery = query;
                state.currentPage = 1;
                loadPaginatedList();
            }, 350);
        });

        // Prevent row selection when clicking inline controls
        $(document).on('click', '.inline-status-toggle, .tf-checkboxes-group, .history-status-group, .row-actions-group', function (e) {
            e.stopPropagation();
        });

        // "All" Timeframes Checkbox Change
        $(document).on('change', '.tf-all-checkbox', function (e) {
            e.stopPropagation();
            const id = parseInt($(this).data('id'), 10);
            if (!id || !state.draftState[id]) return;

            const isChecked = $(this).is(':checked');
            const keys = ['history1M', 'history5M', 'history15M', 'history60M', 'history1D'];

            keys.forEach(k => {
                const prevVal = state.draftState[id][k];
                if (isChecked) {
                    // Enabling sets 0 (pending worker job backfill) if it was null
                    state.draftState[id][k] = (prevVal === 1 || prevVal === 0) ? prevVal : 0;
                } else {
                    // Disabling sets null
                    state.draftState[id][k] = null;
                }
            });

            const $group = $(this).closest('.tf-checkboxes-group');
            $group.replaceWith(renderTfGroupHtml(id));

            checkRowDirtyState(id);
        });

        // Individual Timeframe Checkbox Change
        $(document).on('change', '.tf-checkbox', function (e) {
            e.stopPropagation();
            const id = parseInt($(this).data('id'), 10);
            const tfKey = $(this).data('tf');
            if (!id || !tfKey || !state.draftState[id]) return;

            const isChecked = $(this).is(':checked');
            const prevVal = state.draftState[id][tfKey];

            let newVal;
            if (isChecked) {
                // If previously null, enable sets 0 (pending worker job backfill)
                newVal = (prevVal === 1 || prevVal === 0) ? prevVal : 0;
            } else {
                // Unchecking sets null (disabled)
                newVal = null;
            }

            state.draftState[id][tfKey] = newVal;

            const $group = $(this).closest('.tf-checkboxes-group');
            $group.replaceWith(renderTfGroupHtml(id));

            checkRowDirtyState(id);
        });

        // Active Status Switch Change
        $(document).on('change', '.row-status-checkbox', function (e) {
            e.stopPropagation();
            const id = parseInt($(this).data('id'), 10);
            if (!id || !state.draftState[id]) return;

            const isChecked = $(this).is(':checked');
            state.draftState[id].isActive = isChecked;

            const $label = $(`#statusLabel_${id}`);
            $label.text(isChecked ? 'Active' : 'Inactive');
            if (isChecked) {
                $label.addClass('active-color').removeClass('inactive-color');
            } else {
                $label.addClass('inactive-color').removeClass('active-color');
            }

            checkRowDirtyState(id);
        });

        // Row Save Button Click
        $(document).on('click', '.btn-row-save', function (e) {
            e.preventDefault();
            e.stopPropagation();
            const id = parseInt($(this).data('id'), 10);
            if (id) saveRow(id);
        });

        // Row Delete Button Click
        $(document).on('click', '.btn-row-delete', function (e) {
            e.preventDefault();
            e.stopPropagation();
            const id = parseInt($(this).data('id'), 10);
            if (id) deleteStockRow(id);
        });

        // Row Click
        $(document).on('click', '.stock-row', function () {
            const id = parseInt($(this).data('id'), 10);
            selectStock(id);
        });

        // Pagination Navigation Buttons
        $(document).on('click', '.btn-nav-page', function () {
            if ($(this).is(':disabled')) return;
            const targetPage = parseInt($(this).data('page'), 10);
            if (targetPage && targetPage !== state.currentPage) {
                state.currentPage = targetPage;
                loadPaginatedList();
            }
        });
    }

    // Helper Utility Functions
    function formatNumber(num) {
        return (num || 0).toLocaleString('en-US');
    }

    function formatDate(dateStr) {
        if (!dateStr) return 'N/A';
        const d = new Date(dateStr);
        if (isNaN(d.getTime())) return dateStr;
        return d.toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });
    }

    function escapeHtml(text) {
        return (text || '').toString()
            .replace(/&/g, "&amp;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;")
            .replace(/'/g, "&#039;");
    }
});
