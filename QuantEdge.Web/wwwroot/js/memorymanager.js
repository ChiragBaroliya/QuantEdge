// QuantEdge Memory Usage Manager JavaScript

const API_BASE_URL = window.QuantEdgeConfig?.apiBaseUrl || "";
let autoRefreshTimer = null;
let isAutoRefreshActive = true;

$(document).ready(function () {
    fetchMemoryStats();

    // Set up auto-refresh every 3 seconds
    autoRefreshTimer = setInterval(fetchMemoryStats, 3000);

    $("#btnManualRefresh").on("click", function () {
        fetchMemoryStats();
    });

    $("#toggleAutoRefresh").on("change", function () {
        isAutoRefreshActive = $(this).is(":checked");
        if (isAutoRefreshActive) {
            if (!autoRefreshTimer) {
                autoRefreshTimer = setInterval(fetchMemoryStats, 3000);
            }
        } else {
            if (autoRefreshTimer) {
                clearInterval(autoRefreshTimer);
                autoRefreshTimer = null;
            }
        }
    });
});

async function fetchMemoryStats() {
    const statusText = $("#lastUpdatedTime");
    const refreshBtn = $("#btnManualRefresh");

    try {
        refreshBtn.addClass("spin");
        const response = await fetch(`${API_BASE_URL}/api/marketdata/memory-stats`);
        if (!response.ok) throw new Error("Failed to fetch memory statistics.");

        const data = await response.json();
        renderMemoryStats(data);

        const now = new Date();
        statusText.text(`Updated ${now.toLocaleTimeString()}`);
    } catch (ex) {
        console.error("Failed to load memory stats:", ex);
        statusText.text("Error updating stats");
    } finally {
        setTimeout(() => refreshBtn.removeClass("spin"), 500);
    }
}

function renderMemoryStats(data) {
    if (!data) return;

    // Process Working Set
    $("#valProcessMemory").text(`${data.processWorkingSetMB || 0} MB`);
    const procPct = Math.min(100, Math.max(5, ((data.processWorkingSetMB || 0) / 1024) * 100));
    $("#barProcessMemory").css("width", `${procPct}%`);

    // GC Heap Memory
    $("#valGcHeapMemory").text(`${data.gcTotalMemoryMB || 0} MB`);
    const gcPct = Math.min(100, Math.max(5, ((data.gcTotalMemoryMB || 0) / 512) * 100));
    $("#barGcHeapMemory").css("width", `${gcPct}%`);

    // Market Cache RAM (Estimated)
    $("#valCacheMemory").text(`${data.estimatedCacheMemoryMB || 0} MB`);

    // Cached Symbols & Total Items
    $("#valCachedSymbols").text(data.totalCachedSymbols || 0);
    const totalItems = (data.totalCachedCandles || 0) + (data.totalCachedIndicators || 0);
    $("#valTotalCachedItems").text(totalItems.toLocaleString());

    // Timeframe Breakdown Table
    const tbody = $("#timeframeTableBody");
    tbody.empty();

    const tfMap = data.timeframeCandleCounts || {};
    const timeframes = ["1m", "5m", "15m", "60m", "1d"];

    timeframes.forEach(tf => {
        const count = tfMap[tf] || 0;
        const estMemory = ((count * 200) / (1024 * 1024)).toFixed(2);
        tbody.append(`
            <tr>
                <td><span class="badge-timeframe">${tf.toUpperCase()}</span></td>
                <td><strong>${count.toLocaleString()}</strong> bars</td>
                <td>~${estMemory} MB</td>
            </tr>
        `);
    });

    // GC Collections Table
    $("#valGen0").text(data.gen0Collections || 0);
    $("#valGen1").text(data.gen1Collections || 0);
    $("#valGen2").text(data.gen2Collections || 0);
    $("#valIndicatorsCount").text((data.totalCachedIndicators || 0).toLocaleString());
}
