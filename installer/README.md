# Windows installers (WiX / MSI)

Installers are native **x64** and **ARM64** Windows Installer packages (`.msi`) built with the [WiX Toolset](https://wixtoolset.org/) **through NuGet** (`WixToolset.Sdk`). You only need the **.NET 8 SDK**; you do **not** install WiX separately.

The **online** MSI references **`WixToolset.Util.wixext`** (elevated model download). See that package’s [license / terms](https://github.com/wixtoolset/wix/blob/main/OSMFEULA.txt) if you redistribute installers.

| MSI | Contents |
|-----|----------|
| **Online x64** (`PrimeDictate-*-Windows-x64-Online.msi`) | x64 app under `Program Files\PrimeDictate`, **Start Menu** shortcut, all-users launch-at-login Startup shortcut by default, and **Add/Remove Programs** icon. The x64 MSI is blocked on ARM64 Windows so Copilot+ PCs use the native ARM64/QNN build. |
| **Online ARM64** (`PrimeDictate-*-Windows-arm64-Online.msi`) | Native ARM64 app under `Program Files\PrimeDictate` with the same installer behavior and QNN-capable ARM64 runtime payload. |

After files are installed, a **deferred QuietExec** custom action (LocalSystem) runs **`DownloadModel.cmd`** so **`curl`** can write under Program Files. Console output (including **`curl --progress-bar`**) is captured in the MSI log, not in a separate window. **`RunDownloadModelElevated.cmd`** is included if you prefer a visible UAC/console flow.

## Installer UX

- **Online MSI**: Uses WiX UI with **“Launch PrimeDictate when setup completes”** (checked by default). If checked, setup launches `[INSTALLFOLDER]PrimeDictate.exe` from the finish dialog after the deferred model download custom action runs in execute sequence.
- **Launch at login**: Setup installs `PrimeDictate.lnk` in the all-users Windows Startup folder by default so PrimeDictate runs after users sign in. Silent installs can opt out with `LAUNCHATLOGIN=0`.
- **First-run app entry**: Launching at install finish lands users in the app’s first-run setup when `%LocalAppData%\PrimeDictate\settings.json` is not yet completed.
- **Branding continuity**: ARP metadata, MSI names, Start Menu shortcut text, and finish-page launch prompt now align with the app’s branded status language (**Ready=Blue, Recording=Red, Error=Yellow**).
- **Upgrade continuity**: The online MSI keeps the existing product identity (`Name` + `UpgradeCode`) for clean upgrades.
- **Language**: The installer is pinned to `en-US` UI resources for consistent English setup dialogs.

## Prerequisites (maintainer)

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

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
- Source/release assets: `https://github.com/CakeRepository/PrimeDictate/releases`
- Product overview/docs: `https://www.flowdevs.io/portfolio/project/primedictate-local-ai-dictation-app`

### Maintainer flow (recommended)

1. Ensure `Directory.Build.props` has the intended release version (for example `3.2.0`).
2. Push release commit to `main`.
3. Create and push tag `v<version>` (for example `v3.2.0`).
4. The `build.yml` tag run will:
   - build x64 and ARM64 publish payloads + online MSIs,
   - attach both MSIs and checksum files to the matching GitHub Release,
   - generate and validate winget manifests from those MSI artifacts,
   - submit a winget PR when `WINGET_CREATE_GITHUB_TOKEN` is configured.

If `WINGET_CREATE_GITHUB_TOKEN` is missing, the workflow still builds assets and publishes to GitHub Releases, but skips winget submission.

### winget resubmission (no rebuild)

Use this when winget reviewers request metadata changes for an existing version.

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
| `wix/shared/AppPayload.wxs` | `Program Files\PrimeDictate` tree and harvested publish payload |
| `wix/shared/Branding.wxs` | ARP icon + common Add/Remove Programs metadata |
| `wix/shared/StartMenuShortcuts.wxs` | Shared Start Menu shortcut component used by the online installer |
| `wix/shared/LaunchAtLogin.wxs` | Optional all-users Startup-folder shortcut controlled by `LAUNCHATLOGIN` |
| `wix/online/` | Online package, **Util** QuietExec download, helper `.cmd` scripts |
| `wix/assets/PrimeDictate.ico` | App + installer icon (also **`ApplicationIcon`** on `PrimeDictate.exe`) |
| `wix/assets/DownloadModel.cmd` | Curl download used by QuietExec and by the elevated helper |
| `wix/assets/RunDownloadModelElevated.cmd` | Optional manual re-download with visible UAC |

## Version

`Package` / MSI product version uses `Directory.Build.props` (`Version`) with a fourth field `.0` for Windows Installer (for example `1.0.0` → `1.0.0.0`).

## End-user notes

- Install is **per machine** (`Scope="perMachine"`) under **Program Files**.
- Downloaded ONNX models remain subject to their publishers' terms; redistribute only in compliance with those terms.
