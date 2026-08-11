/**
 * QuantEdge Stock Candle Summary & Timeframe Audit JS Module
 */

let apiBaseUrl = "";
let currentPage = 1;
let pageSize = 25;
let refreshTimerSeconds = 300; // 5 Minutes
let refreshInterval = null;

document.addEventListener("DOMContentLoaded", function () {
    const configElem = document.getElementById("candle-summary-config");
    if (configElem) {
        apiBaseUrl = configElem.dataset.apiBaseUrl || "";
    }

    setDefaultDates();
    loadActiveSymbols();
    loadCandleSummary();

    setupEventListeners();
    startAutoRefreshTimer();
});

function setDefaultDates() {
    const todayStr = new Date().toISOString().split('T')[0];
    const fromInput = document.getElementById("filterFromDate");
    const toInput = document.getElementById("filterToDate");

    if (fromInput) fromInput.value = todayStr;
    if (toInput) toInput.value = todayStr;
}

function setupEventListeners() {
    const btnApply = document.getElementById("btnApplyFilter");
    if (btnApply) {
        btnApply.addEventListener("click", function () {
            currentPage = 1;
            loadCandleSummary();
            resetAutoRefreshTimer();
        });
    }

    const selectSize = document.getElementById("selectPageSize");
    if (selectSize) {
        selectSize.addEventListener("change", function () {
            pageSize = parseInt(this.value, 10) || 25;
            currentPage = 1;
            loadCandleSummary();
        });
    }

    const btnPrev = document.getElementById("btnPrevPage");
    if (btnPrev) {
        btnPrev.addEventListener("click", function () {
            if (currentPage > 1) {
                currentPage--;
                loadCandleSummary();
            }
        });
    }

    const btnNext = document.getElementById("btnNextPage");
    if (btnNext) {
        btnNext.addEventListener("click", function () {
            currentPage++;
            loadCandleSummary();
        });
    }
}

function startAutoRefreshTimer() {
    if (refreshInterval) clearInterval(refreshInterval);

    refreshTimerSeconds = 300; // 5 Minutes
    updateTimerDisplay();

    refreshInterval = setInterval(function () {
        refreshTimerSeconds--;

        if (refreshTimerSeconds <= 0) {
            refreshTimerSeconds = 300;
            loadCandleSummary();
        }

        updateTimerDisplay();
    }, 1000);
}

function resetAutoRefreshTimer() {
    refreshTimerSeconds = 300;
    updateTimerDisplay();
}

function updateTimerDisplay() {
    const timerElem = document.getElementById("refreshTimerText");
    if (!timerElem) return;

    const mins = Math.floor(refreshTimerSeconds / 60);
    const secs = refreshTimerSeconds % 60;
    timerElem.innerText = `${mins.toString().padStart(2, '0')}:${secs.toString().padStart(2, '0')}`;
}

function getEndpointUrl(endpoint) {
    let base = (apiBaseUrl || '').replace(/\/+$/, '');
    let path = endpoint.startsWith('/') ? endpoint : '/' + endpoint;

    if (base.endsWith('/api') && path.startsWith('/api/')) {
        path = path.substring(4);
    }
    return base + path;
}

async function loadActiveSymbols() {
    try {
        const response = await fetch(getEndpointUrl('/api/CandleSummary/symbols'));
        if (!response.ok) return;

        const symbols = await response.json();
        const selectSymbol = document.getElementById("filterSymbol");
        if (!selectSymbol || !symbols) return;

        selectSymbol.innerHTML = '<option value="ALL" selected>All Symbols (~190 NSE)</option>';

        symbols.forEach(sym => {
            const opt = document.createElement("option");
            opt.value = sym;
            opt.textContent = sym;
            selectSymbol.appendChild(opt);
        });

        // Initialize Select2 searchable dropdown
        if (typeof $ !== 'undefined' && $.fn && $.fn.select2) {
            $('.select2-symbol').select2({
                width: '100%',
                placeholder: 'Search Stock Symbol...'
            }).on('change', function () {
                currentPage = 1;
                loadCandleSummary();
                resetAutoRefreshTimer();
            });
        }
    } catch (err) {
        console.error("Failed to load active symbols for candle summary filter:", err);
    }
}

async function loadCandleSummary() {
    const tbody = document.getElementById("tblSummaryBody");
    if (tbody) {
        tbody.innerHTML = `<tr><td colspan="9" class="text-center py-4 text-white" style="color: #ffffff !important;">🔄 Fetching candlestick summary data...</td></tr>`;
    }

    const fromDate = document.getElementById("filterFromDate")?.value || "";
    const toDate = document.getElementById("filterToDate")?.value || "";
    let symbol = document.getElementById("filterSymbol")?.value || "ALL";
    const timeframe = document.getElementById("filterTimeframe")?.value || "ALL";

    // Handle Select2 jQuery fallback value
    if (typeof $ !== 'undefined' && $.fn && $.fn.select2) {
        const selVal = $('#filterSymbol').val();
        if (selVal) symbol = selVal;
    }

    const endpoint = `/api/CandleSummary/summary?fromDate=${encodeURIComponent(fromDate)}&toDate=${encodeURIComponent(toDate)}&symbol=${encodeURIComponent(symbol)}&timeframe=${encodeURIComponent(timeframe)}&page=${currentPage}&pageSize=${pageSize}`;
    const url = getEndpointUrl(endpoint);

    try {
        const response = await fetch(url);
        if (!response.ok) {
            if (tbody) {
                tbody.innerHTML = `<tr><td colspan="9" class="text-center py-4 text-danger">⚠️ Failed to load candle summary data. (HTTP ${response.status})</td></tr>`;
            }
            return;
        }

        const data = await response.json();
        renderSummaryData(data);
    } catch (err) {
        console.error("Failed to fetch candle summary data:", err);
        if (tbody) {
            tbody.innerHTML = `<tr><td colspan="9" class="text-center py-4 text-danger">⚠️ Connection error fetching candle summary.</td></tr>`;
        }
    }
}

function renderSummaryData(data) {
    if (!data) return;

    // 1. KPI Cards
    const kpiTotalStocks = document.getElementById("kpiTotalStocks");
    if (kpiTotalStocks) kpiTotalStocks.innerText = (data.totalStocks || 0).toLocaleString();

    const kpiTotalCandles = document.getElementById("kpiTotalCandles");
    if (kpiTotalCandles) kpiTotalCandles.innerText = (data.totalCandlesCount || 0).toLocaleString();

    const kpiDateRange = document.getElementById("kpiDateRange");
    if (kpiDateRange) {
        const fromStr = data.fromDate ? new Date(data.fromDate).toLocaleDateString("en-IN") : "";
        const toStr = data.toDate ? new Date(data.toDate).toLocaleDateString("en-IN") : "";
        kpiDateRange.innerText = fromStr === toStr ? `Today (${fromStr})` : `${fromStr} - ${toStr}`;
    }

    const counterText = document.getElementById("resultsCounterText");
    if (counterText) {
        const total = data.totalStocks || 0;
        const start = total === 0 ? 0 : (data.page - 1) * data.pageSize + 1;
        const end = Math.min(data.page * data.pageSize, total);
        counterText.innerText = `Showing ${start}–${end} of ${total} stocks`;
    }

    // 2. Data Grid Table Body
    const tbody = document.getElementById("tblSummaryBody");
    if (!tbody) return;

    const items = data.items || [];
    if (items.length === 0) {
        tbody.innerHTML = `<tr><td colspan="9" class="text-center py-4 text-muted">No candle data found for the selected filters.</td></tr>`;
        return;
    }

    tbody.innerHTML = items.map(item => {
        const latestTimeStr = item.latestCandleTime 
            ? new Date(item.latestCandleTime).toLocaleString("en-IN", { hour: '2-digit', minute: '2-digit', second: '2-digit', day: '2-digit', month: 'short' })
            : "—";

        return `
            <tr>
                <td style="font-weight:700; color:#f8fafc;">${item.symbol}</td>
                <td style="color:#94a3b8; font-size:0.85rem;">${item.stockName || item.symbol}</td>
                <td><span class="tf-badge tf-badge-1d">${item.candles1d}</span></td>
                <td><span class="tf-badge tf-badge-60m">${item.candles60m}</span></td>
                <td><span class="tf-badge tf-badge-15m">${item.candles15m}</span></td>
                <td><span class="tf-badge tf-badge-5m">${item.candles5m}</span></td>
                <td><span class="tf-badge tf-badge-1m">${item.candles1m}</span></td>
                <td><strong style="color:#4ade80; font-size:0.95rem;">${item.totalCandles.toLocaleString()}</strong></td>
                <td style="font-size:0.85rem; color:#cbd5e1;">${latestTimeStr}</td>
            </tr>
        `;
    }).join("");

    // 3. Render Pagination
    renderPagination(data.page, data.totalPages);
}

function renderPagination(page, totalPages) {
    currentPage = page;

    const btnPrev = document.getElementById("btnPrevPage");
    if (btnPrev) btnPrev.disabled = page <= 1;

    const btnNext = document.getElementById("btnNextPage");
    if (btnNext) btnNext.disabled = page >= totalPages;

    const container = document.getElementById("pageNumbersContainer");
    if (!container) return;

    container.innerHTML = "";

    let pagesToShow = [];
    if (totalPages <= 5) {
        for (let p = 1; p <= totalPages; p++) pagesToShow.push(p);
    } else {
        pagesToShow.push(1);
        if (page > 2 && page < totalPages) {
            pagesToShow.push(page);
        } else if (page <= 2) {
            pagesToShow.push(2);
        } else if (page >= totalPages - 1) {
            pagesToShow.push(totalPages - 1);
        }
        pagesToShow.push(totalPages);
        pagesToShow = [...new Set(pagesToShow)].sort((a, b) => a - b);
    }

    pagesToShow.forEach(p => {
        const btn = document.createElement("button");
        btn.className = `btn btn-sm ${p === page ? 'btn-primary' : 'btn-outline-secondary'}`;
        btn.style.padding = "2px 10px";
        btn.style.fontSize = "0.82rem";
        btn.innerText = p;
        btn.addEventListener("click", function () {
            currentPage = p;
            loadCandleSummary();
        });
        container.appendChild(btn);
    });
}
