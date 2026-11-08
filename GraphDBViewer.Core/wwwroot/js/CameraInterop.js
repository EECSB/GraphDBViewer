//Ask AI's camera and screenshot (2026-11-02, GDS Viewer's camera.js): a picture from a webcam, or of a screen, a window
//or a browser tab, attached to the next question like a picture pasted into the box. The window shows the camera live in
//a card over the box; the shutter takes the frame on screen as a JPEG and hands it to the paperclip's own input, so it
//goes through every check a chosen file does (floatingDialog.giveFiles, as a paste). A screenshot is the browser's own
//picker of what to share, and one frame of it, as a PNG. Exposed as window.aiCamera; NlQueryModal.razor calls it.
(function () {
    'use strict';

    let stream = null;

    //When a picture was taken, as its name says it: local time, to the second.
    function stamp() {
        const now = new Date();

        function two(n) {
            return String(n).padStart(2, '0');
        }

        return now.getFullYear() + two(now.getMonth() + 1) + two(now.getDate()) + '-' + two(now.getHours()) + two(now.getMinutes()) + two(now.getSeconds());
    }

    //A picture, as a file named for when it was taken, put in the input named as a paste puts one, so it meets every check.
    async function attach(canvas, inputId, prefix, type, quality) {
        const blob = await new Promise(function (resolve) {
            canvas.toBlob(resolve, type, quality);
        });

        if (!blob)
            return 'The picture could not be taken.';

        let extension = 'png';

        if (type === 'image/jpeg')
            extension = 'jpg';

        const transfer = new DataTransfer();

        transfer.items.add(new File([blob], prefix + '-' + stamp() + '.' + extension, { type: type }));

        if (!window.floatingDialog || !window.floatingDialog.giveFiles(inputId, transfer.files))
            return 'There is nowhere to attach the picture yet. Try again in a moment.';

        return '';
    }

    //The frame a video shows now, drawn on a canvas of its own size.
    function frame(video) {
        const canvas = document.createElement('canvas');

        canvas.width = video.videoWidth;
        canvas.height = video.videoHeight;
        canvas.getContext('2d').drawImage(video, 0, 0, canvas.width, canvas.height);

        return canvas;
    }

    //Every track stopped, or the camera's light stays on.
    function stop() {
        if (stream) {
            stream.getTracks().forEach(function (track) {
                track.stop();
            });
        }

        stream = null;
    }

    window.aiCamera = {
        supported: function () {
            return !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia);
        },

        //Shows the camera in the video given. Answers why it could not, or '' once it is showing.
        open: async function (video) {
            if (!video)
                return 'There is nothing to show the camera in.';

            if (!window.aiCamera.supported())
                return 'This browser cannot use a camera.';

            stop();

            try {
                stream = await navigator.mediaDevices.getUserMedia({ video: { width: { ideal: 1280 }, height: { ideal: 720 } }, audio: false });
            }
            catch (e) {
                if (e && e.name === 'NotFoundError')
                    return 'No camera was found.';

                return 'The browser was not allowed to use the camera. Allow it in the address bar, and press the camera again.';
            }

            //Put away while it was being asked for: the card is gone, so the camera goes too.
            if (!video.isConnected) {
                stop();

                return '';
            }

            video.srcObject = stream;

            try {
                await video.play();
            }
            catch (e) { }

            return '';
        },

        //Takes the frame on screen as a JPEG and puts it in the input named, as a pasted picture is put there, named for
        //when it was taken. Answers '' once it is in, or why not.
        capture: async function (video, inputId) {
            if (!video || !stream || video.videoWidth === 0)
                return 'The camera has no picture yet.';

            return await attach(frame(video), inputId, 'camera', 'image/jpeg', 0.9);
        },

        screenshotSupported: function () {
            return !!(navigator.mediaDevices && navigator.mediaDevices.getDisplayMedia);
        },

        //A screenshot: the browser's own picker of a screen, a window or a tab, and one frame of what was picked, as a PNG
        //(sharp text is what a screenshot is for), attached to the next question; the sharing ends as soon as the frame is
        //taken. Answers '' once it is in, 'canceled' when the picker was closed without one, or why not.
        screenshot: async function (inputId) {
            if (!window.aiCamera.screenshotSupported())
                return 'This browser cannot take a screenshot.';

            let shared = null;

            try {
                shared = await navigator.mediaDevices.getDisplayMedia({ video: true, audio: false });
            }
            catch (e) {
                return 'canceled';
            }

            try {
                const video = document.createElement('video');

                video.muted = true;
                video.srcObject = shared;

                await video.play();

                //A frame or two in, so the first is not a blank one.
                await new Promise(function (resolve) {
                    setTimeout(resolve, 150);
                });

                if (video.videoWidth === 0)
                    return 'The screenshot had no picture in it.';

                return await attach(frame(video), inputId, 'screenshot', 'image/png');
            }
            finally {
                shared.getTracks().forEach(function (track) {
                    track.stop();
                });
            }
        },

        close: function () {
            stop();
        }
    };
})();
