# DASMO CYBER CAFE TOOLS — Release & Build Flow

> **⚠️ READ THIS BEFORE EVERY RELEASE — NO SHORTCUTS**
> This document is the single source of truth for how to build, package, install, and publish the software.

---

## 📁 Folder Structure (What Each Folder Is For)

```
DASMO CYBER CAFE/
│
├── src/                          ← SOURCE CODE ONLY (never run from here)
│   ├── SmartSaver/               ← Main WPF app project
│   │   └── bin/Release/.../publish/  ← WiX reads from HERE (auto-synced by build script)
│   └── SmartSaver.Installer/     ← WiX MSI project (DO NOT install MSI from here)
│       └── bin/Release/          ← Intermediate WiX build output (ignore this)
│
├── releases/                     ← ✅ SINGLE SOURCE OF TRUTH FOR RELEASES
│   └── DASMO_CYBER_CAFE_TOOLS_Setup_v1.5.1.msi   ← The OFFICIAL MSI to install & upload
│
└── RELEASE.md                    ← THIS FILE (build/release instructions)
```

> **RULE:** Always install from `releases/` folder. Always upload from `releases/` folder.
> Never install from `src/SmartSaver.Installer/bin/Release/` — that is intermediate output, NOT the official release.

---

## 🔢 Version Checklist (do this FIRST before building)

Before building, bump the version in **TWO** files:

### 1. `src/SmartSaver/SmartSaver.csproj`
```xml
<Version>1.5.4.0</Version>
<AssemblyVersion>1.5.4.0</AssemblyVersion>
<FileVersion>1.5.4.0</FileVersion>
```

### 2. `src/SmartSaver.Installer/Package.wxs`
```xml
<Package Name="DASMO CYBER CAFE TOOLS"
         Version="1.5.4.0"   ← change this
         ...>
```

> Both must match — if they differ, the MSI will install wrong version info.

---

## 🏗️ Step-by-Step Build & Release Process

### STEP 1 — Build the App (requires .NET 8 SDK)

Open PowerShell and run:
```powershell
cd "c:\CODING\coading\DASMO CYBER CAFE"

dotnet publish "src\SmartSaver\SmartSaver.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -o "src\SmartSaver\bin\Release\net8.0-windows10.0.17763.0\win-x64\publish"
```

> **Why this exact `-o` path?** The WiX installer (`SmartSaver.Installer.wixproj`) reads from
> `src\SmartSaver\bin\Release\net8.0-windows10.0.17763.0\win-x64\publish`
> If you publish to any other folder, WiX will package OLD files and you'll ship the wrong version!

After publish, verify:
```powershell
[System.Diagnostics.FileVersionInfo]::GetVersionInfo(
    "src\SmartSaver\bin\Release\net8.0-windows10.0.17763.0\win-x64\publish\DASMO CYBER CAFE TOOLS.exe"
).FileVersion
# Must show the new version e.g. 1.5.4.0
```

---

### STEP 2 — Build the MSI Installer

Run from the installer directory:
```powershell
$wix     = "C:\Users\Mypc3\.dotnet\tools\wix.exe"
$nuget   = "$env:USERPROFILE\.nuget\packages"
$uiExt   = "$nuget\wixtoolset.ui.wixext\5.0.2\wixext5\WixToolset.UI.wixext.dll"
$utilExt = "$nuget\wixtoolset.util.wixext\5.0.2\wixext5\WixToolset.Util.wixext.dll"
$pubDir  = "c:\CODING\coading\DASMO CYBER CAFE\src\SmartSaver\bin\Release\net8.0-windows10.0.17763.0\win-x64\publish"

# Run FROM installer dir so License.rtf resolves correctly
cd "c:\CODING\coading\DASMO CYBER CAFE\src\SmartSaver.Installer"

& $wix build `
    Package.wxs Components.wxs Directories.wxs Registry.wxs `
    -ext $uiExt -ext $utilExt `
    -d "PublishDir=$pubDir" `
    -arch x64 `
    -o "c:\CODING\coading\DASMO CYBER CAFE\releases\DASMO_CYBER_CAFE_TOOLS_Setup_v1.5.4.msi"
```

> **Why output directly to `releases/`?** This keeps `releases/` as the single source of truth.
> The MSI filename must include the version number e.g. `_v1.5.4.msi`

After build, verify the MSI:
```powershell
Get-Item "c:\CODING\coading\DASMO CYBER CAFE\releases\DASMO_CYBER_CAFE_TOOLS_Setup_v1.5.4.msi" | Select-Object Name, @{N='Size(MB)';E={[math]::Round($_.Length/1MB,1)}}, LastWriteTime
# Expected: ~77 MB
```

---

### STEP 3 — Install & Test Locally (YOU FIRST)

1. **Uninstall old version** — `Win+R` → `appwiz.cpl` → find **DASMO CYBER CAFE TOOLS** → Uninstall
2. **Install new MSI** — Double-click:
   ```
   releases\DASMO_CYBER_CAFE_TOOLS_Setup_v1.5.4.msi
   ```
3. **Verify version** after install:
   ```powershell
   (Get-Item "C:\Program Files\DASMO CYBER CAFE TOOLS\DASMO CYBER CAFE TOOLS.exe").VersionInfo.FileVersion
   # Must show: 1.5.4.0
   ```
4. **Test the features** that were changed in this version

---

### STEP 4 — Create GitHub Release & Upload

```powershell
cd "c:\CODING\coading\DASMO CYBER CAFE"

# Create the release tag
git tag v1.5.4
git push origin v1.5.4

# Create GitHub release and upload MSI
gh release create v1.5.4 `
    "releases\DASMO_CYBER_CAFE_TOOLS_Setup_v1.5.4.msi" `
    --repo SUBHOJITPAUL797/DASMO-CYBER-CAFE-TOOLS `
    --title "v1.5.4 — [Short Description]" `
    --notes "## What's New in v1.5.4
- Feature 1
- Bug fix 1
- Improvement 1"
```

> To **replace** a bad asset on an existing release:
> ```powershell
> gh release upload v1.5.4 "releases\DASMO_CYBER_CAFE_TOOLS_Setup_v1.5.4.msi" `
>     --repo SUBHOJITPAUL797/DASMO-CYBER-CAFE-TOOLS --clobber
> ```

---

### STEP 5 — Update Firebase Firestore (Force-Update Policy)

Update the version policy so existing users get notified:

```powershell
# Update in Firebase Console OR via the app's Admin panel:
# Document: projects/dasmo-scanner-android/databases/(default)/documents/system_config/licensing
#
# Fields to update:
#   latestVersion:      "1.5.4"
#   minRequiredVersion: "1.5.4"      ← set this ONLY if update is MANDATORY
#   forceUpdate:        true          ← set to true for mandatory updates
#   updateDownloadUrl:  "https://github.com/SUBHOJITPAUL797/DASMO-CYBER-CAFE-TOOLS/releases/download/v1.5.4/DASMO_CYBER_CAFE_TOOLS_Setup_v1.5.4.msi"
```

---

## 🚨 Common Mistakes (How to Avoid Them)

| Mistake | What Goes Wrong | Prevention |
|---------|----------------|------------|
| Publish to `installer\publish\` instead of `src\SmartSaver\bin\...\publish\` | WiX packages OLD code → users install wrong version | Always use the exact `-o` path in Step 1 |
| Install from `src\SmartSaver.Installer\bin\Release\` | That MSI might be stale from a previous build | Always install from `releases\` |
| Forget to bump version in `Package.wxs` | Installer version mismatches app | Check both files before building |
| Upload wrong MSI to GitHub | Users download and still get old version | Always check MSI size (~77 MB) and date before uploading |
| Don't uninstall before installing new MSI | Old files can survive the upgrade silently | Always uninstall first via `appwiz.cpl` |

---

## 📦 Current Releases Archive

| File in `releases/` | Version | Date | Status |
|---------------------|---------|------|--------|
| `DASMO_CYBER_CAFE_TOOLS_Setup_v1.5.4.msi` | 1.5.4.0 | 2026-09-22 | ✅ Current |
| `DASMO_CYBER_CAFE_TOOLS_Setup_v1.5.3.msi` | 1.5.3.0 | 2026-09-22 | 📦 Previous |
| `DASMO_CYBER_CAFE_TOOLS_Setup_v1.5.2.msi` | 1.5.2.0 | 2026-09-22 | 📦 Previous |
| `DASMO_CYBER_CAFE_TOOLS_Setup_v1.5.1.msi` | 1.5.1.0 | 2026-09-22 | 📦 Previous |

> Old MSI files in the root folder (`DASMO_CYBER_CAFE_TOOLS_Setup_v1.4.x.msi` etc.) are kept for reference only. Do not install or share them.

---

## 🔑 Quick Reference

| Thing | Value |
|-------|-------|
| WiX executable | `C:\Users\Mypc3\.dotnet\tools\wix.exe` |
| UI Extension | `%USERPROFILE%\.nuget\packages\wixtoolset.ui.wixext\5.0.2\wixext5\WixToolset.UI.wixext.dll` |
| Util Extension | `%USERPROFILE%\.nuget\packages\wixtoolset.util.wixext\5.0.2\wixext5\WixToolset.Util.wixext.dll` |
| WiX publish source | `src\SmartSaver\bin\Release\net8.0-windows10.0.17763.0\win-x64\publish\` |
| Official releases folder | `releases\` ← always install & upload from here |
| GitHub repo | `SUBHOJITPAUL797/DASMO-CYBER-CAFE-TOOLS` |
| Firebase project | `dasmo-scanner-android` |
| Firestore doc | `system_config/licensing` |
| Install location | `C:\Program Files\DASMO CYBER CAFE TOOLS\` |
