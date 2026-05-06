using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitSketchPoC.Sketch.Contracts;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace RevitSketchPoC.RevitOperations.CreateElements
{
    /// <summary>Slab floors from closed 2D boundaries (metres) or JSON <c>create_floor</c>.</summary>
    public static class RevitFloorCreationOps
    {
        public static FloorType ResolveFloorType(Document doc, string? requestedName)
        {
            var types = new FilteredElementCollector(doc)
                .OfClass(typeof(FloorType))
                .Cast<FloorType>()
                .ToList();

            if (types.Count == 0)
            {
                throw new InvalidOperationException("No floor types found in the Revit model.");
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

        public static int CreateFloorsFromBoundaries(
            Document doc,
            Level level,
            string? floorTypeName,
            IEnumerable<FloorBoundary> floors)
        {
            var floorType = ResolveFloorType(doc, floorTypeName);
            var created = 0;
            foreach (var f in floors)
            {
                if (f.Boundary == null || f.Boundary.Count < 3)
                {
                    continue;
                }

                try
                {
                    if (TryCreateFloor(doc, level, floorType, f.Boundary, out _))
                    {
                        created++;
                    }
                }
                catch
                {
                    // Skip invalid loops.
                }
            }

            return created;
        }

        public static void RunCreateFloorJsonOp(Document doc, JObject op, StringBuilder log)
        {
            var levelName = op["levelName"]?.ToString();
            var level = RevitWallCreationOps.ResolveLevel(doc, string.IsNullOrWhiteSpace(levelName) ? null : levelName);
            var floorTypeName = op["floorTypeName"]?.ToString();
            var floorType = ResolveFloorType(doc, string.IsNullOrWhiteSpace(floorTypeName) ? null : floorTypeName);

            JArray? boundaryArr = op["boundary"] as JArray;
            if (boundaryArr == null || boundaryArr.Count < 3)
            {
                throw new InvalidOperationException(
                    "create_floor requires \"boundary\" as array of at least 3 points {x,y} in metres.");
            }

            var pts = new List<Point2D>();
            foreach (var t in boundaryArr)
            {
                if (t is JObject jo && TryReadPlanPoint(jo, out var x, out var y))
                {
                    pts.Add(new Point2D { X = x, Y = y });
                }
            }

            if (pts.Count < 3)
            {
                throw new InvalidOperationException("create_floor: could not read boundary points.");
            }

            if (!TryCreateFloor(doc, level, floorType, pts, out var floor))
            {
                throw new InvalidOperationException("create_floor: Revit could not create floor from boundary (open or degenerate loop?).");
            }

            var label = op["name"]?.ToString();
            if (!string.IsNullOrWhiteSpace(label) && floor != null)
            {
                try
                {
                    var p = floor.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                    p?.Set(label);
                }
                catch
                {
                    // optional
                }
            }

            log.AppendLine("create_floor id=" + (floor?.Id.ToString() ?? "?"));
        }

        private static bool TryCreateFloor(
            Document doc,
            Level level,
            FloorType floorType,
            IReadOnlyList<Point2D> boundary,
            out Floor? floor)
        {
            floor = null;
            var elev = level.Elevation;
            var curves = new List<Curve>();
            var n = boundary.Count;
            for (var i = 0; i < n; i++)
            {
                var a = boundary[i];
                var b = boundary[(i + 1) % n];
                var p0 = new XYZ(RevitWallCreationOps.MetersToFeet(a.X), RevitWallCreationOps.MetersToFeet(a.Y), elev);
                var p1 = new XYZ(RevitWallCreationOps.MetersToFeet(b.X), RevitWallCreationOps.MetersToFeet(b.Y), elev);
                var len = p0.DistanceTo(p1);
                if (len < RevitWallCreationOps.MetersToFeet(0.05))
                {
                    continue;
                }

                curves.Add(Line.CreateBound(p0, p1));
            }

            if (curves.Count < 3)
            {
                return false;
            }

            var loop = CurveLoop.Create(curves);
            var loops = new List<CurveLoop> { loop };
            try
            {
                floor = Floor.Create(doc, loops, floorType.Id, level.Id);
                return floor != null;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadPlanPoint(JObject o, out double x, out double y)
        {
            x = y = 0;
            return TryReadNumber(o["x"], out x) && TryReadNumber(o["y"], out y);
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
