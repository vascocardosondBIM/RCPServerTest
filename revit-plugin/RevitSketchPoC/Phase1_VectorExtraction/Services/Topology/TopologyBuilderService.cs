using Newtonsoft.Json.Linq;
using RevitSketchPoC.Phase1_VectorExtraction.Models.Topology;
using System.IO;

namespace RevitSketchPoC.Phase1_VectorExtraction.Services.Topology
{
    /// <summary>
    /// Carrega e valida topologia gerada pelo extrator Python.
    /// Extensível para pós-processamento em C# (loops fechados, graph enrichment).
    /// </summary>
    public static class TopologyBuilderService
    {
        public static TopologyDocument LoadFromFile(string topologyJsonPath)
        {
            var root = JObject.Parse(File.ReadAllText(topologyJsonPath));
            var doc = new TopologyDocument
            {
                Schema = root["schema"]?.ToString() ?? "phase1_topology.v1"
            };

            if (root["intersections"] is JArray intersections)
            {
                foreach (var item in intersections)
                {
                    if (item is not JObject obj)
                    {
                        continue;
                    }

                    doc.Intersections.Add(new IntersectionRecord
                    {
                        EntityA = obj["entity_a"]?.ToString() ?? string.Empty,
                        EntityB = obj["entity_b"]?.ToString() ?? string.Empty,
                        Point = obj["point"]?.ToObject<double[]>() ?? System.Array.Empty<double>()
                    });
                }
            }

            if (root["adjacency"] is JArray adjacency)
            {
                foreach (var item in adjacency)
                {
                    if (item is not JObject obj)
                    {
                        continue;
                    }

                    var record = new AdjacencyRecord
                    {
                        Entity = obj["entity"]?.ToString() ?? string.Empty
                    };
                    if (obj["connected_to"] is JArray connected)
                    {
                        foreach (var c in connected)
                        {
                            var id = c?.ToString();
                            if (!string.IsNullOrWhiteSpace(id))
                            {
                                record.ConnectedTo.Add(id);
                            }
                        }
                    }

                    doc.Adjacency.Add(record);
                }
            }

            return doc;
        }
    }
}
