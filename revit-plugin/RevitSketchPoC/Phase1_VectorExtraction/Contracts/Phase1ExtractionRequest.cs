namespace RevitSketchPoC.Phase1_VectorExtraction.Contracts
{
    public sealed class Phase1ExtractionRequest
    {
        public string PdfPath { get; set; } = string.Empty;
        public int PdfPageNumber { get; set; } = 1;
        public int TileSizePt { get; set; } = 256;
        public int AiRasterDpi { get; set; } = 300;
        public int PreviewRasterDpi { get; set; } = 200;
        public int OcrRasterDpi { get; set; } = 300;
        public int UltraRasterDpi { get; set; } = 400;
        public string WorldUnits { get; set; } = "mm";
        public double? PdfToWorldScale { get; set; }
        public bool ExportTiff { get; set; }
        public bool ExportParquet { get; set; } = true;
    }
}
