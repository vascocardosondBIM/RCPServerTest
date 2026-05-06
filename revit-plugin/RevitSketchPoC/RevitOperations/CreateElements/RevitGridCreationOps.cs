using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace RevitSketchPoC.RevitOperations.CreateElements
{
    /// <summary>JSON <c>create_grid</c>: axis line in plan (metres).</summary>
    public static class RevitGridCreationOps
    {
        public static void RunCreateGridJsonOp(Document doc, JObject op, StringBuilder log)
        {
            if (!TryReadNumber(op["startX"], out var sx) || !TryReadNumber(op["startY"], out var sy) ||
                !TryReadNumber(op["endX"], out var endX) || !TryReadNumber(op["endY"], out var endY))
            {
                throw new InvalidOperationException(
                    "create_grid requires startX, startY, endX, endY (metres in plan).");
            }

            var levelName = op["levelName"]?.ToString();
            var level = RevitWallCreationOps.ResolveLevel(doc, string.IsNullOrWhiteSpace(levelName) ? null : levelName);
            var z = level.Elevation;

            var p0 = new XYZ(RevitWallCreationOps.MetersToFeet(sx), RevitWallCreationOps.MetersToFeet(sy), z);
            var p1 = new XYZ(RevitWallCreationOps.MetersToFeet(endX), RevitWallCreationOps.MetersToFeet(endY), z);
            if (p0.DistanceTo(p1) < RevitWallCreationOps.MetersToFeet(0.1))
            {
                throw new InvalidOperationException("create_grid: line too short.");
            }

            Line line;
            try
            {
                line = Line.CreateBound(p0, p1);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("create_grid: invalid line — " + ex.Message);
            }

            Grid grid;
            try
            {
                grid = Grid.Create(doc, line);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("create_grid: " + ex.Message);
            }

            var name = op["gridName"]?.ToString()?.Trim() ?? op["name"]?.ToString()?.Trim();
            if (!string.IsNullOrEmpty(name))
            {
                try
                {
                    grid.Name = UniqueGridName(doc, name);
                }
                catch (Exception ex)
                {
                    log.AppendLine("create_grid: could not set name: " + ex.Message);
                }
            }

            log.AppendLine("create_grid id=" + grid.Id);
        }

        private static string UniqueGridName(Document doc, string desired)
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>())
            {
                if (!string.IsNullOrEmpty(g.Name))
                {
                    existing.Add(g.Name);
                }
            }

            if (!existing.Contains(desired))
            {
                return desired;
            }

            for (var i = 2; i < 200; i++)
            {
                var candidate = desired + " (" + i + ")";
                if (!existing.Contains(candidate))
                {
                    return candidate;
                }
            }

            return desired + " " + Guid.NewGuid().ToString("N").Substring(0, 4);
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
