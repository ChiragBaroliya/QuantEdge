/**
 * Manage History Page JavaScript Module
 */
$(document).ready(function () {
    const apiBaseUrl = $('.history-container').data('api-base-url') || 'https://localhost:44370';

    let selectedTimeframe = '1m';

    // 1. Initialize Page Controls & Dates
    initPage();

    function initPage() {
        // Default FromDate and ToDate to Today
        const todayStr = new Date().toISOString().split('T')[0];
        $('#historyFromDate').val(todayStr);
        $('#historyToDate').val(todayStr);

        loadStockDropdown();
        bindEvents();
        initSignalR();
    }

    // 2. Load Stock Dropdown Options (All Stocks + Active Stocks List)
    function loadStockDropdown() {
        $.ajax({
            url: apiBaseUrl + '/api/marketdata/stocks',
            type: 'GET',
            success: function (data) {
                const $select = $('#stockSelector');
                $select.empty();

                // Add "ALL STOCKS" default option
                $select.append('<option value="" selected>ALL STOCKS (All Active Instruments)</option>');

                if (Array.isArray(data)) {
                    data.forEach(item => {
                        const sym = item.symbol || item.Symbol || item;
                        if (sym) {
                            $select.append(`<option value="${escapeHtml(sym)}">${escapeHtml(sym)}</option>`);
                        }
                    });
                }

                if ($.fn.select2) {
                    $select.select2({
                        theme: 'bootstrap-5',
                        width: '100%'
                    });
                }
            },
            error: function (xhr, status, err) {
                console.error('Failed to load active stocks:', err);
                addLogEntry('Failed to load stock list from API.', 'error');
            }
        });
    }

    // 3. Event Binds
    function bindEvents() {
        // Timeframe button selection
        $('.timeframe-card-btn').click(function () {
            $('.timeframe-card-btn').removeClass('active');
            $(this).addClass('active');
            selectedTimeframe = $(this).data('tf');
        });

        // Execute Reset History button click
        $('#btnStartHistoryReset').click(function () {
            triggerHistoryReset();
        });
    }

    // 4. Trigger History Reset Action
    async function triggerHistoryReset() {
        const fromDate = $('#historyFromDate').val();
        const toDate = $('#historyToDate').val();
        const symbol = $('#stockSelector').val() || '';

        if (!fromDate || !toDate) {
            Swal.fire({
                icon: 'warning',
                title: 'Date Range Required',
                text: 'Please select both From Date and To Date.',
                background: '#1e293b',
                color: '#f8fafc'
            });
            return;
        }

        if (fromDate > toDate) {
            Swal.fire({
                icon: 'error',
                title: 'Invalid Date Range',
                text: 'From Date cannot be later than To Date.',
                background: '#1e293b',
                color: '#f8fafc'
            });
            return;
        }

        const targetText = symbol ? `Stock: ${symbol}` : 'ALL active stocks';
        const confirmResult = await Swal.fire({
            title: 'Clear & Recreate History?',
            html: `Are you sure you want to clear and insert history for <strong>${escapeHtml(targetText)}</strong><br/>` +
                  `Timeframe: <strong>${selectedTimeframe.toUpperCase()}</strong><br/>` +
                  `Date Range: <strong>${fromDate} to ${toDate}</strong>?`,
            icon: 'warning',
            showCancelButton: true,
            confirmButtonColor: '#ef4444',
            cancelButtonColor: '#64748b',
            confirmButtonText: 'Yes, Reset History',
            background: '#1e293b',
            color: '#f8fafc'
        });

        if (!confirmResult.isConfirmed) return;

        const $btn = $('#btnStartHistoryReset');
        $btn.prop('disabled', true).html('<span class="spinner-border spinner-border-sm me-2"></span> Starting Task...');

        // Reset progress UI
        $('#progressCard').slideDown();
        updateProgressBar(0, `Starting reset task for ${targetText} (${selectedTimeframe})...`);
        addLogEntry(`[${new Date().toLocaleTimeString()}] Initiating history reset for ${targetText} (${selectedTimeframe}) from ${fromDate} to ${toDate}...`, 'info');

        try {
            const url = `${apiBaseUrl}/api/marketdata/history/reset?timeframe=${encodeURIComponent(selectedTimeframe)}&fromDate=${encodeURIComponent(fromDate)}&toDate=${encodeURIComponent(toDate)}&symbol=${encodeURIComponent(symbol)}`;
            
            const res = await fetch(url, { method: 'POST' });
            if (!res.ok) {
                const errText = await res.text();
                throw new Error(errText || 'Failed to start reset task.');
            }

            Swal.fire({
                icon: 'info',
                title: 'Reset Task Started',
                text: `Background task is running. Follow real-time progress below.`,
                background: '#1e293b',
                color: '#f8fafc',
                timer: 3000,
                timerProgressBar: true
            });
        } catch (ex) {
            console.error('History reset error:', ex);
            addLogEntry(`[Error] ${ex.message}`, 'error');
            Swal.fire({
                icon: 'error',
                title: 'Task Failed to Start',
                text: ex.message,
                background: '#1e293b',
                color: '#f8fafc'
            });
            $btn.prop('disabled', false).html('<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M21 12a9 9 0 1 1-9-9c2.52 0 4.93 1 6.74 2.74L21 8"></path><polyline points="21 3 21 8 16 8"></polyline></svg> Clear & Recreate History');
        }
    }

    // 5. SignalR Real-time Progress Setup
    function initSignalR() {
        if (typeof signalR === 'undefined') {
            console.warn('SignalR library not loaded.');
            return;
        }

        const hubUrl = `${apiBaseUrl}/api/hubs/marketdata`;
        const connection = new signalR.HubConnectionBuilder()
            .withUrl(hubUrl)
            .withAutomaticReconnect()
            .build();

        connection.on('SyncProgress', (data) => {
            if (!data) return;
            const msg = data.message || 'Processing...';
            const pct = data.progress ?? 0;
            updateProgressBar(pct, msg);
            addLogEntry(`[Progress ${pct}%] ${msg}`, 'info');
        });

        connection.on('SyncComplete', (data) => {
            const msg = data?.message || 'Sync completed successfully!';
            updateProgressBar(100, msg);
            addLogEntry(`[Success] ${msg}`, 'success');

            $('#btnStartHistoryReset').prop('disabled', false).html('<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M21 12a9 9 0 1 1-9-9c2.52 0 4.93 1 6.74 2.74L21 8"></path><polyline points="21 3 21 8 16 8"></polyline></svg> Clear & Recreate History');

            Swal.fire({
                icon: 'success',
                title: 'History Reset Complete',
                text: msg,
                background: '#1e293b',
                color: '#f8fafc'
            });
        });

        connection.on('SyncError', (data) => {
            const msg = data?.message || 'Error occurred during sync.';
            addLogEntry(`[Error] ${msg}`, 'error');
            $('#btnStartHistoryReset').prop('disabled', false).html('<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M21 12a9 9 0 1 1-9-9c2.52 0 4.93 1 6.74 2.74L21 8"></path><polyline points="21 3 21 8 16 8"></polyline></svg> Clear & Recreate History');

            Swal.fire({
                icon: 'error',
                title: 'Sync Failed',
                text: msg,
                background: '#1e293b',
                color: '#f8fafc'
            });
        });

        connection.start()
            .then(() => console.log('SignalR MarketDataHub connected for Manage History.'))
            .catch(err => console.error('SignalR Hub Connection Error:', err));
    }

    function updateProgressBar(pct, label) {
        $('#progressBarFill').css('width', `${pct}%`);
        $('#progressPercentText').text(`${pct}%`);
        $('#progressStatusText').text(label);
    }

    function addLogEntry(text, type = 'info') {
        const $logBox = $('#historyLogBox');
        const entryHtml = `<div class="log-entry ${type}">${escapeHtml(text)}</div>`;
        $logBox.append(entryHtml);
        $logBox.scrollTop($logBox[0].scrollHeight);
    }

    function escapeHtml(str) {
        if (!str) return '';
        return String(str)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#039;');
    }
});
