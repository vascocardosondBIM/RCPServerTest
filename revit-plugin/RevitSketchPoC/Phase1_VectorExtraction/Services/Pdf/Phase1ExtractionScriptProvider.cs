using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace RevitSketchPoC.Phase1_VectorExtraction.Services.Pdf
{
    public static class Phase1ExtractionScriptProvider
    {
        private const string EmbeddedScriptName = "RevitSketchPoC.Phase1_VectorExtraction.Scripts.phase1_extract.py";

        public static string EnsureScriptOnDisk(string workDir)
        {
            Directory.CreateDirectory(workDir);
            var scriptPath = Path.Combine(workDir, "phase1_extract.py");
            var script = LoadScript();
            File.WriteAllText(scriptPath, script, Encoding.UTF8);
            return scriptPath;
        }

        private static string LoadScript()
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream(EmbeddedScriptName);
            if (stream != null)
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }

            var baseDir = Path.GetDirectoryName(assembly.Location) ?? AppDomain.CurrentDomain.BaseDirectory;
            var sibling = Path.Combine(baseDir, "Phase1_VectorExtraction", "Scripts", "phase1_extract.py");
            if (!File.Exists(sibling))
            {
                sibling = Path.Combine(baseDir, "Scripts", "phase1_extract.py");
            }
            if (File.Exists(sibling))
            {
                return File.ReadAllText(sibling, Encoding.UTF8);
            }

            throw new InvalidOperationException(
                "Script phase1_extract.py não encontrado (embedded resource ou pasta Scripts).");
        }
    }
}
