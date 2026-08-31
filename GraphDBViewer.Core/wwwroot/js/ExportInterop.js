window.exportInterop = {

    //Triggers a browser download of a data URL (e.g. a PNG produced by cy.png()).
    download: function (filename, dataUrl) {
        const a = document.createElement('a');
        a.href = dataUrl;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
    },

    //Triggers a browser download of text content (e.g. CSV) via a Blob.
    downloadText: function (filename, text, mime) {
        const blob = new Blob([text], { type: mime || 'text/plain' });
        const url = URL.createObjectURL(blob);
        this.download(filename, url);
        URL.revokeObjectURL(url);
    }
};
