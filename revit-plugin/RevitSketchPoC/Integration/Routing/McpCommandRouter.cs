using Autodesk.Revit.UI;
using Newtonsoft.Json;
using RevitSketchPoC.Sketch.Contracts;
using RevitSketchPoC.Sketch.Services;
using System;
namespace RevitSketchPoC.Integration.Routing
{
    public sealed class McpCommandRouter
    {
        private readonly SketchToBimCommandHandler _sketchHandler;

        public McpCommandRouter(SketchToBimCommandHandler sketchHandler)
        {
            _sketchHandler = sketchHandler;
        }

        public object Route(UIApplication uiApp, string method, string? paramsJson)
        {
            if (uiApp.ActiveUIDocument == null)
            {
                throw new InvalidOperationException("No active Revit document.");
            }

            if (string.Equals(method, "create_house_from_sketch", StringComparison.OrdinalIgnoreCase))
            {
                var request = string.IsNullOrWhiteSpace(paramsJson)
                    ? new SketchToBimRequest()
                    : JsonConvert.DeserializeObject<SketchToBimRequest>(paramsJson) ?? new SketchToBimRequest();

                var normalizedRequest = SketchInputPreprocessor.NormalizeForLlm(request);
                return _sketchHandler.HandleSync(uiApp.ActiveUIDocument, normalizedRequest);
            }

            throw new InvalidOperationException("Unknown method: " + method);
        }
    }
}
