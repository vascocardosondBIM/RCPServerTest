using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitSketchPoC.Core.Configuration;
using RevitSketchPoC.Sketch.Contracts;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace RevitSketchPoC.RevitOperations.CreateElements
{
    /// <summary>Levels, wall types, wall geometry from segments or JSON <c>create_wall</c>.</summary>
    public static class RevitWallCreationOps
    {
        public static double MetersToFeet(double meters) => meters / 0.3048;

        public static Level ResolveLevel(Document doc, string? requestedLevelName)
        {
            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(x => x.Elevation)
                .ToList();

            if (!levels.Any()) throw new InvalidOperationException("No levels found in the Revit model.");

            if (!string.IsNullOrWhiteSpace(requestedLevelName))
            {
                var match = levels.FirstOrDefault(x => x.Name.Equals(requestedLevelName, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }

            return levels.First();
        }

        public static WallType ResolveWallType(Document doc, string? wallTypeName)
        {
            var types = new FilteredElementCollector(doc)
                .OfClass(typeof(WallType))
                .Cast<WallType>()
                .ToList();

            if (!types.Any()) throw new InvalidOperationException("No wall types found in the Revit model.");

            if (!string.IsNullOrWhiteSpace(wallTypeName))
            {
                var match = types.FirstOrDefault(x => x.Name.Equals(wallTypeName, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }

            return types.First();
        }

        /// <summary>Creates walls from 2D segments (coordinates in metres, plan XY).</summary>
        public static List<Wall> CreateWallsFromSegments(
            Document doc,
            Level level,
            WallType wallType,
            IEnumerable<WallSegment> walls,
            double defaultWallHeightMeters)
        {
            var output = new List<Wall>();
            foreach (var wall in walls)
            {
                var p0 = new XYZ(MetersToFeet(wall.Start.X), MetersToFeet(wall.Start.Y), level.Elevation);
                var p1 = new XYZ(MetersToFeet(wall.End.X), MetersToFeet(wall.End.Y), level.Elevation);
                var lenFt = p0.DistanceTo(p1);
                if (lenFt < MetersToFeet(0.25)) continue;

                var line = Line.CreateBound(p0, p1);
                var height = wall.HeightMeters > 0 ? wall.HeightMeters : defaultWallHeightMeters;
                var revitWall = Wall.Create(doc, line, wallType.Id, level.Id, MetersToFeet(height), 0.0, false, false);
                output.Add(revitWall);
            }

            return output;
        }

        /// <summary>JSON op <c>create_wall</c>: segment in metres (plan).</summary>
        public static void RunCreateWallJsonOp(Document doc, JObject op, PluginSettings settings, StringBuilder log)
        {
            if (!TryReadPlanSegmentMeters(op, out var x0, out var y0, out var x1, out var y1))
            {
                throw new InvalidOperationException(
                    "create_wall requires startX/startY and endX/endY (numbers, metres), or start/end objects with x,y.");
            }

            var levelName = op["levelName"]?.ToString();
            var wallTypeName = op["wallTypeName"]?.ToString();
            var level = ResolveLevel(doc, string.IsNullOrWhiteSpace(levelName) ? null : levelName);
            var wallType = ResolveWallType(doc, string.IsNullOrWhiteSpace(wallTypeName) ? null : wallTypeName);

            var heightM = ReadOptionalPositiveDouble(op, "heightMeters");
            var defaultH = settings.DefaultWallHeightMeters > 0 ? settings.DefaultWallHeightMeters : 3.0;
            var height = heightM ?? defaultH;

            var p0 = new XYZ(MetersToFeet(x0), MetersToFeet(y0), level.Elevation);
            var p1 = new XYZ(MetersToFeet(x1), MetersToFeet(y1), level.Elevation);
            var lenFt = p0.DistanceTo(p1);
            if (lenFt < MetersToFeet(0.25))
            {
                throw new InvalidOperationException("create_wall: segment too short (< 0.25 m).");
            }

            var line = Line.CreateBound(p0, p1);
            var revitWall = Wall.Create(doc, line, wallType.Id, level.Id, MetersToFeet(height), 0.0, false, false);
            var lengthM = lenFt * 0.3048;
            log.AppendLine("create_wall id=" + revitWall.Id + " length~=" + Math.Round(lengthM, 2) + "m");
        }

        private static bool TryReadPlanSegmentMeters(JObject op, out double x0, out double y0, out double x1, out double y1)
        {
            x0 = y0 = x1 = y1 = 0;
            if (op["start"] is JObject s && op["end"] is JObject e)
            {
                if (TryReadNumber(s["x"], out x0) && TryReadNumber(s["y"], out y0) &&
                    TryReadNumber(e["x"], out x1) && TryReadNumber(e["y"], out y1))
                {
                    return true;
                }
            }

            return TryReadNumber(op["startX"], out x0) && TryReadNumber(op["startY"], out y0) &&
                   TryReadNumber(op["endX"], out x1) && TryReadNumber(op["endY"], out y1);
        }

        private static double? ReadOptionalPositiveDouble(JObject op, string key)
        {
            if (!TryReadNumber(op[key], out var v) || v <= 0)
            {
                return null;
            }

            return v;
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
