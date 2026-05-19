using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using RevitSketchPoC.Core.Configuration;
using RevitSketchPoC.Phase1_VectorExtraction.Services;
using RevitSketchPoC.Phase1_VectorExtraction.Views;
using RevitSketchPoC.Sketch.Services;
using System;
using System.Reflection;
using System.Threading.Tasks;

namespace RevitSketchPoC.Phase1_VectorExtraction.Commands
{
    [Transaction(TransactionMode.Manual)]
    public sealed class Phase1VectorExtractionExternalCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, Autodesk.Revit.DB.ElementSet elements)
        {
            try
            {
                var assemblyDir = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
                var settings = PluginSettingsLoader.Load(assemblyDir);
                var window = new Phase1VectorExtractionWindow();

                window.ViewModel.ExtractRequested += (_, request) =>
                {
                    window.ViewModel.IsBusy = true;
                    window.ViewModel.AppendStatus("Fase 1 — extração modular (JSON + topologia leve + raster preview)...");

                    _ = Task.Run(() =>
                    {
                        try
                        {
                            var result = Phase1VectorExtractionOrchestrator.Extract(request);
                            window.Dispatcher.Invoke(() =>
                            {
                                window.ViewModel.SetPhase1ModularResult(
                                    result.OutputRoot,
                                    result.RawJsonPath,
                                    result.IndexJsonPath,
                                    result.CleanJsonPath,
                                    result.SemanticReadyManifestPath,
                                    result.SemanticPixelsPath,
                                    result.TilesDirectoryPath);
                                window.ViewModel.AppendStatus("Output: " + result.OutputRoot);
                                window.ViewModel.AppendStatus("INDEX: " + result.IndexJsonPath);
                                window.ViewModel.AppendStatus("RAW: " + result.RawJsonPath);
                                window.ViewModel.AppendStatus("CLEAN: " + result.CleanJsonPath);
                                window.ViewModel.IsBusy = false;

                                if (!string.IsNullOrWhiteSpace(result.PreviewPngPath) &&
                                    System.IO.File.Exists(result.PreviewPngPath))
                                {
                                    window.ViewModel.AppendStatus(
                                        "Abre «Definir zonas…» para recortar desenho vs. legendas no preview.");
                                    try
                                    {
                                        var editor = new Phase1RegionEditorWindow(result.OutputRoot)
                                        {
                                            Owner = window
                                        };
                                        editor.ShowDialog();
                                        if (editor.RegionsWereExported || editor.ColorLayersWereExported)
                                        {
                                            window.ViewModel.RefreshExtractionSummary();
                                        }
                                    }
                                    catch (Exception regionEx)
                                    {
                                        window.ViewModel.AppendStatus(
                                            "Editor de zonas: " + regionEx.Message);
                                    }
                                }
                            });
                        }
                        catch (Exception ex)
                        {
                            window.Dispatcher.Invoke(() =>
                            {
                                window.ViewModel.AppendStatus("Falha na Fase 1: " + ex.Message);
                                window.ViewModel.IsBusy = false;
                            });
                        }
                    });
                };

                window.ViewModel.RunSemanticRequested += (_, request) =>
                {
                    window.ViewModel.IsBusy = true;
                    window.ViewModel.AppendStatus("Inferência semântica por tile (LLM)...");
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
                                    "Semântica concluída. tiles=" + result.TilesProcessed +
                                    ", detections=" + result.TotalDetections +
                                    ", snapped=" + result.MatchedDetections +
                                    ", unmatched=" + result.UnmatchedDetections + ".");
                                window.ViewModel.AppendStatus("SEMANTIC PIXELS: " + request.SemanticPixelsPath);
                                window.ViewModel.AppendStatus("Real-world: " + result.RealWorldOutputPath);
                                window.ViewModel.AppendStatus("METRICS: " + result.MetricsOutputPath);
                                window.ViewModel.IsBusy = false;
                            });
                        }
                        catch (Exception ex)
                        {
                            window.Dispatcher.Invoke(() =>
                            {
                                window.ViewModel.AppendStatus("Falha na inferência semântica: " + ex.Message);
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
