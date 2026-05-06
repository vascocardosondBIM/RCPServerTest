using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using RevitSketchPoC.Core.Configuration;
using RevitSketchPoC.Sketch.Services;
using RevitSketchPoC.Spike1.Views;
using System;
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
                window.ViewModel.GenerateRequested += (_, request) =>
                {
                    window.ViewModel.IsBusy = true;
                    window.ViewModel.AppendStatus("A gerar JSON vetorial do PDF (Spike 1)...");

                    _ = Task.Run(() =>
                    {
                        try
                        {
                            var result = PdfVectorJsonExtractionService.Extract(
                                request.PdfPath,
                                request.PdfPageNumber,
                                request.TileSizePt,
                                request.RasterDpi);
                            window.Dispatcher.Invoke(() =>
                            {
                                window.ViewModel.SetGeneratedJson(
                                    result.CleanJsonPath,
                                    result.SemanticReadyManifestPath,
                                    result.SemanticPixelsPath,
                                    result.TilesDirectoryPath,
                                    result.CleanJsonPreview);
                                window.ViewModel.AppendStatus(
                                    "Parâmetros: tile_size_pt=" + request.TileSizePt + ", raster_dpi=" + request.RasterDpi);
                                window.ViewModel.AppendStatus("JSON RAW: " + result.RawJsonPath);
                                window.ViewModel.AppendStatus("JSON CLEAN: " + result.CleanJsonPath);
                                window.ViewModel.AppendStatus("MANIFEST (semantic-ready): " + result.SemanticReadyManifestPath);
                                window.ViewModel.AppendStatus("SEMANTIC PIXELS (schema fixo): " + result.SemanticPixelsPath);
                                window.ViewModel.AppendStatus("TILES DIR: " + result.TilesDirectoryPath);
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
                    window.ViewModel.AppendStatus("Spike 2 — A inferir semântica por tile no LLM...");
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var result = await SemanticTileInferenceService.RunAsync(
                                settings,
                                request.CleanJsonPath,
                                request.SemanticReadyManifestPath,
                                request.SemanticPixelsPath,
                                request.MaxSnapDistancePt,
                                new SemanticCalibrationOptions
                                {
                                    Mode = request.CalibrationMode,
                                    ManualScaleDenominator = request.ManualScaleDenominator,
                                    ReferenceP1XPt = request.ReferenceP1XPt,
                                    ReferenceP1YPt = request.ReferenceP1YPt,
                                    ReferenceP2XPt = request.ReferenceP2XPt,
                                    ReferenceP2YPt = request.ReferenceP2YPt,
                                    ReferenceDistanceMeters = request.ReferenceDistanceMeters
                                }).ConfigureAwait(false);

                            window.Dispatcher.Invoke(() =>
                            {
                                window.ViewModel.AppendStatus(
                                    "Spike 2 concluído. tiles=" + result.TilesProcessed +
                                    ", detections=" + result.TotalDetections +
                                    ", snapped=" + result.MatchedDetections +
                                    ", unmatched=" + result.UnmatchedDetections + ".");
                                window.ViewModel.AppendStatus("SEMANTIC PIXELS atualizado: " + request.SemanticPixelsPath);
                                window.ViewModel.AppendStatus(
                                    "Calibração: " + result.CalibrationMethod +
                                    ". Saída real-world: " + result.RealWorldOutputPath);
                                window.ViewModel.AppendStatus(
                                    "Métricas: precision=" + result.MatchPrecision.ToString("0.000") +
                                    ", unmatched_rate=" + result.UnmatchedRate.ToString("0.000") +
                                    ", calibration_error_pct=" +
                                    (result.CalibrationErrorPercent.HasValue
                                        ? result.CalibrationErrorPercent.Value.ToString("0.###")
                                        : "n/a"));
                                window.ViewModel.AppendStatus("METRICS: " + result.MetricsOutputPath);
                                window.ViewModel.IsBusy = false;
                            });
                        }
                        catch (Exception ex)
                        {
                            window.Dispatcher.Invoke(() =>
                            {
                                window.ViewModel.AppendStatus("Falha no Spike 2: " + ex.Message);
                                window.ViewModel.IsBusy = false;
                            });
                        }
                    });
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
