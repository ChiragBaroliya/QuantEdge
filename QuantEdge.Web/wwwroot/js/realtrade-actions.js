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

// Pre-fill defaults for the Manual Real Trade popup, used only if /api/realtrade/settings can't be
// reached - these mirror AutoRealTradeService's own mandatory fallback %.
const MANUAL_TRADE_DEFAULT_SL_PCT = 3.0;
const MANUAL_TRADE_DEFAULT_TSL_PCT = 2.0;

function formatClockTime(date) {
    return date ? date.toLocaleTimeString() : '—';
}

// Opens the Manual Real Trade configuration popup: fetches the latest LTP and the current default
// SL%/Trailing SL% to pre-fill, lets the user override Quantity/SL%/Trailing SL% for this trade only,
// then submits to /api/realtrade/manual-buy - which independently re-fetches its own live quote and
// re-runs every existing risk gate before actually placing the order.
async function confirmSendToRealTrade(symbol, entryPrice, metConditionsCount) {
    symbol = (symbol || '').toUpperCase().trim();

    let settings = null;
    let quote = null;
    try {
        const results = await Promise.allSettled([
            $.get(getEndpointUrl('/api/realtrade/settings')),
            $.get(getEndpointUrl(`/api/realtrade/quote?symbol=${encodeURIComponent(symbol)}`))
        ]);
        if (results[0].status === 'fulfilled') settings = results[0].value;
        if (results[1].status === 'fulfilled') quote = results[1].value;
    } catch (err) {
        // Fall through with nulls - the popup still opens using the scan-time price and fixed defaults.
    }

    const liveLtp = (quote && quote.success && quote.ltp > 0) ? Number(quote.ltp) : Number(entryPrice);
    const quoteAsOf = (quote && quote.success && quote.asOfUtc) ? new Date(quote.asOfUtc) : null;
    const defaultSlPct = (settings && settings.stopLossPct > 0) ? settings.stopLossPct : MANUAL_TRADE_DEFAULT_SL_PCT;
    const defaultTslPct = (settings && settings.trailingSlPct > 0) ? settings.trailingSlPct : MANUAL_TRADE_DEFAULT_TSL_PCT;
    const defaultQty = (settings && settings.fixedAmountPerTrade > 0 && liveLtp > 0)
        ? Math.max(1, Math.floor(settings.fixedAmountPerTrade / liveLtp))
        : 1;

    let refreshTimer = null;

    const result = await Swal.fire({
        title: `⚡ Manual Real Trade — ${symbol}`,
        html: `
            <div style="text-align:left; font-size:13px; color:#cbd5e1;">
                <div style="display:flex; justify-content:space-between; align-items:baseline; margin-bottom:10px;">
                    <span>Side: <strong style="color:#4ade80;">BUY</strong></span>
                    <span>Current LTP: <strong id="mrtLtp" data-ltp="${liveLtp}">₹${liveLtp.toFixed(2)}</strong>
                        <small style="color:#94a3b8;"> (as of <span id="mrtLtpTime">${formatClockTime(quoteAsOf)}</span>)</small>
                    </span>
                </div>
                <div class="mb-2">
                    <label class="form-label" style="display:block; margin-bottom:4px;">Quantity</label>
                    <input type="number" id="mrtQty" class="swal2-input" style="margin:0; width:100%;" min="1" step="1" value="${defaultQty}" />
                </div>
                <div style="display:flex; gap:10px;">
                    <div class="mb-2" style="flex:1;">
                        <label class="form-label" style="display:block; margin-bottom:4px;">Stop Loss (%)</label>
                        <input type="number" id="mrtSl" class="swal2-input" style="margin:0; width:100%;" min="0.1" max="100" step="0.1" value="${defaultSlPct}" />
                    </div>
                    <div class="mb-2" style="flex:1;">
                        <label class="form-label" style="display:block; margin-bottom:4px;">Trailing Stop Loss (%)</label>
                        <input type="number" id="mrtTsl" class="swal2-input" style="margin:0; width:100%;" min="0.1" max="50" step="0.1" value="${defaultTslPct}" />
                    </div>
                </div>
                <div style="margin-top:8px; padding:8px; background:rgba(255,255,255,0.05); border-radius:6px;">
                    <div>Estimated Stop Loss Price: <strong id="mrtSlPrice">-</strong></div>
                    <div>Estimated Risk: <strong id="mrtRisk">-</strong></div>
                </div>
                <p style="color:#94a3b8; margin-top:10px; margin-bottom:0; font-size:12px;">
                    All existing safety checks still apply: Real Trade master switch, trading window, daily
                    trade/loss limits, capital, and no duplicate open position in ${symbol}. The live price is
                    re-checked at the moment you click Place Real Trade - if it has moved, SL/Trailing SL are
                    recalculated against the actual executed price, not this estimate.
                </p>
            </div>
        `,
        showCancelButton: true,
        confirmButtonText: '⚡ Place Real Trade',
        confirmButtonColor: '#dc2626',
        cancelButtonText: 'Cancel',
        background: 'var(--bg-card)',
        color: 'var(--text-primary)',
        showLoaderOnConfirm: true,
        allowOutsideClick: () => !Swal.isLoading(),
        didOpen: () => {
            const qtyEl = document.getElementById('mrtQty');
            const slEl = document.getElementById('mrtSl');
            const ltpEl = document.getElementById('mrtLtp');
            const slPriceEl = document.getElementById('mrtSlPrice');
            const riskEl = document.getElementById('mrtRisk');

            const recalc = () => {
                const qty = parseInt(qtyEl.value, 10) || 0;
                const slPct = parseFloat(slEl.value) || 0;
                const ltp = parseFloat(ltpEl.dataset.ltp) || 0;
                const slPrice = (slPct > 0 && ltp > 0) ? ltp * (1 - slPct / 100) : 0;
                slPriceEl.textContent = slPrice > 0 ? `₹${slPrice.toFixed(2)}` : '-';
                riskEl.textContent = (slPrice > 0 && qty > 0) ? `₹${((ltp - slPrice) * qty).toFixed(2)}` : '-';
            };
            qtyEl.addEventListener('input', recalc);
            slEl.addEventListener('input', recalc);
            recalc();

            // Live-refresh the displayed LTP every 5s while the popup is open - purely for display;
            // the authoritative re-fetch/recalculation always happens server-side at submit time.
            refreshTimer = setInterval(async () => {
                try {
                    const q = await $.get(getEndpointUrl(`/api/realtrade/quote?symbol=${encodeURIComponent(symbol)}`));
                    if (q && q.success && q.ltp > 0) {
                        ltpEl.dataset.ltp = q.ltp;
                        ltpEl.textContent = `₹${Number(q.ltp).toFixed(2)}`;
                        document.getElementById('mrtLtpTime').textContent = formatClockTime(q.asOfUtc ? new Date(q.asOfUtc) : new Date());
                        recalc();
                    }
                } catch (e) {
                    // Transient failure - keep showing the last known price rather than erroring the popup.
                }
            }, 5000);
        },
        willClose: () => {
            if (refreshTimer) {
                clearInterval(refreshTimer);
                refreshTimer = null;
            }
        },
        preConfirm: async () => {
            const qty = parseInt(document.getElementById('mrtQty').value, 10);
            const slPct = parseFloat(document.getElementById('mrtSl').value);
            const tslPct = parseFloat(document.getElementById('mrtTsl').value);

            if (!qty || qty < 1) {
                Swal.showValidationMessage('Quantity must be a positive whole number.');
                return false;
            }
            if (!slPct || slPct <= 0) {
                Swal.showValidationMessage('Stop Loss % must be greater than zero.');
                return false;
            }
            if (!tslPct || tslPct <= 0) {
                Swal.showValidationMessage('Trailing Stop Loss % must be greater than zero.');
                return false;
            }

            const response = await sendToRealTrade(symbol, entryPrice, metConditionsCount, qty, slPct, tslPct);
            if (!response || response.success !== true) {
                Swal.showValidationMessage((response && response.message) || 'Real Trade order was not executed.');
                return false;
            }
            return response;
        }
    });

    if (result.isConfirmed && result.value) {
        Swal.fire({
            icon: 'success',
            title: 'Real Order Submitted',
            text: result.value.message || 'Request completed.',
            confirmButtonColor: 'var(--theme-accent)',
            background: 'var(--bg-card)',
            color: 'var(--text-primary)'
        });
    }
}

// POSTs the Manual Real Trade order. Resolves to a normalized { success, message } object in both the
// success and failure case (network/HTTP errors included) so callers never need a separate catch.
function sendToRealTrade(symbol, entryPrice, metConditionsCount, quantity, stopLossPct, trailingSlPct) {
    return $.ajax({
        url: getEndpointUrl('/api/realtrade/manual-buy'),
        type: "POST",
        contentType: "application/json",
        data: JSON.stringify({
            symbol: symbol,
            entryPrice: entryPrice,
            metConditionsCount: metConditionsCount,
            quantity: quantity,
            stopLossPct: stopLossPct,
            trailingSlPct: trailingSlPct
        })
    }).then(
        function (res) { return res; },
        function (err) {
            return {
                success: false,
                message: (err.responseJSON && err.responseJSON.message) || err.responseText || "Internal server error"
            };
        }
    );
}
