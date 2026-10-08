# Project: DASMO CYBER CAFE TOOLS — One-Click Document Background Whitening & Ink Contrast Enhancement

## Architecture
- **Target Platform**: .NET 8.0 (`net8.0-windows10.0.17763.0`), win-x64, WPF, WinForms interop, `AllowUnsafeBlocks: true`.
- **Imaging Libraries**: `SixLabors.ImageSharp 2.1.13` (raster manipulation), `PdfiumViewer 2.14.5` (PDF rasterization), `PdfSharpCore 1.3.67` (PDF document export), GDI+ `System.Drawing.Common 8.0.10`.
- **Subsystems**:
  - `DocumentEnhancerService` (`IDocumentEnhancerService`): Core image enhancement engine (spatially adaptive 2D block grid background estimation, bilinear interpolation, optical division normalization, chromaticity soft-masking, Hermite cubic S-curve text darkening, 64KB L2 cache LUT).
  - `DocumentStacker`: ID card stacking (Aadhaar, Voter, PAN), 2-sided layout, live preview (`LoadFrontPreview`/`LoadBackPreview`), A4 export, direct printing.
  - `PdfEditor`: PDF page viewing, editing, live 150 DPI preview, 300 DPI high-res export and direct printing.
  - `SmartSaver.Tests`: Test harness in `Program.cs` verifying all 76 existing assertions + new automated benchmark tests.

## Feature Inventory
| # | Feature | Description | Milestone | Source |
|---|---------|-------------|-----------|--------|
| 1 | Core Enhancement Engine | `IDocumentEnhancerService` with adaptive 2D grid whitening, color protection, Hermite S-curve ink contrast, <600ms CPU performance | M1 | Survey |
| 2 | Presets & Calibration | `Light`, `Balanced` (Default), and `Strong` enhancement presets | M1 | Survey |
| 3 | Document Stacker Engine Integration | Upgrade `DocumentStackerService` to utilize `DocumentEnhancerService` for whitening and ink contrast | M2 | Survey |
| 4 | Document Stacker Live Preview Fix | Fix `DocumentStackerViewModel` preview generation to reflect whitening and pitch-black in real-time | M2 | Survey |
| 5 | Document Stacker UI Controls | Add 1-click toggle and preset options to `DocumentStackerWindow.xaml` | M2 | Survey |
| 6 | PDF Editor Engine Integration | Integrate `DocumentEnhancerService` into `PdfEditorViewModel` and export/print pipelines | M3 | Survey |
| 7 | PDF Editor UI Studio Controls | Add 1-click clean toggle and preset selector to `PdfEditorWindow.xaml` toolbar | M3 | Survey |
| 8 | E2E Test Suite & Synthetic Benchmarks | Procedural test documents for gray backgrounds (>160 to >=250 RGB), text stroke darkening (<=35 RGB), color preservation, and <600ms latency | M_E2E | Survey |
| 9 | 100% Test Pass & Zero Regressions | All 76 existing assertions + all new benchmarks pass (0 errors) | M4 | Survey |
| 10 | Adversarial Coverage Hardening | White-box stress testing across edge cases, rotations, extreme shadows, and dense certificates | M4 | Survey |

## Milestones
| # | Name | Scope | Dependencies | Status |
|---|------|-------|-------------|--------|
| M_E2E | E2E Test Suite Track | Build automated benchmarks in `SmartSaver.Tests`, create `TEST_INFRA.md` & `TEST_READY.md` | none | IN_PROGRESS |
| M1 | Core Enhancement Engine | Implement `IDocumentEnhancerService`, `DocumentEnhancerService`, and preset models | none | IN_PROGRESS |
| M2 | Document Stacker Enhancement & Live Preview | Integrate into `DocumentStackerService`, fix preview bug in `DocumentStackerViewModel`, add UI controls in `DocumentStackerWindow.xaml` | M1 | PLANNED |
| M3 | PDF Editor Scanned Page Whitening | Integrate into `PdfEditorViewModel`, `PdfEditorService`, and add UI controls in `PdfEditorWindow.xaml` | M1 | PLANNED |
| M4 | Final Milestone & Adversarial Hardening | Phase 1: 100% E2E test pass (Tiers 1-4). Phase 2: Adversarial coverage hardening (Tier 5). | M_E2E, M2, M3 | PLANNED |

## Interface Contracts
### `IDocumentEnhancerService`
```csharp
namespace SmartSaver.Services;

public enum EnhancementPreset
{
    Light,
    Balanced,
    Strong
}

public class EnhancementOptions
{
    public EnhancementPreset Preset { get; set; } = EnhancementPreset.Balanced;
    public bool WhitenBackground { get; set; } = true;
    public bool DarkenText { get; set; } = true;
    public bool PreserveColor { get; set; } = true;
    public int BlockSize { get; set; } = 32;
    public int WhiteCutoff { get; set; } = 222;
    public int InkCutoff { get; set; } = 80;
    public int DarkTarget { get; set; } = 20;
    public float ColorThresholdLow { get; set; } = 22f;
    public float ColorThresholdHigh { get; set; } = 38f;
    public float SaturationBoost { get; set; } = 1.15f;
}

public interface IDocumentEnhancerService
{
    void EnhanceImage(SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32> image, EnhancementOptions? options = null);
    System.Windows.Media.Imaging.BitmapSource EnhanceBitmapSource(System.Windows.Media.Imaging.BitmapSource source, EnhancementOptions? options = null);
    System.Drawing.Bitmap EnhanceBitmap(System.Drawing.Bitmap bitmap, EnhancementOptions? options = null);
    Task EnhanceFileAsync(string inputPath, string outputPath, EnhancementOptions? options = null);
}
```

## Code Layout
- `src/SmartSaver/Services/IDocumentEnhancerService.cs` (Owned by M1)
- `src/SmartSaver/Services/DocumentEnhancerService.cs` (Owned by M1)
- `src/SmartSaver/Services/DocumentStackerService.cs` (Owned by M2)
- `src/SmartSaver/ViewModels/DocumentStackerViewModel.cs` (Owned by M2)
- `src/SmartSaver/Views/DocumentStackerWindow.xaml` (Owned by M2)
- `src/SmartSaver/Services/PdfEditorService.cs` (Owned by M3)
- `src/SmartSaver/ViewModels/PdfEditorViewModel.cs` (Owned by M3)
- `src/SmartSaver/Views/PdfEditorWindow.xaml` (Owned by M3)
- `src/SmartSaver.Tests/` (Owned by E2E Testing Track & M4)
