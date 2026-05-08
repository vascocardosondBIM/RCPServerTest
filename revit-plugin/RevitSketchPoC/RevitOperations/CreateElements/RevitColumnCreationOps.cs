using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Newtonsoft.Json.Linq;
using RevitSketchPoC.RevitOperations.Shared;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace RevitSketchPoC.RevitOperations.CreateElements
{
    /// <summary>JSON <c>create_pillar</c> — structural column (<see cref="BuiltInCategory.OST_StructuralColumns"/>) at plan XY on a level.</summary>
    public static class RevitColumnCreationOps
    {
        private const double DefaultUnconnectedHeightMeters = 3.0;

        public static void RunCreatePillarJsonOp(
            Document doc,
            JObject op,
            StringBuilder log,
            List<(double x, double y, ElementId levelId)>? placementBatch = null)
        {
            var typeName = op["pillarTypeName"]?.ToString()?.Trim()
                           ?? op["columnTypeName"]?.ToString()?.Trim();
            if (string.IsNullOrEmpty(typeName))
            {
                throw new InvalidOperationException(
                    "create_pillar requires pillarTypeName or columnTypeName (match namedTypesForRevitOps structural column types).");
            }

            if (!TryReadLocationMeters(op, out var lx, out var ly))
            {
                throw new InvalidOperationException(
                    "create_pillar requires locationX/locationY (metres) or location { x, y }.");
            }

            var baseLevelName = op["levelName"]?.ToString();
            var baseLevel = RevitWallCreationOps.ResolveLevel(doc, string.IsNullOrWhiteSpace(baseLevelName) ? null : baseLevelName, log);
            if (placementBatch != null)
            {
                RevitPlanPlacementGuard.AssertNewPlanPoint(doc, baseLevel, lx, ly, placementBatch, checkExistingDoorWindow: true);
            }

            var symbol = ResolveStructuralColumnSymbol(doc, typeName, log);
            if (symbol == null)
            {
                throw new InvalidOperationException(
                    "create_pillar: no structural column FamilySymbol matches \"" + typeName + "\".");
            }

            if (!symbol.IsActive)
            {
                symbol.Activate();
                doc.Regenerate();
            }

            var point = new XYZ(
                RevitWallCreationOps.MetersToFeet(lx),
                RevitWallCreationOps.MetersToFeet(ly),
                baseLevel.Elevation);

            FamilyInstance instance;
            try
            {
                instance = doc.Create.NewFamilyInstance(
                    point,
                    symbol,
                    baseLevel,
                    StructuralType.Column);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("create_pillar: " + ex.Message);
            }

            var levelsOrdered = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();

            var topLevelName = op["topLevelName"]?.ToString();
            var heightM = ReadOptionalPositiveDouble(op["heightMeters"]);
            var baseOffM = ReadOptionalDouble(op["baseOffsetMeters"]);
            var topOffM = ReadOptionalDouble(op["topOffsetMeters"]);

            if (!string.IsNullOrWhiteSpace(topLevelName))
            {
                var topLvl = RevitWallCreationOps.ResolveLevel(doc, topLevelName.Trim(), log);
                TrySetElementIdParam(instance, BuiltInParameter.FAMILY_TOP_LEVEL_PARAM, topLvl.Id);
                if (topOffM.HasValue)
                {
                    TrySetDoubleParam(instance, BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM, RevitWallCreationOps.MetersToFeet(topOffM.Value));
                }
            }
            else if (heightM.HasValue)
            {
                TrySetElementIdParam(instance, BuiltInParameter.FAMILY_TOP_LEVEL_PARAM, baseLevel.Id);
                TrySetDoubleParam(
                    instance,
                    BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM,
                    RevitWallCreationOps.MetersToFeet(heightM.Value));
            }
            else
            {
                var nextUp = levelsOrdered.FirstOrDefault(l => l.Elevation > baseLevel.Elevation + 1e-9);
                if (nextUp != null)
                {
                    TrySetElementIdParam(instance, BuiltInParameter.FAMILY_TOP_LEVEL_PARAM, nextUp.Id);
                    if (topOffM.HasValue)
                    {
                        TrySetDoubleParam(
                            instance,
                            BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM,
                            RevitWallCreationOps.MetersToFeet(topOffM.Value));
                    }
                }
                else
                {
                    TrySetElementIdParam(instance, BuiltInParameter.FAMILY_TOP_LEVEL_PARAM, baseLevel.Id);
                    TrySetDoubleParam(
                        instance,
                        BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM,
                        RevitWallCreationOps.MetersToFeet(DefaultUnconnectedHeightMeters));
                    log.AppendLine(
                        "create_pillar: no level above base; using unconnected height " + DefaultUnconnectedHeightMeters + " m.");
                }
            }

            if (baseOffM.HasValue)
            {
                TrySetDoubleParam(
                    instance,
                    BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM,
                    RevitWallCreationOps.MetersToFeet(baseOffM.Value));
            }

            var rotationDeg = ReadOptionalDouble(op["rotationDegrees"]);
            if (rotationDeg.HasValue && Math.Abs(rotationDeg.Value) > 1e-6)
            {
                try
                {
                    var axis = Line.CreateBound(point, point + XYZ.BasisZ);
                    instance.Location.Rotate(axis, rotationDeg.Value * Math.PI / 180.0);
                }
                catch
                {
                    // optional
                }
            }

            var label = op["name"]?.ToString();
            if (!string.IsNullOrWhiteSpace(label))
            {
                try
                {
                    instance.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(label.Trim());
                }
                catch
                {
                    // optional
                }
            }

            log.AppendLine(
                "create_pillar id=" + instance.Id + " type=\"" + symbol.Family.Name + " : " + symbol.Name + "\"");
        }

        private static FamilySymbol? ResolveStructuralColumnSymbol(Document doc, string requested, StringBuilder? log)
        {
            var symbols = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_StructuralColumns)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .ToList();

            if (symbols.Count == 0)
            {
                return null;
            }

            var req = requested.Trim();
            var exact = symbols.FirstOrDefault(s => s.Name.Equals(req, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
            {
                return exact;
            }

            var exactFamilyType = symbols.FirstOrDefault(s =>
                ((s.Family?.Name ?? "") + " : " + s.Name).Equals(req, StringComparison.OrdinalIgnoreCase));
            if (exactFamilyType != null)
            {
                return exactFamilyType;
            }

            var contains = symbols.FirstOrDefault(s =>
                s.Name.IndexOf(req, StringComparison.OrdinalIgnoreCase) >= 0 ||
                req.IndexOf(s.Name, StringComparison.OrdinalIgnoreCase) >= 0 ||
                ((s.Family?.Name ?? "") + " " + s.Name).IndexOf(req, StringComparison.OrdinalIgnoreCase) >= 0);
            if (contains != null)
            {
                log?.AppendLine("create_pillar: pillarTypeName partial match -> \"" + contains.Family.Name + " : " + contains.Name + "\".");
                return contains;
            }

            return null;
        }

        private static void TrySetElementIdParam(Element el, BuiltInParameter bip, ElementId id)
        {
            try
            {
                var p = el.get_Parameter(bip);
                if (p != null && !p.IsReadOnly)
                {
                    p.Set(id);
                }
            }
            catch
            {
                // family-dependent
            }
        }

        private static void TrySetDoubleParam(Element el, BuiltInParameter bip, double feet)
        {
            try
            {
                var p = el.get_Parameter(bip);
                if (p != null && !p.IsReadOnly)
                {
                    p.Set(feet);
                }
            }
            catch
            {
                // family-dependent
            }
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
                return double.TryParse(t.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
            }
        }

        private static double? ReadOptionalPositiveDouble(JToken? t)
        {
            var d = ReadOptionalDouble(t);
            if (!d.HasValue || d.Value <= 1e-9)
            {
                return null;
            }

            return d;
        }

        private static bool TryReadLocationMeters(JObject op, out double lx, out double ly)
        {
            lx = ly = 0;
            if (op["location"] is JObject loc)
            {
                if (TryReadNumber(loc["x"], out lx) && TryReadNumber(loc["y"], out ly))
                {
                    return true;
                }
            }

            return TryReadNumber(op["locationX"], out lx) && TryReadNumber(op["locationY"], out ly);
        }

        private static bool TryReadNumber(JToken? token, out double value)
        {
            value = 0;
            if (token == null || token.Type == JTokenType.Null)
            {
                return false;
            }

            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
            {
                value = token.Value<double>();
                return true;
            }

            var s = token.ToString().Trim();
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                   double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
        }
    }
}
