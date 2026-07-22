/**
 * Log Manager & Diagnostic Analyzer
 * QuantEdge Web Frontend Logic - Date Range Fetcher (startDate & endDate DatePickers, category=Web)
 */

document.addEventListener('DOMContentLoaded', () => {
    // DOM Elements
    const logStartDateInput = document.getElementById('logStartDateInput');
    const logEndDateInput = document.getElementById('logEndDateInput');
    const btnFetchDateLogs = document.getElementById('btnFetchDateLogs');
    const logFilesList = document.getElementById('logFilesList');
    const fileSearchInput = document.getElementById('fileSearchInput');
    const btnRefreshFiles = document.getElementById('btnRefreshFiles');
    const dropZone = document.getElementById('dropZone');
    const localFileInput = document.getElementById('localFileInput');

    // Header Elements
    const activeFileName = document.getElementById('activeFileName');
    const activeFileTag = document.getElementById('activeFileTag');
    const activeFileMeta = document.getElementById('activeFileMeta');
    const btnExportJson = document.getElementById('btnExportJson');

    // KPI Elements
    const statTotal = document.getElementById('statTotal');
    const statParsedLines = document.getElementById('statParsedLines');
    const statErrors = document.getElementById('statErrors');
    const statErrorPct = document.getElementById('statErrorPct');
    const statWarnings = document.getElementById('statWarnings');
    const statTimeSpan = document.getElementById('statTimeSpan');

    // Filter Elements
    const searchInput = document.getElementById('searchInput');
    const levelFilter = document.getElementById('levelFilter');
    const filterStart = document.getElementById('filterStart');
    const filterEnd = document.getElementById('filterEnd');

    // Table Elements
    const logRowsContainer = document.getElementById('logRowsContainer');
    const matchingCountBadge = document.getElementById('matchingCountBadge');

    // State Variables
    let availableFiles = [];
    let currentSelectedFileName = null;
    let rawLogLinesCount = 0;
    let parsedLogEntries = [];
    let filteredLogEntries = [];

    // Set Default Today's Date in YYYY-MM-DD format for DatePickers
    const today = new Date();
    const formattedTodayIso = formatYyyyMmDd(today);

    if (logStartDateInput && !logStartDateInput.value) {
        logStartDateInput.value = formattedTodayIso;
    }
    if (logEndDateInput && !logEndDateInput.value) {
        logEndDateInput.value = formattedTodayIso;
    }

    // Initialize Page by Fetching Web Logs for Selected Date Range
    fetchLogsForDateRange(logStartDateInput.value, logEndDateInput.value);

    // Event Listeners
    if (btnFetchDateLogs) {
        btnFetchDateLogs.addEventListener('click', () => {
            const startVal = logStartDateInput ? logStartDateInput.value : formattedTodayIso;
            const endVal = logEndDateInput ? logEndDateInput.value : formattedTodayIso;
            fetchLogsForDateRange(startVal, endVal);
        });
    }

    if (btnRefreshFiles) {
        btnRefreshFiles.addEventListener('click', () => {
            const startVal = logStartDateInput ? logStartDateInput.value : formattedTodayIso;
            const endVal = logEndDateInput ? logEndDateInput.value : formattedTodayIso;
            fetchLogsForDateRange(startVal, endVal);
        });
    }

    if (fileSearchInput) {
        fileSearchInput.addEventListener('input', () => filterAndRenderFileList());
    }

    if (searchInput) searchInput.addEventListener('input', applyFilters);
    if (levelFilter) levelFilter.addEventListener('change', applyFilters);
    if (filterStart) filterStart.addEventListener('change', applyFilters);
    if (filterEnd) filterEnd.addEventListener('change', applyFilters);

    if (btnExportJson) {
        btnExportJson.addEventListener('click', exportFilteredLogsAsJson);
    }

    // Drag & Drop / File Input handling
    if (localFileInput) {
        localFileInput.addEventListener('change', (e) => {
            if (e.target.files && e.target.files.length > 0) {
                handleLocalFile(e.target.files[0]);
            }
        });
    }

    if (dropZone) {
        ['dragenter', 'dragover'].forEach(eventName => {
            dropZone.addEventListener(eventName, (e) => {
                e.preventDefault();
                e.stopPropagation();
                dropZone.style.borderColor = 'var(--theme-accent)';
            }, false);
        });

        ['dragleave', 'drop'].forEach(eventName => {
            dropZone.addEventListener(eventName, (e) => {
                e.preventDefault();
                e.stopPropagation();
                dropZone.style.borderColor = 'var(--theme-border)';
            }, false);
        });

        dropZone.addEventListener('drop', (e) => {
            const dt = e.dataTransfer;
            if (dt.files && dt.files.length > 0) {
                handleLocalFile(dt.files[0]);
            }
        });
    }

    // Fetch Daily Web Logs via API Endpoint /Log/GetLogsByDate?startDate=YYYY-MM-DD&endDate=YYYY-MM-DD&category=Web
    async function fetchLogsForDateRange(startDateStr, endDateStr, targetFileName = null) {
        logFilesList.innerHTML = `
            <div class="file-list-loading">
                <div class="spinner-sm"></div>
                <span>Fetching Web logs...</span>
            </div>
        `;

        try {
            let url = `/Log/GetLogsByDate?startDate=${encodeURIComponent(startDateStr || '')}&endDate=${encodeURIComponent(endDateStr || '')}&category=Web`;
            if (targetFileName) {
                url += `&fileName=${encodeURIComponent(targetFileName)}`;
            }

            const response = await fetch(url);
            const data = await response.json();

            if (data.success) {
                // Filter Web logs
                availableFiles = (data.files || []).filter(f =>
                    (f.category && f.category === 'Web') ||
                    (f.appTag && f.appTag.includes('Web')) ||
                    (f.fileName && f.fileName.toLowerCase().startsWith('web'))
                );

                filterAndRenderFileList();

                if (data.selectedFileName && data.content && isWebFile(data.selectedFileName)) {
                    currentSelectedFileName = data.selectedFileName;
                    updateActiveFileCardHighlight(data.selectedFileName);

                    const fileObj = availableFiles.find(f => f.fileName === data.selectedFileName);
                    activeFileName.innerText = data.selectedFileName;
                    activeFileTag.innerText = fileObj ? fileObj.appTag : 'WEB LOG';
                    activeFileMeta.innerText = fileObj ? `Date: ${data.queryDate} · Size: ${fileObj.size} · Modified: ${fileObj.lastModified}` : `Date: ${data.queryDate}`;

                    parseLogText(data.content);
                } else if (availableFiles.length > 0) {
                    selectLogFile(availableFiles[0].fileName);
                } else {
                    renderEmptyFileList(`No Web log files found for selected date range.`);
                    showErrorState(`No Web log files available for selected date range.`);
                }
            } else {
                renderEmptyFileList(data.message || `No Web logs found.`);
                showErrorState(data.message || `Failed to fetch Web logs.`);
            }
        } catch (err) {
            console.error("Failed to fetch Web logs by date range:", err);
            renderEmptyFileList("Error connecting to server.");
            showErrorState("Server error reading Web log files.");
        }
    }

    function isWebFile(fileName) {
        if (!fileName) return false;
        const lower = fileName.toLowerCase();
        return lower.startsWith('web') || lower.includes('web_log');
    }

    function filterAndRenderFileList() {
        if (!availableFiles || availableFiles.length === 0) {
            renderEmptyFileList();
            return;
        }

        const searchQuery = fileSearchInput ? fileSearchInput.value.toLowerCase().trim() : '';

        const filtered = availableFiles.filter(file => {
            if (searchQuery && !file.fileName.toLowerCase().includes(searchQuery)) {
                return false;
            }
            return true;
        });

        renderFileList(filtered);
    }

    function renderFileList(files) {
        if (!files || files.length === 0) {
            renderEmptyFileList();
            return;
        }

        logFilesList.innerHTML = '';
        files.forEach(file => {
            const fileCard = document.createElement('div');
            fileCard.className = `log-file-item ${file.fileName === currentSelectedFileName ? 'active' : ''}`;
            fileCard.setAttribute('data-filename', file.fileName);

            fileCard.innerHTML = `
                <div class="file-item-header">
                    <span class="file-name" title="${escapeHtml(file.fileName)}">${escapeHtml(file.fileName)}</span>
                    <span class="file-app-tag">${escapeHtml(file.appTag || 'Web Log')}</span>
                </div>
                <div class="file-item-footer">
                    <span>${escapeHtml(file.size)}</span>
                    <span>${escapeHtml(file.lastModified)}</span>
                </div>
            `;

            fileCard.addEventListener('click', () => {
                selectLogFile(file.fileName);
            });

            logFilesList.appendChild(fileCard);
        });
    }

    function updateActiveFileCardHighlight(fileName) {
        document.querySelectorAll('.log-file-item').forEach(item => {
            if (item.getAttribute('data-filename') === fileName) {
                item.classList.add('active');
            } else {
                item.classList.remove('active');
            }
        });
    }

    function renderEmptyFileList(msg = "No Web log files found.") {
        logFilesList.innerHTML = `
            <div style="padding: 1.5rem; text-align: center; color: #9ca3af; font-size: 0.85rem;">
                ${escapeHtml(msg)}<br/>Use local upload below.
            </div>
        `;
    }

    // Select and Read Specific Log File
    async function selectLogFile(fileName) {
        currentSelectedFileName = fileName;
        updateActiveFileCardHighlight(fileName);

        if (filterStart) filterStart.value = '';
        if (filterEnd) filterEnd.value = '';
        if (searchInput) searchInput.value = '';
        if (levelFilter) levelFilter.value = 'ALL';

        const fileObj = availableFiles.find(f => f.fileName === fileName);
        activeFileName.innerText = fileName;
        activeFileTag.innerText = fileObj ? fileObj.appTag : 'WEB LOG';
        activeFileMeta.innerText = fileObj ? `Size: ${fileObj.size} · Modified: ${fileObj.lastModified}` : 'Loading...';

        logRowsContainer.innerHTML = `
            <tr>
                <td colspan="3">
                    <div class="empty-state">
                        <div class="spinner-sm" style="width: 24px; height: 24px;"></div>
                        <h3>Reading Web Log File...</h3>
                        <p>Parsing log streams and traces</p>
                    </div>
                </td>
            </tr>
        `;

        try {
            const response = await fetch(`/Log/GetLogContent?fileName=${encodeURIComponent(fileName)}`);
            const data = await response.json();

            if (data.success && data.content) {
                parseLogText(data.content);
            } else {
                showErrorState(data.message || 'Failed to read log file content.');
            }
        } catch (err) {
            console.error("Error fetching log content:", err);
            showErrorState("Server error reading log file.");
        }
    }

    // Local file drop or browsing
    function handleLocalFile(file) {
        currentSelectedFileName = file.name;
        activeFileName.innerText = file.name;
        activeFileTag.innerText = 'LOCAL';
        activeFileMeta.innerText = `Size: ${formatBytes(file.size)} · Local File`;

        if (filterStart) filterStart.value = '';
        if (filterEnd) filterEnd.value = '';
        if (searchInput) searchInput.value = '';
        if (levelFilter) levelFilter.value = 'ALL';

        const reader = new FileReader();
        reader.onload = (e) => {
            parseLogText(e.target.result);
        };
        reader.readAsText(file);
    }

    // Serilog Log Content Parser
    function parseLogText(text) {
        parsedLogEntries = [];
        const lines = text.split(/\r?\n/);
        rawLogLinesCount = lines.length;

        const headerRegex = /^(\d{4}-\d{2}-\d{2}\s\d{2}:\d{2}:\d{2}(?:\.\d+)?)\s*([+-]\d{2}:\d{2})?\s*\[(\w+)\]\s*(.*)$/;

        let currentEntry = null;

        for (let i = 0; i < lines.length; i++) {
            const line = lines[i];
            const match = line.match(headerRegex);

            if (match) {
                if (currentEntry) {
                    parsedLogEntries.push(currentEntry);
                }

                const timestampStr = match[1];
                const tzStr = match[2] || '';
                const levelStr = match[3].toUpperCase();
                const msgStr = match[4];

                currentEntry = {
                    timestamp: timestampStr,
                    timezone: tzStr,
                    level: normalizeLevel(levelStr),
                    rawLevel: levelStr,
                    message: msgStr,
                    details: '',
                    dateObj: parseLogDate(timestampStr)
                };
            } else {
                if (currentEntry) {
                    currentEntry.details += (currentEntry.details ? '\n' : '') + line;
                }
            }
        }

        if (currentEntry) {
            parsedLogEntries.push(currentEntry);
        }

        if (parsedLogEntries.length === 0) {
            showErrorState('No standard Serilog entries detected in this file.');
            return;
        }

        updateMetrics();
        applyFilters();

        btnExportJson.disabled = false;
    }

    function normalizeLevel(level) {
        if (level === 'INF' || level === 'INFO' || level === 'INFORMATION') return 'INF';
        if (level === 'WRN' || level === 'WARN' || level === 'WARNING') return 'WRN';
        if (level === 'ERR' || level === 'ERROR') return 'ERR';
        if (level === 'FTL' || level === 'FATAL') return 'FTL';
        return level;
    }

    // Metric Calculations
    function updateMetrics() {
        const total = parsedLogEntries.length;
        const errors = parsedLogEntries.filter(e => e.level === 'ERR' || e.level === 'FTL').length;
        const warnings = parsedLogEntries.filter(e => e.level === 'WRN').length;

        statTotal.innerText = total.toLocaleString();
        statParsedLines.innerText = `${rawLogLinesCount.toLocaleString()} raw lines parsed`;
        statErrors.innerText = errors.toLocaleString();

        const errorPct = total > 0 ? Math.round((errors / total) * 100) : 0;
        statErrorPct.innerText = `${errorPct}% of log entries`;
        statWarnings.innerText = warnings.toLocaleString();

        const validDates = parsedLogEntries.filter(e => e.dateObj).map(e => e.dateObj);
        if (validDates.length > 0) {
            const minDate = new Date(Math.min(...validDates));
            const maxDate = new Date(Math.max(...validDates));
            statTimeSpan.innerHTML = `${formatShortTime(minDate)} - ${formatShortTime(maxDate)}`;
        } else {
            statTimeSpan.innerText = 'N/A';
        }
    }

    function formatShortTime(d) {
        if (!d) return '';
        const hh = String(d.getHours()).padStart(2, '0');
        const mm = String(d.getMinutes()).padStart(2, '0');
        const ss = String(d.getSeconds()).padStart(2, '0');
        return `${hh}:${mm}:${ss}`;
    }

    // Apply Client Filters
    function applyFilters() {
        if (parsedLogEntries.length === 0) return;

        const query = searchInput ? searchInput.value.toLowerCase().trim() : '';
        const levelVal = levelFilter ? levelFilter.value : 'ALL';
        const startDate = (filterStart && filterStart.value) ? new Date(filterStart.value) : null;
        const endDate = (filterEnd && filterEnd.value) ? new Date(filterEnd.value) : null;

        filteredLogEntries = parsedLogEntries.filter(entry => {
            if (levelVal === 'ERRORS_ONLY' && entry.level !== 'ERR' && entry.level !== 'FTL') return false;
            if (levelVal === 'WARNINGS' && entry.level !== 'WRN' && entry.level !== 'ERR' && entry.level !== 'FTL') return false;
            if (levelVal !== 'ALL' && levelVal !== 'ERRORS_ONLY' && levelVal !== 'WARNINGS' && entry.level !== levelVal) return false;

            if (entry.dateObj) {
                if (startDate && entry.dateObj < startDate) return false;
                if (endDate && entry.dateObj > endDate) return false;
            }

            if (query) {
                const matchMsg = entry.message && entry.message.toLowerCase().includes(query);
                const matchDetails = entry.details && entry.details.toLowerCase().includes(query);
                const matchTs = entry.timestamp && entry.timestamp.toLowerCase().includes(query);
                if (!matchMsg && !matchDetails && !matchTs) return false;
            }

            return true;
        });

        renderTableRows();
    }

    // Render Log Stream Table
    function renderTableRows() {
        logRowsContainer.innerHTML = '';
        matchingCountBadge.innerText = `${filteredLogEntries.length} matching`;

        if (filteredLogEntries.length === 0) {
            logRowsContainer.innerHTML = `
                <tr class="empty-row">
                    <td colspan="3">
                        <div class="empty-state">
                            <svg width="36" height="36" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5"><circle cx="12" cy="12" r="10"></circle><line x1="8" y1="12" x2="16" y2="12"></line></svg>
                            <h3>No Log Entries Found</h3>
                            <p>No log records match the active search filters.</p>
                        </div>
                    </td>
                </tr>
            `;
            return;
        }

        for (let i = 0; i < filteredLogEntries.length; i++) {
            const entry = filteredLogEntries[i];
            const hasDetails = entry.details.trim().length > 0;

            let badgeClass = 'lvl-inf';
            if (entry.level === 'WRN') badgeClass = 'lvl-wrn';
            else if (entry.level === 'ERR') badgeClass = 'lvl-err';
            else if (entry.level === 'FTL') badgeClass = 'lvl-ftl';

            const mainRow = document.createElement('tr');
            if (hasDetails) {
                mainRow.className = 'log-row-expandable';
                mainRow.setAttribute('data-index', i);
                mainRow.addEventListener('click', () => toggleRowDetail(mainRow, i));
            }

            mainRow.innerHTML = `
                <td class="col-ts">
                    ${hasDetails ? '<span class="chevron-icon">▶</span>' : '<span class="chevron-spacer"></span>'}
                    <span>${escapeHtml(entry.timestamp.trim())}</span>
                </td>
                <td class="col-lvl">
                    <span class="lvl-badge ${badgeClass}">[${entry.level}]</span>
                </td>
                <td class="col-msg">${escapeHtml((entry.message || '').trim())}</td>
            `;

            logRowsContainer.appendChild(mainRow);

            if (hasDetails) {
                const detailRow = document.createElement('tr');
                detailRow.className = 'detail-trace-row';
                detailRow.id = `detail-row-${i}`;

                detailRow.innerHTML = `
                    <td colspan="3" class="detail-cell">
                        <div class="trace-box-title">Exception Detail / Stack Trace</div>
                        <pre class="trace-code-block"><code>${escapeHtml(entry.details)}</code></pre>
                    </td>
                `;

                logRowsContainer.appendChild(detailRow);
            }
        }
    }

    function toggleRowDetail(row, index) {
        const detailRow = document.getElementById(`detail-row-${index}`);
        if (!detailRow) return;

        if (row.classList.contains('expanded')) {
            row.classList.remove('expanded');
            detailRow.classList.remove('show');
        } else {
            row.classList.add('expanded');
            detailRow.classList.add('show');
        }
    }

    function showErrorState(msg) {
        btnExportJson.disabled = true;
        logRowsContainer.innerHTML = `
            <tr class="empty-row">
                <td colspan="3">
                    <div class="empty-state">
                        <svg width="36" height="36" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5"><path d="M10.29 3.86L1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z"></path><line x1="12" y1="9" x2="12" y2="13"></line><line x1="12" y1="17" x2="12.01" y2="17"></line></svg>
                        <h3>${escapeHtml(msg)}</h3>
                    </div>
                </td>
            </tr>
        `;
    }

    function exportFilteredLogsAsJson() {
        if (!filteredLogEntries || filteredLogEntries.length === 0) return;

        const dataStr = "data:text/json;charset=utf-8," + encodeURIComponent(JSON.stringify(filteredLogEntries, null, 2));
        const anchor = document.createElement('a');
        anchor.setAttribute("href", dataStr);
        anchor.setAttribute("download", `QuantEdge_WebLog_${currentSelectedFileName || 'export'}.json`);
        document.body.appendChild(anchor);
        anchor.click();
        anchor.remove();
    }

    // Helper utilities
    function formatYyyyMmDd(d) {
        const yyyy = d.getFullYear();
        const mm = String(d.getMonth() + 1).padStart(2, '0');
        const dd = String(d.getDate()).padStart(2, '0');
        return `${yyyy}-${mm}-${dd}`;
    }

    function parseLogDate(str) {
        try {
            const parts = str.trim().split(/\s+/);
            if (parts.length < 2) return null;

            const dates = parts[0].split('-');
            const times = parts[1].split(':');

            const year = parseInt(dates[0], 10);
            const month = parseInt(dates[1], 10) - 1;
            const day = parseInt(dates[2], 10);

            const hour = parseInt(times[0], 10);
            const minute = parseInt(times[1], 10);

            let second = 0;
            if (times[2]) second = parseInt(times[2].split('.')[0], 10);

            const d = new Date(year, month, day, hour, minute, second);
            return isNaN(d.getTime()) ? null : d;
        } catch {
            return null;
        }
    }

    function formatBytes(bytes) {
        if (bytes < 1024) return `${bytes} B`;
        if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
        return `${(bytes / (1024 * 1024)).toFixed(2)} MB`;
    }

    function escapeHtml(text) {
        if (!text) return '';
        return text
            .replace(/&/g, "&amp;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;")
            .replace(/'/g, "&#039;");
    }
});
