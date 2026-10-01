# Windows installers (WiX / MSI)

Installers are native **x64** and **ARM64** Windows Installer packages (`.msi`) built with the [WiX Toolset](https://wixtoolset.org/) **through NuGet** (`WixToolset.Sdk`). You only need the **.NET 10 SDK** (pinned in `global.json`); you do **not** install WiX separately.

From 6.1.0 the installers package the Avalonia app in `src/PrimeDictate.Desktop` (self-contained, output `PrimeDictate.exe`: dictation, tray, transcription workspace and meetings). The WPF app at the repository root (`PrimeDictate.csproj`, what 6.0.0 shipped) is legacy: it still builds but is not packaged. The MSI keeps the same `UpgradeCode`, so 6.1.0 upgrades 6.0.0 in place.

The **online** MSI installs the PrimeDictate app payload only. Model acquisition happens inside PrimeDictate's first-run setup and Settings window.

| MSI | Contents |
|-----|----------|
| **Online x64** (`PrimeDictate-*-Windows-x64-Online.msi`) | x64 app under `Program Files\PrimeDictate`, **Start Menu** shortcut, all-users launch-at-login Startup shortcut by default, and **Add/Remove Programs** icon. The x64 MSI is blocked on ARM64 Windows so ARM64 PCs use the native ARM64 build. |
| **Online ARM64** (`PrimeDictate-*-Windows-arm64-Online.msi`) | Native ARM64 app under `Program Files\PrimeDictate` with the same installer behavior. It also carries the ONNX Runtime QNN natives (`QnnHtp.dll`, `onnxruntime_providers_qnn.dll` and friends), so Snapdragon PCs can run the Qualcomm AI Hub Whisper and Moonshine NPU models. |

After install, users open PrimeDictate and choose a model in first-run setup or Settings. The app can download supported models itself or browse to an existing local model folder.

## Installer UX

- **Online MSI**: Uses WiX UI to install the app payload only. The MSI does not run external download commands or launch PrimeDictate from the finish dialog.
- **Launch at login**: Setup installs `PrimeDictate.lnk` in the all-users Windows Startup folder by default; it runs `PrimeDictate.exe --background` (tray only) after users sign in. Silent installs can opt out with `LAUNCHATLOGIN=0`. The app's "Start PrimeDictate when I sign in" checkbox controls that same shortcut per user (Explorer's StartupApproved switch, no administrator rights); when the shortcut is absent it uses a per-user Run value named `PrimeDictate` instead, never both. On first start the app removes the 6.0.0 per-user startup shortcut and stale Run values (and keeps launch at login on for a user who had it).
- **First-run app entry**: Open PrimeDictate from the Start Menu or Startup shortcut after install to complete first-run setup, including model selection or download.
- **Branding continuity**: ARP metadata, MSI names, and Start Menu shortcut text align with the app’s branded status language (**Ready=Blue, Recording=Red, Error=Yellow**).
- **Upgrade continuity**: The online MSI keeps the existing product identity (`Name` + `UpgradeCode`) for clean upgrades. The 6.0.0 shortcut (argument `--from-login`) is replaced in place by the same component with `--background`, and all WPF files are removed with the old product.
- **Updates**: the app checks GitHub Releases (tray menu "Check for updates...", and automatically at most once a day), verifies the `PrimeDictate-Setup-vX.Y.Z-<arch>.msi` download against its `.sha256`, asks before installing, quits through its clean exit path (a meeting recording is saved), then runs the MSI with `LAUNCHATLOGIN=1` only if the installer shortcut exists.
- **Language**: The installer is pinned to `en-US` UI resources for consistent English setup dialogs.

## Prerequisites (maintainer)

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (version pinned in `global.json`)

## Build

From the repository root:

```powershell
.\scripts\Build-Installers.ps1
```

Outputs are copied to `artifacts\installer\`. Intermediate build outputs live under `installer\wix\online\bin\`.

## winget publishing

PrimeDictate publishes to winget from the same tagged release pipeline as the MSI (`vX.Y.Z` tags only).

- Package id: `FlowDevs.PrimeDictate`
- Community manifests: `https://github.com/microsoft/winget-pkgs`
- Source/release assets: `https://github.com/flowdevs-io/PrimeDictate/releases`
- Product overview/docs: `https://www.flowdevs.io/portfolio/project/primedictate-local-ai-dictation-app`

### Maintainer flow (recommended)

1. Bump `Directory.Build.props` to the release version (`Version`, `AssemblyVersion`, `FileVersion`; for example `6.0.0`) and add the version to `RELEASE_NOTES.md`.
2. Push the "Release <version>" commit to `main`. Merging into `main` publishes nothing by itself.
3. Create and push tag `v<version>` (for example `v6.0.0`).
4. The `build.yml` tag run will:
   - build x64 and ARM64 publish payloads + online MSIs, signed when the Azure Key Vault secrets are set,
   - attach both MSIs and checksum files to the matching GitHub Release,
   - pack and push the Chocolatey package (it then waits in Chocolatey moderation),
   - generate and validate winget manifests from those MSI artifacts,
   - submit a winget PR when `WINGET_CREATE_GITHUB_TOKEN` is configured (it then goes through winget validation and moderation).

If `WINGET_CREATE_GITHUB_TOKEN` is missing, the workflow still builds assets and publishes to GitHub Releases, but skips winget submission.

The winget and Chocolatey steps never fail the release, so a green run can still hide a warning: check both steps' logs after every release.

### When the winget step warns

`The forked repository could not be synced with the upstream commits` means the `winget-pkgs` fork that the token submits from (`CakeRepository/winget-pkgs`) has fallen behind and the token cannot sync it, typically because upstream changed workflow files, which needs the `workflow` scope. Sync the fork with an account that has that scope, then resubmit without rebuilding (below):

```powershell
gh repo sync CakeRepository/winget-pkgs --source microsoft/winget-pkgs --branch master
```

This happened for 6.0.0: the fork was 27,575 commits behind.

### winget resubmission (no rebuild)

Use this when winget reviewers request metadata changes for an existing version, or when the tag run's winget step warned.

1. Run `build.yml` with `workflow_dispatch`.
2. Set `submit_winget_only=true`.
3. Set `target_version=<version>` (for example `3.2.0`).
4. The workflow downloads the two release MSIs for `v<version>`, regenerates manifests, validates them with `winget validate`, and submits a fresh winget PR.

## Silent install and upgrade

- Install x64: `msiexec /i PrimeDictate-<version>-Windows-x64-Online.msi /qn /norestart`
- Install ARM64: `msiexec /i PrimeDictate-<version>-Windows-arm64-Online.msi /qn /norestart`
- Install without launch at login: `msiexec /i PrimeDictate-<version>-Windows-<arch>-Online.msi LAUNCHATLOGIN=0 /qn /norestart`
- Upgrade: `msiexec /i PrimeDictate-<version>-Windows-<arch>-Online.msi REINSTALL=ALL REINSTALLMODE=vomus /qn /norestart`
- Uninstall: `msiexec /x PrimeDictate-<version>-Windows-<arch>-Online.msi /qn /norestart`
- winget install: `winget install --id FlowDevs.PrimeDictate --exact --silent --accept-package-agreements --accept-source-agreements`
- winget install without launch at login: `winget install --id FlowDevs.PrimeDictate --exact --silent --accept-package-agreements --accept-source-agreements --override "LAUNCHATLOGIN=0"`
- winget upgrade: `winget upgrade --id FlowDevs.PrimeDictate --exact --silent --accept-package-agreements --accept-source-agreements`
- winget uninstall: `winget uninstall --id FlowDevs.PrimeDictate --exact --silent`

## Layout

| Path | Role |
|------|------|
| `wix/shared/AppPayload.wxs` | `Program Files\PrimeDictate` tree and harvested publish payload (`src/PrimeDictate.Desktop` publish output) |
| `wix/shared/Branding.wxs` | ARP icon + common Add/Remove Programs metadata |
| `wix/shared/StartMenuShortcuts.wxs` | Shared Start Menu shortcut component used by the online installer |
| `wix/shared/LaunchAtLogin.wxs` | Optional all-users Startup-folder shortcut controlled by `LAUNCHATLOGIN` |
| `wix/online/` | Online package for the app payload only; no install-time model download or finish-page app launch |
| `wix/assets/PrimeDictate.ico` | App + installer icon (also **`ApplicationIcon`** on `PrimeDictate.exe`) |
| `wix/assets/DownloadModel.cmd` | Legacy helper retained for maintainer reference; not invoked by the online MSI |
| `wix/assets/RunDownloadModelElevated.cmd` | Legacy helper retained for maintainer reference; not invoked by the online MSI |

## Version

`Package` / MSI product version uses `Directory.Build.props` (`Version`) with a fourth field `.0` for Windows Installer (for example `1.0.0` → `1.0.0.0`).

## End-user notes

- Install is **per machine** (`Scope="perMachine"`) under **Program Files**.
- Downloaded ONNX models remain subject to their publishers' terms; redistribute only in compliance with those terms.
