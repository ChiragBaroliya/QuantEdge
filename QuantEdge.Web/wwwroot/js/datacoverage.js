/**
 * Data Coverage Manager JavaScript Module
 */
$(document).ready(function () {
    const apiBaseUrl = $('.coverage-container').data('api-base-url') || 'https://localhost:44370';

    let state = {
        currentPage: 1,
        pageSize: 25,
        searchQuery: '',
        statusFilter: 'all',
        historyFilter: 'all',
        totalCount: 0,
        totalPages: 0,
        selectedStockId: null,
        stocksMap: {}
    };

    let searchTimer = null;

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
            url: apiBaseUrl + '/datacoverage/summary',
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
            page: state.currentPage,
            pageSize: state.pageSize
        };

        $.ajax({
            url: apiBaseUrl + '/datacoverage/list',
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
        $('#selectAllCheckbox').prop('checked', false);
        updateBulkDeleteButton();

        if (items.length === 0) {
            renderEmptyTable('No stocks matching the selected filters');
            return;
        }

        items.forEach(stock => {
            const id = stock.id || stock.Id;
            const symbol = stock.symbol || stock.Symbol || '';
            const isActive = stock.isActive ?? stock.IsActive ?? false;
            const is1dStored = (stock.isHistryStored1d ?? stock.IsHistryStored1d ?? 0) === 1;
            const is60mStored = (stock.isHistryStored60m ?? stock.IsHistryStored60m ?? 0) === 1;

            const isSelected = state.selectedStockId === id;

            const statusBadge = isActive
                ? '<span class="badge badge-active">Active</span>'
                : '<span class="badge badge-inactive">Inactive</span>';

            const formatted1d = is1dStored
                ? '<span class="badge badge-active">Stored</span>'
                : '<span class="candle-text no-data">No data</span>';

            const formatted60m = is60mStored
                ? '<span class="badge badge-active">Stored</span>'
                : '<span class="candle-text no-data">No data</span>';

            const rowHtml = `
                <tr class="stock-row ${isSelected ? 'selected-row' : ''}" data-id="${id}">
                    <td onclick="event.stopPropagation();">
                        <input type="checkbox" class="row-checkbox" data-id="${id}" />
                    </td>
                    <td style="font-weight: 600; color: #f8fafc;">${escapeHtml(symbol)}</td>
                    <td>${statusBadge}</td>
                    <td>${formatted1d}</td>
                    <td>${formatted60m}</td>
                </tr>
            `;

            $tbody.append(rowHtml);
        });
    }

    function renderEmptyTable(message) {
        $('#coverageTableBody').html(`
            <tr>
                <td colspan="5" style="text-align: center; padding: 40px; color: #94a3b8;">
                    ${escapeHtml(message)}
                </td>
            </tr>
        `);
        $('#paginationInfo').text('Showing 0 to 0 of 0 stocks');
        $('#paginationControls').empty();
    }

    function showTableLoading() {
        $('#coverageTableBody').html(`
            <tr>
                <td colspan="5" style="text-align: center; padding: 40px; color: #94a3b8;">
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
            <button class="btn-page btn-nav-page" data-page="1" ${state.currentPage === 1 ? 'disabled' : ''}>« First</button>
            <button class="btn-page btn-nav-page" data-page="${state.currentPage - 1}" ${state.currentPage === 1 ? 'disabled' : ''}>‹ Prev</button>
        `);

        // Visible Page Numbers (window of max 5 buttons around current page)
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
            <button class="btn-page btn-nav-page" data-page="${state.currentPage + 1}" ${state.currentPage === state.totalPages ? 'disabled' : ''}>Next ›</button>
            <button class="btn-page btn-nav-page" data-page="${state.totalPages}" ${state.currentPage === state.totalPages ? 'disabled' : ''}>Last »</button>
        `);
    }

    // 3. Select Stock & Populate Detail Card with Edit Form
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

        $('#detailSymbol').text(symbol);
        $('#detailName').text(name);
        $('#detailExchange').text(exchange);
        $('#detailInstrumentToken').text(token);
        $('#detailStatus').html(isActive ? '<span class="badge badge-active">Active</span>' : '<span class="badge badge-inactive">Inactive</span>');
        $('#detailLastCandleDate').text(lastCandleDate ? formatDate(lastCandleDate) : 'No data');

        // Populate Form Controls
        $('#editStockId').val(stockId);
        $('#chkIsActive').prop('checked', isActive);
        $('#chkHistory1m').prop('checked', (stock.isHistryStored1m ?? stock.IsHistryStored1m ?? 0) === 1);
        $('#chkHistory5m').prop('checked', (stock.isHistryStored5m ?? stock.IsHistryStored5m ?? 0) === 1);
        $('#chkHistory15m').prop('checked', (stock.isHistryStored15m ?? stock.IsHistryStored15m ?? 0) === 1);
        $('#chkHistory60m').prop('checked', (stock.isHistryStored60m ?? stock.IsHistryStored60m ?? 0) === 1);
        $('#chkHistory1d').prop('checked', (stock.isHistryStored1d ?? stock.IsHistryStored1d ?? 0) === 1);

        $('#detailCard').slideDown(250);
        $('html, body').animate({
            scrollTop: $('#detailCard').offset().top - 100
        }, 300);
    }

    // 4. Save / Update Stock Coverage Flags
    function saveCoverageFlags() {
        const id = parseInt($('#editStockId').val(), 10);
        if (!id) return;

        const payload = {
            id: id,
            isActive: $('#chkIsActive').is(':checked'),
            isHistryStored1m: $('#chkHistory1m').is(':checked') ? 1 : 0,
            isHistryStored5m: $('#chkHistory5m').is(':checked') ? 1 : 0,
            isHistryStored15m: $('#chkHistory15m').is(':checked') ? 1 : 0,
            isHistryStored60m: $('#chkHistory60m').is(':checked') ? 1 : 0,
            isHistryStored1d: $('#chkHistory1d').is(':checked') ? 1 : 0
        };

        const $btn = $('#btnSaveCoverageFlags');
        $btn.prop('disabled', true).html('Saving...');

        $.ajax({
            url: apiBaseUrl + '/datacoverage/update',
            type: 'POST',
            contentType: 'application/json',
            data: JSON.stringify(payload),
            success: function (res) {
                $btn.prop('disabled', false).html(`
                    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                        <path d="M19 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11l5 5v11a2 2 0 0 1-2 2z"></path>
                        <polyline points="17 21 17 13 7 13 7 21"></polyline>
                        <polyline points="7 3 7 8 15 8"></polyline>
                    </svg>
                    Save / Update Flags
                `);

                Swal.fire({
                    icon: 'success',
                    title: 'Stock Flags Updated',
                    text: 'Active status and timeframe history flags saved successfully.',
                    timer: 2000,
                    showConfirmButton: false,
                    background: 'var(--bg-card, #1e293b)',
                    color: '#f8fafc'
                });

                // Reload data
                loadSummary();
                loadPaginatedList();
            },
            error: function (xhr, status, err) {
                $btn.prop('disabled', false).html('Save / Update Flags');
                Swal.fire({
                    icon: 'error',
                    title: 'Update Failed',
                    text: xhr.responseText || err || 'Failed to save stock flags.',
                    background: 'var(--bg-card, #1e293b)',
                    color: '#f8fafc'
                });
            }
        });
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

        $('#pageSizeSelect').on('change', function () {
            state.pageSize = parseInt($(this).val(), 10);
            state.currentPage = 1;
            loadPaginatedList();
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

        // Select All Checkbox
        $('#selectAllCheckbox').on('change', function () {
            const isChecked = $(this).is(':checked');
            $('.row-checkbox').prop('checked', isChecked);
            updateBulkDeleteButton();
        });

        $(document).on('change', '.row-checkbox', function () {
            updateBulkDeleteButton();
        });

        // Row Click & Action Button Click
        $(document).on('click', '.stock-row', function () {
            const id = parseInt($(this).data('id'), 10);
            selectStock(id);
        });

        $(document).on('click', '.btn-view-detail, .btn-edit-flag', function (e) {
            e.stopPropagation();
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

        // Save Flags Button
        $('#btnSaveCoverageFlags').on('click', function (e) {
            e.preventDefault();
            saveCoverageFlags();
        });

        // Delete Stock Button
        $('#btnDeleteStock').on('click', function (e) {
            e.preventDefault();
            deleteStock();
        });

        // Bulk Delete Button
        $('#btnBulkDelete').on('click', function (e) {
            e.preventDefault();
            bulkDeleteStocks();
        });
    }

    function updateBulkDeleteButton() {
        const selectedIds = getSelectedStockIds();
        const count = selectedIds.length;
        $('#selectedCount').text(count);
        if (count > 0) {
            $('#btnBulkDelete').fadeIn(150);
        } else {
            $('#btnBulkDelete').fadeOut(150);
            $('#selectAllCheckbox').prop('checked', false);
        }
    }

    function getSelectedStockIds() {
        const ids = [];
        $('.row-checkbox:checked').each(function () {
            const id = parseInt($(this).data('id'), 10);
            if (id) ids.push(id);
        });
        return ids;
    }

    // 5. Delete Single Stock Record
    function deleteStock() {
        const id = parseInt($('#editStockId').val(), 10);
        if (!id) return;

        const stock = state.stocksMap[id];
        const symbol = stock ? (stock.symbol || stock.Symbol || '') : 'this stock';

        Swal.fire({
            title: 'Delete Stock?',
            text: `Are you sure you want to permanently delete ${symbol} from the database?`,
            icon: 'warning',
            showCancelButton: true,
            confirmButtonColor: '#ef4444',
            cancelButtonColor: '#475569',
            confirmButtonText: 'Yes, Delete',
            background: 'var(--bg-card, #1e293b)',
            color: '#f8fafc'
        }).then((result) => {
            if (result.isConfirmed) {
                $.ajax({
                    url: apiBaseUrl + '/datacoverage/' + id,
                    type: 'DELETE',
                    success: function (res) {
                        Swal.fire({
                            icon: 'success',
                            title: 'Deleted!',
                            text: `${symbol} has been deleted successfully.`,
                            timer: 2000,
                            showConfirmButton: false,
                            background: 'var(--bg-card, #1e293b)',
                            color: '#f8fafc'
                        });

                        $('#detailCard').slideUp(200);
                        state.selectedStockId = null;

                        loadSummary();
                        loadPaginatedList();
                    },
                    error: function (xhr, status, err) {
                        Swal.fire({
                            icon: 'error',
                            title: 'Delete Failed',
                            text: xhr.responseText || err || 'Failed to delete stock.',
                            background: 'var(--bg-card, #1e293b)',
                            color: '#f8fafc'
                        });
                    }
                });
            }
        });
    }

    // 6. Bulk Delete Stocks
    function bulkDeleteStocks() {
        const selectedIds = getSelectedStockIds();
        if (selectedIds.length === 0) return;

        Swal.fire({
            title: `Delete ${selectedIds.length} Selected Stocks?`,
            text: `Are you sure you want to permanently delete ${selectedIds.length} selected stocks from the database?`,
            icon: 'warning',
            showCancelButton: true,
            confirmButtonColor: '#ef4444',
            cancelButtonColor: '#475569',
            confirmButtonText: `Yes, Delete ${selectedIds.length} Stocks`,
            background: 'var(--bg-card, #1e293b)',
            color: '#f8fafc'
        }).then((result) => {
            if (result.isConfirmed) {
                $.ajax({
                    url: apiBaseUrl + '/datacoverage/bulk-delete',
                    type: 'POST',
                    contentType: 'application/json',
                    data: JSON.stringify(selectedIds),
                    success: function (res) {
                        Swal.fire({
                            icon: 'success',
                            title: 'Bulk Delete Complete',
                            text: `${selectedIds.length} stocks deleted successfully.`,
                            timer: 2000,
                            showConfirmButton: false,
                            background: 'var(--bg-card, #1e293b)',
                            color: '#f8fafc'
                        });

                        if (selectedIds.includes(state.selectedStockId)) {
                            $('#detailCard').slideUp(200);
                            state.selectedStockId = null;
                        }

                        updateBulkDeleteButton();
                        loadSummary();
                        loadPaginatedList();
                    },
                    error: function (xhr, status, err) {
                        Swal.fire({
                            icon: 'error',
                            title: 'Bulk Delete Failed',
                            text: xhr.responseText || err || 'Failed to bulk delete selected stocks.',
                            background: 'var(--bg-card, #1e293b)',
                            color: '#f8fafc'
                        });
                    }
                });
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
