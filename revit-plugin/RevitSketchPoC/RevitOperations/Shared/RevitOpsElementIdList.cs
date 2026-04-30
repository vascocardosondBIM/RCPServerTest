using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;

namespace RevitSketchPoC.RevitOperations.Shared
{
    /// <summary>Parses element id arrays from JSON ops (bounded).</summary>
    public static class RevitOpsElementIdList
    {
        public static List<ElementId> Read(JToken? token, int maxIdsPerOp)
        {
            var list = new List<ElementId>();
            if (token is not JArray arr)
            {
                return list;
            }

            foreach (var t in arr.Take(maxIdsPerOp))
            {
                long? v = t.Type == JTokenType.Integer ? t.Value<long>() : null;
                if (v == null && long.TryParse(t.ToString(), out var parsed))
                {
                    v = parsed;
                }

                if (v != null)
                {
                    list.Add(new ElementId((long)v));
                }
            }

            return list;
        }
    }
}
