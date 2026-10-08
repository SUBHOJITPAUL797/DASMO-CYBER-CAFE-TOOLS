using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace SmartSaver.Services;

/// <summary>
/// Predefined operator enhancement presets for cyber cafe workflows.
/// </summary>
public enum EnhancementPreset
{
    Light,      // Mild whitening, gentle darkening (preserves delicate pencil/light lines)
    Balanced,   // Standard cyber cafe preset: crisp pitch-black text, pure white paper, color stamps protected
    Strong,     // Deep shadow removal, maximum contrast for faint carbon copies / aged paper
    Custom      // Operator-customized parameters
}

/// <summary>
/// Tuning parameters and calibration options for document background whitening and ink sharpening.
/// Supports both PROJECT.md and specification miner property naming conventions.
/// </summary>
public class EnhancementOptions
{
    public EnhancementPreset Preset { get; set; } = EnhancementPreset.Balanced;

    public bool WhitenBackground { get; set; } = true;
    public bool DarkenText { get; set; } = true;
    public bool PreserveColor { get; set; } = true;
    public bool PreserveColors { get => PreserveColor; set => PreserveColor = value; }

    /// <summary>
    /// Block grid size for local background surface estimation (e.g. 24, 32, 48). Default 32.
    /// </summary>
    public int BlockSize { get; set; } = 32;

    /// <summary>
    /// White paper threshold (0-255). Normalized luminance above this becomes pure 255 white.
    /// </summary>
    public int WhiteCutoff { get; set; } = 220;
    public int WhiteThreshold { get => WhiteCutoff; set => WhiteCutoff = value; }

    /// <summary>
    /// Black text stroke threshold (0-255). Normalized luminance below this is deepened to pitch black.
    /// </summary>
    public int InkCutoff { get; set; } = 80;
    public int BlackThreshold { get => InkCutoff; set => InkCutoff = value; }

    /// <summary>
    /// Darkest target value for core text strokes (e.g. 0-35 RGB, default 20 RGB).
    /// </summary>
    public int DarkTarget { get; set; } = 20;
    public int DarkCoreLevel { get => DarkTarget; set => DarkTarget = value; }

    /// <summary>
    /// Lower chroma threshold (max(RGB)-min(RGB)). Pixels below this are treated as neutral.
    /// </summary>
    public float ColorThresholdLow { get; set; } = 22f;
    public int ChromaThresholdLow { get => (int)ColorThresholdLow; set => ColorThresholdLow = value; }

    /// <summary>
    /// Upper chroma threshold. Pixels above this are treated as 100% chromatic stamp/photo.
    /// </summary>
    public float ColorThresholdHigh { get; set; } = 38f;
    public int ChromaThresholdHigh { get => (int)ColorThresholdHigh; set => ColorThresholdHigh = value; }

    /// <summary>
    /// Multiplier to boost color saturation of preserved stamps/signatures (e.g. 1.05 to 1.25, default 1.15).
    /// </summary>
    public float SaturationBoost { get; set; } = 1.15f;
    public float ColorSaturationBoost { get => SaturationBoost; set => SaturationBoost = value; }

    public EnhancementOptions()
    {
    }

    public EnhancementOptions(EnhancementPreset preset)
    {
        ApplyPreset(preset);
    }

    public void ApplyPreset(EnhancementPreset preset)
    {
        Preset = preset;
        switch (preset)
        {
            case EnhancementPreset.Light:
                BlockSize = 48;
                WhiteCutoff = 235;
                InkCutoff = 65;
                DarkTarget = 32;
                ColorThresholdLow = 18f;
                ColorThresholdHigh = 32f;
                SaturationBoost = 1.05f;
                break;
            case EnhancementPreset.Strong:
                BlockSize = 24;
                WhiteCutoff = 195;
                InkCutoff = 105;
                DarkTarget = 0;
                ColorThresholdLow = 26f;
                ColorThresholdHigh = 44f;
                SaturationBoost = 1.25f;
                break;
            case EnhancementPreset.Balanced:
                BlockSize = 32;
                WhiteCutoff = 220;
                InkCutoff = 80;
                DarkTarget = 20;
                ColorThresholdLow = 22f;
                ColorThresholdHigh = 38f;
                SaturationBoost = 1.15f;
                break;
            case EnhancementPreset.Custom:
                break;
        }
    }

    /// <summary>
    /// Creates options initialized to a standard preset.
    /// </summary>
    public static EnhancementOptions FromPreset(EnhancementPreset preset) => new(preset);
}

/// <summary>
/// Result container for document enhancement operations.
/// </summary>
public sealed class EnhancementResult
{
    public bool Success { get; set; }
    public string? OutputPath { get; set; }
    public byte[]? OutputBytes { get; set; }
    public long ElapsedMilliseconds { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// High-performance document enhancement service contract for adaptive background whitening,
/// shadow removal, ink contrast sharpening, and color component preservation.
/// </summary>
public interface IDocumentEnhancerService
{
    /// <summary>
    /// Enhances an in-memory ImageSharp image in-place with zero re-encoding overhead.
    /// </summary>
    void EnhanceImage(Image<Rgba32> image, EnhancementOptions? options = null);

    /// <summary>
    /// In-place alias for EnhanceImage.
    /// </summary>
    void EnhanceImageInPlace(Image<Rgba32> image, EnhancementOptions? options = null);

    /// <summary>
    /// Enhances an in-memory WPF BitmapSource, returning a frozen WriteableBitmap/BitmapSource for instant UI preview.
    /// </summary>
    BitmapSource EnhanceBitmapSource(BitmapSource source, EnhancementOptions? options = null);

    /// <summary>
    /// Enhances a GDI+ Bitmap, returning an enhanced 32bpp ARGB Bitmap.
    /// </summary>
    System.Drawing.Bitmap EnhanceBitmap(System.Drawing.Bitmap bitmap, EnhancementOptions? options = null);

    /// <summary>
    /// Enhances an image file on disk, writing the normalized result to outputPath.
    /// </summary>
    Task EnhanceFileAsync(string inputPath, string outputPath, EnhancementOptions? options = null);

    /// <summary>
    /// Enhances an image file on disk with detailed result metrics and optional size compression.
    /// </summary>
    Task<EnhancementResult> EnhanceFileWithResultAsync(
        string inputPath,
        string outputPath,
        EnhancementOptions? options = null,
        long? targetBytes = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a fast in-memory preview (downsampled to maxDimension) as JPEG bytes for live UI display.
    /// </summary>
    byte[]? GeneratePreviewBytes(string sourcePath, EnhancementOptions? options = null, int maxDimension = 1000);
}
