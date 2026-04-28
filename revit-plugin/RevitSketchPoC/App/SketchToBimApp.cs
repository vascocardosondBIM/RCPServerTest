using Autodesk.Revit.UI;
using RevitSketchPoC.Commands;
using RevitSketchPoC.Routing;
using RevitSketchPoC.Rpc;
using RevitSketchPoC.Services;
using System;
using System.IO;
using System.Reflection;

namespace RevitSketchPoC.App
{
    public sealed class SketchToBimApp : IExternalApplication
    {
        private TcpJsonRpcServer? _server;

        /// <summary>Used after UI closes so LLM can run off the UI thread; <see cref="Execute"/> applies walls on the Revit thread.</summary>
        internal static SketchApplyFromBackgroundHandler? ApplySketchHandler { get; private set; }

        internal static ExternalEvent? ApplySketchEvent { get; private set; }

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                RegisterRibbon(application);

                ApplySketchHandler = new SketchApplyFromBackgroundHandler();
                ApplySketchEvent = ExternalEvent.Create(ApplySketchHandler);

                var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
                var settings = PluginSettingsLoader.Load(assemblyDir);
                var sketchHandler = new SketchToBimCommandHandler(
                    SketchInterpreterFactory.Create(settings),
                    new RevitModelBuilder(settings));
                var router = new McpCommandRouter(sketchHandler);
                var dispatcher = new RevitExternalEventDispatcher(router);
                var externalEvent = ExternalEvent.Create(dispatcher);

                _server = new TcpJsonRpcServer(settings.TcpPort, dispatcher, externalEvent);
                _server.Start();

                return Result.Succeeded;
            }
            catch
            {
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            _server?.Dispose();
            _server = null;
            ApplySketchEvent = null;
            ApplySketchHandler = null;
            return Result.Succeeded;
        }

        private static void RegisterRibbon(UIControlledApplication app)
        {
            const string tabName = "Sketch AI PoC";
            const string panelName = "Sketch to BIM";
            try { app.CreateRibbonTab(tabName); } catch { }

            var panel = app.CreateRibbonPanel(tabName, panelName);
            var assemblyPath = Assembly.GetExecutingAssembly().Location;
            var pushButton = new PushButtonData(
                "SketchUploadButton",
                "Upload Sketch",
                assemblyPath,
                typeof(SketchUploadExternalCommand).FullName);

            panel.AddItem(pushButton);
        }
    }
}
