using System;
using System.IO;

namespace RevitSketchPoC.Phase1_VectorExtraction.Services.Export
{
    /// <summary>
    /// Copia recursivamente toda a árvore de output da Fase 1 para uma pasta à escolha do utilizador.
    /// </summary>
    public static class Phase1OutputFolderExporter
    {
        public sealed class ExportResult
        {
            public string DestinationRoot { get; set; } = string.Empty;
            public int FileCount { get; set; }
            public int DirectoryCount { get; set; }
        }

        public static ExportResult CopyTo(string phase1OutputRoot, string destinationParentDirectory)
        {
            if (string.IsNullOrWhiteSpace(phase1OutputRoot) || !Directory.Exists(phase1OutputRoot))
            {
                throw new DirectoryNotFoundException("Pasta de output da Fase 1 não encontrada: " + phase1OutputRoot);
            }

            if (string.IsNullOrWhiteSpace(destinationParentDirectory) || !Directory.Exists(destinationParentDirectory))
            {
                throw new DirectoryNotFoundException("A pasta de destino não existe ou é inválida.");
            }

            var folderName = new DirectoryInfo(phase1OutputRoot).Name;
            var destRoot = Path.Combine(destinationParentDirectory, folderName);
            for (var i = 1; Directory.Exists(destRoot); i++)
            {
                destRoot = Path.Combine(destinationParentDirectory, folderName + "_copy" + i);
            }

            CopyRecursive(phase1OutputRoot, destRoot, out var files, out var dirs);

            return new ExportResult
            {
                DestinationRoot = destRoot,
                FileCount = files,
                DirectoryCount = dirs
            };
        }

        private static void CopyRecursive(string sourceDir, string destDir, out int fileCount, out int dirCount)
        {
            fileCount = 0;
            dirCount = 0;
            Directory.CreateDirectory(destDir);
            dirCount++;

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var name = Path.GetFileName(file);
                File.Copy(file, Path.Combine(destDir, name), overwrite: true);
                fileCount++;
            }

            foreach (var sub in Directory.GetDirectories(sourceDir))
            {
                var name = Path.GetFileName(sub);
                CopyRecursive(sub, Path.Combine(destDir, name), out var fc, out var dc);
                fileCount += fc;
                dirCount += dc;
            }
        }
    }
}
