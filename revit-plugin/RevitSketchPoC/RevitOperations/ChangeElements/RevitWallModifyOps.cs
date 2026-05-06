using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitSketchPoC.RevitOperations.Shared;
using System.Collections.Generic;
using System.Text;

namespace RevitSketchPoC.RevitOperations.ChangeElements
{
    /// <summary>JSON <c>flip_wall</c>: flip facing / hand for walls by id.</summary>
    public static class RevitWallModifyOps
    {
        private const int MaxWallIds = 50;

        public static void RunFlipWallJsonOp(Document doc, JObject op, StringBuilder log)
        {
            var ids = RevitOpsElementIdList.Read(op["elementIds"], MaxWallIds);
            if (ids.Count == 0)
            {
                var single = op["elementId"];
                if (single != null && single.Type != JTokenType.Null)
                {
                    var v = single.Value<long?>() ?? single.Value<int?>();
                    if (v != null)
                    {
                        ids.Add(new ElementId((long)v));
                    }
                }
            }

            if (ids.Count == 0)
            {
                throw new System.InvalidOperationException("flip_wall requires elementIds (array) or elementId.");
            }

            foreach (var id in ids)
            {
                var el = doc.GetElement(id);
                if (el is not Wall wall)
                {
                    log.AppendLine("flip_wall: skip id=" + id + " (not a wall).");
                    continue;
                }

                try
                {
                    wall.Flip();
                    log.AppendLine("flip_wall id=" + wall.Id);
                }
                catch (System.Exception ex)
                {
                    log.AppendLine("flip_wall id=" + wall.Id + ": " + ex.Message);
                }
            }
        }
    }
}
