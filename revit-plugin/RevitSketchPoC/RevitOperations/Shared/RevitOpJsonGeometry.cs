using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitSketchPoC.Sketch.Contracts;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RevitSketchPoC.RevitOperations.Shared
{
    /// <summary>Reads plan XY boundaries from revitOps JSON (metres).</summary>
    public static class RevitOpJsonGeometry
    {
        private const double MinSegmentLengthMeters = 0.05;

        public static List<Point2D> ReadPlanBoundaryMeters(JArray? boundaryArr, int minPoints = 3)
        {
            var pts = new List<Point2D>();
            if (boundaryArr == null || boundaryArr.Count < minPoints)
            {
                return pts;
            }

            foreach (var t in boundaryArr)
            {
                if (t is JObject jo && TryReadPlanPoint(jo, out var x, out var y))
                {
                    pts.Add(new Point2D { X = x, Y = y });
                }
            }

            return pts;
        }

        public static bool TryReadPlanPoint(JObject o, out double x, out double y)
        {
            x = y = 0;
            return TryReadNumber(o["x"], out x) && TryReadNumber(o["y"], out y);
        }

        public static bool TryReadNumber(JToken? token, out double value)
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

        public static List<Curve> BuildHorizontalBoundaryCurves(Level level, IReadOnlyList<Point2D> boundary)
        {
            var curves = new List<Curve>();
            var elev = level.Elevation;
            var n = boundary.Count;
            for (var i = 0; i < n; i++)
            {
                var a = boundary[i];
                var b = boundary[(i + 1) % n];
                var p0 = new XYZ(RevitInternalUnits.MetersToFeet(a.X), RevitInternalUnits.MetersToFeet(a.Y), elev);
                var p1 = new XYZ(RevitInternalUnits.MetersToFeet(b.X), RevitInternalUnits.MetersToFeet(b.Y), elev);
                if (p0.DistanceTo(p1) < RevitInternalUnits.MetersToFeet(MinSegmentLengthMeters))
                {
                    continue;
                }

                curves.Add(Line.CreateBound(p0, p1));
            }

            return curves;
        }

        public static CurveLoop? TryBuildCurveLoop(Level level, IReadOnlyList<Point2D> boundary)
        {
            var curves = BuildHorizontalBoundaryCurves(level, boundary);
            return TryCreateCurveLoopFromCurves(curves, RevitInternalUnits.MetersToFeet(MinSegmentLengthMeters));
        }

        public static CurveLoop? TryBuildCurveLoop(Level level, JObject op, out string error)
        {
            error = string.Empty;
            if (TryReadCircleLoop(level, op, out var circleCurves, out var circleError))
            {
                if (circleCurves.Count < 2)
                {
                    error = "invalid circle boundary.";
                    return null;
                }

                var cLoop = TryCreateCurveLoopFromCurves(circleCurves, RevitInternalUnits.MetersToFeet(MinSegmentLengthMeters));
                if (cLoop == null)
                {
                    error = circleError;
                }

                return cLoop;
            }

            if (op["boundarySegments"] is JArray segs && segs.Count > 0)
            {
                if (!TryBuildBoundarySegmentCurves(level, segs, out var segCurves, out var segError))
                {
                    error = segError;
                    return null;
                }

                var segLoop = TryCreateCurveLoopFromCurves(segCurves, RevitInternalUnits.MetersToFeet(MinSegmentLengthMeters));
                if (segLoop == null)
                {
                    error = "boundarySegments do not form a valid closed loop.";
                }

                return segLoop;
            }

            var boundaryArr = op["boundary"] as JArray;
            var pts = ReadPlanBoundaryMeters(boundaryArr, 3);
            if (pts.Count < 3)
            {
                error = "requires \"boundary\" (>=3 points), \"boundarySegments\", or circle (center+radius).";
                return null;
            }

            var pLoop = TryBuildCurveLoop(level, pts);
            if (pLoop == null)
            {
                error = "boundary points do not form a valid loop.";
            }

            return pLoop;
        }

        public static CurveLoop? TryCreateCurveLoopFromCurves(IEnumerable<Curve> curves, double minLengthFeet)
        {
            var list = new List<Curve>();
            foreach (var c in curves ?? Enumerable.Empty<Curve>())
            {
                if (c == null || c.Length < minLengthFeet)
                {
                    continue;
                }

                list.Add(c);
            }

            if (list.Count < 2)
            {
                return null;
            }

            try
            {
                return CurveLoop.Create(list);
            }
            catch
            {
                return null;
            }
        }

        public static CurveArray ToCurveArray(Application app, IEnumerable<Curve> curves)
        {
            var ca = app.Create.NewCurveArray();
            foreach (var c in curves)
            {
                ca.Append(c);
            }

            return ca;
        }

        private static bool TryBuildBoundarySegmentCurves(
            Level level,
            JArray segments,
            out List<Curve> curves,
            out string error)
        {
            curves = new List<Curve>();
            error = string.Empty;
            var z = level.Elevation;
            var minLenFt = RevitInternalUnits.MetersToFeet(MinSegmentLengthMeters);

            foreach (var token in segments)
            {
                if (token is not JObject seg)
                {
                    continue;
                }

                var kind = (seg["kind"]?.ToString() ?? "line").Trim().ToLowerInvariant();
                switch (kind)
                {
                    case "line":
                        if (!TryReadPoint(seg, "start", "startX", "startY", z, out var l0) ||
                            !TryReadPoint(seg, "end", "endX", "endY", z, out var l1))
                        {
                            error = "line segment requires start/end points.";
                            return false;
                        }

                        if (l0.DistanceTo(l1) >= minLenFt)
                        {
                            curves.Add(Line.CreateBound(l0, l1));
                        }

                        break;

                    case "arc":
                        if (TryReadPoint(seg, "start", "startX", "startY", z, out var a0) &&
                            TryReadPoint(seg, "mid", "midX", "midY", z, out var am) &&
                            TryReadPoint(seg, "end", "endX", "endY", z, out var a1))
                        {
                            curves.Add(Arc.Create(a0, a1, am));
                            break;
                        }

                        if (!TryReadPoint(seg, "center", "centerX", "centerY", z, out var c) ||
                            !TryReadNumber(seg["radiusMeters"], out var rMeters) ||
                            !TryReadNumber(seg["startAngleDegrees"], out var aDeg0) ||
                            !TryReadNumber(seg["endAngleDegrees"], out var aDeg1))
                        {
                            error = "arc segment requires start/mid/end or center+radiusMeters+startAngleDegrees+endAngleDegrees.";
                            return false;
                        }

                        var rFt = RevitInternalUnits.MetersToFeet(Math.Abs(rMeters));
                        if (rFt < minLenFt)
                        {
                            error = "arc segment radius is too small.";
                            return false;
                        }

                        curves.Add(Arc.Create(c, rFt, DegreesToRadians(aDeg0), DegreesToRadians(aDeg1), XYZ.BasisX, XYZ.BasisY));
                        break;

                    default:
                        error = "unsupported boundary segment kind \"" + kind + "\". Use line or arc.";
                        return false;
                }
            }

            if (curves.Count == 0)
            {
                error = "no valid boundary segments were provided.";
                return false;
            }

            return true;
        }

        private static bool TryReadCircleLoop(
            Level level,
            JObject op,
            out List<Curve> curves,
            out string error)
        {
            curves = new List<Curve>();
            error = string.Empty;

            JObject? cObj = op["circle"] as JObject;
            var centerToken = cObj?["center"] ?? op["center"];
            var centerXToken = cObj?["centerX"] ?? op["centerX"];
            var centerYToken = cObj?["centerY"] ?? op["centerY"];
            var radiusToken = cObj?["radiusMeters"] ?? op["radiusMeters"];
            if (centerToken == null && (centerXToken == null || centerYToken == null || radiusToken == null))
            {
                return false;
            }

            if (!TryReadPoint(centerToken, centerXToken, centerYToken, level.Elevation, out var center) ||
                !TryReadNumber(radiusToken, out var rMeters))
            {
                error = "circle requires center (or centerX/centerY) and radiusMeters.";
                return false;
            }

            var rFt = RevitInternalUnits.MetersToFeet(Math.Abs(rMeters));
            if (rFt < RevitInternalUnits.MetersToFeet(MinSegmentLengthMeters))
            {
                error = "circle radiusMeters is too small.";
                return false;
            }

            // Revit profile loops cannot use a single full-circle Arc; use two semicircles.
            curves.Add(Arc.Create(center, rFt, 0, Math.PI, XYZ.BasisX, XYZ.BasisY));
            curves.Add(Arc.Create(center, rFt, Math.PI, Math.PI * 2.0, XYZ.BasisX, XYZ.BasisY));
            return true;
        }

        private static bool TryReadPoint(
            JObject obj,
            string nestedName,
            string xName,
            string yName,
            double z,
            out XYZ point)
        {
            point = XYZ.Zero;
            var nested = obj[nestedName];
            var xToken = obj[xName];
            var yToken = obj[yName];
            return TryReadPoint(nested, xToken, yToken, z, out point);
        }

        private static bool TryReadPoint(
            JToken? nested,
            JToken? xToken,
            JToken? yToken,
            double z,
            out XYZ point)
        {
            point = XYZ.Zero;
            if (nested is JObject jo && TryReadPlanPoint(jo, out var nx, out var ny))
            {
                point = new XYZ(RevitInternalUnits.MetersToFeet(nx), RevitInternalUnits.MetersToFeet(ny), z);
                return true;
            }

            if (!TryReadNumber(xToken, out var x) || !TryReadNumber(yToken, out var y))
            {
                return false;
            }

            point = new XYZ(RevitInternalUnits.MetersToFeet(x), RevitInternalUnits.MetersToFeet(y), z);
            return true;
        }

        private static double DegreesToRadians(double degrees)
        {
            return degrees * Math.PI / 180.0;
        }
    }
}
