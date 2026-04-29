using Autodesk.Revit.UI;
using RevitSketchPoC.App;
using RevitSketchPoC.Sketch.Contracts;
using RevitSketchPoC.Sketch.Views;
using System;
using System.Threading.Tasks;

namespace RevitSketchPoC.Sketch.Services
{
    /// <summary>
    /// Starts LLM on a worker thread and applies geometry via <see cref="SketchApplyFromBackgroundHandler"/> on the Revit API thread.
    /// </summary>
    public static class SketchGenerationRunner
    {
        public static void Run(SketchUploadWindow window, SketchToBimCommandHandler pipeline, UIDocument uidoc, SketchToBimRequest request)
        {
            var applyEvent = SketchToBimApp.ApplySketchEvent;
            var applyHandler = SketchToBimApp.ApplySketchHandler;
            if (applyEvent == null || applyHandler == null)
            {
                Ui(window, w => w.ViewModel.AppendStatus("Erro: o plugin nÃ£o expÃ´s o ExternalEvent. Reinicia o Revit."));
                return;
            }

            window.ViewModel.IsBusy = true;
            window.ViewModel.ClearStatus();
            window.ViewModel.AppendStatus("Passo 1/3 â€” A preparar pedido (imagem + instruÃ§Ãµes).");
            window.ViewModel.AppendStatus("Passo 2/3 â€” A enviar ao Ollama em segundo plano (o Revit deve continuar a responder; pode demorar).");

            var builder = pipeline.Builder;

            _ = Task.Run(() =>
            {
                try
                {
                    var interpretation = pipeline.InterpretOnlyAsync(request).ConfigureAwait(false).GetAwaiter().GetResult();
                    Ui(window, w => w.ViewModel.AppendStatus("Resposta do modelo recebida."));
                    Ui(window, w => w.ViewModel.AppendStatus("Passo 3/3 â€” A criar paredes/quartos/portas no Revit (thread principal)..."));
                    applyHandler.PrepareSuccess(uidoc, request, interpretation, builder, window);
                    applyEvent.Raise();
                }
                catch (Exception ex)
                {
                    applyHandler.PrepareError(ex.Message, window);
                    applyEvent.Raise();
                }
            });
        }

        private static void Ui(SketchUploadWindow window, Action<SketchUploadWindow> action)
        {
            if (window.Dispatcher.CheckAccess())
            {
                action(window);
            }
            else
            {
                window.Dispatcher.BeginInvoke(new Action(() => action(window)));
            }
        }
    }
}
