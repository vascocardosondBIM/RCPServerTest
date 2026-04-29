using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using System;
using System.Windows.Threading;

namespace RevitSketchPoC.Chat.Services
{
    /// <summary>Applies <see cref="ChatRevitOpsExecutor"/> on the Revit API thread after chat LLM returns.</summary>
    public sealed class ChatApplyOpsFromChatHandler : IExternalEventHandler
    {
        private readonly object _gate = new object();
        private UIDocument? _uiDocument;
        private JArray? _ops;
        private Dispatcher? _uiDispatcher;
        private Action<string>? _onComplete;

        public void Prepare(
            UIDocument uiDocument,
            JArray ops,
            Dispatcher uiDispatcher,
            Action<string> onCompleteOnUiThread)
        {
            lock (_gate)
            {
                _uiDocument = uiDocument;
                _ops = ops;
                _uiDispatcher = uiDispatcher;
                _onComplete = onCompleteOnUiThread;
            }
        }

        public void Execute(UIApplication app)
        {
            UIDocument? uidoc;
            JArray? ops;
            Dispatcher? dispatcher;
            Action<string>? onComplete;

            lock (_gate)
            {
                uidoc = _uiDocument;
                ops = _ops;
                dispatcher = _uiDispatcher;
                onComplete = _onComplete;
                _uiDocument = null;
                _ops = null;
                _uiDispatcher = null;
                _onComplete = null;
            }

            if (uidoc == null || ops == null || ops.Count == 0)
            {
                return;
            }

            string summary;
            try
            {
                summary = ChatRevitOpsExecutor.Execute(uidoc, ops);
            }
            catch (Exception ex)
            {
                summary = "Falha ao executar revitOps: " + ex.Message;
            }

            if (onComplete != null && dispatcher != null)
            {
                dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => onComplete(summary)));
            }
        }

        public string GetName()
        {
            return "RevitSketchPoC â€” AI Chat revitOps";
        }
    }
}
