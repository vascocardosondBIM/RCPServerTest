using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Newtonsoft.Json.Linq;
using RevitSketchPoC.Core.Geometry;
using RevitSketchPoC.RevitOperations.Shared;
using System;
using System.Globalization;
using System.Text;

namespace RevitSketchPoC.RevitOperations.CreateElements
{
    /// <summary>JSON <c>create_beam</c> — structural framing <see cref="FamilyInstance"/> along a plan line.</summary>
    public static class RevitBeamCreationOps
    {
        public static void RunCreateBeamJsonOp(Document doc, JObject op, StringBuilder log)
        {
            var typeName = op["beamTypeName"]?.ToString()?.Trim()
                           ?? op["structuralFramingTypeName"]?.ToString()?.Trim();
            if (string.IsNullOrEmpty(typeName))
            {
                throw new InvalidOperationException(
                    "create_beam requires beamTypeName (match namedTypesForRevitOps.structuralFramingTypeNames).");
            }

            if (!RevitWallCreationOps.TryReadPlanSegmentMetersFromJson(op, out var x0, out var y0, out var x1, out var y1))
            {
                throw new InvalidOperationException(
                    "create_beam requires startX/startY and endX/endY (metres), or start/end objects with x,y (same as create_wall).");
            }

            var levelName = op["levelName"]?.ToString();
            var level = RevitWallCreationOps.ResolveLevel(doc, string.IsNullOrWhiteSpace(levelName) ? null : levelName, log);

            var lenM = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
            if (lenM < PlanGeometryRules.MinWallSegmentLengthMeters)
            {
                throw new InvalidOperationException(
                    "create_beam: span too short (< " + PlanGeometryRules.MinWallSegmentLengthMeters.ToString("0.##", CultureInfo.InvariantCulture) + " m).");
            }

            var symbol = RevitFamilySymbolByName.ResolveInCategory(
                doc,
                BuiltInCategory.OST_StructuralFraming,
                typeName);
            if (symbol == null)
            {
                throw new InvalidOperationException(
                    "create_beam: no structural framing FamilySymbol matches \"" + typeName +
                    "\" (see namedTypesForRevitOps.structuralFramingTypeNames).");
            }

            if (!symbol.IsActive)
            {
                symbol.Activate();
                doc.Regenerate();
            }

            var baseZ = level.Elevation;
            var zOffM = ReadOptionalDouble(op["zOffsetMeters"]);
            if (zOffM.HasValue)
            {
                baseZ += RevitWallCreationOps.MetersToFeet(zOffM.Value);
            }

            var p0 = new XYZ(
                RevitWallCreationOps.MetersToFeet(x0),
                RevitWallCreationOps.MetersToFeet(y0),
                baseZ);
            var p1 = new XYZ(
                RevitWallCreationOps.MetersToFeet(x1),
                RevitWallCreationOps.MetersToFeet(y1),
                baseZ);

            if (p0.DistanceTo(p1) < 1e-5)
            {
                throw new InvalidOperationException("create_beam: start and end are coincident.");
            }

            var line = Line.CreateBound(p0, p1);

            FamilyInstance beam;
            try
            {
                beam = doc.Create.NewFamilyInstance(line, symbol, level, StructuralType.Beam);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("create_beam: " + ex.Message);
            }

            var name = op["name"]?.ToString()?.Trim();
            if (!string.IsNullOrEmpty(name))
            {
                try
                {
                    var p = beam.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                    if (p != null && !p.IsReadOnly)
                    {
                        p.Set(name);
                    }
                }
                catch
                {
                    // optional
                }
            }

            log.AppendLine(
                "create_beam id=" + beam.Id + " type=\"" + symbol.Family.Name + " : " + symbol.Name + "\" span~=" +
                Math.Round(lenM, 3).ToString(CultureInfo.InvariantCulture) + "m");
        }

        private static double? ReadOptionalDouble(JToken? t)
        {
            if (t == null || t.Type == JTokenType.Null)
            {
                return null;
            }

            try
            {
                return t.Value<double>();
            }
            catch
            {
                return null;
            }
        }
    }
}
