// Live clock
function updateClock() {
    const el = document.getElementById('currentTime');
    if (el) {
        const now = new Date();
        el.textContent = now.toLocaleTimeString('en-IN', {
            hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: true
        });
    }
}

// Theme selector listener
$(document).ready(function() {
    updateClock();
    setInterval(updateClock, 1000);

    const currentTheme = localStorage.getItem("theme-color") || "blue";
    
    // Format Select2 options to show a beautiful colored dot next to each theme
    function formatThemeOption(state) {
        if (!state.id) {
            return state.text;
        }
        const colorMap = {
            blue: '#4f9cf9',
            green: '#34d399',
            red: '#f87171',
            amber: '#fbbf24',
            purple: '#a78bfa'
        };
        const color = colorMap[state.element.value] || '#4f9cf9';
        const $state = $(
            `<span style="display: flex; align-items: center; gap: 8px;"><span style="width: 8px; height: 8px; border-radius: 50%; background-color: ${color}; display: inline-block;"></span>${state.text}</span>`
        );
        return $state;
    }

    const themeSelector = $("#themeSelector");
    if (themeSelector.length) {
        themeSelector.val(currentTheme);
        
        themeSelector.select2({
            minimumResultsForSearch: Infinity,
            templateResult: formatThemeOption,
            templateSelection: formatThemeOption,
            width: '120px'
        });

        themeSelector.on("change", function() {
            localStorage.setItem("theme-color", this.value);
            window.location.reload(); // Refresh the whole page!
        });
    }
});
