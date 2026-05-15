using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using RevitSketchPoC.Core.Configuration;
using RevitSketchPoC.Sketch.Contracts;
using RevitSketchPoC.Sketch.Services;
using RevitSketchPoC.Spike1.Views;
using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

namespace RevitSketchPoC.Spike1.Commands
{
    [Transaction(TransactionMode.Manual)]
    public sealed class PdfSpike1ExternalCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, Autodesk.Revit.DB.ElementSet elements)
        {
            try
            {
                var assemblyDir = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
                var settings = PluginSettingsLoader.Load(assemblyDir);
                var window = new PdfSpike1Window();
                Spike2ProgressWindow? spike2ProgressWindow = null;
                window.ViewModel.GenerateRequested += (_, request) =>
                {
                    window.ViewModel.IsBusy = true;
                    window.ViewModel.AppendStatus("A gerar JSON vetorial do PDF (Spike 1)...");

                    _ = Task.Run(() =>
                    {
                        try
                        {
                            var extraction = PdfVectorJsonExtractionService.Extract(
                                request.PdfPath,
                                request.PdfPageNumber,
                                request.TileSizePt,
                                request.RasterDpi);

                            var job = JobPipelineCheckpointService.CreateJobAfterSpike1(request, settings, extraction);
                            var preview = File.Exists(job.CleanJsonPath)
                                ? File.ReadAllText(job.CleanJsonPath)
                                : extraction.CleanJsonPreview;
                            if (preview.Length > 30000)
                            {
                                preview = preview.Substring(0, 30000) + Environment.NewLine + Environment.NewLine +
                                          "... (preview truncado; usa \"Guardar JSON\" para obter o ficheiro completo)";
                            }
                            window.Dispatcher.Invoke(() =>
                            {
                                window.ViewModel.SetGeneratedJson(
                                    job.CleanJsonPath,
                                    job.SemanticReadyManifestPath,
                                    job.SemanticPixelsPath,
                                    job.TilesDirectoryPath,
                                    preview);
                                window.ViewModel.CurrentJobId = job.JobId;
                                window.ViewModel.AppendStatus(
                                    "Parâmetros: tile_size_pt=" + request.TileSizePt + ", raster_dpi=" + request.RasterDpi);
                                window.ViewModel.AppendStatus("JOB ID: " + job.JobId);
                                window.ViewModel.AppendStatus("JOB FILE: " + job.JobFilePath);
                                window.ViewModel.AppendStatus("JSON RAW: " + job.RawJsonPath);
                                window.ViewModel.AppendStatus("JSON CLEAN: " + job.CleanJsonPath);
                                window.ViewModel.AppendStatus("MANIFEST (semantic-ready): " + job.SemanticReadyManifestPath);
                                window.ViewModel.AppendStatus("SEMANTIC PIXELS (schema fixo): " + job.SemanticPixelsPath);
                                window.ViewModel.AppendStatus("TILES DIR: " + job.TilesDirectoryPath);
                                window.ViewModel.IsBusy = false;
                            });
                        }
                        catch (Exception ex)
                        {
                            window.Dispatcher.Invoke(() =>
                            {
                                window.ViewModel.AppendStatus("Falha na geração de JSON: " + ex.Message);
                                window.ViewModel.IsBusy = false;
                            });
                        }
                    });
                };

                window.ViewModel.RunSemanticRequested += (_, request) =>
                {
                    window.ViewModel.IsBusy = true;
                    var isGuidedMode = string.Equals(request.ExecutionMode, JobExecutionMode.Guided, StringComparison.OrdinalIgnoreCase);

                    void LogStatus(string message)
                    {
                        window.Dispatcher.Invoke(() =>
                        {
                            window.ViewModel.AppendStatus(message);
                            spike2ProgressWindow?.AppendStatus(message);
                        });
                    }

                    window.Dispatcher.Invoke(() =>
                    {
                        if (spike2ProgressWindow == null || !spike2ProgressWindow.IsVisible)
                        {
                            spike2ProgressWindow = new Spike2ProgressWindow
                            {
                                Owner = window
                            };
                            spike2ProgressWindow.Show();
                        }
                        else
                        {
                            spike2ProgressWindow.Activate();
                        }
                    });

                    LogStatus(
                        isGuidedMode
                            ? "Spike 2 (Guiado) — a executar o próximo step semântico..."
                            : "Spike 2 (Auto) — a executar todos os steps semânticos...");
                    if (!isGuidedMode)
                    {
                        LogStatus(
                            "Ordem de steps: walls -> doors_windows_openings -> rooms -> floors_ceilings -> fixtures_furniture.");
                    }

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var calibrationOptions = new SemanticCalibrationOptions
                            {
                                Mode = request.CalibrationMode,
                                ManualScaleDenominator = request.ManualScaleDenominator,
                                ReferenceP1XPt = request.ReferenceP1XPt,
                                ReferenceP1YPt = request.ReferenceP1YPt,
                                ReferenceP2XPt = request.ReferenceP2XPt,
                                ReferenceP2YPt = request.ReferenceP2YPt,
                                ReferenceDistanceMeters = request.ReferenceDistanceMeters
                            };

                            var run = isGuidedMode
                                ? await StepPipelineOrchestrator.RunGuidedNextAsync(
                                    settings,
                                    request,
                                    calibrationOptions,
                                    progress =>
                                    {
                                        if (string.Equals(progress.Status, "started", StringComparison.OrdinalIgnoreCase))
                                        {
                                            LogStatus("Step em execução: " + progress.StepName + "...");
                                            return;
                                        }

                                        if (string.Equals(progress.Status, "tile_started", StringComparison.OrdinalIgnoreCase))
                                        {
                                            var current = (progress.TilesProcessed ?? 0) + 1;
                                            var total = progress.TotalTiles ?? 0;
                                            LogStatus("Tile " + current + "/" + total + " em processamento (" + progress.StepName + ")...");
                                            return;
                                        }

                                        if (string.Equals(progress.Status, "tile_finished", StringComparison.OrdinalIgnoreCase))
                                        {
                                            return;
                                        }

                                        if (string.Equals(progress.Status, "tile_failed", StringComparison.OrdinalIgnoreCase))
                                        {
                                            var current = progress.TilesProcessed ?? 0;
                                            var total = progress.TotalTiles ?? 0;
                                            LogStatus("Tile " + current + "/" + total + " falhou e foi ignorado (" + progress.StepName + ").");
                                            return;
                                        }

                                        LogStatus(
                                            "Step concluído: " + progress.StepName +
                                            " (novos detections=" + (progress.StepDetections ?? 0) +
                                            ", total=" + (progress.TotalDetections ?? 0) + ").");
                                    }).ConfigureAwait(false)
                                : await StepPipelineOrchestrator.RunAutoAsync(
                                    settings,
                                    request,
                                    calibrationOptions,
                                    progress =>
                                    {
                                        if (string.Equals(progress.Status, "started", StringComparison.OrdinalIgnoreCase))
                                        {
                                            LogStatus("Step em execução: " + progress.StepName + "...");
                                            return;
                                        }

                                        if (string.Equals(progress.Status, "tile_started", StringComparison.OrdinalIgnoreCase))
                                        {
                                            var current = (progress.TilesProcessed ?? 0) + 1;
                                            var total = progress.TotalTiles ?? 0;
                                            LogStatus("Tile " + current + "/" + total + " em processamento (" + progress.StepName + ")...");
                                            return;
                                        }

                                        if (string.Equals(progress.Status, "tile_finished", StringComparison.OrdinalIgnoreCase))
                                        {
                                            return;
                                        }

                                        if (string.Equals(progress.Status, "tile_failed", StringComparison.OrdinalIgnoreCase))
                                        {
                                            var current = progress.TilesProcessed ?? 0;
                                            var total = progress.TotalTiles ?? 0;
                                            LogStatus("Tile " + current + "/" + total + " falhou e foi ignorado (" + progress.StepName + ").");
                                            return;
                                        }

                                        LogStatus(
                                            "Step concluído: " + progress.StepName +
                                            " (novos detections=" + (progress.StepDetections ?? 0) +
                                            ", total=" + (progress.TotalDetections ?? 0) + ").");
                                    }).ConfigureAwait(false);

                            if (!run.ExecutedAnyStep)
                            {
                                LogStatus("Modo Guiado: todos os steps já foram concluídos para este job.");
                                window.Dispatcher.Invoke(() =>
                                {
                                    window.ViewModel.IsBusy = false;
                                });
                                return;
                            }

                            var result = run.LastResult;
                            if (result == null)
                            {
                                throw new InvalidOperationException("Execução semântica finalizou sem resultado.");
                            }

                            LogStatus(
                                "Spike 2 concluído. tiles=" + result.TilesProcessed +
                                ", detections=" + result.TotalDetections +
                                ", snapped=" + result.MatchedDetections +
                                ", unmatched=" + result.UnmatchedDetections + ".");
                            LogStatus("SEMANTIC PIXELS atualizado: " + request.SemanticPixelsPath);
                            LogStatus(
                                "Calibração: " + result.CalibrationMethod +
                                ". Saída real-world: " + result.RealWorldOutputPath);
                            LogStatus(
                                "Métricas: precision=" + result.MatchPrecision.ToString("0.000") +
                                ", unmatched_rate=" + result.UnmatchedRate.ToString("0.000") +
                                ", calibration_error_pct=" +
                                (result.CalibrationErrorPercent.HasValue
                                    ? result.CalibrationErrorPercent.Value.ToString("0.###")
                                    : "n/a"));
                            LogStatus("METRICS: " + result.MetricsOutputPath);

                            if (run.IsGuided)
                            {
                                LogStatus(
                                    run.HasMoreSteps
                                        ? "Modo Guiado em pausa. Clique \"Executar Spike 2 (LLM)\" para confirmar e seguir para o próximo step."
                                        : "Modo Guiado finalizado. Todos os steps semânticos concluídos.");
                            }

                            window.Dispatcher.Invoke(() =>
                            {
                                window.ViewModel.IsBusy = false;
                            });
                        }
                        catch (Exception ex)
                        {
                            if (!string.IsNullOrWhiteSpace(request.JobId))
                            {
                                JobPipelineCheckpointService.MarkSemanticFailed(request.JobId, ex);
                            }

                            LogStatus("Falha no Spike 2: " + ex.Message);
                            window.Dispatcher.Invoke(() =>
                            {
                                window.ViewModel.IsBusy = false;
                            });
                        }
                    });
                };

                window.Closed += (_, __) =>
                {
                    if (spike2ProgressWindow != null && spike2ProgressWindow.IsVisible)
                    {
                        spike2ProgressWindow.Close();
                    }
                };

                window.Show();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
