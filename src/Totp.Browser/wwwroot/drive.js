const clientIdKey = "totp-everywhere:google-drive-client-id";
const scope = "https://www.googleapis.com/auth/drive.file";
const driveFiles = "https://www.googleapis.com/drive/v3/files";
const marker = "totp-everywhere-vault-v1";
const maxBytes = 16 * 1024 * 1024;
let identityScript;

function loadIdentity() {
    if (globalThis.google?.accounts?.oauth2)
        return Promise.resolve();
    return identityScript ??= new Promise((resolve, reject) => {
        const script = document.createElement("script");
        script.src = "https://accounts.google.com/gsi/client";
        script.async = true;
        script.onload = resolve;
        script.onerror = () => {
            identityScript = undefined;
            script.remove();
            reject(new Error("Google sign-in could not load."));
        };
        document.head.append(script);
    });
}

function button(label) {
    return Object.assign(document.createElement("button"), { type: "button", textContent: label });
}

function showDialog(title, interact) {
    return new Promise((resolve, reject) => {
        const dialog = document.createElement("dialog");
        const heading = Object.assign(document.createElement("h2"), { textContent: title });
        const content = document.createElement("div");
        const cancel = button("Cancel");
        dialog.append(heading, content, cancel);
        document.body.append(dialog);
        const finish = value => { dialog.remove(); resolve(value); };
        cancel.onclick = () => finish(null);
        dialog.oncancel = event => { event.preventDefault(); finish(null); };
        dialog.showModal();
        interact(content, finish, error => { dialog.remove(); reject(error); });
    });
}

async function authorize() {
    const configuredClient = fetch(new URL("drive-config.json", import.meta.url), { cache: "no-cache" })
        .then(response => response.ok ? response.json() : null)
        .then(config => typeof config?.clientId === "string" ? config.clientId : "")
        .catch(() => "");
    const [, defaultClientId] = await Promise.all([loadIdentity(), configuredClient]);
    return showDialog("Connect to Google Drive", (content, finish, fail) => {
        const label = document.createElement("label");
        label.textContent = "Google OAuth web client ID";
        const input = Object.assign(document.createElement("input"), {
            type: "text", autocomplete: "off", value: localStorage.getItem(clientIdKey) ?? defaultClientId
        });
        label.append(input);
        const help = Object.assign(document.createElement("p"), {
            textContent: "Use a Google Cloud OAuth web client configured for this site’s origin."
        });
        const signIn = button("Sign in with Google");
        const status = document.createElement("p");
        status.setAttribute("role", "status");
        content.append(label, help, signIn, status);
        signIn.onclick = () => {
            const clientId = input.value.trim();
            if (!/^[0-9]+-[a-z0-9]+\.apps\.googleusercontent\.com$/i.test(clientId)) {
                status.textContent = "Enter a valid Google OAuth web client ID.";
                return;
            }
            signIn.disabled = true;
            status.textContent = "Waiting for Google…";
            try {
                localStorage.setItem(clientIdKey, clientId);
                // Request a short-lived token from a user click. Tokens stay in memory for this operation.
                google.accounts.oauth2.initTokenClient({
                    client_id: clientId,
                    scope,
                    include_granted_scopes: false,
                    prompt: "select_account",
                    callback: response => {
                        if (response.error || !response.access_token ||
                            !google.accounts.oauth2.hasGrantedAllScopes(response, scope))
                            fail(new Error("Google Drive access was not granted."));
                        else
                            finish(response.access_token);
                    },
                    error_callback: error => {
                        if (error.type === "popup_closed")
                            finish(null);
                        else
                            fail(new Error("Google sign-in could not open. Allow pop-ups for this site."));
                    }
                }).requestAccessToken();
            } catch {
                fail(new Error("Could not start Google sign-in. Check the OAuth client ID."));
            }
        };
    });
}

async function driveRequest(url, token, options = {}) {
    const response = await fetch(url, {
        ...options,
        headers: { ...options.headers, Authorization: `Bearer ${token}` },
        cache: "no-store"
    });
    if (!response.ok)
        throw new Error(`Google Drive request failed (HTTP ${response.status}).`);
    return response;
}

globalThis.totpDriveExport = async bytes => {
    const token = await authorize();
    if (!token)
        return false;
    const boundary = "totp-" + crypto.randomUUID();
    const created = new Date().toISOString().replaceAll(":", "-");
    const metadata = JSON.stringify({
        name: `TOTP Everywhere vault ${created}.json`,
        mimeType: "application/json",
        appProperties: { totpTransfer: marker }
    });
    const body = new Blob([
        `--${boundary}\r\nContent-Type: application/json; charset=UTF-8\r\n\r\n${metadata}\r\n`,
        `--${boundary}\r\nContent-Type: application/json\r\n\r\n`, bytes,
        `\r\n--${boundary}--\r\n`
    ]);
    await driveRequest("https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart", token, {
        method: "POST", headers: { "Content-Type": `multipart/related; boundary=${boundary}` }, body
    });
    return true;
};

async function listBackups(token) {
    const backups = [];
    let pageToken;
    do {
        const query = new URLSearchParams({
            q: `trashed = false and appProperties has { key='totpTransfer' and value='${marker}' }`,
            fields: "nextPageToken,files(id,name,modifiedTime,size)",
            orderBy: "modifiedTime desc",
            pageSize: "100"
        });
        if (pageToken)
            query.set("pageToken", pageToken);
        const response = await driveRequest(`${driveFiles}?${query}`, token);
        const page = await response.json();
        backups.push(...page.files);
        pageToken = page.nextPageToken;
    } while (pageToken);
    return backups;
}

globalThis.totpDriveImport = async () => {
    const token = await authorize();
    if (!token)
        return null;
    const files = await listBackups(token);
    if (!files.length)
        throw new Error("No TOTP Everywhere backups were found in this Google Drive account.");
    const file = await showDialog("Choose a vault backup", (content, finish) => {
        const list = document.createElement("select");
        list.setAttribute("aria-label", "Vault backup");
        for (const file of files)
            list.add(new Option(file.name, file.id));
        const select = button("Import selected backup");
        select.onclick = () => finish(files.find(file => file.id === list.value));
        content.append(list, select);
    });
    if (!file)
        return null;
    if (Number(file.size) > maxBytes)
        throw new Error("The transfer exceeds the 16 MB limit.");
    const response = await driveRequest(`${driveFiles}/${encodeURIComponent(file.id)}?alt=media`, token);
    const reader = response.body.getReader();
    const decoder = new TextDecoder("utf-8", { fatal: true });
    let bytes = 0;
    let text = "";
    try {
        for (;;) {
            const { value, done } = await reader.read();
            if (done)
                return text + decoder.decode();
            bytes += value.byteLength;
            if (bytes > maxBytes)
                throw new Error("The transfer exceeds the 16 MB limit.");
            text += decoder.decode(value, { stream: true });
        }
    } finally {
        await reader.cancel();
    }
};
