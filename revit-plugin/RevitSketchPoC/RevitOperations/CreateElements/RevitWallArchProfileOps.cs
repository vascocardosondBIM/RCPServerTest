using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RevitSketchPoC.RevitOperations.CreateElements
{
    /// <summary>
    /// Roman-arch void in a straight wall via <see cref="Wall.CreateProfileSketch"/> and <see cref="SketchEditScope"/>.
    /// JSON op <c>create_wall_roman_arch_profile</c>.
    /// Callers must not hold an open outer <see cref="Transaction"/> around <see cref="RunCreateWallRomanArchProfileJsonOp"/>.
    /// </summary>
    public static class RevitWallArchProfileOps
    {
        private const double MinEdgeClearanceMeters = 0.12;

        /// <summary>
        /// Minimum inset between profile void loops and the outer wall boundary (m).
        /// Shared edges (e.g. opening base = wall base) often crash Revit's profile kernel.
        /// </summary>
        private const double MinVoidShellInsetMeters = 0.05;

        public static void RunCreateWallRomanArchProfileJsonOp(Document doc, JObject op, StringBuilder log)
        {
            var hostIdTok = op["hostWallId"];
            if (hostIdTok == null || hostIdTok.Type == JTokenType.Null)
            {
                throw new InvalidOperationException(
                    "create_wall_roman_arch_profile requires hostWallId (straight wall).");
            }

            var idVal = hostIdTok.Value<long?>() ?? hostIdTok.Value<int?>();
            if (idVal == null)
            {
                throw new InvalidOperationException("create_wall_roman_arch_profile: invalid hostWallId.");
            }

            var wall = doc.GetElement(new ElementId((long)idVal)) as Wall;
            if (wall == null)
            {
                throw new InvalidOperationException("create_wall_roman_arch_profile: host is not a wall.");
            }

            if (wall.Location is not LocationCurve locCurve || locCurve.Curve is not Line)
            {
                throw new InvalidOperationException(
                    "create_wall_roman_arch_profile: only straight walls (line location) are supported.");
            }

            if (!TryReadNumber(op["openingWidthMeters"], out var widthM))
            {
                throw new InvalidOperationException("create_wall_roman_arch_profile requires openingWidthMeters.");
            }

            var openBaseOffM = ReadOptionalNumber(op, "openingBaseOffsetMeters") ?? 0.0;
            var jambM = ReadOptionalNumber(op, "jambHeightMeters");
            var totalM = ReadOptionalNumber(op, "openingTotalHeightMeters");
            double jambResolved;
            if (totalM.HasValue)
            {
                jambResolved = totalM.Value - widthM / 2.0;
            }
            else if (jambM.HasValue)
            {
                jambResolved = jambM.Value;
            }
            else
            {
                throw new InvalidOperationException(
                    "create_wall_roman_arch_profile requires jambHeightMeters or openingTotalHeightMeters " +
                    "(total = jamb + semicircle rise = width/2).");
            }

            if (widthM <= 0.05 || jambResolved < 0.05)
            {
                throw new InvalidOperationException(
                    "create_wall_roman_arch_profile: opening width / jamb height invalid.");
            }

            var level = doc.GetElement(wall.LevelId) as Level
                        ?? throw new InvalidOperationException("create_wall_roman_arch_profile: wall has no level.");
            var baseZ = level.Elevation + (wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0.0);
            var heightFt = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble()
                           ?? throw new InvalidOperationException("create_wall_roman_arch_profile: could not read wall height.");
            var topZ = baseZ + heightFt;

            var insetFt = RevitWallCreationOps.MetersToFeet(MinVoidShellInsetMeters);
            var openBaseZRequested = baseZ + RevitWallCreationOps.MetersToFeet(openBaseOffM);
            var openBaseZ = Math.Max(openBaseZRequested, baseZ + insetFt);
            if (openBaseZ > openBaseZRequested + 1e-7)
            {
                log.AppendLine(
                    "create_wall_roman_arch_profile: opening base lifted by ~" +
                    Math.Round((openBaseZ - openBaseZRequested) * 0.3048, 3) +
                    " m shell inset (avoids coincident edges with outer profile).");
            }

            var springZ = openBaseZ + RevitWallCreationOps.MetersToFeet(jambResolved);
            var riseFt = RevitWallCreationOps.MetersToFeet(widthM) / 2.0;
            var keystoneZ = springZ + riseFt;
            if (keystoneZ > topZ - insetFt)
            {
                throw new InvalidOperationException(
                    "create_wall_roman_arch_profile: arch too close to wall top after shell inset. " +
                    "Increase wall height, reduce openingTotalHeightMeters/openingWidthMeters, or lower jamb.");
            }

            if (springZ <= openBaseZ + RevitWallCreationOps.MetersToFeet(0.02))
            {
                throw new InvalidOperationException(
                    "create_wall_roman_arch_profile: jamb height too small after geometry adjustment.");
            }

            var curve = locCurve.Curve;
            var lenFt = curve.Length;
            var alongFt = ResolveAlongPositionFeet(op, curve, lenFt);
            var halfW = RevitWallCreationOps.MetersToFeet(widthM) / 2.0;
            var minCenterFt = RevitWallCreationOps.MetersToFeet(MinEdgeClearanceMeters) + halfW;
            var maxCenterFt = lenFt - minCenterFt;
            if (maxCenterFt <= minCenterFt)
            {
                throw new InvalidOperationException(
                    "create_wall_roman_arch_profile: opening too wide for wall length.");
            }

            var autoClamp = ReadOptionalBool(op, "autoClamp") ?? false;
            if (autoClamp)
            {
                alongFt = Math.Max(minCenterFt, Math.Min(maxCenterFt, alongFt));
            }
            else if (alongFt < minCenterFt || alongFt > maxCenterFt)
            {
                throw new InvalidOperationException(
                    "create_wall_roman_arch_profile: opening center too close to wall ends.");
            }

            var gc0 = curve.GetEndPoint(0);
            var gc1 = curve.GetEndPoint(1);
            var dir = new XYZ(gc1.X - gc0.X, gc1.Y - gc0.Y, 0).Normalize();

            XYZ WallPoint(double alongFromStart, double zAbs)
            {
                return new XYZ(gc0.X, gc0.Y, zAbs) + dir * alongFromStart;
            }

            var outerCurves = BuildOuterCurves(WallPoint, lenFt, baseZ, topZ);
            var innerCurves = BuildInnerRomanCurves(WallPoint, alongFt, halfW, openBaseZ, springZ, riseFt);

            ElementId sketchId;
            using (var txSketch = new Transaction(doc, "Roman arch — wall profile sketch"))
            {
                txSketch.Start();
                if (wall.SketchId != ElementId.InvalidElementId)
                {
                    wall.RemoveProfileSketch();
                    doc.Regenerate();
                }

                var sketch = wall.CreateProfileSketch();
                doc.Regenerate();
                sketchId = sketch.Id;
                txSketch.Commit();
            }

            using (var ses = new SketchEditScope(doc, "Roman arch — edit wall profile"))
            {
                ses.Start(sketchId);
                using (var txCurves = new Transaction(doc, "Roman arch — profile curves"))
                {
                    txCurves.Start();
                    var sketchEl = doc.GetElement(sketchId) as global::Autodesk.Revit.DB.Sketch
                                   ?? throw new InvalidOperationException(
                                       "create_wall_roman_arch_profile: sketch not found after create.");
                    var plane = sketchEl.SketchPlane
                                ?? throw new InvalidOperationException(
                                    "create_wall_roman_arch_profile: sketch has no SketchPlane.");

                    foreach (var depId in sketchEl.GetAllElements())
                    {
                        if (doc.GetElement(depId) is ModelCurve)
                        {
                            doc.Delete(depId);
                        }
                    }

                    foreach (var c in outerCurves)
                    {
                        doc.Create.NewModelCurve(c, plane);
                    }

                    foreach (var c in innerCurves)
                    {
                        doc.Create.NewModelCurve(c, plane);
                    }

                    txCurves.Commit();
                }

                ses.Commit(new ContinueFailuresPreprocessor());
            }

            doc.Regenerate();
            log.AppendLine("create_wall_roman_arch_profile ok hostWallId=" + wall.Id + " sketchId=" + sketchId);
        }

        private static List<Curve> BuildOuterCurves(Func<double, double, XYZ> wallPt, double lenFt, double baseZ, double topZ)
        {
            var a = wallPt(0, baseZ);
            var b = wallPt(lenFt, baseZ);
            var c = wallPt(lenFt, topZ);
            var d = wallPt(0, topZ);
            return new List<Curve>
            {
                Line.CreateBound(a, b),
                Line.CreateBound(b, c),
                Line.CreateBound(c, d),
                Line.CreateBound(d, a)
            };
        }

        private static List<Curve> BuildInnerRomanCurves(
            Func<double, double, XYZ> wallPt,
            double centerAlongFt,
            double halfWidthFt,
            double openBaseZ,
            double springZ,
            double riseFt)
        {
            var u0 = centerAlongFt - halfWidthFt;
            var u1 = centerAlongFt + halfWidthFt;
            var bl = wallPt(u0, openBaseZ);
            var br = wallPt(u1, openBaseZ);
            var ir = wallPt(u1, springZ);
            var il = wallPt(u0, springZ);
            var key = wallPt(centerAlongFt, springZ + riseFt);
            var arc = Arc.Create(ir, il, key);
            return new List<Curve>
            {
                Line.CreateBound(bl, br),
                Line.CreateBound(br, ir),
                arc,
                Line.CreateBound(il, bl)
            };
        }

        private static double ResolveAlongPositionFeet(JObject op, Curve curve, double lenFt)
        {
            if (TryReadNumber(op["positionAlongWallMeters"], out var alongM))
            {
                return RevitWallCreationOps.MetersToFeet(alongM);
            }

            if (TryReadNumber(op["positionRatio"], out var ratio))
            {
                if (ratio <= 0.0 || ratio >= 1.0)
                {
                    throw new InvalidOperationException(
                        "create_wall_roman_arch_profile: positionRatio must be inside (0,1).");
                }

                return ratio * lenFt;
            }

            throw new InvalidOperationException(
                "create_wall_roman_arch_profile requires positionAlongWallMeters or positionRatio.");
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

            return bool.TryParse(token.ToString().Trim(), out var b) ? b : null;
        }

        private sealed class ContinueFailuresPreprocessor : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
            {
                return FailureProcessingResult.Continue;
            }
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
