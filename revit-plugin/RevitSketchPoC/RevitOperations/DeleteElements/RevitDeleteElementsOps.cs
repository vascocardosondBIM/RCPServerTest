using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitSketchPoC.RevitOperations.Shared;
using System;
using System.Text;

namespace RevitSketchPoC.RevitOperations.DeleteElements
{
    /// <summary>Applies <c>delete_elements</c> JSON ops.</summary>
    public static class RevitDeleteElementsOps
    {
        public static void Run(Document doc, JObject op, StringBuilder log, int maxIdsPerOp)
        {
            var ids = RevitOpsElementIdList.Read(op["elementIds"], maxIdsPerOp);
            if (ids.Count == 0)
            {
                throw new InvalidOperationException("delete_elements requires elementIds.");
            }

            doc.Delete(ids);
            log.AppendLine("delete_elements count=" + ids.Count);
        }
    }
}
