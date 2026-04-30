using Autodesk.Revit.UI;
using RevitSketchPoC.RevitOperations.SketchBuild;
using RevitSketchPoC.Sketch.Contracts;
using RevitSketchPoC.Sketch.Views;
using System;
using System.Windows;
using System.Windows.Threading;

namespace RevitSketchPoC.Sketch.Services
{
    /// <summary>
    /// Applies a sketch interpretation on the Revit main thread after LLM work finished on a background thread.
    /// </summary>
    public sealed class SketchApplyFromBackgroundHandler : IExternalEventHandler
    {
        private readonly object _gate = new object();
        private string? _errorMessage;
        private UIDocument? _uiDocument;
        private SketchToBimRequest? _request;
        private SketchInterpretation? _interpretation;
        private RevitModelBuilder? _builder;
        private SketchUploadWindow? _ownerWindow;

        public void PrepareSuccess(
            UIDocument uiDocument,
            SketchToBimRequest request,
            SketchInterpretation interpretation,
            RevitModelBuilder builder,
            SketchUploadWindow? ownerWindow)
        {
            lock (_gate)
            {
                _errorMessage = null;
                _uiDocument = uiDocument;
                _request = request;
                _interpretation = interpretation;
                _builder = builder;
                _ownerWindow = ownerWindow;
            }
        }

        public void PrepareError(string message, SketchUploadWindow? ownerWindow)
        {
            lock (_gate)
            {
                _errorMessage = message;
                _uiDocument = null;
                _request = null;
                _interpretation = null;
                _builder = null;
                _ownerWindow = ownerWindow;
            }
        }

        public void Execute(UIApplication app)
        {
            string? error;
            UIDocument? uiDocument;
            SketchToBimRequest? request;
            SketchInterpretation? interpretation;
            RevitModelBuilder? builder;
            SketchUploadWindow? ownerWindow;

            lock (_gate)
            {
                error = _errorMessage;
                uiDocument = _uiDocument;
                request = _request;
                interpretation = _interpretation;
                builder = _builder;
                ownerWindow = _ownerWindow;
                _errorMessage = null;
                _uiDocument = null;
                _request = null;
                _interpretation = null;
                _builder = null;
                _ownerWindow = null;
            }

            if (!string.IsNullOrEmpty(error))
            {
                FinishUi(ownerWindow, w =>
                {
                    w.ViewModel.AppendStatus("Falhou: " + error);
                    w.ViewModel.IsBusy = false;
                    w.Close();
                }, () =>
                {
                    MessageBox.Show(error, "Sketch to BIM â€” Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                });
                return;
            }

            if (uiDocument == null || request == null || interpretation == null || builder == null)
            {
                return;
            }

            try
            {
                var result = builder.Build(uiDocument, request, interpretation);
                var summary = "ConcluÃ­do. Paredes: " + result.WallsCreated +
                              ", Quartos: " + result.RoomsCreated +
                              ", Portas: " + result.DoorsCreated + ".";
                FinishUi(ownerWindow, w =>
                {
                    w.ViewModel.AppendStatus(summary);
                    w.ViewModel.IsBusy = false;
                    w.Close();
                }, () =>
                {
                    MessageBox.Show(summary, "Sketch to BIM â€” Done", MessageBoxButton.OK, MessageBoxImage.Information);
                });
            }
            catch (Exception ex)
            {
                FinishUi(ownerWindow, w =>
                {
                    w.ViewModel.AppendStatus("Falhou ao criar no Revit: " + ex.Message);
                    w.ViewModel.IsBusy = false;
                    w.Close();
                }, () =>
                {
                    MessageBox.Show(ex.Message, "Sketch to BIM â€” Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
        }

        private static void FinishUi(SketchUploadWindow? ownerWindow, Action<SketchUploadWindow> withWindow, Action withoutWindow)
        {
            if (ownerWindow != null)
            {
                if (ownerWindow.Dispatcher.CheckAccess())
                {
                    withWindow(ownerWindow);
                }
                else
                {
                    ownerWindow.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => withWindow(ownerWindow)));
                }
            }
            else
            {
                withoutWindow();
            }
        }

        public string GetName()
        {
            return "Sketch to BIM â€” apply from background";
        }
    }
}
