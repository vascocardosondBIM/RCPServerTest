using Autodesk.Revit.UI;
using RevitSketchPoC.Chat.Commands;
using RevitSketchPoC.Chat.Services;
using RevitSketchPoC.Core.Configuration;
using RevitSketchPoC.Integration.Rpc;
using RevitSketchPoC.Integration.Routing;
using RevitSketchPoC.RevitOperations.SketchBuild;
using RevitSketchPoC.Sketch.Commands;
using RevitSketchPoC.Sketch.Services;
using RevitSketchPoC.Phase1_VectorExtraction.Commands;
using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RevitSketchPoC.App
{
    public sealed class SketchToBimApp : IExternalApplication
    {
        private TcpJsonRpcServer? _server;

        /// <summary>Used after UI closes so LLM can run off the UI thread; <see cref="Execute"/> applies walls on the Revit thread.</summary>
        internal static SketchApplyFromBackgroundHandler? ApplySketchHandler { get; private set; }

        internal static ExternalEvent? ApplySketchEvent { get; private set; }

        internal static ChatApplyOpsFromChatHandler? ChatApplyOpsHandler { get; private set; }

        internal static ExternalEvent? ChatApplyOpsEvent { get; private set; }

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                RegisterRibbon(application);

                ApplySketchHandler = new SketchApplyFromBackgroundHandler();
                ApplySketchEvent = ExternalEvent.Create(ApplySketchHandler);

                ChatApplyOpsHandler = new ChatApplyOpsFromChatHandler();
                ChatApplyOpsEvent = ExternalEvent.Create(ChatApplyOpsHandler);

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
            ChatApplyOpsEvent = null;
            ChatApplyOpsHandler = null;
            return Result.Succeeded;
        }

        private static void RegisterRibbon(UIControlledApplication app)
        {
            const string tabName = "Sketch AI PoC";
            const string panelName = "Sketch to BIM";
            try { app.CreateRibbonTab(tabName); } catch { }

            var panel = app.CreateRibbonPanel(tabName, panelName);
            var assemblyPath = Assembly.GetExecutingAssembly().Location;
            var pushButtonData = new PushButtonData(
                "SketchUploadButton",
                "Upload\nSketch",
                assemblyPath,
                typeof(SketchUploadExternalCommand).FullName);

            var chatButtonData = new PushButtonData(
                "LlmChatButton",
                "AI\nChat",
                assemblyPath,
                typeof(LlmChatExternalCommand).FullName);

            var phase1ButtonData = new PushButtonData(
                "Phase1VectorExtractionButton",
                "Fase 1\nPDF Extract",
                assemblyPath,
                typeof(Phase1VectorExtractionExternalCommand).FullName);

            var sketchButton = panel.AddItem(pushButtonData) as PushButton;
            var chatButton = panel.AddItem(chatButtonData) as PushButton;
            var phase1Button = panel.AddItem(phase1ButtonData) as PushButton;

            if (sketchButton != null)
            {
                sketchButton.LargeImage = CreateMonogramIcon("SK", Colors.SteelBlue, 32);
                sketchButton.Image = CreateMonogramIcon("SK", Colors.SteelBlue, 16);
            }

            if (chatButton != null)
            {
                chatButton.LargeImage = CreateMonogramIcon("AI", Colors.MediumSeaGreen, 32);
                chatButton.Image = CreateMonogramIcon("AI", Colors.MediumSeaGreen, 16);
            }

            if (phase1Button != null)
            {
                phase1Button.LargeImage = CreateMonogramIcon("P1", Colors.IndianRed, 32);
                phase1Button.Image = CreateMonogramIcon("P1", Colors.IndianRed, 16);
            }
        }

        private static BitmapSource CreateMonogramIcon(string text, Color color, int size)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(color), null, new Rect(0, 0, size, size), 6, 6);

                var fontSize = size <= 16 ? 8 : 14;
                var formatted = new FormattedText(
                    text,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI Semibold"),
                    fontSize,
                    Brushes.White);

                var x = (size - formatted.Width) / 2.0;
                var y = (size - formatted.Height) / 2.0;
                dc.DrawText(formatted, new Point(x, y));
            }

            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }
    }
}
