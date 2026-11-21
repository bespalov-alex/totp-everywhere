# TOTP Everywhere

A local authenticator for Windows, macOS, Linux, iOS, and the web. It generates six- and eight-digit codes from the device clock using RFC 6238.

## Run from source

Install the .NET 10 SDK. Run these commands from the repository root. The full solution includes iOS and requires macOS, Xcode, and both workloads. [Download .NET 10](https://dotnet.microsoft.com/download/dotnet/10.0)

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
| Web | `TotpEverywhere-web` | Extract the TAR archive. Serve its contents, including `index.html` and `_framework`, from a static web host. |

The macOS app uses an ad hoc signature. If Gatekeeper blocks a trusted download, try opening it first and then choose **System Settings → Privacy & Security → Open Anyway** when available. Automatic trust for distributed macOS builds requires Developer ID signing and Apple notarization.

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
| Web | Browser local storage for the current site and browser profile |

The vault uses AES-256-GCM with a fresh nonce for each write and a key derived from the username and password using PBKDF2-HMAC-SHA256 with 600,000 iterations and a random salt. A hash of the username identifies each profile. Clearing site data removes the web vault. Keep a copy of the encrypted files if you need a desktop backup.

## Project layout and tests

- `Totp.Core` implements code generation, QR parsing, and encrypted storage.
- `Totp.App` contains the shared interface and QR image decoding.
- `Totp.Desktop`, `Totp.iOS`, and `Totp.Browser` connect the interface to each platform.
- `Totp.Tests` covers the core behavior with xUnit.

```sh
dotnet test tests/Totp.Tests -c Release
```
