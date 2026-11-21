import { dotnet } from "./_framework/dotnet.js";

const storageKey = id => "totp-everywhere:" + id;
globalThis.totpRead = id => localStorage.getItem(storageKey(id));
globalThis.totpReplace = async (id, expected, value) => navigator.locks.request(storageKey(id), () => {
    const key = storageKey(id);
    if (localStorage.getItem(key) !== expected)
        return false;
    localStorage.setItem(key, value);
    return true;
});

globalThis.totpCamera = async () => {
    const dialog = document.createElement("dialog");
    const video = Object.assign(document.createElement("video"), { muted: true, playsInline: true });
    const capture = Object.assign(document.createElement("button"), { textContent: "Снять QR", disabled: true });
    const cancel = Object.assign(document.createElement("button"), { textContent: "Отмена" });
    dialog.append(video, capture, cancel);
    document.body.append(dialog);
    let stream;
    let closed = false;

    try {
        return await new Promise(resolve => {
            const finish = result => {
                closed = true;
                resolve(result);
            };
            cancel.onclick = () => finish("cancel");
            dialog.oncancel = () => finish("cancel");
            capture.onclick = () => {
                try {
                    const canvas = document.createElement("canvas");
                    canvas.width = video.videoWidth;
                    canvas.height = video.videoHeight;
                    canvas.getContext("2d").drawImage(video, 0, 0);
                    finish(canvas.toDataURL("image/png").split(",")[1] || "error");
                } catch {
                    finish("error");
                }
            };
            dialog.showModal();
            (async () => {
                try {
                    stream = await navigator.mediaDevices.getUserMedia({
                        video: { facingMode: { ideal: "environment" }, width: { ideal: 1280 } },
                        audio: false
                    });
                    if (closed) {
                        stream.getTracks().forEach(track => track.stop());
                        return;
                    }
                    video.srcObject = stream;
                    await video.play();
                    capture.disabled = false;
                } catch {
                    finish("error");
                }
            })();
        });
    } finally {
        stream?.getTracks().forEach(track => track.stop());
        dialog.remove();
    }
};

const runtime = await dotnet.create();
await runtime.runMain(runtime.getConfig().mainAssemblyName, []);
