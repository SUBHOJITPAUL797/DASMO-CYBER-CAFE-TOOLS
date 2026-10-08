# DASMO CYBER CAFE TOOLS — Developer & AI Agent Invariants

This file defines mandatory rules and architectural invariants for all AI agents and developers working in this repository.

---

## 🚨 MANDATORY INVARIANT 1: GDI+ DPI Scaling & `DrawImageUnscaled` Ban

**CRITICAL DEFECT HISTORY (24-Hour Incident)**:
In .NET Windows GDI+, calling `Graphics.DrawImageUnscaled(source, 0, 0)` does **NOT** draw 1:1 raw pixels.
Instead, GDI+ scales based on physical DPI units (`inches = pixels / DPI`).
When drawing a high-DPI image (e.g., 145.14 DPI from PDFium or 300 DPI from a scanner) onto a default 96-DPI bitmap, GDI+ automatically rescales by `96.0 / DPI = 66%`, shrinking the document into the top-left corner and leaving vast blank white space on the right and bottom.

### Strict Rules:
1. **`DrawImageUnscaled` is STRICTLY FORBIDDEN**:
   Never use `Graphics.DrawImageUnscaled(bitmap, x, y)` anywhere in this codebase.
2. **Explicit DPI Matching & Pixel Units**:
   Always set the target bitmap resolution explicitly and draw specifying exact pixel coordinates:
   ```csharp
   flattened.SetResolution(96f, 96f);
   using (var g = System.Drawing.Graphics.FromImage(flattened))
   {
       g.Clear(System.Drawing.Color.White);
       g.DrawImage(bitmap, 
           new System.Drawing.Rectangle(0, 0, width, height), 
           0, 0, width, height, 
           System.Drawing.GraphicsUnit.Pixel);
   }
   ```
3. **Mandatory Test Verification**:
   Before submitting any changes affecting `PdfRendererService`, `DocumentEnhancerService`, or preview dialogs, you **MUST** run:
   ```powershell
   & "$env:USERPROFILE\.dotnet\dotnet.exe" test src\SmartSaver.Tests\SmartSaver.Tests.csproj -c Release
   ```
   and ensure that **`[TEST 84] PDF Edge-to-Edge Preview Presentation & Aspect Ratio Math`** passes with 100% pixel coverage.

---

## 🚨 MANDATORY INVARIANT 2: WPF Preview Viewport Geometry

In `QuickPeekDialog.xaml`, `ExcelPrintStudioDialog.xaml`, and `PdfEditorWindow.xaml`:
1. **Never use `Stretch="Fill"`** on document and PDF pages. Always use `Stretch="Uniform"` or proportional width/height math.
2. In proportional sizing math, always subtract standard 36px viewport margin padding (`availableWidth = Math.Max(200, vWidth - 36)`) and assign:
   ```csharp
   double fitScale = Math.Min(availableWidth / effectiveWidth, availableHeight / effectiveHeight);
   PdfPageImage.Width = bmpWidth * fitScale;
   PdfPageImage.Height = bmpHeight * fitScale;
   ```
3. A4 documents must always maintain their natural `1 : 1.414` aspect ratio.

---

## 🚨 MANDATORY INVARIANT 3: MSI Version Synchronization & In-Place Upgrades

When building or updating the installer:
1. Synchronize the version across `src/SmartSaver/SmartSaver.csproj` (`<Version>`, `<AssemblyVersion>`, `<FileVersion>`) and `src/SmartSaver.Installer/Package.wxs` (`<Package Version="...">`).
2. Always bump the version on new releases (e.g., `1.5.36.0` -> `1.5.37.0`) so Windows Installer performs an automatic in-place `MajorUpgrade` without conflict.
3. Keep `<util:CloseApplication>` and `<CustomAction Id="LaunchAppAfterInstall">` in `Package.wxs` so old processes terminate cleanly before file replacement and the new application auto-launches upon install completion.
