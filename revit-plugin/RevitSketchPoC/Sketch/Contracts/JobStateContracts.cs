using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace RevitSketchPoC.Sketch.Contracts
{
    public static class JobStepNames
    {
        public const string Spike1Preparation = "spike1_preparation";
        public const string Walls = "walls";
        public const string Openings = "doors_windows_openings";
        public const string Rooms = "rooms";
        public const string FloorsCeilings = "floors_ceilings";
        public const string FixturesFurniture = "fixtures_furniture";
        public const string Calibration = "calibration";
        public const string Metrics = "metrics";

        public static IReadOnlyList<string> OrderedDefaults { get; } = new[]
        {
            Spike1Preparation,
            Walls,
            Openings,
            Rooms,
            FloorsCeilings,
            FixturesFurniture,
            Calibration,
            Metrics
        };
    }

    public static class JobExecutionMode
    {
        public const string Auto = "auto";
        public const string Guided = "guided";
    }

    public static class JobRunStatus
    {
        public const string Pending = "pending";
        public const string Running = "running";
        public const string Paused = "paused";
        public const string Done = "done";
        public const string Failed = "failed";
        public const string Cancelled = "cancelled";
    }

    public sealed class SpikePipelineJobState
    {
        [JsonProperty("job_id")]
        public string JobId { get; set; } = string.Empty;

        [JsonProperty("status")]
        public string Status { get; set; } = JobRunStatus.Pending;

        [JsonProperty("created_at_utc")]
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        [JsonProperty("updated_at_utc")]
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

        [JsonProperty("input")]
        public SpikePipelineJobInput Input { get; set; } = new SpikePipelineJobInput();

        [JsonProperty("paths")]
        public SpikePipelineJobPaths Paths { get; set; } = new SpikePipelineJobPaths();

        [JsonProperty("spike1_artifacts")]
        public SpikePipelineSpike1Artifacts Spike1Artifacts { get; set; } = new SpikePipelineSpike1Artifacts();

        [JsonProperty("steps")]
        public List<SpikePipelineStepState> Steps { get; set; } = new List<SpikePipelineStepState>();

        [JsonProperty("final_outputs")]
        public SpikePipelineFinalOutputs FinalOutputs { get; set; } = new SpikePipelineFinalOutputs();

        [JsonProperty("last_error")]
        public SpikePipelineErrorInfo? LastError { get; set; }
    }

    public sealed class SpikePipelineJobInput
    {
        [JsonProperty("pdf_path")]
        public string PdfPath { get; set; } = string.Empty;

        [JsonProperty("page")]
        public int Page { get; set; } = 1;

        [JsonProperty("tile_size_pt")]
        public int TileSizePt { get; set; } = 256;

        [JsonProperty("raster_dpi")]
        public int RasterDpi { get; set; } = 300;

        [JsonProperty("execution_mode")]
        public string ExecutionMode { get; set; } = JobExecutionMode.Auto;

        [JsonProperty("provider")]
        public string Provider { get; set; } = "Ollama";

        [JsonProperty("calibration_mode")]
        public string CalibrationMode { get; set; } = "AutoScale";
    }

    public sealed class SpikePipelineJobPaths
    {
        [JsonProperty("jobs_root")]
        public string JobsRootPath { get; set; } = string.Empty;

        [JsonProperty("job_dir")]
        public string JobDirectoryPath { get; set; } = string.Empty;

        [JsonProperty("job_file")]
        public string JobFilePath { get; set; } = string.Empty;

        [JsonProperty("artifacts_dir")]
        public string ArtifactsDirectoryPath { get; set; } = string.Empty;

        [JsonProperty("logs_dir")]
        public string LogsDirectoryPath { get; set; } = string.Empty;
    }

    public sealed class SpikePipelineSpike1Artifacts
    {
        [JsonProperty("raw_json")]
        public string RawJsonPath { get; set; } = string.Empty;

        [JsonProperty("clean_json")]
        public string CleanJsonPath { get; set; } = string.Empty;

        [JsonProperty("semantic_manifest")]
        public string SemanticManifestPath { get; set; } = string.Empty;

        [JsonProperty("semantic_pixels")]
        public string SemanticPixelsPath { get; set; } = string.Empty;

        [JsonProperty("tiles_dir")]
        public string TilesDirectoryPath { get; set; } = string.Empty;
    }

    public sealed class SpikePipelineStepState
    {
        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("order")]
        public int Order { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; } = JobRunStatus.Pending;

        [JsonProperty("started_at_utc")]
        public DateTime? StartedAtUtc { get; set; }

        [JsonProperty("ended_at_utc")]
        public DateTime? EndedAtUtc { get; set; }

        [JsonProperty("artifacts")]
        public Dictionary<string, string> Artifacts { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        [JsonProperty("metrics")]
        public SpikePipelineStepMetrics Metrics { get; set; } = new SpikePipelineStepMetrics();

        [JsonProperty("error")]
        public SpikePipelineErrorInfo? Error { get; set; }
    }

    public sealed class SpikePipelineStepMetrics
    {
        [JsonProperty("tiles_processed")]
        public int? TilesProcessed { get; set; }

        [JsonProperty("detections")]
        public int? Detections { get; set; }

        [JsonProperty("matched")]
        public int? Matched { get; set; }

        [JsonProperty("unmatched")]
        public int? Unmatched { get; set; }

        [JsonProperty("duration_ms")]
        public long? DurationMs { get; set; }
    }

    public sealed class SpikePipelineFinalOutputs
    {
        [JsonProperty("semantic_pixels")]
        public string SemanticPixelsPath { get; set; } = string.Empty;

        [JsonProperty("semantic_real_world")]
        public string SemanticRealWorldPath { get; set; } = string.Empty;

        [JsonProperty("semantic_metrics")]
        public string SemanticMetricsPath { get; set; } = string.Empty;
    }

    public sealed class SpikePipelineErrorInfo
    {
        [JsonProperty("code")]
        public string Code { get; set; } = string.Empty;

        [JsonProperty("message")]
        public string Message { get; set; } = string.Empty;

        [JsonProperty("details")]
        public string? Details { get; set; }

        [JsonProperty("at_utc")]
        public DateTime AtUtc { get; set; } = DateTime.UtcNow;
    }
}
