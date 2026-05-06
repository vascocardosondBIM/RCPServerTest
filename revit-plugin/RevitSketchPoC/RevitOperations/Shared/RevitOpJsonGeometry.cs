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
                if (p0.DistanceTo(p1) < RevitInternalUnits.MetersToFeet(0.05))
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
            if (curves.Count < 3)
            {
                return null;
            }

            try
            {
                return CurveLoop.Create(curves);
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
    }
}
