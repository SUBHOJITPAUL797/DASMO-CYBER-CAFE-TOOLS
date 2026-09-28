# DASMO CYBER CAFE TOOLS — Release Documentation

## Version 1.5.29: Universal Windows-Wide DEVMODE Color Detection & Interactive Toast HUD

---

### Executive Overview
Version **1.5.29** addresses a critical print spooler tracking bug where documents printed in **Color / High Quality** from applications such as **Adobe Acrobat**, **Google Chrome**, **Microsoft Edge**, **Microsoft Word**, **Windows Photo Viewer**, or **DASMO Passport Photo Studio** were falsely detected as `⚫ B&W` (Monochrome) instead of `🌈 Color`.

This release introduces an authoritative, multi-tier **Win32 DEVMODE evaluation engine**, provides an interactive **1-click override directly on the desktop HUD notification toast**, adds a dedicated safety net for photo sheets, and prepares the application for automated in-app updates.

---

### Root Cause Analysis

#### The Technical Issue in Earlier Versions (≤ v1.5.27)
In `PrintTrackerService.cs`, spooler DEVMODE parsing evaluated the print job's color mode using:
```csharp
if (devMode.dmColor == 1)
{
    isColor = false;
}
else if (devMode.dmICMIntent >= 1 && devMode.dmICMIntent <= 4)
{
    isColor = true;
}
else
{
    isColor = false; // <-- ROOT CAUSE BUG
}
```

#### Why Color Prints Were Detected as B&W:
1. **ICM Intent is Unset in Standard Windows Printing**:
   Standard Windows GDI / EMF printing applications (including Adobe Acrobat, Microsoft Office, web browsers, and image viewers) do **not** engage Image Color Management intent flags in the print job DEVMODE (`dmICMIntent` remains `0`).
2. **DEVMODE Color Flag Was Ignored**:
   When users configure "Color" or "High Quality", Windows and printer drivers (such as the Brother DCP-T530DW) set:
   * `dmColor = 2` (`DMCOLOR_COLOR`)
   * `dmPrintQuality = -4` (`DMRES_HIGH`) or `≥ 600 DPI`
   * `dmBitsPerPel = 24` or `32` (True Color)
3. Because `dmICMIntent == 0`, the previous code fell through to `else { isColor = false; }`, **forcibly converting every color print job into Black & White**.

---

### Comprehensive Architecture & Fixes (v1.5.29)

#### 1. Multi-Tier Win32 DEVMODE Color Detection Engine
A unified `EvaluateDevModeColor(DEVMODE devMode)` method now governs all print spooler polling and job inspection:

```mermaid
flowchart TD
    A["Spooler Job Arrives (winspool.drv)"] --> B{"dmColor == 1 (Monochrome)\nAND plain paper\nAND standard DPI?"}
    B -- Yes --> C["⚫ Black & White (Monochrome)"]
    B -- No --> D{"dmColor == 2 (DMCOLOR_COLOR)?"}
    D -- Yes --> E["🌈 Color"]
    D -- No --> F{"dmPrintQuality <= -3 (High Quality)\nOR >= 600 DPI?"}
    F -- Yes --> E
    F -- No --> G{"dmMediaType > 1\n(Glossy/Photo Paper)?"}
    G -- Yes --> E
    G -- No --> H{"dmBitsPerPel >= 24\n(True Color RGB)?"}
    H -- Yes --> E
    H -- No --> I{"dmICMIntent in 1..4\n(ICC Active)?"}
    I -- Yes --> E
    I -- No --> J{"Document Name contains\n'Passport Photo' or 'Photo Sheet'?"}
    J -- Yes --> E
    J -- No --> C
```

#### Detection Hierarchy Rules:
1. **Explicit Monochrome (`dmColor == 1`)**:
   If `dmColor == 1`, plain paper (`dmMediaType <= 1`), and normal draft/standard quality (`dmPrintQuality > -3 && dmPrintQuality < 600`), the print job is strictly categorized as **Monochrome (B&W)**.
2. **Explicit Color (`dmColor == 2`)**:
   Standard Microsoft Win32 `DMCOLOR_COLOR` flag immediately categorizes the job as **Color**.
3. **High Resolution / High Quality (`dmPrintQuality <= -3` or `≥ 600 DPI`)**:
   Flags `DMRES_HIGH (-4)` and `DMRES_MEDIUM (-3)` set by Adobe Acrobat and graphic applications categorize the job as **Color**.
4. **Specialty / Photo Media (`dmMediaType > 1`)**:
   Glossy (`3`), Transparency (`2`), and Photo Paper (`4+`) categorize the job as **Color**.
5. **Color Bit Depth (`dmBitsPerPel >= 24`)**:
   24-bit and 32-bit RGB color submissions are categorized as **Color**.
6. **Passport Photo Studio Safety**:
   Documents titled `DASMO Passport Photo Sheet` or containing `Photo Sheet` are safeguarded to always categorize as **Color**.
7. **Native Print Settings Synchronization**:
   In `NativePrintViewModel.cs`, both `pd.DefaultPageSettings.Color` and `pd.PrinterSettings.DefaultPageSettings.Color` are synchronized to prevent driver-level monochromatic overrides.

---

### 2. 1-Click Interactive Toast HUD Override

To give the cyber cafe operator immediate manual control without having to open the main studio:
* **Interactive Badges**:
  * **`[🌈 Color ⇋]` / `[⚫ B&W ⇋]`**: Click to toggle instantly between Color and B&W.
  * **`[📑 Duplex ⇋]` / `[📄 Single ⇋]`**: Click to toggle instantly between Duplex (2-in-1) and Single-Sided.
* **Instant Recalculation**:
  Clicking immediately recalculates the job cost, updates the customer's active cart in memory, and triggers background synchronization with the linked Excel accounts ledger.
* **Visual States**:
  * Color mode displays in gold/amber (`#F59E0B`).
  * B&W mode displays in cyan (`#00BCD4`).
  * Duplex displays in emerald green (`#10B981`).

---

### 3. In-App Update Engine Workflow (`AppUpdateService.cs`)

The application includes an automated background update mechanism:
1. **Background Polling**: On startup and scheduled intervals, `AppUpdateService` queries the GitHub Releases API (`https://api.github.com/repos/SUBHOJITPAUL797/DASMO-CYBER-CAFE-TOOLS/releases/latest`).
2. **Semantic Version Comparison**:
   * Installed Version: `1.5.28` (or earlier)
   * Available Release: `1.5.29`
   * Trigger Condition: `LatestVersion > CurrentVersion`
3. **User Prompt**:
   A Windows desktop notification toast appears:
   > *"🚀 A new update (v1.5.29) is available for DASMO CYBER CAFE TOOLS!"*
4. **One-Click Download & Install**:
   Clicking the notification downloads the official MSI installer package to `%TEMP%` and launches it with automatic closing of the previous version.
5. **Manual Check**:
   Operators can also open Dashboard $\rightarrow$ **Tab 11: About & Updates** $\rightarrow$ Click **"Check for Updates"** to review changelogs and trigger installation on demand.

---

### 4. Automated Verification Test Suite

A comprehensive test suite of **70 automated tests** verifies the entire application stack:

| Test ID | Area Tested | Outcome |
| :--- | :--- | :--- |
| `[TEST 1 - 20]` | Document Stacker, Scan Enhancer, Card Extractor, Multi-format Exports | ✅ PASSED |
| `[TEST 21 - 34]` | Passport Studio, EXIF Normalization, Vector In-Place PDF Edits, Layout Engine | ✅ PASSED |
| `[TEST 40 - 53]` | Sub-pixel Alignment, Ghost Text Occlusion, Bangla Normalization, Anti-Tamper | ✅ PASSED |
| `[TEST 54 - 60]` | Exact Dimension Scaling, Universal Decoders, Spooler Interceptor, Bill Engine | ✅ PASSED |
| `[TEST 61 - 67]` | Cash Drawer Accounts, Brother SNMP Live Audit, Khata Ledger, Repayments | ✅ PASSED |
| `[TEST 68 - 69]` | Virtual Printer Gate, ₹3 Xerox vs ₹5 PC Print, Zero-Config Silent Excel Auto-Sync | ✅ PASSED |
| **`[TEST 70]`** | **Win32 DEVMODE Acrobat High Quality, 600 DPI, Glossy, TrueColor, B&W Override & STA Toast HUD** | **✅ PASSED** |

```
==================================================================
   TEST RESULTS: 70 PASSED, 0 FAILED
==================================================================
```

---

### 5. Build & Packaging Policy
* **Standard Development Mode**:
  After code modifications, run unit and integration tests (`SmartSaver.Tests.csproj`) to verify zero regressions.
* **MSI Creation Policy**:
  **Do NOT build the MSI installer package after incremental changes.**
  The final MSI build is executed **only when explicitly requested by the project owner**.
