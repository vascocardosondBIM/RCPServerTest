using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json.Linq;
using RevitSketchPoC.Core.Geometry;
using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace RevitSketchPoC.RevitOperations.CreateElements
{
    /// <summary>
    /// JSON <c>create_guardrail</c> / <c>create_railing</c> — corrimão / guarda-corpo ao longo de um traçado recto em planta (<see cref="Railing"/>).
    /// Espaçamento e altura dos balaustres vêm do <see cref="RailingType"/> escolhido no Revit.
    /// </summary>
    public static class RevitGuardrailCreationOps
    {
        public static void RunCreateGuardrailJsonOp(Document doc, JObject op, StringBuilder log)
        {
            var typeName = op["railingTypeName"]?.ToString()?.Trim()
                           ?? op["guardrailTypeName"]?.ToString()?.Trim();
            if (string.IsNullOrEmpty(typeName))
            {
                throw new InvalidOperationException(
                    "create_guardrail requires railingTypeName (match namedTypesForRevitOps.railingTypeNames).");
            }

            if (!RevitWallCreationOps.TryReadPlanSegmentMetersFromJson(op, out var x0, out var y0, out var x1, out var y1))
            {
                throw new InvalidOperationException(
                    "create_guardrail requires startX/startY and endX/endY (metres), or start/end objects with x,y (same as create_wall).");
            }

            var levelName = op["levelName"]?.ToString();
            var level = RevitWallCreationOps.ResolveLevel(doc, string.IsNullOrWhiteSpace(levelName) ? null : levelName, log);

            var lenM = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
            if (lenM < PlanGeometryRules.MinWallSegmentLengthMeters)
            {
                throw new InvalidOperationException(
                    "create_guardrail: path too short (< " +
                    PlanGeometryRules.MinWallSegmentLengthMeters.ToString("0.##", CultureInfo.InvariantCulture) + " m).");
            }

            var railingType = ResolveRailingType(doc, typeName, log);

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
                throw new InvalidOperationException("create_guardrail: start and end are coincident.");
            }

            var loop = new CurveLoop();
            loop.Append(Line.CreateBound(p0, p1));

            Railing railing;
            try
            {
                railing = Railing.Create(doc, loop, railingType.Id, level.Id);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("create_guardrail: " + ex.Message);
            }

            var name = op["name"]?.ToString()?.Trim();
            if (!string.IsNullOrEmpty(name))
            {
                try
                {
                    var p = railing.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
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
                "create_guardrail id=" + railing.Id + " type=\"" + railingType.Name + "\" length~=" +
                Math.Round(lenM, 3).ToString(CultureInfo.InvariantCulture) + "m");
        }

        /// <summary>Resolves a <see cref="RailingType"/> by name (exact, partial, or first in project) — shared by <c>create_guardrail</c> and <c>create_stairs</c>.</summary>
        public static RailingType ResolveRailingType(Document doc, string? railingTypeName, StringBuilder? log)
        {
            var types = new FilteredElementCollector(doc)
                .OfClass(typeof(RailingType))
                .Cast<RailingType>()
                .ToList();

            if (!types.Any())
            {
                throw new InvalidOperationException("No railing types found in the Revit model (load a railing type).");
            }

            var requested = NormalizeName(railingTypeName);
            if (!string.IsNullOrEmpty(requested))
            {
                var exact = types.FirstOrDefault(x =>
                    string.Equals(x.Name, requested, StringComparison.OrdinalIgnoreCase));
                if (exact != null)
                {
                    return exact;
                }

                var contains = types.FirstOrDefault(x =>
                    NormalizeName(x.Name).IndexOf(requested, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    requested.IndexOf(NormalizeName(x.Name), StringComparison.OrdinalIgnoreCase) >= 0);
                if (contains != null)
                {
                    log?.AppendLine("create_guardrail: railingTypeName fallback by partial match -> \"" + contains.Name + "\".");
                    return contains;
                }
            }

            var preferred = types.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).First();
            if (!string.IsNullOrEmpty(requested))
            {
                log?.AppendLine(
                    "create_guardrail: railingTypeName \"" + (railingTypeName ?? string.Empty) + "\" not found; fallback -> \"" +
                    preferred.Name + "\".");
            }

            return preferred;
        }

        private static string NormalizeName(string? s) =>
            string.IsNullOrWhiteSpace(s) ? string.Empty : s.Trim();

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
