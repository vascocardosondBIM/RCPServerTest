using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using RevitSketchPoC.Core.Configuration;
using RevitSketchPoC.Sketch.Services;
using RevitSketchPoC.Sketch.Views;
using System;
using System.Reflection;

namespace RevitSketchPoC.Sketch.Commands
{
    [Transaction(TransactionMode.Manual)]
    public sealed class SketchUploadExternalCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, Autodesk.Revit.DB.ElementSet elements)
        {
            try
            {
                var assemblyDir = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
                var settings = PluginSettingsLoader.Load(assemblyDir);
                var pipeline = new SketchToBimCommandHandler(
                    SketchInterpreterFactory.Create(settings),
                    new RevitModelBuilder(settings));

                var window = new SketchUploadWindow();
                window.ViewModel.RunRequested += (_, request) =>
                {
                    var uiDoc = commandData.Application.ActiveUIDocument;
                    if (uiDoc == null)
                    {
                        window.ViewModel.AppendStatus("Erro: nÃ£o hÃ¡ documento ativo.");
                        return;
                    }

                    SketchGenerationRunner.Run(window, pipeline, uiDoc, request);
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
