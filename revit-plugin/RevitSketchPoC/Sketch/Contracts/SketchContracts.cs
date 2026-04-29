using System.Collections.Generic;
using Newtonsoft.Json;

namespace RevitSketchPoC.Sketch.Contracts
{
    public sealed class SketchToBimRequest
    {
        [JsonProperty("imagePath")]
        public string? ImagePath { get; set; }

        [JsonProperty("imageBase64")]
        public string? ImageBase64 { get; set; }

        [JsonProperty("mimeType")]
        public string? MimeType { get; set; }

        [JsonProperty("prompt")]
        public string? Prompt { get; set; }

        [JsonProperty("targetLevelName")]
        public string? TargetLevelName { get; set; }

        [JsonProperty("wallTypeName")]
        public string? WallTypeName { get; set; }

        [JsonProperty("autoCreateRooms")]
        public bool AutoCreateRooms { get; set; } = true;

        [JsonProperty("autoCreateDoors")]
        public bool AutoCreateDoors { get; set; } = true;

        [JsonProperty("showPreviewUi")]
        public bool ShowPreviewUi { get; set; } = true;
    }

    public sealed class SketchInterpretation
    {
        [JsonProperty("walls")]
        public List<WallSegment> Walls { get; set; } = new List<WallSegment>();

        [JsonProperty("rooms")]
        public List<RoomRegion> Rooms { get; set; } = new List<RoomRegion>();

        [JsonProperty("doors")]
        public List<DoorPlacement> Doors { get; set; } = new List<DoorPlacement>();

        [JsonProperty("notes")]
        public string? Notes { get; set; }
    }

    public sealed class Point2D
    {
        [JsonProperty("x")]
        public double X { get; set; }

        [JsonProperty("y")]
        public double Y { get; set; }
    }

    public sealed class WallSegment
    {
        [JsonProperty("start")]
        public Point2D Start { get; set; } = new Point2D();

        [JsonProperty("end")]
        public Point2D End { get; set; } = new Point2D();

        [JsonProperty("heightMeters")]
        public double HeightMeters { get; set; } = 3.0;
    }

    public sealed class RoomRegion
    {
        [JsonProperty("name")]
        public string Name { get; set; } = "Room";

        [JsonProperty("boundary")]
        public List<Point2D> Boundary { get; set; } = new List<Point2D>();
    }

    public sealed class DoorPlacement
    {
        [JsonProperty("location")]
        public Point2D Location { get; set; } = new Point2D();
    }

    public sealed class BuildResult
    {
        public bool Ok { get; set; }
        public int WallsCreated { get; set; }
        public int RoomsCreated { get; set; }
        public int DoorsCreated { get; set; }
        public string? Notes { get; set; }
    }
}
