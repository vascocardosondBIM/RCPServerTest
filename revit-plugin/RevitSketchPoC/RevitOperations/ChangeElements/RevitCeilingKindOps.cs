using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitSketchPoC.Core.Configuration;
using RevitSketchPoC.RevitOperations.Shared;
using System;
using System.Text;

namespace RevitSketchPoC.RevitOperations.ChangeElements
{
    /// <summary>JSON <c>change_ceiling_kind</c> — switch between false ceiling and slab-painted placement.</summary>
    public static class RevitCeilingKindOps
    {
        public static void RunChangeCeilingKindJsonOp(Document doc, JObject op, PluginSettings? settings, StringBuilder log)
        {
            var idVal = op["ceilingId"]?.Value<long?>() ?? op["elementId"]?.Value<long?>();
            if (idVal == null)
            {
                throw new InvalidOperationException("change_ceiling_kind requires ceilingId or elementId.");
            }

            if (doc.GetElement(new ElementId((long)idVal)) is not Ceiling ceiling)
            {
                throw new InvalidOperationException("change_ceiling_kind: element is not a Ceiling.");
            }

            var kind = RevitCeilingVerticalPlacement.ParseKind(op);
            var drop = RevitCeilingVerticalPlacement.ReadFalseCeilingDropMeters(op, settings);

            if (!RevitCeilingVerticalPlacement.TryApplyToExisting(doc, ceiling, kind, drop, out var msg))
            {
                throw new InvalidOperationException("change_ceiling_kind: " + msg);
            }

            log.AppendLine("change_ceiling_kind id=" + idVal + " " + msg);
        }
    }
}
