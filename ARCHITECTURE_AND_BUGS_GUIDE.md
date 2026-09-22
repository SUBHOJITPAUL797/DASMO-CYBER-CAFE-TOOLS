# 🧠 DASMO CYBER CAFE TOOLS — AI & Developer Deep Architecture & Bug Post-Mortem Guide

> **Primary Knowledge Base for AI Agents, Developers, and Maintainers.**  
> This file contains the complete, detailed technical blueprints, root cause analyses, mathematical formulas, thread-safety patterns, and bug post-mortems for the DASMO Cyber Cafe Tools codebase.  
> Whenever an AI agent or engineer works on this repository in the future, read this document to understand the subsystems and avoid regressions.

---

## 📌 Table of Contents
1. [Core Architectural Principles & Threading](#1-core-architectural-principles--threading)
2. [Bengali PDF Unicode Normalization & Shaded Background Sampling (v1.4.5)](#2-bengali-pdf-unicode-normalization--shaded-background-sampling-v145)
3. [Font Weight Preservation & Dynamic Horizontal Text Flow (v1.4.5)](#3-font-weight-preservation--dynamic-horizontal-text-flow-v145)
4. [Snug Dynamic Text Sizing, Shrinkage & Test Draft Isolation (v1.4.6)](#4-snug-dynamic-text-sizing-shrinkage--test-draft-isolation-v146)
5. [Zero-Lag Realtime Text Editing & Debounced Draft Persistence (v1.4.7)](#5-zero-lag-realtime-text-editing--debounced-draft-persistence-v147)
6. [Headless Testing & WPF Threading Guidelines](#6-headless-testing--wpf-threading-guidelines)
7. [Installer (MSI) Packaging & 3-File Version Sync Pipeline](#7-installer-msi-packaging--3-file-version-sync-pipeline)
8. [Dual-State Undo/Redo Engine & Font Weight Retention (v1.4.8)](#8-dual-state-undoredo-engine--font-weight-retention-v148)
10. [Orientation-Agnostic Background Replacement & Shirt Protection (v1.4.9)](#10-orientation-agnostic-background-replacement--shirt-protection-v149)
11. [Enterprise Multi-App Cloud Licensing, Hardware Lock & Admin Control (v1.4.9)](#11-enterprise-multi-app-cloud-licensing-hardware-lock--admin-control-v149)

---

## 1. Core Architectural Principles & Threading

### UI Thread vs Background Thread Separation
* **WPF UI Thread**: Must remain 100% responsive (60+ FPS, <16ms per frame).
  - **Rule**: NEVER perform synchronous disk I/O (`File.WriteAllText`, `File.ReadAllText`), cryptographic hashing (`SHA256`), or heavy JSON serialization on the UI thread during active user typing or dragging.
  - **Rule**: Pure in-memory mutations (<0.01ms) happen on UI thread; persistence to disk is debounced and offloaded via `Task.Run`.
* **WPF Object Affinity**:
  - WPF Visuals (`BitmapSource`, `Visual`, `DependencyObject`) have thread affinity and cannot be touched by background threads.
  - Decouple data via serializable Plain Old CLR Objects / DTOs (`PdfEditItemDto`, `PdfDraftSession`) before dispatching to background tasks.

---

## 2. Bengali PDF Unicode Normalization & Shaded Background Sampling (v1.4.5)

### The Problem
When editing Indian government documents (such as West Bengal Land & Land Reforms Department Record of Rights / Banglarbhumi ROR), two critical issues occurred:
1. **Garbled / Cyrillic Substitute Glyphs**: Legacy government portal PDF generators use TrueType subset fonts lacking standard Unicode CMaps. Text extractors (`PdfPig`) read Cyrillic or Greek surrogate codepoints (e.g. `Ы` for `ত্ত`, `Ѿ` for `স্ব`, `έ` for visual E-kar `ে`, `Ν`/`Μ` for I-kar `ি`). Pre-base vowels were ordered visually rather than logically, and rendering in `Arial` lacked Indic OpenType ligatures.
2. **Grey Table Cell Whiteout Glitch**: In government forms, cells (such as Khatian `115`) have shaded grey backgrounds (`#EFEFEF` / `rgb(239, 239, 239)`). In-place editing previously used hardcoded `#FFFFFF` whiteout boxes, resulting in an ugly white square over grey table cells.

### The Solution
1. **Dynamic Background Sampling (`PdfEditorViewModel.SampleBackgroundColor`)**:
   - Inspects `CurrentPagePreview` at the text block's location.
   - Maps PDF points to bitmap pixels and samples around the perimeter of the text bounding box.
   - Discards dark ink pixels (`luminance < 0.4`) and averages background pixels to determine the dominant cell background hex (e.g. `#EFEFEF` or `#FFFFFF`).
   - Sets `PdfWhiteoutItem.FillColorHex` and `PdfTextItem.BackgroundColorHex` to this sampled color so whiteout seamlessly blends with shaded table cells.

2. **Bengali Text Normalization (`BengaliTextHelper.cs`)**:
   - **Legacy Subset Glyph Mapping Table**:
     - `\u042B` (Ы) $\rightarrow$ `"ত্ত"` (e.g. `আকাল দত্ত`)
     - `\u047E` (Ѿ) $\rightarrow$ `"স্ব"` (e.g. `পিতা/স্বামী`, `অত্রস্বত্বের`)
     - `\u03AD` (έ) $\rightarrow$ `"\u09C7"` (E-kar)
     - `\u039D` / `\u039C` (Ν/Μ) $\rightarrow$ `"\u09BF"` (I-kar)
     - `\u03DC` (Ϝ) $\rightarrow$ `"শ্র"` (e.g. `শ্রেণী`)
     - `\u049D` (ҝ) $\rightarrow$ `"্য"` (e.g. `সংখ্যা`, `মধ্যে`)
     - `\u043E` (о) $\rightarrow$ `"ন্ত"` (e.g. `মন্তব্য`)
     - `\u0414` (Д) $\rightarrow$ `"ঙ"` (e.g. `ডাঙা`)
     - `\u113F` (ᄿ) $\rightarrow$ `"স্তু"` (e.g. `বাস্তু`)
     - `\u03CF` (Ϗ) $\rightarrow$ `"ত্র"` (e.g. `অত্র`)
     - `\u042F` / `\u03EC` (Я/Ϭ) $\rightarrow$ `"ত্ব"` (e.g. `অত্রসত্বের`)
     - `\u03BA` (κ) $\rightarrow$ `"র্"` (reph)
     - `\u0434` (д) $\rightarrow$ `"দ্ধ"` (e.g. `অর্দ্ধগ্রাম`)
     - `\u03C2` (ς) $\rightarrow$ `"গ্র"` (e.g. `গ্রাম`)
     - `\u0405` (Ѕ) $\rightarrow$ `"ক্ত"` (e.g. `ব্যক্তি`)
   - **Vowel & Reph Reordering**: Converts visual pre-base vowels (`ি`, `ে`, `ৈ`) into logical order, and visual post-base reph into logical pre-base reph.
   - **Font Mapping**: Maps Bengali text to Windows standard Indic OpenType font **`Nirmala UI`** (fallback to `Vrinda`).
   - **Syllable Fragment Merging**: Merges split syllables on the same baseline (`gap` between `-8.0` and `+1.0` pt) into cohesive words.

---

## 3. Font Weight Preservation & Dynamic Horizontal Text Flow (v1.4.5)

### Font Weight Detection
* **Issue**: Clicking bold text jumped from **Bold** to **Normal**, making edited text look noticeably fake.
* **Fix**: PDF fonts often encode boldness via numeric weights (`Weight >= 500`) rather than font family names. `PdfEditorService.ExtractTextBlocks` now checks both `firstLetter.Font.IsBold` and `firstLetter.Font.Weight >= 500`.

### Intelligent Phrase & Label Grouping
* **Issue**: Words were previously extracted individually (e.g. `রায়তের`, `নাম`, `:` were 3 separate boxes). Editing `নাম` caused typed text to collide into `:`.
* **Fix**: Words on the same baseline within standard inter-word gaps are grouped into cohesive label blocks. Colons (`:`) within 35 pt are merged into the label, but stop grouping so subsequent field values (`115`, `আকাল দত্ত`) remain clean, separate edit items.

### Dynamic Horizontal Text Shifting (`ShiftSubsequentItemsOnLine`)
* When user types and text expands rightward:
  - Subsequent edit items on the same baseline shift rightward automatically (`shift = requiredX - other.X`).
  - Unedited background text blocks that would be overlapped are automatically promoted to edit items, whited out underneath, and shifted to the right to prevent collisions.

---

## 4. Snug Dynamic Text Sizing, Shrinkage & Test Draft Isolation (v1.4.6)

### The Oversized Box Defect
* **Symptom**: Clicking cell `115` created a text box stretching ~212 pt horizontally across the table.
* **Root Causes**:
  1. **Monotonic Expansion Guard**: `PdfTextItem.Text` used `if (estimatedWidth + 10.0 > Width) { Width = ... }` and `whiteout.Width = Math.Max(whiteout.Width, ...)`. This expanded when typing, but **never shrank** when text was backspaced or deleted.
  2. **Test Pollution of AppData**: Automated tests typed test string `"115/2026/ROR/MEMBER/VERIFIED/EXPANDED"` against `AKAL DUTTA 115 ROR.pdf` and saved a draft to `%APPDATA%\DASMO CYBER CAFE TOOLS\Drafts\`. When the user opened the file, the bloated draft was restored.

### The Solution
* **Snug Bidirectional Auto-Width (`UpdateAutoWidth`)**:
  $$W = \max(\text{Round}(\text{EstimateTextWidth}(\text{Text}, \text{FontFamily}, \text{FontSizePt}, \text{IsBold}) + 6.0), 15.0)$$
  - Dynamically recalculates in `Text.set`, `FontFamily.set`, `FontSizePt.set`, and `IsBold.set`.
  - Expands when typing and **shrinks immediately** when deleting or backspacing.
* **Bounded Dynamic Whiteout**:
  - `whiteout.Width` mirrors `textItem.Width + padX * 2`, bounded below by `OriginalBlockWidth + padX * 2 + 2.0` so original text never peeks through.
* **Test Draft Isolation (`DraftsFolder`)**:
  - `PdfDraftService.Instance.DraftsFolder` is configurable. Automated tests redirect drafts to temporary isolated folders (`testDir/Drafts`), guaranteeing zero test pollution in `%APPDATA%`.

---

## 5. Zero-Lag Realtime Text Editing & Debounced Draft Persistence (v1.4.7)

### The Typing Lag Defect
* **Symptom**: Typing or editing text in the PDF Editor Studio felt choppy and laggy with dropped keystrokes.
* **Root Cause Analysis**:
  1. **12 Synchronous Disk Writes per Keystroke**:
     - `TextBox.Text` has `UpdateSourceTrigger=PropertyChanged`.
     - Keystroke $\rightarrow$ `Text.set` $\rightarrow$ `UpdateAutoWidth()` sets `Width`.
     - Both `Text` and `Width` changes invoked `Item_PropertyChanged`.
     - Inside `Item_PropertyChanged`, setting `whiteout.X`, `Y`, `Width`, `Height` fired 4 recursive calls because `_isSuppressingItemPropertySync` was missing!
     - Each call ran `SaveCurrentEditsToStore()` $\rightarrow$ synchronous SHA-256 hash, indented JSON serialization, and `File.WriteAllText` to disk, plus another write for `OutputHistoryService`.
     - Windows Defender / AV filter drivers intercepted each write, causing **50–200ms freezes per character**.
  2. **Uncached WPF Typeface Allocations**: `EstimateTextWidth` instantiated `new FontFamily` and `new Typeface` on every keystroke.
  3. **Duplicate Shifting**: `ShiftSubsequentItemsOnLine` executed twice per keystroke (on both `Text` and `Width`).

### The Architectural Solution
1. **Debounced In-Memory Draft Pipeline (`_draftDebounceTimer`)**:
   - In [`PdfEditorViewModel.cs`](file:///c:/CODING/coading/DASMO%20CYBER%20CAFE/src/SmartSaver/ViewModels/PdfEditorViewModel.cs):
     - Active typing updates in-memory collections and properties instantly (<0.01ms).
     - Draft disk persistence is debounced via a 500ms `DispatcherTimer`.
     - Keystroke only resets the timer (<0.001ms) with **ZERO disk I/O on the UI thread**.
     - When user pauses typing for 500ms, the timer tick takes a lightweight DTO snapshot on the UI thread and offloads serialization and disk writing to a background thread (`Task.Run`).
2. **Immediate Disk Save Preservation (`immediateDiskSave: true`)**:
   - Retained for discrete lifecycle actions:
     - Page switching (`CurrentPageIndex.set`)
     - Document saving (`SavePdfAsync`) & Printing (`PrintCurrentPdf`)
     - Undo / Redo
     - Item deletion (`DeleteSelectedItem`)
     - Focus lost or Enter/Escape key (`TextItem_LostFocus`, `TextItem_PreviewKeyDown`)
     - Window closing cancellation (`Window_Closing` $\rightarrow$ `FlushDraftNow()`)
3. **Cascading Sync Suppression**:
   - Linked whiteout updates and line shifting are wrapped in `try { _isSuppressingItemPropertySync = true; ... } finally { _isSuppressingItemPropertySync = false; }`.
   - Whiteout updates and line shifting trigger strictly on `Width` events.
4. **Static Typeface Caching**:
   - `PdfTextItem` introduces `_typefaceCache` (`ConcurrentDictionary<(string font, bool isBold), Typeface>`) in [`PdfEditItem.cs`](file:///c:/CODING/coading/DASMO%20CYBER%20CAFE/src/SmartSaver/Models/PdfEditItem.cs), eliminating font allocation overhead.
5. **Thread-Safe DTO Persistence (`SaveDraftFromDtos`)**:
   - In [`PdfDraftService.cs`](file:///c:/CODING/coading/DASMO%20CYBER%20CAFE/src/SmartSaver/Services/PdfDraftService.cs), accepts pre-built DTO snapshots detached from WPF UI visual trees.

---

## 6. Headless Testing & WPF Threading Guidelines

### STA vs MTA Threading
* WPF UI components and `RelayCommand` utilize `CommandManager.RequerySuggested`, which requires a Single-Threaded Apartment (STA) thread.
* The test runner (`SmartSaver.Tests/Program.cs`) runs on an MTA console thread.
* **Rule**: All ViewModel/Window tests must spawn an explicit STA thread:
  ```csharp
  var thread = new Thread(() => { ... });
  thread.SetApartmentState(ApartmentState.STA);
  thread.Start();
  thread.Join(TimeSpan.FromSeconds(15));
  ```
* **Deadlock Warning**: Do not call `asyncMethod.GetAwaiter().GetResult()` on a dispatcher-less STA thread. Use synchronous overloads or pump the dispatcher.

---

## 7. Installer (MSI) Packaging & 3-File Version Sync Pipeline

### The 3-File Version Sync Rule
Before compiling an installer update, the version number must be updated across all 3 files:
1. `src/SmartSaver/SmartSaver.csproj`: `<Version>1.4.x.0</Version>`
2. `src/SmartSaver.Installer/Package.wxs`: `Version="1.4.x.0"`
3. `src/SmartSaver/Views/MainWindow.xaml` & `SettingsWindow.xaml`: Visible UI version text (`v1.4.x`)

### Mandatory 2-Step MSI Build Pipeline
```powershell
$dotnet = "C:\Users\Mypc3\.dotnet\dotnet.exe"

# Step 1: Publish self-contained binaries
& $dotnet publish "src\SmartSaver\SmartSaver.csproj" -c Release -r win-x64 --self-contained true -o "src\SmartSaver\bin\Release\net8.0-windows10.0.17763.0\win-x64\publish"

# Step 2: Build WiX Setup MSI
& $dotnet build "src\SmartSaver.Installer\SmartSaver.Installer.wixproj" -c Release --nologo

# Step 3: Copy output MSI to root
Copy-Item "src\SmartSaver.Installer\bin\Release\DASMO_CYBER_CAFE_TOOLS_Setup.msi" -Destination "DASMO_CYBER_CAFE_TOOLS_v1.4.8_Setup.msi" -Force
Copy-Item "src\SmartSaver.Installer\bin\Release\DASMO_CYBER_CAFE_TOOLS_Setup.msi" -Destination "DASMO_CYBER_CAFE_TOOLS_Setup.msi" -Force
```

---

## 8. Dual-State Undo/Redo Engine & Font Weight Retention (v1.4.8)

### Defect 1: Text Thickness / Weight Reversion on Edit
* **Symptom**: Clicking on a bold text item (e.g. `ব্যক্তি` in Bengali land records like `AKAL DUTTA 115 ROR.pdf`) caused the text to immediately jump from thick/bold to a hairline-thin normal font. Edits appeared blatantly doctored.
* **Root Causes**:
  1. **Subset CIDFont Weight Metadata**: In government land records, PDF fonts (e.g. `CIDFont+F7`) often omit the standard bold flag or encode bold as numeric weights (`Weight: 500` or `Weight: 0` when font descriptors are sparse). Previously, `block.IsBold` defaulted to `false` unless explicitly flagged as bold.
  2. **ViewModel Toolbar Bold Desynchronization**: When `StartEditingExtractedBlock` created a `PdfTextItem`, it selected the item and overwrote the toolbar's `_isBold` state with `false`, permanently turning off the bold toggle button.
  3. **WPF Subpixel Antialiasing Thinning**: In WPF, default `TextFormattingMode="Ideal"` and grayscale rendering can thin out Indic glyph stems at small point sizes (~9.1pt).
* **The Solution**:
  1. **Expanded Bold Extraction (`PdfEditorService.ExtractTextBlocks`)**:
     - Evaluates `(firstLetter.Font.IsBold == true) || (firstLetter.Font.Weight >= 500)`.
     - For Bengali subset fonts, retains bold if `Weight >= 450 || Weight == 0 || Font == null`.
     - Maps font descriptor names containing `SEMIBOLD`, `DEMIBOLD`, `SBOLD`, `FAT`, or `DARK` directly to bold weights.
  2. **Direct Typography Binding (`FontWeightValue` & `FontStyleValue`)**:
     - Added dynamic `FontWeightValue` (`FontWeights.Bold` vs `FontWeights.Normal`) and `FontStyleValue` properties to `PdfTextItem` with `OnPropertyChanged` notifications on `IsBold` and `IsItalic`.
     - Bound `FontWeight="{Binding FontWeightValue}"` and `FontStyle="{Binding FontStyleValue}"` in `PdfEditorWindow.xaml`.
     - Added `TextOptions.TextFormattingMode="Display"` and `TextOptions.TextRenderingMode="ClearType"` to prevent subpixel thinning.
  3. **Toolbar Synchronization**:
     - In `PdfEditorViewModel.StartEditingExtractedBlock`, explicitly updates `_isBold = block.IsBold`, `_fontSizePt`, and triggers `OnPropertyChanged(nameof(IsBold))` so the toolbar button remains active.

---

### Defect 2: Click-to-Edit Inoperable After Undo
* **Symptom**: When a user clicked an extracted text block to edit, and subsequently clicked the **Undo** button, clicking on that same text again did nothing—the inline edit box would not appear.
* **Root Causes**:
  1. `StartEditingExtractedBlock` intentionally removed the clicked `block` from `ExtractedTextBlocks` to prevent ghost duplicate rendering under the editing overlay.
  2. The undo engine (`_undoStacks`) previously stored only `CurrentPageEdits` (`List<PdfEditItem>`), completely unaware of `ExtractedTextBlocks`.
  3. Upon calling `Undo()`, the whiteout and text edit items were popped from `CurrentPageEdits`, but the extracted text block remained permanently removed from `ExtractedTextBlocks`. The transparent hit-test overlay was empty at that coordinate, making subsequent clicks register as misses.
* **The Solution**:
  1. **Dual-State Undo Engine (`PageUndoState`)**:
     ```csharp
     public class PageUndoState
     {
         public List<PdfEditItem> Edits { get; set; } = new();
         public List<PdfExtractedTextBlock> Blocks { get; set; } = new();
     }
     ```
  2. **Synchronous Dual-Stack Snapshots**:
     - `_undoStacks` and `_redoStacks` now store `Stack<PageUndoState>`.
     - `PushUndoState()` creates deep copies of both `CurrentPageEdits` and `ExtractedTextBlocks` (via `PdfExtractedTextBlock.Clone()`).
     - `Undo()` and `Redo()` restore both collections atomically, immediately resurrecting the clickable text block on the overlay.
  3. **Delete Restoration Safeguard**:
     - When deleting an edit item via `DeleteSelectedItem()`, if the item was created from an extracted text block (`OriginalTextToRedact`), the engine automatically recovers and inserts the original `PdfExtractedTextBlock` back into `ExtractedTextBlocks`.

---

## 9. Universal Crop Tool Null-Safety & Intelligent Photo Rotation (v1.4.9)

### Defect 1: Crop Tool Crash (`NullReferenceException`)
* **Symptom**: Clicking "✂️ Crop Photo" in Passport Photo Studio or "✏️ Crop" in A4 Document Stacker threw:
  ```text
  Crop Error: Crop tool error: Object reference not set to an instance of an object.
  ```
* **Root Causes**:
  1. **Premature XAML Parsing Callbacks**: In `ImageCropDialog.xaml`, `RbModeBox` had `IsChecked="True" Checked="CropMode_Changed"`. During `InitializeComponent()`, WPF fired the `Checked` event before child elements (`CropBox`, `QuadPolygon`, `TxtStatus`, etc.) were constructed, causing `NullReferenceException` inside `CropMode_Changed` and `UpdateModeVisibility`.
  2. **GDI+ Bitmap Race Conditions**: In `PassportStudioViewModel`, `_loadedOriginalBmp.Save(tempFile)` executed on the UI thread while a background task (`TriggerSheetUpdate()`) was concurrently calling `_studioService.ProcessPortrait(_loadedOriginalBmp)`. Disposing or mutating an unmanaged GDI+ bitmap while another thread reads it corrupts GDI+ internal handles.
  3. **Unsafe `Application.Current` Dereference**: `DocumentStackerViewModel` accessed `Application.Current.MainWindow` directly without null propagation.
* **The Solution**:
  1. Added `_isInitialized` guard flag in `ImageCropDialog` to suppress all XAML-parsing events until construction completes. Added null-conditional checks on all UI elements in `UpdateModeVisibility`, `CropMode_Changed`, `Preset_Checked`, `UpdateOverlayAndHandles`, and `UpdateQuadOverlay`.
  2. Added thread-safe `lock (_loadedOriginalBmp)` and created an isolated deep copy before saving the temp file for cropping.
  3. Added dedicated `RbPassport` (3.5 × 4.5 cm / ratio 0.78) and `RbStamp` (2.5 × 3.0 cm / ratio 0.83) presets, and a 1-click `SnapToPassportRatio_Click` button.
  4. Added Quad pin rotation in `Rotate90_Click` and `RotateCCW_Click` so 4-corner document deskew pins rotate alongside the image.

### Defect 2: Rotated Photo Distorted / Squished on Passport Sheet
* **Symptom**: When a user rotated a loaded portrait photo (e.g., using `↻ Rotate`), the photos on the sheet preview became horizontally compressed and vertically stretched like a funhouse mirror.
* **Root Causes**:
  1. `RotatePhoto` called `_loadedOriginalBmp.RotateFlip(...)`, swapping the source image from portrait (e.g. 600×800) to landscape (800×600).
  2. The custom batch slot dimensions remained locked at `3.5cm × 4.5cm` (portrait).
  3. In `PassportStudioService.GenerateSheetAsync`, `g.DrawImage(portraitBmp, new Rectangle(x, y, itemWPx, itemHPx))` blindly stretched the landscape bitmap into the portrait slot without aspect-ratio preservation.
* **The Solution**:
  1. **Intelligent Slot Dimension Swapping**: When `RotatePhoto` rotates 90° CW or CCW, the engine automatically swaps `PhotoWidthCm` and `PhotoHeightCm` (`3.5 × 4.5` ↔ `4.5 × 3.5`), re-evaluating `UpdateMaxCopies()` to optimize sheet capacity with zero waste.
  2. **Combo Mode Orientation Adaptation**: In `BuildCurrentConfig()`, combo batch dimensions dynamically adapt when the photo is landscape (`4.5×3.5`, `3.0×2.5`, `3.5×2.5`).
  3. **Aspect-Ratio-Preserved UniformToFill Drawing (`DrawPortraitAspectFilled`)**:
     ```csharp
     public static void DrawPortraitAspectFilled(Graphics g, Bitmap portraitBmp, Rectangle destRect)
     {
         double srcRatio = (double)portraitBmp.Width / portraitBmp.Height;
         double destRatio = (double)destRect.Width / destRect.Height;
         // Crop excess margin uniformly from center (UniformToFill)
         // Zero squishing, zero facial distortion, 100% natural proportions
     }
     ```

---

## 10. Orientation-Agnostic Background Replacement & Shirt Protection (v1.4.9)

### Defect 3: Inverted Portrait Shirt Bleaching / Erosion Glitch
* **Symptom**: When a user rotates a portrait photo upside-down (180° or inverted), the person's shirt is heavily eroded and bleached with white blotches and holes across the preview and generated sheets.
* **Root Causes**:
  1. **Top-Border Sampling Assumption**: In `PassportStudioService.ReplacePortraitBackground`, the background color was sampled exclusively from the top border band (`y = 0` to `sampleH = h / 25`) under the naive assumption that the head is always at the top. When the portrait is rotated 180° upside-down, the top rows contain the subject's shirt (`R=21, G=34, B=68`). The algorithm sampled the shirt as the "background" color.
  2. **Global Color Keying**: Once the shirt color was misidentified as the background, a global color-distance loop scanned the entire bitmap and replaced all pixels matching the shirt with `StudioWhite` (`#FFFFFF`), erasing over 15,000 pixels of clothing.
* **The Solution**:
  1. **4-Corner Standard Deviation Variance Analysis**:
     - The engine analyzes all 4 corner boxes (`boxW × boxH` at top-left, top-right, bottom-right, bottom-left):
       $$\sigma = \sqrt{\frac{1}{N} \sum_{i=1}^N \left( (R_i - \bar{R})^2 + (G_i - \bar{G})^2 + (B_i - \bar{B})^2 \right)}$$
     - Studio backdrops and uniform walls have minimal variance ($\sigma < 5$), while clothing, patterns, and body features exhibit high variance ($\sigma > 40$).
     - The corner with the lowest variance is selected as the primary backdrop reference, and its adjacent corners are inspected to determine whether the subject's clothing is positioned at the top, bottom, or sides.
  2. **Edge-Connected BFS Flood-Fill Masking**:
     - Background replacement is performed via edge-connected Breadth-First Search (BFS) flood fill rather than global color replacement.
     - Seeds are placed strictly on verified backdrop edges (skipping the clothing side).
     - The fill expands only through contiguous background pixels. Interior features within the subject silhouette—such as white plaid checks, buttons, collars, or light-colored stripes—are physically isolated and 100% preserved.
  3. **Stackalloc Loop Warning Elimination (`CA2014`)**:
     - Inlined 4-way neighbor coordinate checks (`cy > 0`, `cy < h - 1`, `cx > 0`, `cx < w - 1`) directly within the BFS loop, eliminating stack allocations and compiler warnings.

---

## 11. Enterprise Multi-App Cloud Licensing, Hardware Lock & Admin Control (v1.4.9)

### The Requirement & Architectural Constraints
The DASMO ecosystem comprises multiple independent applications:
1. **DASMO CYBER CAFE TOOLS (SmartSaver)**: Windows PC Suite for automated image compression, PDF editing, government card auto-extraction, passport sheet studio, and A4 stacker.
2. **DASMO CYBER CAPTURE**: High-performance mobile camera streaming client for Windows PC studio capture.
3. **DASMO DOC SCANNER & CYBER CAFE TOOL**: Mobile scanning and document utilities.
4. **DASMO PHOTO PRINT**: Mobile passport photo studio.
5. **SMS FORWARDER**: Cloud OTP & SMS forwarder.

### Core Security & Isolation Mandates
1. **Strict Collection Isolation (Zero Crosstalk)**:
   - Each application is assigned its own dedicated collection in Firebase Firestore:
     - `dasmo_pc_users` $\rightarrow$ Windows PC Suite
     - `dasmo_cyber_capture_users` $\rightarrow$ Mobile Cyber Capture
     - `dasmo_scanner_users` $\rightarrow$ Doc Scanner
     - `dasmo_photo_print_users` $\rightarrow$ Photo Print
     - `sms_forwarder_users` $\rightarrow$ SMS Forwarder
   - **Cross-App Boundary Rule**: Approving a user in one app **never** grants access to another app. Approvals, expirations, and status changes operate in 100% isolation.

2. **Physical Hardware Fingerprint & Single-Device Lock**:
   - **Rule**: One approved account can run on exactly one physical machine / device. Account sharing across multiple PCs or phones is strictly prevented.
   - **Windows PC SHA-256 Fingerprinting (`HardwareIdService.cs`)**:
     - Queries Motherboard UUID (`Win32_ComputerSystemProduct.UUID`).
     - Queries Windows Cryptography MachineGuid (`HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography\MachineGuid`).
     - Queries CPU Serial Number (`Win32_Processor.ProcessorId`).
     - Computes SHA-256 hash and formats as `PC-XXXX-XXXX-XXXX-XXXX`.
     - When an approved account attempts login on a different PC, the system triggers `DEVICE_MISMATCH`, locks the interface, and displays a warning dialog.
   - **Android Hardware Binding**:
     - Uses `Settings.Secure.ANDROID_ID`. If the registered `deviceId` does not match the active phone, the app presents a device-mismatch warning screen with an unbind request copy button.

3. **Gated Startup Architecture (`AuthGateWindow` & `CyberAuthGateScreen`)**:
   - The software does not load main dashboards, camera engines, or editing tools until cloud license validation succeeds.
   - If the user is unapproved or pending, the app displays real-time radar pulsing with the user's registration details and hardware model.

4. **Real-Time Live Approval Radar (Zero-Click Unlock)**:
   - Both Windows and Android apps attach real-time Firestore document snapshot listeners.
   - When Super Admin (`subhojitpaul26042004@gmail.com`) clicks **"Approve"** in the PC Admin Panel, the change is written to Firestore and pushed via WebSockets/gRPC to the client app.
   - The software unlocks immediately in real time without requiring an app restart.

5. **Exclusive Super Admin Licensing Control Panel**:
   - Strictly reserved for `subhojitpaul26042004@gmail.com`.
   - Invisible to regular users.
   - Provides multi-app tabbed browsing (`dasmo_pc_users`, `dasmo_cyber_capture_users`, `dasmo_scanner_users`, `dasmo_photo_print_users`).
   - One-click actions:
     - ✅ **Approve**: Sets `isApproved = true`, `status = "approved"`.
     - ⏳ **Set Pending**: Revokes active access.
     - 🚫 **Ban**: Blocks user permanently.
     - 💻 **Unbind PC / Device**: Clears `deviceId` to permit license transfer to a new device.
     - ♾️ **Lifetime License**: Sets `expiryTimestamp = 0`.
     - 🗑️ **Delete User**: Purges record from collection.
