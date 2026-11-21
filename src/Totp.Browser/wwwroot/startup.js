const root = new URL("./", import.meta.url);
const loading = document.getElementById("loading");
const progress = document.getElementById("boot-progress");
const status = document.getElementById("boot-status");
const sizes = new Map();
const received = new Map();
const mib = bytes => (bytes / 1048576).toFixed(1);
let total = 0;
let frame;

function displayBytes() {
    frame = undefined;
    const bytes = [...received.values()].reduce((sum, size) => sum + size, 0);
    progress.max = total;
    progress.value = Math.min(bytes, total);
    status.textContent = `Downloading app resources · ${mib(bytes)} / ${mib(total)} MiB decoded`;
}

async function loadResource(url, integrity) {
    const address = new URL(url, root);
    const path = address.pathname.slice(root.pathname.length);
    if (address.origin !== root.origin || !address.pathname.startsWith(root.pathname) || !sizes.has(path))
        throw new Error("Unexpected WebAssembly resource URL.");

    const expected = sizes.get(path);
    const response = await fetch(address, { cache: "no-cache" });
    if (!response.ok || !response.body)
        throw new Error(`Could not download ${path} (HTTP ${response.status}).`);

    // Fetch integrity buffers the whole resource before exposing its stream. Verify the same hash
    // after collecting its chunks so the progress bar can advance while a large file downloads.
    const reader = response.body.getReader();
    const bytes = new Uint8Array(expected);
    let offset = 0;
    try {
        for (;;) {
            const { value, done } = await reader.read();
            if (done)
                break;
            bytes.set(value, offset);
            offset += value.byteLength;
            received.set(path, offset);
            frame ??= requestAnimationFrame(displayBytes);
        }
    } finally {
        await reader.cancel();
    }
    if (offset !== expected)
        throw new Error(`Unexpected size for ${path}.`);
    const match = /^(sha(?:256|384|512))-(\S+)$/.exec(integrity);
    if (!match)
        throw new Error(`Missing integrity information for ${path}.`);
    const digest = new Uint8Array(await crypto.subtle.digest(match[1].replace("sha", "SHA-"), bytes));
    if (btoa(String.fromCharCode(...digest)) !== match[2])
        throw new Error(`Integrity check failed for ${path}.`);
    return new Response(bytes, { headers: { "Content-Type": response.headers.get("Content-Type") || "application/wasm" } });
}

export async function startApp(dotnet) {
    try {
        // Builds served directly by dotnet run have no size manifest. Keep the loader indeterminate there.
        const manifest = await fetch(new URL("boot-sizes.json", root), { cache: "no-cache" });
        if (manifest.ok && manifest.headers.get("Content-Type")?.includes("json")) {
            for (const [path, size] of Object.entries(await manifest.json())) {
                if (Number.isSafeInteger(size) && size > 0)
                    sizes.set(path, size);
            }
            total = [...sizes.values()].reduce((sum, size) => sum + size, 0);
            if (total)
                displayBytes();
        }

        const runtime = await dotnet.withResourceLoader((type, _name, url, integrity) => {
            const address = new URL(url, root);
            const path = address.pathname.slice(root.pathname.length);
            if (type !== "dotnetjs" && address.origin === root.origin && sizes.has(path))
                return loadResource(url, integrity);
        }).create();
        cancelAnimationFrame(frame);
        progress.removeAttribute("value");
        status.textContent = "Starting TOTP Everywhere…";
        await runtime.runMain(runtime.getConfig().mainAssemblyName, []);
        loading.remove();
    } catch (error) {
        cancelAnimationFrame(frame);
        console.error("TOTP Everywhere could not start.", error);
        progress.hidden = true;
        status.textContent = "The app could not load. Check your connection and try again.";
        document.getElementById("boot-retry").hidden = false;
    }
}
