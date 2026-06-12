// QuantEdge Token Manager page scripts

function handleCreateTokenClick(event, btn) {
    // Show loading state
    btn.classList.add('loading');
    btn.querySelector('.btn-text').textContent = 'Connecting to API...';

    // Allow navigation to proceed (don't preventDefault)
    setTimeout(() => {
        // Reset if user comes back (e.g. cancelled Zerodha login)
        btn.classList.remove('loading');
        btn.querySelector('.btn-text').textContent = 'Create Token';
    }, 8000);
}
