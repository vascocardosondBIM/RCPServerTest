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

        [JsonProperty("floorTypeName")]
        public string? FloorTypeName { get; set; }

        [JsonProperty("autoCreateRooms")]
        public bool AutoCreateRooms { get; set; } = true;

        [JsonProperty("autoCreateDoors")]
        public bool AutoCreateDoors { get; set; } = true;

        [JsonProperty("autoCreateWindows")]
        public bool AutoCreateWindows { get; set; } = true;

        [JsonProperty("autoCreateFloors")]
        public bool AutoCreateFloors { get; set; } = true;

        [JsonProperty("showPreviewUi")]
        public bool ShowPreviewUi { get; set; } = true;

        [JsonProperty("pdfPageNumber")]
        public int PdfPageNumber { get; set; } = 1;
    }

    public sealed class PdfVectorJsonRequest
    {
        public string PdfPath { get; set; } = string.Empty;
        public int PdfPageNumber { get; set; } = 1;
        public int TileSizePt { get; set; } = 256;
        public int RasterDpi { get; set; } = 300;
    }

    public sealed class SemanticTileInferenceRequest
    {
        public string JobId { get; set; } = string.Empty;
        public string ExecutionMode { get; set; } = JobExecutionMode.Auto;
        public string CleanJsonPath { get; set; } = string.Empty;
        public string SemanticReadyManifestPath { get; set; } = string.Empty;
        public string SemanticPixelsPath { get; set; } = string.Empty;
        public double MaxSnapDistancePt { get; set; } = 6.0;
        public string CalibrationMode { get; set; } = "AutoScale";
        public int ManualScaleDenominator { get; set; } = 100;
        public double ReferenceP1XPt { get; set; }
        public double ReferenceP1YPt { get; set; }
        public double ReferenceP2XPt { get; set; }
        public double ReferenceP2YPt { get; set; }
        public double ReferenceDistanceMeters { get; set; }
    }

    public sealed class SketchInterpretation
    {
        [JsonProperty("walls")]
        public List<WallSegment> Walls { get; set; } = new List<WallSegment>();

        [JsonProperty("rooms")]
        public List<RoomRegion> Rooms { get; set; } = new List<RoomRegion>();

        [JsonProperty("doors")]
        public List<DoorPlacement> Doors { get; set; } = new List<DoorPlacement>();

        [JsonProperty("windows")]
        public List<WindowPlacement> Windows { get; set; } = new List<WindowPlacement>();

        [JsonProperty("floors")]
        public List<FloorBoundary> Floors { get; set; } = new List<FloorBoundary>();

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

        /// <summary>Optional Revit door type name (matches project door family symbol).</summary>
        [JsonProperty("doorTypeName")]
        public string? DoorTypeName { get; set; }
    }

    public sealed class WindowPlacement
    {
        [JsonProperty("location")]
        public Point2D Location { get; set; } = new Point2D();

        [JsonProperty("windowTypeName")]
        public string? WindowTypeName { get; set; }
    }

    public sealed class FloorBoundary
    {
        [JsonProperty("name")]
        public string? Name { get; set; }

        [JsonProperty("boundary")]
        public List<Point2D> Boundary { get; set; } = new List<Point2D>();
    }

    public sealed class BuildResult
    {
        public bool Ok { get; set; }
        public int WallsCreated { get; set; }
        public int RoomsCreated { get; set; }
        public int DoorsCreated { get; set; }
        public int WindowsCreated { get; set; }
        public int FloorsCreated { get; set; }
        public string? Notes { get; set; }
    }
}
