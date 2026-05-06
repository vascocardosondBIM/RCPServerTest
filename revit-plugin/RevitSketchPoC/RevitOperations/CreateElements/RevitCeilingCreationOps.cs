using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitSketchPoC.RevitOperations.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RevitSketchPoC.RevitOperations.CreateElements
{
    /// <summary>Ceilings from closed 2D boundaries (metres) — JSON <c>create_ceiling</c>.</summary>
    public static class RevitCeilingCreationOps
    {
        public static CeilingType ResolveCeilingType(Document doc, string? requestedName)
        {
            var types = new FilteredElementCollector(doc)
                .OfClass(typeof(CeilingType))
                .Cast<CeilingType>()
                .ToList();

            if (types.Count == 0)
            {
                throw new InvalidOperationException("No ceiling types found in the Revit model.");
            }

            if (!string.IsNullOrWhiteSpace(requestedName))
            {
                var match = types.FirstOrDefault(x => x.Name.Equals(requestedName.Trim(), StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    return match;
                }
            }

            return types[0];
        }

        public static void RunCreateCeilingJsonOp(Document doc, JObject op, StringBuilder log)
        {
            var levelName = op["levelName"]?.ToString();
            var level = RevitWallCreationOps.ResolveLevel(doc, string.IsNullOrWhiteSpace(levelName) ? null : levelName);
            var typeName = op["ceilingTypeName"]?.ToString();
            var ceilingType = ResolveCeilingType(doc, string.IsNullOrWhiteSpace(typeName) ? null : typeName);

            var boundaryArr = op["boundary"] as JArray;
            var pts = RevitOpJsonGeometry.ReadPlanBoundaryMeters(boundaryArr, 3);
            if (pts.Count < 3)
            {
                throw new InvalidOperationException(
                    "create_ceiling requires \"boundary\" as array of at least 3 points {x,y} in metres.");
            }

            var loop = RevitOpJsonGeometry.TryBuildCurveLoop(level, pts);
            if (loop == null)
            {
                throw new InvalidOperationException("create_ceiling: invalid boundary loop.");
            }

            Ceiling? ceiling;
            try
            {
                ceiling = Ceiling.Create(doc, new List<CurveLoop> { loop }, ceilingType.Id, level.Id);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("create_ceiling: " + ex.Message);
            }

            if (ceiling == null)
            {
                throw new InvalidOperationException("create_ceiling: Revit returned null.");
            }

            var label = op["name"]?.ToString();
            if (!string.IsNullOrWhiteSpace(label))
            {
                try
                {
                    ceiling.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(label);
                }
                catch
                {
                    // optional
                }
            }

            log.AppendLine("create_ceiling id=" + ceiling.Id);
        }
    }
}
