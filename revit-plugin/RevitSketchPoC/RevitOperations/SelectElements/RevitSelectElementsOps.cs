using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitSketchPoC.RevitOperations.Shared;
using System;
using System.Text;

namespace RevitSketchPoC.RevitOperations.SelectElements
{
    /// <summary>Applies <c>select_elements</c> JSON ops (UI selection).</summary>
    public static class RevitSelectElementsOps
    {
        public static void Run(UIDocument uidoc, JObject op, StringBuilder log, int maxIdsPerOp)
        {
            var ids = RevitOpsElementIdList.Read(op["elementIds"], maxIdsPerOp);
            if (ids.Count == 0)
            {
                throw new InvalidOperationException("select_elements requires elementIds.");
            }

            uidoc.Selection.SetElementIds(ids);
            log.AppendLine("select_elements count=" + ids.Count);
        }
    }
}
