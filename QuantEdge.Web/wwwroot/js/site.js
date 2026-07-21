/* =====================================================
   QuantEdge Web — Main Layout & Responsive Interactions
   ===================================================== */
document.addEventListener('DOMContentLoaded', function () {
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

    if (sidebarToggle) {
        sidebarToggle.addEventListener('click', openSidebar);
    }

    if (sidebarClose) {
        sidebarClose.addEventListener('click', closeSidebar);
    }

    if (sidebarBackdrop) {
        sidebarBackdrop.addEventListener('click', closeSidebar);
    }

    // Close sidebar when clicking any nav item link on mobile/tablet
    const navItems = document.querySelectorAll('.sidebar-nav .nav-item');
    navItems.forEach(function (item) {
        item.addEventListener('click', function () {
            if (window.innerWidth < 1200) {
                closeSidebar();
            }
        });
    });

    // Close sidebar on window resize if viewport expands to desktop
    window.addEventListener('resize', function () {
        if (window.innerWidth >= 1200) {
            closeSidebar();
        }
    });
});
