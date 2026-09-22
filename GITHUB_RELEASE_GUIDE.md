# 🚀 DASMO CYBER CAFE TOOLS — GITHUB RELEASE & AUTO-UPDATE GUIDE

> **Authoritative Specification & Operating Guide for Developers and AI Agents**  
> **Last Verified & Published**: September 21, 2026 (Release `v1.5.0`)

---

## 📌 1. Repository Separation Architecture (CRITICAL)

The mobile and desktop ecosystems use **dedicated repositories** to prevent update collisions:

| Ecosystem | Target Platform | Repository Name | Release Asset Format | Auto-Update Target |
|---|---|---|---|---|
| **Android Phone App** (`dasmo-scanner`) | Android Smartphones | [`SUBHOJITPAUL797/DASMO-CYBER-CAFE-TOOL`](https://github.com/SUBHOJITPAUL797/DASMO-CYBER-CAFE-TOOL) *(Singular)* | `.apk` (`app-debug.apk`, `dasmo-scanner-v*.apk`) | Looks for `.apk` on `/releases/latest` |
| **Windows PC Software** (`DASMO CYBER CAFE TOOLS`) | Windows 10/11 Desktop | [`SUBHOJITPAUL797/DASMO-CYBER-CAFE-TOOLS`](https://github.com/SUBHOJITPAUL797/DASMO-CYBER-CAFE-TOOLS) *(Plural with `S`)* | `.msi` (`DASMO_CYBER_CAFE_TOOLS_Setup.msi`) | Looks for `.msi` on `/releases/latest` |

### ⚠️ Why Separate Repositories Are Required (Never Mix Them):
1. Both the **Android App** (`UpdateChecker.kt`) and the **Windows App** (`AppUpdateService.cs`) query GitHub's endpoint:
   `https://api.github.com/repos/{owner}/{repo}/releases/latest`
2. If Android APKs and Windows MSIs were released in the same repository:
   - Whichever release is published last becomes the **`latest`** release for all clients!
   - When Android phones check for updates, seeing a Windows release would cause them to search for an `.apk` inside an MSI release, resulting in broken updates.
   - When Windows PCs check for updates, seeing an Android release would cause them to search for an `.msi` inside an APK release.
3. By keeping them completely separated:
   - Android phones **ONLY** check `DASMO-CYBER-CAFE-TOOL` (currently `v1.2.4` and up) -> **100% Reliable**.
   - Windows PCs **ONLY** check `DASMO-CYBER-CAFE-TOOLS` (currently `v1.5.0` and up) -> **100% Reliable**.
   - Neither platform can ever interfere with or break the other!

---

## 🔗 2. Windows PC Official Links

- **Repository**: [https://github.com/SUBHOJITPAUL797/DASMO-CYBER-CAFE-TOOLS](https://github.com/SUBHOJITPAUL797/DASMO-CYBER-CAFE-TOOLS)
- **Official Releases Page**: [https://github.com/SUBHOJITPAUL797/DASMO-CYBER-CAFE-TOOLS/releases](https://github.com/SUBHOJITPAUL797/DASMO-CYBER-CAFE-TOOLS/releases)
- **Latest Windows Release**: [v1.5.0 Release](https://github.com/SUBHOJITPAUL797/DASMO-CYBER-CAFE-TOOLS/releases/tag/v1.5.0)
- **Developer Portfolio**: [https://subhojit-paul.pages.dev/](https://subhojit-paul.pages.dev/)
- **Developer Contact**: Subhojit Paul (`subhojitpaul26042004@gmail.com` | WhatsApp: `+91 8927408840`)

---

## 🔄 3. How the In-App Auto-Updater System Works

```
+-------------------------------------------------------------------------+
|                      WINDOWS PC APPLICATION STARTUP                     |
+-------------------------------------------------------------------------+
                                    |
                                    v
           +-------------------------------------------------+
           | Layer 1: Firebase Cloud Policy Check            |
           | Document: system_config/licensing               |
           +-------------------------------------------------+
                                    |
            +-----------------------+-----------------------+
            |                                               |
  [Force Update Flag = TRUE]                     [Normal Operation]
  OR [CurrentVersion < MinRequired]                         |
            |                                               v
            v                              +---------------------------------+
+------------------------------------+     | Layer 2: GitHub Releases Check  |
| 🛑 HARDWARE LOCK / BLOCK SCREEN    |     | GET /SUBHOJITPAUL797/           |
| - Custom Admin Announcement Shown  |     |     DASMO-CYBER-CAFE-TOOLS/     |
| - App Core Functions Blocked       |     |     releases/latest             |
| - Force User to Download Update    |     +---------------------------------+
+------------------------------------+                      |
            |                                      +--------+--------+
            |                                      |                 |
            |                                    (Yes)              (No)
            |                                      |                 |
            +------------------------------------->v                 v
                       +---------------------------------------+ [App Continues]
                       | 📥 IN-APP UPDATE MODAL                |
                       | - Changelog displayed from release    |
                       | - Animated Progress Bar & Speed MB/s  |
                       | - 1-Click Silent MSI Installation     |
                       +---------------------------------------+
```

---

## 🛠️ 4. How to Release a PC Update (Step-by-Step)

Whenever you want to release a new version (e.g. `v1.5.1`):

### Step 1: Bump the Version Number in Code
1. `src/SmartSaver/SmartSaver.csproj` (`<Version>1.5.1</Version>`)
2. `src/SmartSaver.Installer/Package.wxs` (`Version="1.5.1"`)
3. `src/SmartSaver/Services/FirebaseCloudAuthService.cs` (`CurrentAppVersion = "1.5.1"`)
4. `src/SmartSaver/Services/AppUpdateService.cs` (`CurrentVersion = "1.5.1"`)

### Step 2: Build and Package the Self-Contained MSI
Run these PowerShell commands in the root directory:

```powershell
# 1. Publish self-contained win-x64 binary
& "$HOME\.dotnet\dotnet.exe" publish src/SmartSaver/SmartSaver.csproj -c Release -r win-x64 --self-contained true

# 2. Compile the WiX MSI Installer
& "$HOME\.dotnet\dotnet.exe" build src/SmartSaver.Installer/SmartSaver.Installer.wixproj -c Release

# 3. Copy the fresh MSI to the root folder
Copy-Item -Path "src\SmartSaver.Installer\bin\Release\DASMO_CYBER_CAFE_TOOLS_Setup.msi" -Destination "DASMO_CYBER_CAFE_TOOLS_Setup.msi" -Force
```

### Step 3: Publish to GitHub Releases

#### Option A: 1-Click Command via GitHub CLI (`gh`) [Recommended]
```powershell
gh release create v1.5.1 "DASMO_CYBER_CAFE_TOOLS_Setup.msi" `
  -R SUBHOJITPAUL797/DASMO-CYBER-CAFE-TOOLS `
  --title "v1.5.1 - [Summary of Changes]" `
  --notes "### What's New in v1.5.1`n- Feature 1...`n- Bug fix 2..."
```

#### Option B: Via GitHub Web Interface
1. Go to: **[https://github.com/SUBHOJITPAUL797/DASMO-CYBER-CAFE-TOOLS/releases](https://github.com/SUBHOJITPAUL797/DASMO-CYBER-CAFE-TOOLS/releases)**
2. Click **"Draft a new release"**.
3. Choose a tag: Type `v1.5.1` and select **"Create new tag on publish"**.
4. Set release title and description notes.
5. Drag and drop `DASMO_CYBER_CAFE_TOOLS_Setup.msi` into the attachments box.
6. Click **"Publish release"**.

---

## 👑 5. Controlling Updates from the Admin Panel

As the Super Admin (`subhojitpaul26042004@gmail.com`):
1. Open the PC application and go to **Admin Panel** -> **App Version & Remote Update Policy**.
2. Set:
   - **Latest Version**: (e.g. `1.5.1`)
   - **Minimum Required Version**: (e.g. `1.5.0`)
   - **Force Update Mandatory**: Checked (if you want to strictly prevent older versions from running)
   - **Custom Message**: Custom broadcast text shown on locked screens.
   - **GitHub Repo**: Pre-filled with `SUBHOJITPAUL797/DASMO-CYBER-CAFE-TOOLS`.
3. Click **"Save Policy to Cloud"**. All active client apps will immediately respond.

---

## 🔒 6. Source Files Reference

All future developers and AI agents must preserve this configuration:

1. **`src/SmartSaver/Services/AppUpdateService.cs`**:
   ```csharp
   public const string DefaultGithubRepo = "SUBHOJITPAUL797/DASMO-CYBER-CAFE-TOOLS";
   ```
2. **`src/SmartSaver/Services/FirebaseCloudAuthService.cs`**:
   ```csharp
   public class AppVersionPolicy
   {
       public string GithubRepo { get; set; } = "SUBHOJITPAUL797/DASMO-CYBER-CAFE-TOOLS";
   }
   ```
3. **`src/SmartSaver/ViewModels/MainViewModel.cs`**:
   ```csharp
   OpenGitHubReleasesCommand = new RelayCommand(_ => 
       OpenUrl("https://github.com/SUBHOJITPAUL797/DASMO-CYBER-CAFE-TOOLS/releases"));
   ```
