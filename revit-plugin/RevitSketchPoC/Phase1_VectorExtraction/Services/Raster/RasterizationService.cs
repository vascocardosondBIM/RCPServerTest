using RevitSketchPoC.Phase1_VectorExtraction.Configuration;

namespace RevitSketchPoC.Phase1_VectorExtraction.Services.Raster
{
    /// <summary>
    /// Presets multi-DPI para rasterização (preview, OCR, AI, ultra).
    /// A rasterização efectiva corre no script Python phase1_extract.py.
    /// </summary>
    public static class RasterizationService
    {
        public static Phase1RasterSettings ResolveSettings(string preset, int? customTilePt, int? customAiDpi)
        {
            var settings = new Phase1RasterSettings();
            Phase1RasterPresets.ApplyPreset(preset, settings);
            if (customTilePt.HasValue && customTilePt.Value > 0)
            {
                settings.TileSizePt = customTilePt.Value;
            }

            if (customAiDpi.HasValue && customAiDpi.Value > 0)
            {
                settings.AiRasterDpi = customAiDpi.Value;
            }

            return settings;
        }
    }
}
