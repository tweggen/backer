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
