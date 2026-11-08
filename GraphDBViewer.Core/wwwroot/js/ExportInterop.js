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
    },

    //Triggers a browser download of binary content handed over as base64 (a .glb), via a Blob rather than a data URL,
    //which a large model would make enormous.
    downloadBase64: function (filename, base64, mime) {
        const binary = atob(base64);
        const bytes = new Uint8Array(binary.length);

        for (let i = 0; i < binary.length; i++)
            bytes[i] = binary.charCodeAt(i);

        const blob = new Blob([bytes], { type: mime || 'application/octet-stream' });
        const url = URL.createObjectURL(blob);
        this.download(filename, url);
        URL.revokeObjectURL(url);
    }
};
