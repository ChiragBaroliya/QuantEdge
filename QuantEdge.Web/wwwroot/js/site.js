/* =====================================================
   QuantEdge Web — Main Layout & Global Utilities v2.0
   ===================================================== */
document.addEventListener('DOMContentLoaded', function () {

    // ---- Sidebar toggle (Mobile/Tablet) ----
    const sidebar = document.getElementById('appSidebar');
    const sidebarToggle = document.getElementById('sidebarToggle');
    const sidebarClose = document.getElementById('sidebarClose');
    const sidebarBackdrop = document.getElementById('sidebarBackdrop');

    function openSidebar() {
        if (sidebar) sidebar.classList.add('show');
        if (sidebarBackdrop) sidebarBackdrop.classList.add('show');
        document.body.style.overflow = 'hidden';
    }

    function closeSidebar() {
        if (sidebar) sidebar.classList.remove('show');
        if (sidebarBackdrop) sidebarBackdrop.classList.remove('show');
        document.body.style.overflow = '';
    }

    if (sidebarToggle) sidebarToggle.addEventListener('click', openSidebar);
    if (sidebarClose)  sidebarClose.addEventListener('click', closeSidebar);
    if (sidebarBackdrop) sidebarBackdrop.addEventListener('click', closeSidebar);

    // Close sidebar when clicking nav items on mobile
    document.querySelectorAll('.sidebar-nav .nav-item').forEach(function (item) {
        item.addEventListener('click', function () {
            if (window.innerWidth < 768) closeSidebar();
        });
    });

    // Close on resize to desktop
    window.addEventListener('resize', function () {
        if (window.innerWidth >= 768) closeSidebar();
    });

    // ---- Nav Submenu Group Toggler ----
    document.querySelectorAll('.nav-item-header').forEach(function (header) {
        header.addEventListener('click', function () {
            var group = this.closest('.nav-item-group');
            if (group) {
                group.classList.toggle('open');
            }
        });
    });

    // ---- Live Clock ----
    var timeEl = document.getElementById('currentTime');
    if (timeEl) {
        function updateClock() {
            var now = new Date();
            var h = String(now.getHours()).padStart(2, '0');
            var m = String(now.getMinutes()).padStart(2, '0');
            var s = String(now.getSeconds()).padStart(2, '0');
            timeEl.textContent = h + ':' + m + ':' + s + ' IST';
        }
        updateClock();
        setInterval(updateClock, 1000);
    }

    // ---- Theme Selector ----
    var themes = {
        blue: {
            accent: '#4f9cf9', secondary: '#2563eb',
            glow: 'rgba(79,156,249,0.15)', glowSec: 'rgba(37,99,235,0.08)',
            glowCard: 'rgba(79,156,249,0.08)', glowHover: 'rgba(79,156,249,0.35)',
            glowHoverStrong: 'rgba(79,156,249,0.55)', border: 'rgba(79,156,249,0.25)'
        },
        green: {
            accent: '#34d399', secondary: '#059669',
            glow: 'rgba(52,211,153,0.15)', glowSec: 'rgba(5,150,105,0.08)',
            glowCard: 'rgba(52,211,153,0.08)', glowHover: 'rgba(52,211,153,0.35)',
            glowHoverStrong: 'rgba(52,211,153,0.55)', border: 'rgba(52,211,153,0.25)'
        },
        red: {
            accent: '#f87171', secondary: '#dc2626',
            glow: 'rgba(248,113,113,0.15)', glowSec: 'rgba(220,38,38,0.08)',
            glowCard: 'rgba(248,113,113,0.08)', glowHover: 'rgba(248,113,113,0.35)',
            glowHoverStrong: 'rgba(248,113,113,0.55)', border: 'rgba(248,113,113,0.25)'
        },
        amber: {
            accent: '#fbbf24', secondary: '#d97706',
            glow: 'rgba(251,191,36,0.15)', glowSec: 'rgba(217,119,6,0.08)',
            glowCard: 'rgba(251,191,36,0.08)', glowHover: 'rgba(251,191,36,0.35)',
            glowHoverStrong: 'rgba(251,191,36,0.55)', border: 'rgba(251,191,36,0.25)'
        },
        purple: {
            accent: '#a78bfa', secondary: '#7c3aed',
            glow: 'rgba(167,139,250,0.15)', glowSec: 'rgba(124,58,237,0.10)',
            glowCard: 'rgba(167,139,250,0.08)', glowHover: 'rgba(167,139,250,0.35)',
            glowHoverStrong: 'rgba(167,139,250,0.5)', border: 'rgba(167,139,250,0.25)'
        }
    };

    function applyTheme(name) {
        var t = themes[name] || themes.purple;
        var root = document.documentElement;
        root.style.setProperty('--theme-accent', t.accent);
        root.style.setProperty('--theme-secondary', t.secondary);
        root.style.setProperty('--theme-glow', t.glow);
        root.style.setProperty('--theme-glow-sec', t.glowSec);
        root.style.setProperty('--theme-glow-card', t.glowCard);
        root.style.setProperty('--theme-glow-hover', t.glowHover);
        root.style.setProperty('--theme-glow-hover-strong', t.glowHoverStrong);
        root.style.setProperty('--theme-border', t.border);
        try { localStorage.setItem('qe-theme', name); } catch (e) {}
    }

    // Load saved theme
    var savedTheme = 'purple';
    try { savedTheme = localStorage.getItem('qe-theme') || 'purple'; } catch (e) {}
    applyTheme(savedTheme);

    var themeSelector = document.getElementById('themeSelector');
    if (themeSelector) {
        themeSelector.value = savedTheme;
        themeSelector.addEventListener('change', function () {
            applyTheme(this.value);
        });
    }
});

/* ---- Global Toast Notification System ---- */
window.showToast = function (message, type, duration) {
    type = type || 'info';
    duration = duration || 4000;

    var container = document.getElementById('toastContainer');
    if (!container) {
        container = document.createElement('div');
        container.id = 'toastContainer';
        document.body.appendChild(container);
    }

    var icons = {
        success: '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5"><polyline points="20 6 9 17 4 12"></polyline></svg>',
        error:   '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5"><circle cx="12" cy="12" r="10"></circle><line x1="15" y1="9" x2="9" y2="15"></line><line x1="9" y1="9" x2="15" y2="15"></line></svg>',
        warning: '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5"><path d="M10.29 3.86L1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z"></path><line x1="12" y1="9" x2="12" y2="13"></line><line x1="12" y1="17" x2="12.01" y2="17"></line></svg>',
        info:    '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5"><circle cx="12" cy="12" r="10"></circle><line x1="12" y1="16" x2="12" y2="12"></line><line x1="12" y1="8" x2="12.01" y2="8"></line></svg>'
    };

    var toast = document.createElement('div');
    toast.className = 'toast toast-' + type;
    toast.innerHTML =
        '<div class="toast-icon">' + (icons[type] || icons.info) + '</div>' +
        '<div class="toast-msg">' + message + '</div>' +
        '<button class="toast-close" onclick="this.parentElement.remove()">×</button>';

    container.appendChild(toast);

    setTimeout(function () {
        toast.classList.add('toast-out');
        setTimeout(function () { if (toast.parentElement) toast.remove(); }, 300);
    }, duration);

    return toast;
};

/* ============================================================
   QUANTEDGE GLOBAL GLASSMORPHIC TOOLTIP SYSTEM
   ============================================================ */
(function () {
    let tooltipEl = null;

    function getOrCreateTooltip() {
        if (!tooltipEl) {
            tooltipEl = document.createElement('div');
            tooltipEl.id = 'qe-global-tooltip';
            tooltipEl.className = 'qe-global-tooltip';
            document.body.appendChild(tooltipEl);
        }
        return tooltipEl;
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

    function formatTooltipContent(rawText, el) {
        if (!rawText) return '';

        const text = rawText.trim();
        
        // If rawText already contains HTML markup (e.g. qe-tt-gu-box), render directly
        if (text.startsWith('<') && (text.includes('class="') || text.includes("class='"))) {
            return text;
        }

        // Check if tooltip contains Gujarati characters
        const isGujarati = /[\u0A80-\u0AFF]/.test(text);

        if (isGujarati) {
            // Check for bold title markers like **Title** or Title:
            let title = '';
            let body = text;
            let tip = '';

            // Extract Tip if present (e.g. ⚡ ટિપ: ...)
            const tipMatch = body.match(/(⚡|💡|📌)?\s*(ટિપ|નોંધ|Tip|Note):\s*(.*)/i);
            if (tipMatch) {
                tip = tipMatch[3] || tipMatch[0];
                body = body.replace(tipMatch[0], '').trim();
            }

            // Extract Title if present (e.g. 🔑 **ટોકન મેનેજર**: વિગત...)
            const titleMatch = body.match(/^([^\n:\*]+(?:\*\*)?):?\s*[\n\r]*(.*)/s);
            if (titleMatch && titleMatch[1] && titleMatch[2] && titleMatch[1].length < 60) {
                title = titleMatch[1].replace(/\*\*/g, '').trim();
                body = titleMatch[2].trim();
            } else {
                title = el.getAttribute('data-qe-label') || 'Info';
            }

            // Format body paragraphs/lines
            body = escapeHtml(body).replace(/\n/g, '<br/>');

            return `
                <div class="qe-tt-gu-box">
                    <div class="qe-tt-gu-header">
                        <span class="qe-tt-gu-title">
                            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5"><circle cx="12" cy="12" r="10"></circle><line x1="12" y1="16" x2="12" y2="12"></line><line x1="12" y1="8" x2="12.01" y2="8"></line></svg>
                            ${escapeHtml(title)}
                        </span>
                        <span class="qe-tt-gu-badge">Info</span>
                    </div>
                    <div class="qe-tt-gu-desc">${body}</div>
                    ${tip ? `<div class="qe-tt-gu-tip"><span>⚡</span><span>${escapeHtml(tip)}</span></div>` : ''}
                </div>
            `;
        }

        const decisionText = (el.textContent || '').trim();

        // Check if string contains decision or score patterns
        const scoreMatch = text.match(/^(Downgraded to HOLD\s*\/?[^()]*|STRONG BUY|BUY|SELL|AVOID|HOLD|NEUTRAL)?\s*\*?\*?\(?(Score:\s*\d+\/\d+|\d+\/\d+)\)?\.?\s*(.*)/i);
        const isDecisionBadge = el.classList.contains('rec-badge') || /^(BUY|STRONG BUY|AVOID|SELL|HOLD|NEUTRAL)$/i.test(decisionText) || scoreMatch;

        if (isDecisionBadge && (scoreMatch || text.includes('Score:') || text.includes('Failed factors') || text.includes('Passed'))) {
            let decision = decisionText || (scoreMatch && scoreMatch[1]) || 'INFO';
            if (decision.toLowerCase().includes('hold')) decision = 'HOLD';
            else if (decision.toLowerCase().includes('avoid')) decision = 'AVOID';
            else if (decision.toLowerCase().includes('strong buy')) decision = 'STRONG BUY';
            else if (decision.toLowerCase().includes('buy')) decision = 'BUY';
            else if (decision.toLowerCase().includes('sell')) decision = 'SELL';

            let scoreStr = (scoreMatch && scoreMatch[2]) ? scoreMatch[2] : '';
            if (scoreStr && !scoreStr.toLowerCase().startsWith('score')) {
                scoreStr = 'Score: ' + scoreStr;
            }

            let remainder = text;
            // Remove decision/score prefix from remainder
            remainder = remainder.replace(/^(Downgraded to HOLD\s*\/?[^()]*|STRONG BUY|BUY|SELL|AVOID|HOLD|NEUTRAL)?\s*\(?Score:\s*\d+\/\d+\)?\.?\s*/i, '').trim();

            let decClass = 'neutral';
            const dUpper = decision.toUpperCase();
            if (dUpper.includes('STRONG BUY')) decClass = 'strong-buy';
            else if (dUpper.includes('BUY')) decClass = 'buy';
            else if (dUpper.includes('SELL')) decClass = 'sell';
            else if (dUpper.includes('AVOID')) decClass = 'avoid';
            else if (dUpper.includes('HOLD')) decClass = 'hold';

            let bodyHtml = '';
            
            if (remainder) {
                let sectionTitle = 'Analysis Details';
                let sectionClass = 'info';

                if (/failed factors/i.test(remainder)) {
                    sectionTitle = 'Failed Factors';
                    sectionClass = 'fail';
                    const parts = remainder.split(/failed factors:?/i);
                    remainder = parts[1] || parts[0];
                } else if (/passed/i.test(remainder)) {
                    sectionTitle = 'Passed Conditions';
                    sectionClass = 'pass';
                    const parts = remainder.split(/passed (key criteria|factors|conditions)?:?/i);
                    remainder = parts[parts.length - 1];
                }

                // Split into list items by semicolon, period before capital, or new line
                const items = remainder.split(/;\s*|\.\s+(?=[A-Z])/).map(s => s.trim().replace(/\.$/, '')).filter(Boolean);
                
                if (items.length > 0) {
                    const listItemsHtml = items.map(item => {
                        let formattedItem = escapeHtml(item);
                        const colonIdx = formattedItem.indexOf(':');
                        if (colonIdx > 0 && colonIdx < 40) {
                            const title = formattedItem.substring(0, colonIdx).trim();
                            const desc = formattedItem.substring(colonIdx + 1).trim();
                            formattedItem = `<strong>${title}:</strong> ${desc}`;
                        }

                        const iconChar = sectionClass === 'fail' ? '✕' : (sectionClass === 'pass' ? '✓' : '•');
                        return `
                            <li class="qe-tt-item">
                                <span class="qe-tt-icon ${sectionClass}">${iconChar}</span>
                                <span>${formattedItem}</span>
                            </li>
                        `;
                    }).join('');

                    bodyHtml = `
                        <div class="qe-tt-sec-title ${sectionClass}">${sectionTitle}</div>
                        <ul class="qe-tt-list">
                            ${listItemsHtml}
                        </ul>
                    `;
                } else {
                    bodyHtml = `<div class="qe-tt-plain-text">${escapeHtml(remainder)}</div>`;
                }
            }

            return `
                <div class="qe-tt-card">
                    <div class="qe-tt-header">
                        <span class="qe-tt-badge ${decClass}">${escapeHtml(decision)}</span>
                        ${scoreStr ? `<span class="qe-tt-score">${escapeHtml(scoreStr)}</span>` : ''}
                    </div>
                    ${bodyHtml}
                </div>
            `;
        }

        // Standard descriptive tooltip
        return `
            <div class="qe-tt-plain-text">
                <svg class="qe-tt-plain-icon" width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5"><circle cx="12" cy="12" r="10"></circle><line x1="12" y1="16" x2="12" y2="12"></line><line x1="12" y1="8" x2="12.01" y2="8"></line></svg>
                <span>${escapeHtml(text)}</span>
            </div>
        `;
    }

    function positionTooltip(targetEl, tooltipEl) {
        const targetRect = targetEl.getBoundingClientRect();
        
        // Reset top/left to measure natural size
        tooltipEl.style.top = '0px';
        tooltipEl.style.left = '0px';

        const tooltipWidth = tooltipEl.offsetWidth;
        const tooltipHeight = tooltipEl.offsetHeight;

        const viewportWidth = window.innerWidth;
        const viewportHeight = window.innerHeight;

        // Position above target centered
        let top = targetRect.top - tooltipHeight - 10;
        let left = targetRect.left + (targetRect.width / 2) - (tooltipWidth / 2);

        // If overflowing top of screen, place below target
        if (top < 10) {
            top = targetRect.bottom + 10;
        }

        // Keep inside horizontal viewport boundaries with 12px margin
        if (left < 12) {
            left = 12;
        } else if (left + tooltipWidth > viewportWidth - 12) {
            left = viewportWidth - tooltipWidth - 12;
        }

        tooltipEl.style.top = `${top}px`;
        tooltipEl.style.left = `${left}px`;
    }

    // Intercept mouseover globally
    document.addEventListener('mouseover', function (e) {
        const target = e.target.closest('[data-tooltip], [data-bs-toggle="tooltip"], .rec-badge, [title]');
        if (!target) return;

        let text = target.getAttribute('data-tooltip') || target.getAttribute('data-qe-title');
        
        // Suppress native title tooltip by moving title -> data-qe-title
        if (!text && target.hasAttribute('title')) {
            const rawTitle = target.getAttribute('title');
            if (rawTitle) {
                target.setAttribute('data-qe-title', rawTitle);
                target.removeAttribute('title');
                text = rawTitle;
            }
        }

        if (!text) return;

        const tt = getOrCreateTooltip();
        tt.innerHTML = formatTooltipContent(text, target);
        tt.classList.add('visible');

        positionTooltip(target, tt);
    });

    document.addEventListener('mouseout', function (e) {
        const target = e.target.closest('[data-tooltip], [data-bs-toggle="tooltip"], .rec-badge, [data-qe-title]');
        if (target && tooltipEl) {
            tooltipEl.classList.remove('visible');
        }
    });

    window.addEventListener('scroll', function () {
        if (tooltipEl) tooltipEl.classList.remove('visible');
    }, true);

    window.addEventListener('resize', function () {
        if (tooltipEl) tooltipEl.classList.remove('visible');
    });
})();

