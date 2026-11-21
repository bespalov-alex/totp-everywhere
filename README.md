# TOTP Everywhere

A local authenticator for Windows, macOS, Linux, iOS, Android, and the web. It generates six- and eight-digit codes from the device clock using RFC 6238.

## Run from source

Install the .NET 10 SDK. Run these commands from the repository root. The full solution includes iOS and requires macOS, Xcode, and the iOS, Android, and WebAssembly workloads. [Download .NET 10](https://dotnet.microsoft.com/download/dotnet/10.0)

### Desktop

```sh
dotnet run --project src/Totp.Desktop
```

To scan a QR code with a desktop camera, install FFmpeg. On Apple Silicon, `brew install ffmpeg` makes it available to the Finder app. Point the camera at the QR code before choosing **Scan QR with camera**. [Download FFmpeg](https://ffmpeg.org/download.html)

| OS | Camera selection |
| --- | --- |
| Windows | Run `ffmpeg -list_devices true -f dshow -i dummy`. Set the device name in PowerShell, for example `$env:TOTP_CAMERA = 'Integrated Camera'`, then launch the app from that session. |
| macOS | Index 0 is the default. To choose another camera, use `ffmpeg -f avfoundation -list_devices true -i ""` and set the index with `export TOTP_CAMERA=1`. Grant camera access when macOS asks. |
| Linux | The default is `/dev/video0`. Set another device with, for example, `export TOTP_CAMERA=/dev/video2`. |

### Web

```sh
dotnet workload install wasm-tools
dotnet run --project src/Totp.Browser
```

Open the URL shown in the terminal. Use a current browser with WebAssembly, Web Crypto, and Web Locks. Serve the published static files over HTTPS or localhost for storage and camera access.

### iOS

Install Xcode and its compatible .NET iOS workload on a Mac. Launch or select an iOS Simulator before running these commands on Apple Silicon.

```sh
dotnet workload install ios
dotnet build src/Totp.iOS -r iossimulator-arm64
dotnet build src/Totp.iOS -t:Run -r iossimulator-arm64 --no-restore
```

Use `iossimulator-x64` on Intel. QR images can be imported in the simulator. Camera capture requires a physical device and a signed `ios-arm64` build.

### Android

Install the Android SDK and JDK 21, then install the .NET Android workload. Publish an APK for a physical ARM64 device and install it using Android Debug Bridge.

```sh
dotnet workload install android
dotnet publish src/Totp.Android -c Release -r android-arm64 -o publish/android
adb install publish/android/com.bespalov.totpeverywhere-Signed.apk
```

The GitHub Actions APK uses a development signing key. Signing keys can differ between runs, so export your vault before replacing an installation that Android cannot update in place.

## Download builds

Open **Actions → Build downloadable artifacts → Run workflow**, select the platforms, and download files from **Artifacts** in the completed run.

| Platform | Artifact | Use |
| --- | --- | --- |
| Linux x64 | `TotpEverywhere-linux-x64` | Extract the TAR archive and run `./Totp.Desktop`. |
| macOS Apple Silicon | `TotpEverywhere-osx-arm64-app` | Extract the inner ZIP, move `TotpEverywhere.app` to Applications, and double-click it. |
| macOS Apple Silicon | `TotpEverywhere-osx-arm64` | Extract the TAR archive and run `./Totp.Desktop` from Terminal. |
| Windows x64 | `TotpEverywhere-win-x64` | Extract and run the self-contained `Totp.Desktop.exe`. |
| iOS Simulator | `TotpEverywhere-iossimulator-arm64` | Extract the inner ZIP and run `xcrun simctl install booted ./Totp.iOS.app`. |
| iOS device | `TotpEverywhere-ios-arm64` | Sign the extracted `.app` with an Apple certificate and provisioning profile before installing. |
| Android ARM64 | `TotpEverywhere-android-arm64` | Extract and install the APK on an Android 6.0 or later device. |
| Web | `TotpEverywhere-web` | Extract the TAR archive. Serve its contents, including `index.html` and `_framework`, from a static web host. |

The macOS app uses an ad hoc signature. If Gatekeeper blocks a trusted download, try opening it first and then choose **System Settings → Privacy & Security → Open Anyway** when available. Automatic trust for distributed macOS builds requires Developer ID signing and Apple notarization.

## Publish to GitHub Pages on demand

1. In the repository, open **Settings → Pages → Build and deployment** and set **Source** to **GitHub Actions**. GitHub Pages for private repositories requires a supported GitHub plan. Check the site's visibility before enabling it.
2. Once this workflow is on `main`, open **Actions → Deploy Web to GitHub Pages → Run workflow** and select `main`.
3. Open the deployment URL shown in the workflow. For this repository, the default project site is `https://bespalov-alex.github.io/totp-everywhere/`.

The workflow uploads `publish/wwwroot` as a temporary Pages artifact. The repository contains source files and the workflow. The site is deployed without a `gh-pages` branch or a commit of generated files.

Vaults are stored in the browser for the current origin. Use **Transfer vault** and **Import vault** to move an existing vault from localhost or another origin to the Pages site.

## Accounts and storage

1. Register with a username and password of at least 12 characters.
2. Open **Add account** to enter a Base32 secret, upload a QR image, or scan a QR with the camera. For Google Authenticator, choose **Transfer accounts → Export accounts** and import every QR page in the batch. The app also accepts standard `otpauth://totp` links in QR codes and skips HOTP entries.
3. Use **Edit** or **Remove** on an account card to manage it. A blank secret field during editing keeps the current key.
4. Open **Storage settings** from sign-in or from the account list to see the current location. On desktop, **Choose folder** switches to another local directory. Switching signs you out and leaves existing encrypted files in their original directory. **Use default folder** restores the default. To use an existing profile from another location, copy its encrypted `.json` file there while signed out, then select that folder and sign in.
5. Use **Change password** and provide the current password to re-encrypt the vault.

| Platform | Default vault location |
| --- | --- |
| macOS | `~/Library/Application Support/TotpEverywhere` |
| Windows | `%LOCALAPPDATA%\TotpEverywhere` |
| Linux | `$XDG_DATA_HOME/TotpEverywhere`, or `~/.local/share/TotpEverywhere` |
| iOS | The app’s private data directory, shown under **Storage settings** |
| Android | The app’s private data directory, shown under **Storage settings** |
| Web | Browser local storage for the current site and browser profile |

The vault uses AES-256-GCM with a fresh nonce for each write and a key derived from the username and password using PBKDF2-HMAC-SHA256 with 600,000 iterations and a random salt. A hash of the username identifies each profile. Clearing site data removes the web vault. Keep a copy of the encrypted files if you need a desktop backup.

## Transfer an encrypted vault

1. On the source device, sign in and open **Transfer vault**. Export the JSON file or choose **Show transfer QR** when the data fits in one QR code.
2. On the destination device, select **Import vault** on the sign-in screen, or **Transfer vault** if already signed in. Open the JSON file, scan the QR code, or upload its image.
3. Enter the source username and password. An import creates the profile if it is new. If a local profile already exists with the same credentials, the app merges accounts and skips identical entries. It does not remove local entries. If the local password differs, make the passwords match before importing.

The file and QR contain the format version, source platform, a generic description, the username, a CRC-32 checksum, and the encrypted vault. The username and other metadata are visible. Service names, account labels, and TOTP keys remain inside the AES-GCM ciphertext. The checksum detects corruption. Authentication uses the vault encryption key, derived from the username and password. Each export retains the password that was in use when it was created.

## Manual Google Drive backups

On the web, open **Transfer vault → Export to Google Drive** to upload an encrypted vault. To restore it, open **Import vault → Import from Google Drive**, sign in to Google, choose a backup, and enter the vault username and password. The app uses the `drive.file` permission and lists backups it created with the same Google OAuth project. Drive access happens only when you choose an import or export. The Google access token stays in memory for that operation.

To enable Google sign-in, enable the Google Drive API in a Google Cloud project and create an OAuth client of type **Web application**. Add the exact site origin, without the repository path, to **Authorized JavaScript origins**. For GitHub Pages use `https://bespalov-alex.github.io`. Add test accounts to the OAuth consent configuration if the app is in testing mode. Enter the public OAuth client ID in the app, or set the GitHub Actions repository variable `GOOGLE_DRIVE_CLIENT_ID` and redeploy Pages to prefill it. The flow uses no client secret. [Google OAuth setup](https://developers.google.com/identity/oauth2/web/guides/get-google-api-clientid)

## Project layout and tests

- `Totp.Core` implements code generation, QR parsing, and encrypted storage.
- `Totp.App` contains the shared interface and QR image decoding.
- `Totp.Desktop`, `Totp.iOS`, `Totp.Android`, and `Totp.Browser` connect the interface to each platform.
- `Totp.Tests` covers the core behavior with xUnit.

```sh
dotnet test tests/Totp.Tests -c Release
```
