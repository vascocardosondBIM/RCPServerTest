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
    /// <summary>
    /// JSON <c>create_pillar</c> — structural (<see cref="BuiltInCategory.OST_StructuralColumns"/>) or architectural
    /// (<see cref="BuiltInCategory.OST_Columns"/>) column <see cref="FamilySymbol"/> at plan XY on a level.
    /// </summary>
    public static class RevitColumnCreationOps
    {
        private const double DefaultUnconnectedHeightMeters = 3.0;
        private const double VerifyLengthToleranceFeet = 0.05;
        private const int AmbiguousTypeNameListCap = 12;

        public static void RunCreatePillarJsonOp(
            Document doc,
            JObject op,
            StringBuilder log,
            List<(double x, double y, ElementId levelId, PlanPlacementBatchKind kind)>? placementBatch = null)
        {
            var typeName = op["pillarTypeName"]?.ToString()?.Trim()
                           ?? op["columnTypeName"]?.ToString()?.Trim();
            if (string.IsNullOrEmpty(typeName))
            {
                throw new InvalidOperationException(
                    "create_pillar requires pillarTypeName or columnTypeName (match namedTypesForRevitOps.structuralColumnTypeNames or architecturalColumnTypeNames).");
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
                RevitPlanPlacementGuard.ValidateNewPlanPoint(
                    doc,
                    baseLevel,
                    lx,
                    ly,
                    placementBatch,
                    checkExistingDoorWindow: true,
                    PlanPlacementBatchKind.Pillar);
            }

            var symbol = ResolvePillarColumnSymbol(doc, typeName, out var resolveDetail);
            if (symbol == null)
            {
                throw new InvalidOperationException(
                    resolveDetail != null
                        ? "create_pillar: " + resolveDetail
                        : "create_pillar: no column FamilySymbol matches \"" + typeName +
                          "\" (see namedTypesForRevitOps.structuralColumnTypeNames or architecturalColumnTypeNames).");
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

            var structuralType = symbol.Category?.BuiltInCategory == BuiltInCategory.OST_StructuralColumns
                ? StructuralType.Column
                : StructuralType.NonStructural;

            FamilyInstance instance;
            try
            {
                instance = doc.Create.NewFamilyInstance(
                    point,
                    symbol,
                    baseLevel,
                    structuralType);
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

            var verifyTopLevelTarget = baseLevel.Id;
            double? verifyTopOffsetFeet = null;
            double? verifyBaseOffsetFeet = null;

            if (!string.IsNullOrWhiteSpace(topLevelName))
            {
                var topLvl = RevitWallCreationOps.ResolveLevel(doc, topLevelName.Trim(), log);
                verifyTopLevelTarget = topLvl.Id;
                TrySetElementIdParam(instance, BuiltInParameter.FAMILY_TOP_LEVEL_PARAM, topLvl.Id);
                if (topOffM.HasValue)
                {
                    verifyTopOffsetFeet = RevitWallCreationOps.MetersToFeet(topOffM.Value);
                    TrySetDoubleParam(
                        instance,
                        BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM,
                        verifyTopOffsetFeet.Value);
                }
            }
            else if (heightM.HasValue)
            {
                verifyTopLevelTarget = baseLevel.Id;
                verifyTopOffsetFeet = RevitWallCreationOps.MetersToFeet(heightM.Value);
                TrySetElementIdParam(instance, BuiltInParameter.FAMILY_TOP_LEVEL_PARAM, baseLevel.Id);
                TrySetDoubleParam(
                    instance,
                    BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM,
                    verifyTopOffsetFeet.Value);
            }
            else
            {
                var nextUp = levelsOrdered.FirstOrDefault(l => l.Elevation > baseLevel.Elevation + 1e-9);
                if (nextUp != null)
                {
                    verifyTopLevelTarget = nextUp.Id;
                    TrySetElementIdParam(instance, BuiltInParameter.FAMILY_TOP_LEVEL_PARAM, nextUp.Id);
                    if (topOffM.HasValue)
                    {
                        verifyTopOffsetFeet = RevitWallCreationOps.MetersToFeet(topOffM.Value);
                        TrySetDoubleParam(
                            instance,
                            BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM,
                            verifyTopOffsetFeet.Value);
                    }
                }
                else
                {
                    verifyTopLevelTarget = baseLevel.Id;
                    verifyTopOffsetFeet = RevitWallCreationOps.MetersToFeet(DefaultUnconnectedHeightMeters);
                    TrySetElementIdParam(instance, BuiltInParameter.FAMILY_TOP_LEVEL_PARAM, baseLevel.Id);
                    TrySetDoubleParam(
                        instance,
                        BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM,
                        verifyTopOffsetFeet.Value);
                    log.AppendLine(
                        "create_pillar: no level above base; using unconnected height " + DefaultUnconnectedHeightMeters + " m.");
                }
            }

            if (baseOffM.HasValue)
            {
                verifyBaseOffsetFeet = RevitWallCreationOps.MetersToFeet(baseOffM.Value);
                TrySetDoubleParam(
                    instance,
                    BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM,
                    verifyBaseOffsetFeet.Value);
            }

            var rotationDeg = ReadOptionalDouble(op["rotationDegrees"]);
            if (rotationDeg.HasValue && Math.Abs(rotationDeg.Value) > 1e-6)
            {
                try
                {
                    var axis = Line.CreateBound(point, point + XYZ.BasisZ);
                    instance.Location.Rotate(axis, rotationDeg.Value * Math.PI / 180.0);
                }
                catch (Exception ex)
                {
                    log.AppendLine("create_pillar: rotation failed: " + ex.Message);
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

            VerifyPillarParameters(
                doc,
                instance,
                verifyTopLevelTarget,
                verifyTopOffsetFeet,
                verifyBaseOffsetFeet,
                log);

            if (placementBatch != null)
            {
                RevitPlanPlacementGuard.AddPlanPointToBatch(placementBatch, lx, ly, baseLevel, PlanPlacementBatchKind.Pillar);
            }
        }

        private static void VerifyPillarParameters(
            Document doc,
            FamilyInstance instance,
            ElementId intendedTopLevelId,
            double? intendedTopOffsetFeet,
            double? intendedBaseOffsetFeet,
            StringBuilder log)
        {
            try
            {
                doc.Regenerate();
            }
            catch (Exception ex)
            {
                log.AppendLine("create_pillar verify: warning regenerate failed: " + ex.Message);
                return;
            }

            var issues = new List<string>();

            var topLevelParam = instance.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM);
            if (topLevelParam == null || topLevelParam.StorageType != StorageType.ElementId)
            {
                issues.Add("top level parameter not readable on this family");
            }
            else
            {
                var actualTop = topLevelParam.AsElementId();
                if (!actualTop.Equals(intendedTopLevelId))
                {
                    issues.Add(
                        "top level id mismatch (expected " + intendedTopLevelId.IntegerValue + ", got " +
                        actualTop.IntegerValue + ")");
                }
            }

            if (intendedTopOffsetFeet.HasValue)
            {
                var p = instance.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM);
                if (p == null || p.StorageType != StorageType.Double)
                {
                    issues.Add("top offset parameter not readable on this family");
                }
                else
                {
                    var actual = p.AsDouble();
                    if (Math.Abs(actual - intendedTopOffsetFeet.Value) > VerifyLengthToleranceFeet)
                    {
                        issues.Add(
                            "top offset feet mismatch (expected ~" +
                            intendedTopOffsetFeet.Value.ToString("0.###", CultureInfo.InvariantCulture) + ", got " +
                            actual.ToString("0.###", CultureInfo.InvariantCulture) + ")");
                    }
                }
            }

            if (intendedBaseOffsetFeet.HasValue)
            {
                var p = instance.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM);
                if (p == null || p.StorageType != StorageType.Double)
                {
                    issues.Add("base offset parameter not readable on this family");
                }
                else
                {
                    var actual = p.AsDouble();
                    if (Math.Abs(actual - intendedBaseOffsetFeet.Value) > VerifyLengthToleranceFeet)
                    {
                        issues.Add(
                            "base offset feet mismatch (expected ~" +
                            intendedBaseOffsetFeet.Value.ToString("0.###", CultureInfo.InvariantCulture) + ", got " +
                            actual.ToString("0.###", CultureInfo.InvariantCulture) + ")");
                    }
                }
            }

            if (issues.Count == 0)
            {
                log.AppendLine("create_pillar verify: OK");
            }
            else
            {
                log.AppendLine("create_pillar verify: warning " + string.Join("; ", issues));
            }
        }

        private static List<FamilySymbol> CollectColumnFamilySymbols(Document doc, BuiltInCategory columnCategory)
        {
            return new FilteredElementCollector(doc)
                .OfCategory(columnCategory)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .ToList();
        }

        private static FamilySymbol? ResolvePillarColumnSymbol(Document doc, string requested, out string? failureDetail)
        {
            failureDetail = null;
            var structural = CollectColumnFamilySymbols(doc, BuiltInCategory.OST_StructuralColumns);
            var architectural = CollectColumnFamilySymbols(doc, BuiltInCategory.OST_Columns);
            var symbols = structural
                .Concat(architectural)
                .GroupBy(s => s.Id.IntegerValue)
                .Select(g => g.First())
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

            var partialMatches = symbols
                .Where(s => PartialTypeMatch(s, req))
                .GroupBy(s => s.Id.IntegerValue)
                .Select(g => g.First())
                .ToList();

            if (partialMatches.Count > 1)
            {
                var names = partialMatches
                    .Select(s => "\"" + (s.Family?.Name ?? "?") + " : " + s.Name + "\"")
                    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                    .Take(AmbiguousTypeNameListCap)
                    .ToList();
                failureDetail =
                    "ambiguous pillarTypeName — multiple column types match \"" + req + "\": " +
                    string.Join("; ", names) +
                    (partialMatches.Count > AmbiguousTypeNameListCap
                        ? " (+" + (partialMatches.Count - AmbiguousTypeNameListCap) + " more)"
                        : "");
                return null;
            }

            if (partialMatches.Count == 1)
            {
                return partialMatches[0];
            }

            return null;
        }

        private static bool PartialTypeMatch(FamilySymbol s, string req)
        {
            return s.Name.IndexOf(req, StringComparison.OrdinalIgnoreCase) >= 0
                   || req.IndexOf(s.Name, StringComparison.OrdinalIgnoreCase) >= 0
                   || ((s.Family?.Name ?? "") + " " + s.Name).IndexOf(req, StringComparison.OrdinalIgnoreCase) >= 0;
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
