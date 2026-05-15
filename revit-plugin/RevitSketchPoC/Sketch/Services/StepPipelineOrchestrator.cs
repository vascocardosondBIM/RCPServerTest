using RevitSketchPoC.Core.Configuration;
using RevitSketchPoC.Sketch.Contracts;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RevitSketchPoC.Sketch.Services
{
    /// <summary>
    /// Coordinates semantic step execution in auto/guided modes and updates job checkpoints.
    /// </summary>
    public static class StepPipelineOrchestrator
    {
        public static async Task<StepPipelineRunResult> RunAutoAsync(
            PluginSettings settings,
            SemanticTileInferenceRequest request,
            SemanticCalibrationOptions calibrationOptions,
            Action<StepPipelineProgress>? progressCallback = null)
        {
            ValidateRequest(request);
            var summaries = new List<StepPipelineStepSummary>();
            SemanticTileInferenceResult? last = null;

            if (!string.IsNullOrWhiteSpace(request.JobId))
            {
                JobPipelineCheckpointService.MarkSemanticStarted(request.JobId, request.CalibrationMode, JobExecutionMode.Auto);
            }

            foreach (var stepName in SemanticTileInferenceService.SemanticStepOrder)
            {
                progressCallback?.Invoke(StepPipelineProgress.Started(stepName));
                last = await ExecuteStepAsync(
                    settings,
                    request,
                    calibrationOptions,
                    stepName,
                    progressCallback).ConfigureAwait(false);
                summaries.Add(new StepPipelineStepSummary(last.StepName, last.StepDetections, last.TotalDetections));
                progressCallback?.Invoke(StepPipelineProgress.Finished(last.StepName, last.StepDetections, last.TotalDetections));
            }

            return new StepPipelineRunResult
            {
                IsGuided = false,
                ExecutedAnyStep = summaries.Count > 0,
                HasMoreSteps = false,
                LastResult = last,
                StepSummaries = summaries
            };
        }

        public static async Task<StepPipelineRunResult> RunGuidedNextAsync(
            PluginSettings settings,
            SemanticTileInferenceRequest request,
            SemanticCalibrationOptions calibrationOptions,
            Action<StepPipelineProgress>? progressCallback = null)
        {
            ValidateRequest(request);
            if (string.IsNullOrWhiteSpace(request.JobId))
            {
                throw new InvalidOperationException("Modo guiado requer JobId válido.");
            }

            JobPipelineCheckpointService.MarkSemanticStarted(request.JobId, request.CalibrationMode, JobExecutionMode.Guided);
            var nextStep = JobPipelineCheckpointService.GetNextPendingSemanticStep(request.JobId);
            if (string.IsNullOrWhiteSpace(nextStep))
            {
                return new StepPipelineRunResult
                {
                    IsGuided = true,
                    ExecutedAnyStep = false,
                    HasMoreSteps = false
                };
            }

            progressCallback?.Invoke(StepPipelineProgress.Started(nextStep!));
            var result = await ExecuteStepAsync(
                settings,
                request,
                calibrationOptions,
                nextStep!,
                progressCallback).ConfigureAwait(false);
            progressCallback?.Invoke(StepPipelineProgress.Finished(result.StepName, result.StepDetections, result.TotalDetections));
            var hasMore = JobPipelineCheckpointService.HasPendingSemanticSteps(request.JobId);
            return new StepPipelineRunResult
            {
                IsGuided = true,
                ExecutedAnyStep = true,
                HasMoreSteps = hasMore,
                LastResult = result,
                StepSummaries = new List<StepPipelineStepSummary>
                {
                    new StepPipelineStepSummary(result.StepName, result.StepDetections, result.TotalDetections)
                }
            };
        }

        public static async Task<StepPipelineRunResult> RerunStepAsync(
            PluginSettings settings,
            SemanticTileInferenceRequest request,
            SemanticCalibrationOptions calibrationOptions,
            string stepName)
        {
            ValidateRequest(request);
            if (string.IsNullOrWhiteSpace(request.JobId))
            {
                throw new InvalidOperationException("Rerun de step requer JobId válido.");
            }

            if (string.IsNullOrWhiteSpace(stepName))
            {
                throw new InvalidOperationException("Step inválido para rerun.");
            }

            JobPipelineCheckpointService.ResetSemanticStepForRerun(request.JobId, stepName);
            JobPipelineCheckpointService.MarkSemanticStarted(request.JobId, request.CalibrationMode, request.ExecutionMode);
            var result = await ExecuteStepAsync(settings, request, calibrationOptions, stepName, progressCallback: null).ConfigureAwait(false);

            return new StepPipelineRunResult
            {
                IsGuided = string.Equals(request.ExecutionMode, JobExecutionMode.Guided, StringComparison.OrdinalIgnoreCase),
                ExecutedAnyStep = true,
                HasMoreSteps = !string.IsNullOrWhiteSpace(JobPipelineCheckpointService.GetNextPendingSemanticStep(request.JobId)),
                LastResult = result,
                StepSummaries = new List<StepPipelineStepSummary>
                {
                    new StepPipelineStepSummary(result.StepName, result.StepDetections, result.TotalDetections)
                }
            };
        }

        private static async Task<SemanticTileInferenceResult> ExecuteStepAsync(
            PluginSettings settings,
            SemanticTileInferenceRequest request,
            SemanticCalibrationOptions calibrationOptions,
            string stepName,
            Action<StepPipelineProgress>? progressCallback)
        {
            if (!string.IsNullOrWhiteSpace(request.JobId))
            {
                JobPipelineCheckpointService.MarkSemanticStepStarted(request.JobId, request, stepName);
            }

            var result = await SemanticTileInferenceService.RunStepAsync(
                settings,
                request.CleanJsonPath,
                request.SemanticReadyManifestPath,
                request.SemanticPixelsPath,
                stepName,
                request.MaxSnapDistancePt,
                calibrationOptions,
                tileProgress =>
                {
                    progressCallback?.Invoke(StepPipelineProgress.Tile(
                        stepName,
                        tileProgress.TileId,
                        tileProgress.Status,
                        tileProgress.TilesProcessed,
                        tileProgress.TotalTiles));
                }).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(request.JobId))
            {
                JobPipelineCheckpointService.MarkSemanticStepFinished(request.JobId, request, result, stepName);
            }

            return result;
        }

        private static void ValidateRequest(SemanticTileInferenceRequest request)
        {
            if (request == null)
            {
                throw new InvalidOperationException("Request semântico não pode ser nulo.");
            }

            if (string.IsNullOrWhiteSpace(request.CleanJsonPath))
            {
                throw new InvalidOperationException("CleanJsonPath não pode ser vazio.");
            }

            if (string.IsNullOrWhiteSpace(request.SemanticReadyManifestPath))
            {
                throw new InvalidOperationException("SemanticReadyManifestPath não pode ser vazio.");
            }

            if (string.IsNullOrWhiteSpace(request.SemanticPixelsPath))
            {
                throw new InvalidOperationException("SemanticPixelsPath não pode ser vazio.");
            }
        }
    }

    public sealed class StepPipelineRunResult
    {
        public bool IsGuided { get; set; }
        public bool ExecutedAnyStep { get; set; }
        public bool HasMoreSteps { get; set; }
        public SemanticTileInferenceResult? LastResult { get; set; }
        public List<StepPipelineStepSummary> StepSummaries { get; set; } = new List<StepPipelineStepSummary>();
    }

    public sealed class StepPipelineStepSummary
    {
        public StepPipelineStepSummary(string stepName, int stepDetections, int totalDetections)
        {
            StepName = stepName;
            StepDetections = stepDetections;
            TotalDetections = totalDetections;
        }

        public string StepName { get; }
        public int StepDetections { get; }
        public int TotalDetections { get; }
    }

    public sealed class StepPipelineProgress
    {
        private StepPipelineProgress(
            string stepName,
            string status,
            int? stepDetections,
            int? totalDetections,
            string? tileId = null,
            int? tilesProcessed = null,
            int? totalTiles = null)
        {
            StepName = stepName;
            Status = status;
            StepDetections = stepDetections;
            TotalDetections = totalDetections;
            TileId = tileId;
            TilesProcessed = tilesProcessed;
            TotalTiles = totalTiles;
        }

        public string StepName { get; }
        public string Status { get; }
        public int? StepDetections { get; }
        public int? TotalDetections { get; }
        public string? TileId { get; }
        public int? TilesProcessed { get; }
        public int? TotalTiles { get; }

        public static StepPipelineProgress Started(string stepName)
        {
            return new StepPipelineProgress(stepName, "started", null, null);
        }

        public static StepPipelineProgress Finished(string stepName, int stepDetections, int totalDetections)
        {
            return new StepPipelineProgress(stepName, "finished", stepDetections, totalDetections);
        }

        public static StepPipelineProgress Tile(
            string stepName,
            string tileId,
            string tileStatus,
            int tilesProcessed,
            int totalTiles)
        {
            return new StepPipelineProgress(
                stepName,
                tileStatus,
                null,
                null,
                tileId,
                tilesProcessed,
                totalTiles);
        }
    }
}
