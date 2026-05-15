using System.IO;

namespace RevitSketchPoC.Phase1_VectorExtraction.Services.Export
{
    /// <summary>
    /// Parquet é gerado pelo script Python (pandas/pyarrow). Este serviço valida presença dos ficheiros.
    /// </summary>
    public static class ParquetExportService
    {
        public static bool HasParquetOutputs(string parquetDirectory)
        {
            if (string.IsNullOrWhiteSpace(parquetDirectory) || !Directory.Exists(parquetDirectory))
            {
                return false;
            }

            return Directory.GetFiles(parquetDirectory, "*.parquet").Length > 0;
        }
    }
}
