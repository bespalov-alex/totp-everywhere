import { dotnet } from "./_framework/dotnet.js";

// Web Locks serialize writes from different tabs of the same origin.
const storageKey = id => "totp-everywhere:" + id;
globalThis.totpOrigin = () => location.origin;
globalThis.totpRead = id => localStorage.getItem(storageKey(id));
globalThis.totpReplace = async (id, expected, value) => navigator.locks.request(storageKey(id), () => {
    const key = storageKey(id);
    if (localStorage.getItem(key) !== expected)
        return false;
    localStorage.setItem(key, value);
    return true;
});

globalThis.totpDeriveKey = async (credential, salt, iterations) => {
    try {
        const key = await crypto.subtle.importKey("raw", credential, "PBKDF2", false, ["deriveBits"]);
        const result = new Uint8Array(await crypto.subtle.deriveBits(
            { name: "PBKDF2", hash: "SHA-256", salt, iterations }, key, 256));
        try {
            return btoa(String.fromCharCode(...result));
        } finally {
            result.fill(0);
        }
    } finally {
        credential.fill(0);
    }
};

// A Blob download also works when the browser has no native file save picker.
globalThis.totpDownload = (name, bytes) => {
    const url = URL.createObjectURL(new Blob([bytes], { type: "application/json" }));
    const link = document.createElement("a");
    link.href = url;
    link.download = name;
    document.body.append(link);
    try {
        link.click();
    } finally {
        link.remove();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    }
};

globalThis.totpCamera = async () => {
    const dialog = document.createElement("dialog");
    const video = Object.assign(document.createElement("video"), { muted: true, playsInline: true });
    const capture = Object.assign(document.createElement("button"), { textContent: "Capture QR", disabled: true });
    const cancel = Object.assign(document.createElement("button"), { textContent: "Cancel" });
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
