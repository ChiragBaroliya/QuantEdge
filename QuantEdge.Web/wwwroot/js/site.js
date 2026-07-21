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
