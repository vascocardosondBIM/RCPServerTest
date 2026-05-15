using RevitSketchPoC.Core.Configuration;
using RevitSketchPoC.Sketch.Contracts;
using System;
using System.Linq;
using System.IO;

namespace RevitSketchPoC.Sketch.Services
{
    /// <summary>
    /// Persists checkpoints and materializes artifacts into job-scoped folders.
    /// </summary>
    public static class JobPipelineCheckpointService
    {
        private static readonly string[] SemanticSteps = {
            JobStepNames.Walls,
            JobStepNames.Openings,
            JobStepNames.Rooms,
            JobStepNames.FloorsCeilings,
            JobStepNames.FixturesFurniture
        };

        public static JobMaterializedArtifacts CreateJobAfterSpike1(
            PdfVectorJsonRequest request,
            PluginSettings settings,
            PdfVectorJsonExtractionResult extraction)
        {
            var input = new SpikePipelineJobInput
            {
                PdfPath = request.PdfPath,
                Page = request.PdfPageNumber,
                TileSizePt = request.TileSizePt,
                RasterDpi = request.RasterDpi,
                ExecutionMode = JobExecutionMode.Auto,
                Provider = settings.LlmProvider,
                CalibrationMode = "AutoScale"
            };

            var state = JobStateStore.CreateNew(input);
            var spike1Dir = Path.Combine(state.Paths.ArtifactsDirectoryPath, JobStepNames.Spike1Preparation);
            Directory.CreateDirectory(spike1Dir);

            var raw = CopyFileTo(extraction.RawJsonPath, Path.Combine(spike1Dir, Path.GetFileName(extraction.RawJsonPath)));
            var clean = CopyFileTo(extraction.CleanJsonPath, Path.Combine(spike1Dir, Path.GetFileName(extraction.CleanJsonPath)));
            var manifest = CopyFileTo(extraction.SemanticReadyManifestPath, Path.Combine(spike1Dir, Path.GetFileName(extraction.SemanticReadyManifestPath)));
            var semantic = CopyFileTo(extraction.SemanticPixelsPath, Path.Combine(spike1Dir, Path.GetFileName(extraction.SemanticPixelsPath)));
            var tiles = CopyDirectoryTo(extraction.TilesDirectoryPath, Path.Combine(spike1Dir, Path.GetFileName(extraction.TilesDirectoryPath)));

            state.Spike1Artifacts.RawJsonPath = raw;
            state.Spike1Artifacts.CleanJsonPath = clean;
            state.Spike1Artifacts.SemanticManifestPath = manifest;
            state.Spike1Artifacts.SemanticPixelsPath = semantic;
            state.Spike1Artifacts.TilesDirectoryPath = tiles;
            state.FinalOutputs.SemanticPixelsPath = semantic;

            MarkStepDone(state, JobStepNames.Spike1Preparation);
            state.Status = JobRunStatus.Paused;
            JobStateStore.Save(state);

            return new JobMaterializedArtifacts
            {
                JobId = state.JobId,
                RawJsonPath = raw,
                CleanJsonPath = clean,
                SemanticReadyManifestPath = manifest,
                SemanticPixelsPath = semantic,
                TilesDirectoryPath = tiles,
                JobFilePath = state.Paths.JobFilePath
            };
        }

        public static void MarkSemanticStarted(string jobId, string calibrationMode, string executionMode = JobExecutionMode.Auto)
        {
            if (!JobStateStore.TryLoad(jobId, out var state) || state == null)
            {
                return;
            }

            state.Status = JobRunStatus.Running;
            state.Input.CalibrationMode = calibrationMode;
            state.Input.ExecutionMode = string.Equals(executionMode, JobExecutionMode.Guided, StringComparison.OrdinalIgnoreCase)
                ? JobExecutionMode.Guided
                : JobExecutionMode.Auto;
            JobStateStore.Save(state);
        }

        public static string? GetNextPendingSemanticStep(string jobId)
        {
            if (!JobStateStore.TryLoad(jobId, out var state) || state == null)
            {
                return null;
            }

            foreach (var stepName in SemanticSteps)
            {
                var step = EnsureStep(state, stepName);
                if (!string.Equals(step.Status, JobRunStatus.Done, StringComparison.OrdinalIgnoreCase))
                {
                    return stepName;
                }
            }

            return null;
        }

        public static bool HasPendingSemanticSteps(string jobId)
        {
            return GetNextPendingSemanticStep(jobId) != null;
        }

        public static void MarkSemanticStepStarted(string jobId, SemanticTileInferenceRequest request, string stepName)
        {
            if (!JobStateStore.TryLoad(jobId, out var state) || state == null)
            {
                return;
            }

            state.Status = JobRunStatus.Running;
            state.Input.CalibrationMode = request.CalibrationMode;
            state.Input.ExecutionMode = string.Equals(request.ExecutionMode, JobExecutionMode.Guided, StringComparison.OrdinalIgnoreCase)
                ? JobExecutionMode.Guided
                : JobExecutionMode.Auto;
            MarkStepRunning(state, stepName);
            JobStateStore.Save(state);
        }

        public static void MarkSemanticStepFinished(
            string jobId,
            SemanticTileInferenceRequest request,
            SemanticTileInferenceResult result,
            string stepName)
        {
            if (!JobStateStore.TryLoad(jobId, out var state) || state == null)
            {
                return;
            }

            MarkStepDone(state, stepName);
            var hasMoreSemanticSteps = SemanticSteps.Any(name =>
            {
                var step = EnsureStep(state, name);
                return !string.Equals(step.Status, JobRunStatus.Done, StringComparison.OrdinalIgnoreCase);
            });

            state.Status = hasMoreSemanticSteps && string.Equals(request.ExecutionMode, JobExecutionMode.Guided, StringComparison.OrdinalIgnoreCase)
                ? JobRunStatus.Paused
                : (hasMoreSemanticSteps ? JobRunStatus.Running : JobRunStatus.Done);
            state.FinalOutputs.SemanticPixelsPath = request.SemanticPixelsPath;
            state.FinalOutputs.SemanticRealWorldPath = result.RealWorldOutputPath;
            state.FinalOutputs.SemanticMetricsPath = result.MetricsOutputPath;

            if (!hasMoreSemanticSteps)
            {
                MarkStepDone(state, JobStepNames.Calibration);
                MarkStepDone(state, JobStepNames.Metrics);
            }

            var metricsStep = EnsureStep(state, JobStepNames.Metrics);
            metricsStep.Metrics.Detections = result.TotalDetections;
            metricsStep.Metrics.Matched = result.MatchedDetections;
            metricsStep.Metrics.Unmatched = result.UnmatchedDetections;
            metricsStep.Artifacts["semantic_metrics"] = result.MetricsOutputPath;
            metricsStep.Artifacts["semantic_real_world"] = result.RealWorldOutputPath;
            metricsStep.Artifacts["semantic_pixels"] = request.SemanticPixelsPath;

            JobStateStore.Save(state);
        }

        public static void ResetSemanticStepForRerun(string jobId, string stepName)
        {
            if (!JobStateStore.TryLoad(jobId, out var state) || state == null)
            {
                return;
            }

            var step = EnsureStep(state, stepName);
            step.Status = JobRunStatus.Pending;
            step.StartedAtUtc = null;
            step.EndedAtUtc = null;
            step.Error = null;
            step.Artifacts.Clear();
            step.Metrics = new SpikePipelineStepMetrics();
            state.Status = JobRunStatus.Paused;
            JobStateStore.Save(state);
        }

        public static void MarkSemanticFailed(string jobId, Exception ex)
        {
            if (!JobStateStore.TryLoad(jobId, out var state) || state == null)
            {
                return;
            }

            state.Status = JobRunStatus.Failed;
            state.LastError = new SpikePipelineErrorInfo
            {
                Code = "semantic_pipeline_error",
                Message = ex.Message,
                Details = ex.ToString(),
                AtUtc = DateTime.UtcNow
            };

            var running = state.Steps.Find(s => string.Equals(s.Status, JobRunStatus.Running, StringComparison.OrdinalIgnoreCase));
            if (running != null)
            {
                running.Status = JobRunStatus.Failed;
                running.EndedAtUtc = DateTime.UtcNow;
                running.Error = state.LastError;
            }

            JobStateStore.Save(state);
        }

        public static void MarkSemanticStopped(string jobId, string reason)
        {
            if (!JobStateStore.TryLoad(jobId, out var state) || state == null)
            {
                return;
            }

            state.Status = JobRunStatus.Cancelled;
            state.LastError = new SpikePipelineErrorInfo
            {
                Code = "semantic_pipeline_cancelled",
                Message = reason,
                AtUtc = DateTime.UtcNow
            };
            JobStateStore.Save(state);
        }

        private static void MarkStepRunning(SpikePipelineJobState state, string stepName)
        {
            var step = EnsureStep(state, stepName);
            if (!step.StartedAtUtc.HasValue)
            {
                step.StartedAtUtc = DateTime.UtcNow;
            }

            step.Status = JobRunStatus.Running;
        }

        private static void MarkStepDone(SpikePipelineJobState state, string stepName)
        {
            var step = EnsureStep(state, stepName);
            if (!step.StartedAtUtc.HasValue)
            {
                step.StartedAtUtc = DateTime.UtcNow;
            }

            step.Status = JobRunStatus.Done;
            step.EndedAtUtc = DateTime.UtcNow;
            step.Error = null;
        }

        private static SpikePipelineStepState EnsureStep(SpikePipelineJobState state, string stepName)
        {
            var step = state.Steps.Find(s => string.Equals(s.Name, stepName, StringComparison.OrdinalIgnoreCase));
            if (step != null)
            {
                return step;
            }

            step = new SpikePipelineStepState
            {
                Name = stepName,
                Order = state.Steps.Count + 1,
                Status = JobRunStatus.Pending
            };
            state.Steps.Add(step);
            return step;
        }

        private static string CopyFileTo(string sourcePath, string destinationPath)
        {
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException("Artefacto esperado não encontrado.", sourcePath);
            }

            var dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.Copy(sourcePath, destinationPath, overwrite: true);
            return destinationPath;
        }

        private static string CopyDirectoryTo(string sourceDir, string destinationDir)
        {
            if (!Directory.Exists(sourceDir))
            {
                throw new DirectoryNotFoundException("Pasta de artefacto esperada não encontrada: " + sourceDir);
            }

            Directory.CreateDirectory(destinationDir);
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var fileName = Path.GetFileName(file);
                File.Copy(file, Path.Combine(destinationDir, fileName), overwrite: true);
            }

            foreach (var child in Directory.GetDirectories(sourceDir))
            {
                var name = Path.GetFileName(child);
                CopyDirectoryTo(child, Path.Combine(destinationDir, name));
            }

            return destinationDir;
        }
    }

    public sealed class JobMaterializedArtifacts
    {
        public string JobId { get; set; } = string.Empty;
        public string JobFilePath { get; set; } = string.Empty;
        public string RawJsonPath { get; set; } = string.Empty;
        public string CleanJsonPath { get; set; } = string.Empty;
        public string SemanticReadyManifestPath { get; set; } = string.Empty;
        public string SemanticPixelsPath { get; set; } = string.Empty;
        public string TilesDirectoryPath { get; set; } = string.Empty;
    }
}
