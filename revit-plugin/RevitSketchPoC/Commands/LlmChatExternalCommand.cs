using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using RevitSketchPoC.Services;
using RevitSketchPoC.ViewModels;
using RevitSketchPoC.Views;
using System;
using System.Reflection;

namespace RevitSketchPoC.Commands
{
    [Transaction(TransactionMode.Manual)]
    public sealed class LlmChatExternalCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, Autodesk.Revit.DB.ElementSet elements)
        {
            try
            {
                var uidoc = commandData.Application.ActiveUIDocument;
                if (uidoc == null)
                {
                    TaskDialog.Show(
                        "Assistente IA",
                        "Abre ou cria um documento Revit antes de abrir o chat (o assistente precisa de um projeto ativo).");
                    return Result.Cancelled;
                }

                var assemblyDir = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
                var settings = PluginSettingsLoader.Load(assemblyDir);
                var chat = new LlmChatService(settings);
                var vm = new LlmChatViewModel(chat, uidoc);
                var window = new LlmChatWindow(vm);
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
