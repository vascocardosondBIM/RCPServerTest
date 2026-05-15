using System.Collections.Generic;

namespace RevitSketchPoC.Phase1_VectorExtraction.Models.Topology
{
    public sealed class TopologyDocument
    {
        public string Schema { get; set; } = "phase1_topology.v1";
        public List<IntersectionRecord> Intersections { get; set; } = new List<IntersectionRecord>();
        public List<AdjacencyRecord> Adjacency { get; set; } = new List<AdjacencyRecord>();
        public List<ContainmentRecord> Containment { get; set; } = new List<ContainmentRecord>();
        public List<ClosedLoopRecord> ClosedLoops { get; set; } = new List<ClosedLoopRecord>();
    }

    public sealed class IntersectionRecord
    {
        public string EntityA { get; set; } = string.Empty;
        public string EntityB { get; set; } = string.Empty;
        public double[] Point { get; set; } = System.Array.Empty<double>();
    }

    public sealed class AdjacencyRecord
    {
        public string Entity { get; set; } = string.Empty;
        public List<string> ConnectedTo { get; set; } = new List<string>();
    }

    public sealed class ContainmentRecord
    {
        public string Container { get; set; } = string.Empty;
        public List<string> Contains { get; set; } = new List<string>();
    }

    public sealed class ClosedLoopRecord
    {
        public string Id { get; set; } = string.Empty;
        public List<string> EntityIds { get; set; } = new List<string>();
        public double[] BboxPt { get; set; } = System.Array.Empty<double>();
    }
}
