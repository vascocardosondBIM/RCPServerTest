using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using RevitSketchPoC.Sketch.Services;
using RevitSketchPoC.Spike1.Views;
using System;
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
                var window = new PdfSpike1Window();
                window.ViewModel.GenerateRequested += (_, request) =>
                {
                    window.ViewModel.IsBusy = true;
                    window.ViewModel.AppendStatus("A gerar JSON vetorial do PDF (Spike 1)...");

                    _ = Task.Run(() =>
                    {
                        try
                        {
                            var result = PdfVectorJsonExtractionService.Extract(request.PdfPath, request.PdfPageNumber);
                            window.Dispatcher.Invoke(() =>
                            {
                                window.ViewModel.SetGeneratedJson(result.CleanJsonPath, result.CleanJsonPreview);
                                window.ViewModel.AppendStatus("JSON RAW: " + result.RawJsonPath);
                                window.ViewModel.AppendStatus("JSON CLEAN: " + result.CleanJsonPath);
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
