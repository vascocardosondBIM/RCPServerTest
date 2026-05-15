using Newtonsoft.Json.Linq;
using RevitSketchPoC.Phase1_VectorExtraction.Configuration;
using System;
using System.IO;

namespace RevitSketchPoC.Phase1_VectorExtraction.Services
{
    /// <summary>
    /// Caminhos absolutos resolvidos a partir de <c>phase1_index.v1</c> no output root.
    /// </summary>
    public sealed class Phase1ResolvedArtifacts
    {
        public string IndexPath { get; set; } = string.Empty;
        public string RawJsonPath { get; set; } = string.Empty;
        public string CleanJsonPath { get; set; } = string.Empty;
        public string ProjectJsonPath { get; set; } = string.Empty;
        public string SemanticReadyManifestPath { get; set; } = string.Empty;
        public string SemanticPixelsPath { get; set; } = string.Empty;
        public string TopologyIntersectionsPath { get; set; } = string.Empty;
        public string TopologyAdjacencyPath { get; set; } = string.Empty;
    }

    public static class Phase1IndexResolver
    {
        public static Phase1ResolvedArtifacts Resolve(string outputRoot)
        {
            if (string.IsNullOrWhiteSpace(outputRoot) || !Directory.Exists(outputRoot))
            {
                throw new DirectoryNotFoundException("Pasta de output da Fase 1 inválida: " + outputRoot);
            }

            var indexPath = Path.Combine(outputRoot, Phase1ArtifactLayout.IndexFileName);
            if (!File.Exists(indexPath))
            {
                throw new FileNotFoundException("phase1_index.json não encontrado.", indexPath);
            }

            var jo = JObject.Parse(File.ReadAllText(indexPath));
            if (jo["paths"] is not JObject paths)
            {
                throw new InvalidOperationException("phase1_index.json deve conter objeto \"paths\".");
            }

            string Abs(string key)
            {
                var rel = paths[key]?.ToString();
                if (string.IsNullOrWhiteSpace(rel))
                {
                    return string.Empty;
                }

                rel = rel.Replace('/', Path.DirectorySeparatorChar);
                return Path.GetFullPath(Path.Combine(outputRoot, rel));
            }

            return new Phase1ResolvedArtifacts
            {
                IndexPath = indexPath,
                RawJsonPath = Abs("raw_json"),
                CleanJsonPath = Abs("clean_json"),
                ProjectJsonPath = Abs("metadata_project"),
                SemanticReadyManifestPath = Abs("semantic_manifest"),
                SemanticPixelsPath = Abs("semantic_pixels"),
                TopologyIntersectionsPath = Abs("topology_intersections"),
                TopologyAdjacencyPath = Abs("topology_adjacency")
            };
        }
    }
}
