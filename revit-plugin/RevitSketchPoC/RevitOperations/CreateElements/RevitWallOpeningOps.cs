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
    /// <summary>Rectangular opening in a wall — JSON <c>create_wall_opening</c>.</summary>
    public static class RevitWallOpeningOps
    {
        private const double MinOpeningWidthMeters = 0.10;
        private const double MinOpeningHeightMeters = 0.10;
        private const double MinEdgeClearanceMeters = 0.12;
        private const double MinInsertCenterSeparationMeters = 0.30;
        private const double DefaultMaxHostDistanceMeters = 1.50;

        /// <summary>
        /// Rectangular wall opening with robust placement:
        /// - hostWallId OR locationX/locationY(+levelName)
        /// - positionAlongWallMeters OR positionRatio OR location projection
        /// - openingWidthMeters, openingHeightMeters (or openingTopOffsetMeters + openingBaseOffsetMeters)
        /// </summary>
        public static void RunCreateWallOpeningJsonOp(
            Document doc,
            JObject op,
            StringBuilder log,
            ISet<string>? openingBatchKeys = null)
        {
            var wall = ResolveHostWall(doc, op);
            if (wall == null)
            {
                throw new InvalidOperationException(
                    "create_wall_opening: no suitable host wall found. Provide hostWallId or locationX/locationY (+ optional levelName).");
            }

            if (!TryReadNumber(op["openingWidthMeters"], out var widthM))
            {
                throw new InvalidOperationException("create_wall_opening requires openingWidthMeters.");
            }

            var baseOffM = ReadOptionalNumber(op, "openingBaseOffsetMeters") ?? 0.0;
            var topOffM = ReadOptionalNumber(op, "openingTopOffsetMeters");
            var explicitHeightM = ReadOptionalNumber(op, "openingHeightMeters");
            var heightM = explicitHeightM ?? (topOffM.HasValue ? topOffM.Value - baseOffM : 0.0);
            if (widthM < MinOpeningWidthMeters || heightM < MinOpeningHeightMeters)
            {
                throw new InvalidOperationException(
                    "create_wall_opening: width/height too small. Minimum is " +
                    MinOpeningWidthMeters.ToString("0.##", CultureInfo.InvariantCulture) + " m.");
            }

            var locCurve = wall.Location as LocationCurve;
            if (locCurve?.Curve == null)
            {
                throw new InvalidOperationException("create_wall_opening: host wall has no location curve.");
            }

            var curve = locCurve.Curve;
            var lenFt = curve.Length;
            var alongFt = ResolveAlongOnWallFeet(op, wall, curve);
            var halfW = RevitWallCreationOps.MetersToFeet(widthM) / 2.0;
            var minCenterFt = RevitWallCreationOps.MetersToFeet(MinEdgeClearanceMeters) + halfW;
            var maxCenterFt = lenFt - minCenterFt;
            if (maxCenterFt <= minCenterFt)
            {
                throw new InvalidOperationException(
                    "create_wall_opening: opening width does not fit host wall after edge clearance.");
            }

            var autoClamp = ReadOptionalBool(op, "autoClamp") ?? false;
            if (autoClamp)
            {
                alongFt = Math.Max(minCenterFt, Math.Min(maxCenterFt, alongFt));
            }
            else if (alongFt < minCenterFt || alongFt > maxCenterFt)
            {
                throw new InvalidOperationException(
                    "create_wall_opening: opening is too close to wall ends. Use positionRatio/positionAlongWallMeters inside safe limits.");
            }

            var normalized = Math.Max(0.0, Math.Min(1.0, alongFt / lenFt));
            var pCenter = curve.Evaluate(normalized, true);
            var tangent = curve.ComputeDerivatives(normalized, true).BasisX.Normalize();
            var p0 = pCenter - tangent * halfW;
            var p1 = pCenter + tangent * halfW;

            var lvlWall = doc.GetElement(wall.LevelId) as Level;
            var wallBaseElev = lvlWall?.Elevation ?? 0;
            var wallBaseOffsetFt = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0.0;
            var zLow = wallBaseElev + wallBaseOffsetFt + RevitWallCreationOps.MetersToFeet(baseOffM);
            var zHigh = zLow + RevitWallCreationOps.MetersToFeet(heightM);

            AssertNoNearbyInsertOnHost(doc, wall, curve, alongFt, halfW);
            RegisterOpeningBatchKey(wall, alongFt, widthM, baseOffM, heightM, openingBatchKeys);

            var start = new XYZ(p0.X, p0.Y, zLow);
            var end = new XYZ(p1.X, p1.Y, zHigh);
            Opening opening;
            try
            {
                opening = doc.Create.NewOpening(wall, start, end);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("create_wall_opening: " + ex.Message);
            }

            log.AppendLine("create_wall_opening id=" + opening.Id + " hostWallId=" + wall.Id);
        }

        /// <summary>
        /// Arch opening using a door family type (roman arch style when family supports it).
        /// host/position fields follow create_wall_opening semantics.
        /// </summary>
        public static void RunCreateWallArchOpeningJsonOp(
            Document doc,
            JObject op,
            StringBuilder log,
            ISet<string>? openingBatchKeys = null)
        {
            var wall = ResolveHostWall(doc, op);
            if (wall == null)
            {
                throw new InvalidOperationException(
                    "create_wall_arch_opening: no suitable host wall found. Provide hostWallId or locationX/locationY (+ optional levelName).");
            }

            var locCurve = wall.Location as LocationCurve;
            if (locCurve?.Curve == null)
            {
                throw new InvalidOperationException("create_wall_arch_opening: host wall has no location curve.");
            }

            var curve = locCurve.Curve;
            var lenFt = curve.Length;
            var alongFt = ResolveAlongOnWallFeet(op, wall, curve);

            var requestedType = op["archTypeName"]?.ToString();
            if (string.IsNullOrWhiteSpace(requestedType))
            {
                requestedType = op["doorTypeName"]?.ToString();
            }

            var symbol = ResolveArchDoorSymbol(doc, requestedType, out var usedFallbackArchType);
            if (symbol == null)
            {
                throw new InvalidOperationException(
                    "create_wall_arch_opening: no arched door family type found. Load an arched door and use archTypeName.");
            }

            if (usedFallbackArchType)
            {
                log.AppendLine(
                    "create_wall_arch_opening: requested type was not arch-like; fallback to \"" +
                    (symbol.Family?.Name ?? "Door") + " : " + symbol.Name + "\".");
            }

            if (!symbol.IsActive)
            {
                symbol.Activate();
                doc.Regenerate();
            }

            var widthM = ReadOptionalNumber(op, "openingWidthMeters");
            var heightM = ReadOptionalNumber(op, "openingHeightMeters");
            var baseOffM = ReadOptionalNumber(op, "openingBaseOffsetMeters") ?? 0.0;

            var familyWidthFt = TryGetFamilyDimensionFeet(symbol, BuiltInParameter.DOOR_WIDTH, "Width", "Rough Width");
            var familyHeightFt = TryGetFamilyDimensionFeet(symbol, BuiltInParameter.DOOR_HEIGHT, "Height", "Rough Height");
            var useWidthFt = widthM.HasValue ? RevitWallCreationOps.MetersToFeet(widthM.Value) : familyWidthFt;
            var halfW = useWidthFt > 0 ? useWidthFt / 2.0 : RevitWallCreationOps.MetersToFeet(0.45);

            var minCenterFt = RevitWallCreationOps.MetersToFeet(MinEdgeClearanceMeters) + halfW;
            var maxCenterFt = lenFt - minCenterFt;
            if (maxCenterFt <= minCenterFt)
            {
                throw new InvalidOperationException(
                    "create_wall_arch_opening: arch width does not fit host wall after edge clearance.");
            }

            var autoClamp = ReadOptionalBool(op, "autoClamp") ?? false;
            if (autoClamp)
            {
                alongFt = Math.Max(minCenterFt, Math.Min(maxCenterFt, alongFt));
            }
            else if (alongFt < minCenterFt || alongFt > maxCenterFt)
            {
                throw new InvalidOperationException(
                    "create_wall_arch_opening: arch position too close to wall ends.");
            }

            AssertNoNearbyInsertOnHost(doc, wall, curve, alongFt, halfW);
            RegisterArchOpeningBatchKey(wall, alongFt, widthM, heightM, baseOffM, openingBatchKeys);

            var normalized = Math.Max(0.0, Math.Min(1.0, alongFt / lenFt));
            var insertPoint = curve.Evaluate(normalized, true);
            var level = doc.GetElement(wall.LevelId) as Level ?? RevitWallCreationOps.ResolveLevel(doc, null);

            FamilyInstance instance;
            try
            {
                instance = doc.Create.NewFamilyInstance(
                    insertPoint,
                    symbol,
                    wall,
                    level,
                    StructuralType.NonStructural);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("create_wall_arch_opening: " + ex.Message);
            }

            if (widthM.HasValue)
            {
                TrySetInstanceOrTypeLength(instance, symbol, RevitWallCreationOps.MetersToFeet(widthM.Value), BuiltInParameter.DOOR_WIDTH, "Width", "Rough Width");
            }

            if (heightM.HasValue)
            {
                TrySetInstanceOrTypeLength(instance, symbol, RevitWallCreationOps.MetersToFeet(heightM.Value), BuiltInParameter.DOOR_HEIGHT, "Height", "Rough Height");
            }

            if (Math.Abs(baseOffM) > 1e-6)
            {
                var sillParam = instance.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM) ??
                                instance.LookupParameter("Sill Height");
                if (sillParam != null && !sillParam.IsReadOnly && sillParam.StorageType == StorageType.Double)
                {
                    try
                    {
                        sillParam.Set(RevitWallCreationOps.MetersToFeet(baseOffM));
                    }
                    catch
                    {
                        // Optional parameter set.
                    }
                }
            }

            log.AppendLine(
                "create_wall_arch_opening id=" + instance.Id +
                " hostWallId=" + wall.Id +
                " type=\"" + (symbol.Family?.Name ?? "Door") + " : " + symbol.Name + "\"");
        }

        private static FamilySymbol? ResolveArchDoorSymbol(Document doc, string? requestedTypeName, out bool usedFallback)
        {
            usedFallback = false;
            var exact = RevitFamilySymbolByName.ResolveInCategory(
                doc,
                BuiltInCategory.OST_Doors,
                string.IsNullOrWhiteSpace(requestedTypeName) ? null : requestedTypeName);
            if (exact != null && IsArchLikeSymbolName(exact))
            {
                return exact;
            }

            var allDoorSymbols = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Doors)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .ToList();

            var fallback = allDoorSymbols.FirstOrDefault(IsArchLikeSymbolName);
            if (fallback != null)
            {
                usedFallback = exact != null || !string.IsNullOrWhiteSpace(requestedTypeName);
                return fallback;
            }

            // If no arch-like names exist at all, keep previous behavior only when request is absent;
            // otherwise force explicit failure to avoid false "roman arch" success on rectangular doors.
            if (exact != null && string.IsNullOrWhiteSpace(requestedTypeName))
            {
                return exact;
            }

            return null;
        }

        private static bool IsArchLikeSymbolName(FamilySymbol symbol)
        {
            var full = ((symbol.Family?.Name ?? string.Empty) + " : " + symbol.Name).ToLowerInvariant();
            return full.Contains("arch") ||
                   full.Contains("arco") ||
                   full.Contains("roman") ||
                   full.Contains("round") ||
                   full.Contains("semi-circular") ||
                   full.Contains("semicircular") ||
                   full.Contains("volta perfeita");
        }

        private static Wall? ResolveHostWall(Document doc, JObject op)
        {
            var hostIdToken = op["hostWallId"];
            if (hostIdToken != null && hostIdToken.Type != JTokenType.Null)
            {
                var idVal = hostIdToken.Value<long?>() ?? hostIdToken.Value<int?>();
                if (idVal != null)
                {
                    return doc.GetElement(new ElementId((long)idVal)) as Wall;
                }
            }

            if (!TryReadLocationMeters(op, out var lx, out var ly))
            {
                return null;
            }

            var levelName = op["levelName"]?.ToString();
            var level = RevitWallCreationOps.ResolveLevel(doc, string.IsNullOrWhiteSpace(levelName) ? null : levelName);
            var point = new XYZ(RevitWallCreationOps.MetersToFeet(lx), RevitWallCreationOps.MetersToFeet(ly), level.Elevation);
            var host = RevitDoorCreationOps.FindNearestWall(RevitDoorCreationOps.CollectWallsOnLevel(doc, level), point);
            if (host == null)
            {
                return null;
            }

            var maxHostDistM = ReadOptionalNumber(op, "maxHostDistanceMeters") ?? DefaultMaxHostDistanceMeters;
            var curve = (host.Location as LocationCurve)?.Curve;
            var projection = curve?.Project(point);
            if (projection == null)
            {
                return null;
            }

            return projection.Distance <= RevitWallCreationOps.MetersToFeet(maxHostDistM) ? host : null;
        }

        private static double ResolveAlongOnWallFeet(JObject op, Wall wall, Curve curve)
        {
            if (TryReadNumber(op["positionAlongWallMeters"], out var alongM))
            {
                return RevitWallCreationOps.MetersToFeet(alongM);
            }

            if (TryReadNumber(op["positionRatio"], out var ratio))
            {
                if (ratio <= 0.0 || ratio >= 1.0)
                {
                    throw new InvalidOperationException("create_wall_opening: positionRatio must be inside (0,1).");
                }

                return ratio * curve.Length;
            }

            if (TryReadLocationMeters(op, out var lx, out var ly))
            {
                var level = wall.Document.GetElement(wall.LevelId) as Level;
                var z = level?.Elevation ?? curve.GetEndPoint(0).Z;
                var point = new XYZ(RevitWallCreationOps.MetersToFeet(lx), RevitWallCreationOps.MetersToFeet(ly), z);
                var projection = curve.Project(point);
                if (projection != null)
                {
                    return ComputeAlongDistanceFeet(curve, projection.Parameter);
                }
            }

            throw new InvalidOperationException(
                "create_wall_opening requires positionAlongWallMeters, positionRatio, or locationX/locationY.");
        }

        private static double ComputeAlongDistanceFeet(Curve curve, double parameter)
        {
            try
            {
                var startParam = curve.GetEndParameter(0);
                var clone = curve.Clone();
                if (parameter >= startParam)
                {
                    clone.MakeBound(startParam, parameter);
                }
                else
                {
                    clone.MakeBound(parameter, startParam);
                }

                return clone.Length;
            }
            catch
            {
                return curve.Evaluate(parameter, false).DistanceTo(curve.GetEndPoint(0));
            }
        }

        private static void AssertNoNearbyInsertOnHost(
            Document doc,
            Wall wall,
            Curve curve,
            double targetAlongFt,
            double halfWidthFt)
        {
            var minSeparationFt = halfWidthFt + RevitWallCreationOps.MetersToFeet(MinInsertCenterSeparationMeters);
            var insertIds = wall.FindInserts(
                addRectOpenings: true,
                includeShadows: false,
                includeEmbeddedWalls: true,
                includeSharedEmbeddedInserts: true);
            foreach (var id in insertIds)
            {
                var insert = doc.GetElement(id);
                if (insert == null || !TryGetElementPlanPoint(insert, out var p))
                {
                    continue;
                }

                var projection = curve.Project(p);
                if (projection == null)
                {
                    continue;
                }

                var alongFt = ComputeAlongDistanceFeet(curve, projection.Parameter);
                if (Math.Abs(alongFt - targetAlongFt) < minSeparationFt)
                {
                    throw new InvalidOperationException(
                        "create_wall_opening: too close to another insert/opening on the same host wall.");
                }
            }
        }

        private static bool TryGetElementPlanPoint(Element element, out XYZ point)
        {
            point = XYZ.Zero;
            if (element.Location is LocationPoint lp)
            {
                point = lp.Point;
                return true;
            }

            if (element.Location is LocationCurve lc && lc.Curve != null)
            {
                point = lc.Curve.Evaluate(0.5, true);
                return true;
            }

            var bb = element.get_BoundingBox(null);
            if (bb != null)
            {
                point = (bb.Min + bb.Max) * 0.5;
                return true;
            }

            return false;
        }

        private static void RegisterOpeningBatchKey(
            Wall wall,
            double alongFt,
            double widthM,
            double baseOffM,
            double heightM,
            ISet<string>? batch)
        {
            if (batch == null)
            {
                return;
            }

            var key = "open|" +
                      wall.Id.IntegerValue.ToString(CultureInfo.InvariantCulture) + "|" +
                      Math.Round(alongFt * 0.3048, 3).ToString("0.###", CultureInfo.InvariantCulture) + "|" +
                      Math.Round(widthM, 3).ToString("0.###", CultureInfo.InvariantCulture) + "|" +
                      Math.Round(baseOffM, 3).ToString("0.###", CultureInfo.InvariantCulture) + "|" +
                      Math.Round(heightM, 3).ToString("0.###", CultureInfo.InvariantCulture);
            if (!batch.Add(key))
            {
                throw new InvalidOperationException(
                    "create_wall_opening: duplicate opening in the same request batch.");
            }
        }

        private static void RegisterArchOpeningBatchKey(
            Wall wall,
            double alongFt,
            double? widthM,
            double? heightM,
            double baseOffM,
            ISet<string>? batch)
        {
            if (batch == null)
            {
                return;
            }

            var key = "arch-open|" +
                      wall.Id.IntegerValue.ToString(CultureInfo.InvariantCulture) + "|" +
                      Math.Round(alongFt * 0.3048, 3).ToString("0.###", CultureInfo.InvariantCulture) + "|" +
                      Math.Round(widthM ?? 0.0, 3).ToString("0.###", CultureInfo.InvariantCulture) + "|" +
                      Math.Round(heightM ?? 0.0, 3).ToString("0.###", CultureInfo.InvariantCulture) + "|" +
                      Math.Round(baseOffM, 3).ToString("0.###", CultureInfo.InvariantCulture);
            if (!batch.Add(key))
            {
                throw new InvalidOperationException(
                    "create_wall_arch_opening: duplicate arch opening in the same request batch.");
            }
        }

        private static double TryGetFamilyDimensionFeet(
            FamilySymbol symbol,
            BuiltInParameter builtIn,
            params string[] nameFallbacks)
        {
            var p = symbol.get_Parameter(builtIn);
            if (p != null && p.StorageType == StorageType.Double)
            {
                return p.AsDouble();
            }

            foreach (var name in nameFallbacks ?? Array.Empty<string>())
            {
                var q = symbol.LookupParameter(name);
                if (q != null && q.StorageType == StorageType.Double)
                {
                    return q.AsDouble();
                }
            }

            return 0.0;
        }

        private static void TrySetInstanceOrTypeLength(
            FamilyInstance instance,
            FamilySymbol symbol,
            double valueFeet,
            BuiltInParameter builtIn,
            params string[] nameFallbacks)
        {
            if (TrySetLength(instance.get_Parameter(builtIn), valueFeet))
            {
                return;
            }

            if (TrySetLength(symbol.get_Parameter(builtIn), valueFeet))
            {
                return;
            }

            foreach (var name in nameFallbacks ?? Array.Empty<string>())
            {
                if (TrySetLength(instance.LookupParameter(name), valueFeet))
                {
                    return;
                }

                if (TrySetLength(symbol.LookupParameter(name), valueFeet))
                {
                    return;
                }
            }
        }

        private static bool TrySetLength(Parameter? p, double valueFeet)
        {
            if (p == null || p.IsReadOnly || p.StorageType != StorageType.Double)
            {
                return false;
            }

            try
            {
                return p.Set(valueFeet);
            }
            catch
            {
                return false;
            }
        }

        private static double? ReadOptionalNumber(JObject op, string key)
        {
            return TryReadNumber(op[key], out var v) ? v : (double?)null;
        }

        private static bool? ReadOptionalBool(JObject op, string key)
        {
            var token = op[key];
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            if (token.Type == JTokenType.Boolean)
            {
                return token.Value<bool>();
            }

            var s = token.ToString().Trim();
            if (bool.TryParse(s, out var b))
            {
                return b;
            }

            return null;
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
