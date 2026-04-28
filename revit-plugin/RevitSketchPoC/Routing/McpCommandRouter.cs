using Autodesk.Revit.UI;
using Newtonsoft.Json;
using RevitSketchPoC.Contracts;
using RevitSketchPoC.Services;
using System;
namespace RevitSketchPoC.Routing
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

                return _sketchHandler.HandleSync(uiApp.ActiveUIDocument, request);
            }

            throw new InvalidOperationException("Unknown method: " + method);
        }
    }
}
