// Shared manual "Send to Real Trade" helpers used by SwingTrading/Index.cshtml and SwingTrading/EtfList.cshtml.
// Requires a page-level `const API_BASE_URL = "@ViewBag.ApiBaseUrl";` to be declared before this file is loaded.

function getEndpointUrl(path) {
    let base = (API_BASE_URL || '').replace(/\/+$/, '');
    let endpoint = path.startsWith('/') ? path : '/' + path;

    if (base.endsWith('/api') && endpoint.startsWith('/api/')) {
        endpoint = endpoint.substring(4);
    }
    return base + endpoint;
}

function confirmSendToRealTrade(symbol, entryPrice, metConditionsCount) {
    Swal.fire({
        icon: 'warning',
        title: `⚡ Send ${symbol} to Real Trade?`,
        html: `
            <div style="text-align:left; font-size: 13px; color: #cbd5e1;">
                <p>This places a <strong style="color:#f87171;">REAL MONEY</strong> BUY order with your broker for <strong>${symbol}</strong> at ~₹${Number(entryPrice).toFixed(2)}, right now &mdash; it does not wait for the next auto-scan.</p>
                <p>Quantity is computed automatically from your Real Trade Settings (Fixed Amount per Trade / entry price), same as automatic execution.</p>
                <p>All existing safety checks still apply: Real Trade master switch must be ON, trading window, daily trade limit, daily loss limit, capital, and no duplicate open position in ${symbol}.</p>
            </div>
        `,
        showCancelButton: true,
        confirmButtonText: 'Yes, Place Real Order',
        confirmButtonColor: '#dc2626',
        cancelButtonText: 'Cancel',
        background: 'var(--bg-card)',
        color: 'var(--text-primary)'
    }).then(function(result) {
        if (result.isConfirmed) {
            sendToRealTrade(symbol, entryPrice, metConditionsCount);
        }
    });
}

function sendToRealTrade(symbol, entryPrice, metConditionsCount) {
    $.ajax({
        url: getEndpointUrl('/api/realtrade/manual-buy'),
        type: "POST",
        contentType: "application/json",
        data: JSON.stringify({
            symbol: symbol,
            entryPrice: entryPrice,
            metConditionsCount: metConditionsCount
        }),
        success: function(res) {
            Swal.fire({
                icon: res && res.success ? 'success' : 'info',
                title: res && res.success ? 'Real Order Submitted' : 'Not Executed',
                text: (res && res.message) || 'Request completed.',
                confirmButtonColor: 'var(--theme-accent)',
                background: 'var(--bg-card)',
                color: 'var(--text-primary)'
            });
        },
        error: function(err) {
            Swal.fire({
                icon: 'error',
                title: 'Request Failed',
                text: (err.responseJSON && err.responseJSON.message) || err.responseText || "Internal server error",
                confirmButtonColor: 'var(--theme-accent)',
                background: 'var(--bg-card)',
                color: 'var(--text-primary)'
            });
        }
    });
}
