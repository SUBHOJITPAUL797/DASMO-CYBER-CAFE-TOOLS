# Product Requirements Document (PRD)
## SmartSaver — Windows Native File Auto-Compressor & Image Resizer

**Version:** 1.0  
**Author:** DASMO Cybercafe  
**Target Platform:** Windows 10 / Windows 11 (x64)  
**Document Purpose:** Full technical specification for an AI coding assistant to build this application from scratch.

---

## 1. Product Overview

SmartSaver is a Windows-native desktop utility that provides two core features:

1. **Auto-Compress on Save/Download** — Intercepts any file being saved or downloaded on the system and automatically compresses it to a user-defined target file size (in KB or MB) before it lands in its final destination folder.

2. **Right-Click Image Resizer** — Adds a Windows Shell Context Menu option ("Resize to Target Size") on image files so the user can right-click any image and resize it to a user-defined target file size (in KB) or target pixel dimensions (Width × Height), with quality preserved as much as possible.

The application must be lightweight, run as a system tray background service, require no coding knowledge to use, and must not corrupt files or degrade quality beyond what compression requires.

---

## 2. Goals & Non-Goals

### Goals
- Run silently in the Windows system tray after startup.
- Intercept downloaded/saved files in watched folders and compress them automatically.
- Allow right-click compression/resize of image files directly from Windows Explorer.
- Support target size input in KB or MB (e.g., "compress to under 200 KB").
- Support target dimension input for images (e.g., "resize to 1920×1080").
- Use lossless compression where possible; use lossy only when needed to meet the size target.
- Never corrupt the file — if a target size is impossible to reach without corruption, notify the user and keep the original.
- Provide a simple, clean Settings UI for the user to configure all options.
- Installer must be a single `.exe` setup file.

### Non-Goals
- This is NOT a file manager or folder organizer.
- This does NOT encrypt, password-protect, or archive (ZIP/RAR) files.
- This does NOT sync files to cloud storage.
- This does NOT batch-rename files.

---

## 3. Target Users

- BIG business operators (cybercafes, document centers) who handle large volumes of document scans, photos, and downloads daily.
- Users who regularly need to compress PDFs and images to under a specific size (e.g., for government portal uploads that enforce a 200 KB limit).
- Non-technical users — the UI must be simple with no jargon.

---

## 4. Architecture Overview

### 4.1 Technology Stack

| Component | Technology |
|---|---|
| Language | C# (.NET 8, Windows-only) |
| UI Framework | WPF (Windows Presentation Foundation) |
| System Tray | `NotifyIcon` via WPF or WinForms interop |
| File Watching | `System.IO.FileSystemWatcher` |
| Shell Context Menu | Windows Registry entries + COM Shell Extension (C#) |
| Image Processing | `ImageSharp` (SixLabors) or `MagickNET` (ImageMagick wrapper) |
| PDF Compression | `PdfSharpCore` or `iText7 Community` |
| Office/DOCX Compression | `Open XML SDK` (re-compress embedded images inside DOCX/XLSX) |
| Video Compression | FFmpeg (bundled CLI, invoked via `System.Diagnostics.Process`) |
| Settings Storage | JSON config file at `%APPDATA%\SmartSaver\settings.json` |
| Installer | Inno Setup or WiX Toolset |
| Logging | `Serilog` → log file at `%APPDATA%\SmartSaver\logs\` |

### 4.2 Process Architecture

```
[SmartSaver.exe]  (runs on Windows startup, lives in system tray)
    │
    ├── FileWatcherService       → watches configured folders for new/modified files
    ├── CompressionEngine        → handles compression logic per file type
    ├── ShellExtension.dll       → registered COM component for right-click menu
    ├── SettingsManager          → reads/writes settings.json
    ├── NotificationService      → shows Windows toast notifications on completion
    └── SettingsWindow (WPF)     → opened when user clicks tray icon
```

---

## 5. Feature Specifications

---

### Feature 1: System Tray Background Service

#### 5.1.1 Behavior
- On Windows startup (if "Start with Windows" is enabled in settings), `SmartSaver.exe` launches and minimizes to the system tray.
- The tray icon shows a small "S" or compress-arrow logo.
- Right-clicking the tray icon shows a context menu:
  - **Open Settings** — opens the Settings window
  - **Pause / Resume** — temporarily disables auto-compression
  - **View Log** — opens the log file in Notepad
  - **Exit** — closes the application

#### 5.1.2 Settings Window
Opened via tray icon → "Open Settings". Contains three tabs:

**Tab 1: Auto-Compress Settings**  
**Tab 2: Image Resize Settings**  
**Tab 3: General Settings**

---

### Feature 2: Auto-Compress on Download/Save

This feature monitors one or more folders (e.g., the Windows Downloads folder) and automatically compresses any new file that appears, before the user manually opens it.

#### 5.2.1 How It Works (Step by Step)
1. User configures one or more "Watch Folders" in Settings (e.g., `C:\Users\Username\Downloads`).
2. User sets a "Target Max Size" (e.g., 200 KB or 2 MB).
3. When a new file is detected in a watch folder by `FileSystemWatcher`:
   a. Wait 2 seconds after the file stops being written (check that file handle is released).
   b. Check the file's current size.
   c. If the file size is **already under** the target size → do nothing.
   d. If the file size is **over** the target size → run the appropriate compression engine.
   e. On success: replace the original file with the compressed version (same filename, same location). Optionally keep a backup copy in a `_SmartSaver_Originals\` subfolder.
   f. Show a Windows toast notification: *"SmartSaver: invoice.pdf compressed from 1.2 MB → 198 KB"*
   g. On failure (cannot reach target size): show a notification and log the error. Keep the original file untouched.

#### 5.2.2 Supported File Types for Auto-Compress

| File Type | Extension(s) | Compression Method | AND ANY TYPE OF COMPRESSIBLE FILE FORMAT I WANT BRO OK 
|---|---|---|
| PDF | `.pdf` | Re-compress embedded images (reduce DPI to 150); subset fonts; remove metadata |
| JPEG Image | `.jpg`, `.jpeg` | Progressive JPEG re-encode with quality reduction until target size is met |
| PNG Image | `.png` | PNG quantization (pngquant algorithm) + metadata strip |
| Word Document | `.docx` | Re-compress embedded images inside the DOCX ZIP package |
| Excel Document | `.xlsx` | Re-compress embedded images inside the XLSX ZIP package |
| PNG/JPEG inside ZIP | N/A | (Future scope — skip for v1) |

> **Important:** For file types not in the above list (e.g., `.exe`, `.mp4`, `.zip`), SmartSaver must skip the file silently and log it as "unsupported type — skipped."

#### 5.2.3 Compression Strategy — Target Size Approach
The compression engine must use a **binary search / iterative approach** to hit the target size:

```
function CompressToTargetSize(file, targetSizeBytes):
    quality = 85  // start at 85% quality for images
    attempt = 0 
    while attempt < 10:
        compressed = Compress(file, quality)
        if compressed.size <= targetSizeBytes:
            return compressed  // success
        quality -= 8
        attempt++
    // If after 10 attempts still over target:
    if compressed.size is within 10% of target:
        return compressed  // close enough — acceptable
    else:
        return null  // failure — notify user, keep original
```

For PDFs, the iterative approach adjusts embedded image DPI (300 → 200 → 150 → 96) rather than quality.

#### 5.2.4 Auto-Compress Settings UI (Tab 1)
- **Watch Folders list** — Add/Remove folder paths. Default: `%USERPROFILE%\Downloads`
- **Target Max Size** — Number input + dropdown (KB / MB). Default: 200 KB
- **Keep Original Backup** — Toggle (Yes/No). Default: Yes
- **Backup Folder Path** — Text field (auto-filled as `[WatchFolder]\_Originals\`)
- **File types to compress** — Checkboxes: PDF ✓, JPEG ✓, PNG ✓, DOCX ✓, XLSX ✓
- **Enable Auto-Compress** — Master ON/OFF toggle

---

### Feature 3: Right-Click Image Resize (Shell Context Menu)

This adds a "SmartSaver: Resize Image" option when the user right-clicks any image file (`.jpg`, `.jpeg`, `.png`, `.bmp`, `.webp`, `.tiff`) in Windows Explorer.

#### 5.3.1 How It Works
1. User right-clicks an image file in Explorer.
2. Sees menu item: **"SmartSaver → Resize / Compress Image"**
3. A small popup dialog appears with two resize modes:

**Mode A: Resize by File Size Target**
- Input field: "Target file size" (number + KB/MB selector)
- SmartSaver re-encodes the image, reducing quality until the file fits under the target size.
- Output: Replaces the original OR saves as `filename_compressed.jpg` (based on settings).

**Mode B: Resize by Pixel Dimensions**
- Two input fields: Width (px) and Height (px)
- Checkbox: "Maintain Aspect Ratio" (default: checked)
- If aspect ratio is maintained and only one dimension is filled, the other is auto-calculated.
- SmartSaver resizes the image to exact pixel dimensions using high-quality Lanczos resampling.
- Output: Replaces the original OR saves as `filename_resized.jpg`.

#### 5.3.2 Shell Extension Registration
- Register a COM In-Process Shell Extension (`ShellExtension.dll`) implementing `IContextMenu` and `IShellExtInit`.
- Register it under `HKEY_CLASSES_ROOT\SystemFileAssociations\image\shellex\ContextMenuHandlers\SmartSaver`.
- The DLL must be installed to `C:\Program Files\SmartSaver\ShellExtension.dll`.
- Registration and unregistration must be handled automatically by the installer/uninstaller.

#### 5.3.3 Image Quality Rules
- **JPEG**: Use progressive encoding. Minimum quality floor = 40% (below this, visible artifacting is considered corruption).
- **PNG**: Use quantization (reduce color palette depth). Never re-save PNG as JPEG unless user explicitly chooses.
- **Lossless first**: Before trying lossy, attempt lossless compression (strip metadata/EXIF, optimize encoding). Only go lossy if lossless is insufficient.
- **Never upscale**: If user enters pixel dimensions larger than the original image, show warning: *"Target dimensions are larger than original. Upscaling reduces quality. Proceed?"*

#### 5.3.4 Image Resize Settings UI (Tab 2)
- **Default Resize Mode** — Radio: "By File Size" / "By Dimensions" / "Ask every time" (default: Ask every time)
- **Default Target File Size** — Number + KB/MB selector. Default: 200 KB
- **Default Width × Height** — Two number fields. Default: blank
- **Maintain Aspect Ratio by default** — Toggle. Default: ON
- **Output Mode** — Radio: "Replace original" / "Save as new file (add suffix)"
- **Output Suffix** — Text field (used when "Save as new file" is selected). Default: `_resized`
- **Minimum Quality Floor** — Slider 10%–60%. Default: 40% (for JPEG)

---

### Feature 4: Notifications

- Use Windows Toast Notifications (`Microsoft.Toolkit.Uwp.Notifications` or `Windows.UI.Notifications` via WinRT interop).
- Show notification on:
  - ✅ Successful compression/resize (show original size → new size)
  - ⚠️ File skipped (already under target size)
  - ❌ Compression failed (could not reach target size; original kept)
  - ℹ️ File type not supported (silently logged, no notification unless Debug mode is on)

---

### Feature 5: General Settings (Tab 3)

- **Start with Windows** — Toggle. Adds/removes registry key `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\SmartSaver`. Default: ON
- **Show tray icon** — Toggle. Default: ON
- **Enable toast notifications** — Toggle. Default: ON
- **Log level** — Dropdown: Info / Debug / Off. Default: Info
- **Open log folder** — Button
- **Reset all settings to default** — Button (with confirmation dialog)
- **About** — Shows version number, build date, and link to documentation

---

## 6. File Naming & Output Rules

| Scenario | Output Behavior |
|---|---|
| Auto-compress (watch folder) | Overwrite original in-place. Backup to `_Originals\` if enabled. |
| Right-click → "Replace original" | Overwrite original file. No backup (warn user before doing this). |
| Right-click → "Save as new file" | Save `filename_resized.jpg` or `filename_compressed.pdf` in the same folder. |

---

## 7. Error Handling & Safety Rules

1. **Never delete the original file** until the compressed version is verified to be readable and non-zero bytes.
2. **File lock check**: Before processing, verify the file is not locked by another process. Retry up to 5 times with 1-second delay. If still locked, skip and log.
3. **Minimum size floor**: Never compress a file below 5 KB regardless of target (to avoid producing empty/corrupt files).
4. **Impossible target warning**: If the target size is smaller than what any compression can achieve (e.g., user sets 1 KB for a 500 KB PDF), show a clear error: *"This file cannot be compressed below [X] KB without corruption. Compressed to [X] KB instead."*
5. **Recursive watch folder protection**: Do not watch the `_Originals\` backup folder — this would cause an infinite loop.
6. **Temp file approach**: Always compress to a temp file first (`filename.tmp`), verify it, then replace/rename. Never modify the original in-place directly.

---

## 8. Installer Requirements

- Single `SmartSaverSetup.exe` file built with Inno Setup or WiX.
- Installer must:
  1. Install binaries to `C:\Program Files\SmartSaver\`
  2. Register `ShellExtension.dll` as a COM server (`regsvr32` or registry write)
  3. Create Start Menu shortcut
  4. Optionally add to Windows startup (registry)
  5. Install Visual C++ Redistributable if missing (bundled)
  6. Install .NET 8 Desktop Runtime if missing (check and prompt download)
- Uninstaller must:
  1. Unregister `ShellExtension.dll` from COM and Explorer
  2. Remove all registry keys
  3. Remove all installed files
  4. Optionally delete settings/logs (ask user)

---

## 9. Performance Requirements

| Metric | Target |
|---|---|
| Compression of a 2 MB JPEG | < 3 seconds |
| Compression of a 5 MB PDF | < 10 seconds |
| CPU usage while idle (watching folders) | < 1% |
| RAM usage while idle | < 50 MB |
| UI launch time (Settings window) | < 1 second |
| Shell context menu appearance delay | < 200ms |

---

## 10. Non-Functional Requirements

- **No internet connection required** — All processing is local. No telemetry, no analytics, no license server.
- **No admin rights required to run** — But admin rights ARE required during installation (to register COM extension and write to `Program Files`).
- **Portable mode (optional future scope)** — For v1, installation is required.
- **Antivirus compatibility** — Avoid file system hooks that trigger AV false positives. Use `FileSystemWatcher` (not kernel-level hooks).
- **Multi-monitor / DPI-aware** — Settings UI must be DPI-aware (`PerMonitorV2`).

---

## 11. Folder & File Structure (Installed)

```
C:\Program Files\SmartSaver\
├── SmartSaver.exe               ← Main application (tray + settings UI)
├── ShellExtension.dll           ← COM shell context menu handler
├── ImageSharp.dll               ← Image processing library
├── PdfSharpCore.dll             ← PDF compression library
├── ffmpeg.exe                   ← (Optional) for video, future scope
├── Serilog.dll                  ← Logging library
└── Resources\
    └── tray_icon.ico

%APPDATA%\SmartSaver\
├── settings.json                ← User configuration
└── logs\
    └── smartsaver_2025-07-09.log
```

---

## 12. Settings JSON Schema

```json
{
  "autoCompress": {
    "enabled": true,
    "watchFolders": [
      "C:\\Users\\Username\\Downloads"
    ],
    "targetSizeKB": 200,
    "keepOriginalBackup": true,
    "backupFolderName": "_Originals",
    "supportedExtensions": [".pdf", ".jpg", ".jpeg", ".png", ".docx", ".xlsx"]
  },
  "imageResize": {
    "defaultMode": "ask",
    "defaultTargetSizeKB": 200,
    "defaultWidth": 0,
    "defaultHeight": 0,
    "maintainAspectRatio": true,
    "outputMode": "newFile",
    "outputSuffix": "_resized",
    "minimumJpegQuality": 40
  },
  "general": {
    "startWithWindows": true,
    "showTrayIcon": true,
    "enableNotifications": true,
    "logLevel": "Info"
  }
}
```

---

## 13. UI Mockup Descriptions

### Settings Window — Tab 1 (Auto-Compress)
```
┌─────────────────────────────────────────────────────────┐
│  SmartSaver Settings                              [X]   │
├──────────────┬──────────────┬───────────────────────────┤
│ Auto-Compress│ Image Resize │ General                   │
├──────────────┴──────────────┴───────────────────────────┤
│                                                         │
│  ✅ Enable Auto-Compress                                │
│                                                         │
│  Watch Folders:                                         │
│  ┌───────────────────────────────────┐  [+ Add] [- Remove] │
│  │ C:\Users\Username\Downloads       │                   │
│  └───────────────────────────────────┘                   │
│                                                         │
│  Target Max Size:  [200] [KB ▼]                         │
│                                                         │
│  ✅ Keep original backup in "_Originals" subfolder       │
│                                                         │
│  Compress file types:                                   │
│  ✅ PDF   ✅ JPEG   ✅ PNG   ✅ DOCX   ✅ XLSX          │
│                                                         │
│              [Save Settings]  [Cancel]                  │
└─────────────────────────────────────────────────────────┘
```

### Right-Click Dialog (Image Resize)
```
┌──────────────────────────────────────┐
│  SmartSaver — Resize Image      [X] │
├──────────────────────────────────────┤
│  File: photo.jpg (1.8 MB)           │
│                                      │
│  ○ Resize by File Size              │
│      Target Size: [200] [KB ▼]      │
│                                      │
│  ● Resize by Dimensions             │
│      Width:  [1920] px              │
│      Height: [1080] px              │
│      ✅ Maintain Aspect Ratio        │
│                                      │
│  Output: ● New file  ○ Replace      │
│                                      │
│       [Resize Now]  [Cancel]         │
└──────────────────────────────────────┘
```

---

## 14. Out of Scope for Version 1.0

- Video compression (FFmpeg integration is prepared but UI not included in v1)
- Batch processing of existing files (only new/downloaded files are auto-compressed)
- Cloud storage integration
- Network/shared folder watching
- macOS or Linux support
- ZIP/RAR archive compression

---

## 15. Acceptance Criteria

The software is considered complete when all of the following are true:

- [ ] Installer installs cleanly on Windows 10 and Windows 11 with no errors.
- [ ] After installation, right-clicking a `.jpg` file shows the "SmartSaver → Resize / Compress Image" menu item.
- [ ] A 1.5 MB JPEG can be compressed to under 200 KB via right-click without visible corruption at 40%+ quality.
- [ ] A new file saved to the Downloads folder triggers auto-compression within 5 seconds.
- [ ] A PDF file over 200 KB placed in the watch folder is compressed and replaced with a version under 200 KB.
- [ ] If a file cannot reach the target size, the original is kept and a notification is shown.
- [ ] Settings are saved persistently across application restarts.
- [ ] Uninstaller removes all traces including the shell context menu entry.
- [ ] RAM usage while idle is under 50 MB as measured in Task Manager.

---

*End of PRD — SmartSaver v1.0*
