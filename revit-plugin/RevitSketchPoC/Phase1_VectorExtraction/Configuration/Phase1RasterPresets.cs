namespace RevitSketchPoC.Phase1_VectorExtraction.Configuration
{
    public static class Phase1RasterPresets
    {
        public const string PresetFast = "Rápido";
        public const string PresetBalanced = "Balanceado";
        public const string PresetHighPrecision = "Alta precisão";
        public const string PresetCustom = "Customizado";

        public static void ApplyPreset(string preset, Phase1RasterSettings settings)
        {
            switch (preset)
            {
                case PresetFast:
                    settings.TileSizePt = 384;
                    settings.AiRasterDpi = 200;
                    settings.PreviewRasterDpi = 150;
                    settings.OcrRasterDpi = 200;
                    settings.UltraRasterDpi = 300;
                    break;
                case PresetHighPrecision:
                    settings.TileSizePt = 192;
                    settings.AiRasterDpi = 400;
                    settings.PreviewRasterDpi = 200;
                    settings.OcrRasterDpi = 400;
                    settings.UltraRasterDpi = 600;
                    break;
                case PresetBalanced:
                default:
                    settings.TileSizePt = 256;
                    settings.AiRasterDpi = 300;
                    settings.PreviewRasterDpi = 200;
                    settings.OcrRasterDpi = 300;
                    settings.UltraRasterDpi = 400;
                    break;
            }
        }
    }

    public sealed class Phase1RasterSettings
    {
        public int TileSizePt { get; set; } = 256;
        public int AiRasterDpi { get; set; } = 300;
        public int PreviewRasterDpi { get; set; } = 200;
        public int OcrRasterDpi { get; set; } = 300;
        public int UltraRasterDpi { get; set; } = 400;
    }
}
