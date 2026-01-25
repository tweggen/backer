// Backer site-wide JavaScript functions

/**
 * Trigger a file download with the given filename and content
 * @param {string} filename - The name of the file to download
 * @param {string} content - The content of the file
 */
function downloadFile(filename, content) {
    const blob = new Blob([content], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    
    const link = document.createElement('a');
    link.href = url;
    link.download = filename;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
    
    URL.revokeObjectURL(url);
}

// PWA Install functionality
let deferredPrompt = null;

window.addEventListener('beforeinstallprompt', (e) => {
    // Prevent Chrome's default mini-infobar
    e.preventDefault();
    // Save the event for triggering later
    deferredPrompt = e;
    // Show the install button
    showInstallButton();
});

window.addEventListener('appinstalled', () => {
    // Hide the install button after successful install
    hideInstallButton();
    deferredPrompt = null;
    console.log('Backer PWA installed successfully');
});

function showInstallButton() {
    const btn = document.getElementById('pwaInstallButton');
    if (btn) {
        btn.classList.remove('d-none');
    }
}

function hideInstallButton() {
    const btn = document.getElementById('pwaInstallButton');
    if (btn) {
        btn.classList.add('d-none');
    }
}

async function installPwa() {
    if (!deferredPrompt) {
        console.log('Install prompt not available');
        return false;
    }
    
    // Show the install prompt
    deferredPrompt.prompt();
    
    // Wait for user response
    const { outcome } = await deferredPrompt.userChoice;
    
    if (outcome === 'accepted') {
        console.log('User accepted PWA install');
    } else {
        console.log('User dismissed PWA install');
    }
    
    // Clear the deferred prompt - can only be used once
    deferredPrompt = null;
    hideInstallButton();
    
    return outcome === 'accepted';
}

// Check if app is already installed (running in standalone mode)
function isPwaInstalled() {
    return window.matchMedia('(display-mode: standalone)').matches 
        || window.navigator.standalone === true;
}
