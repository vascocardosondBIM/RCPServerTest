using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace RevitSketchPoC.RevitOperations.CreateElements
{
    /// <summary>JSON <c>create_level</c>: new level at elevation (metres from internal origin).</summary>
    public static class RevitLevelCreationOps
    {
        public static void RunCreateLevelJsonOp(Document doc, JObject op, StringBuilder log)
        {
            var name = op["name"]?.ToString()?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                throw new InvalidOperationException("create_level requires \"name\" for the new level.");
            }

            if (!TryReadNumber(op["elevationMeters"], out var elevM))
            {
                throw new InvalidOperationException("create_level requires elevationMeters (number, metres).");
            }

            var elevFt = RevitWallCreationOps.MetersToFeet(elevM);
            Level level;
            try
            {
                level = Level.Create(doc, elevFt);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("create_level: " + ex.Message);
            }

            try
            {
                level.Name = UniqueLevelName(doc, name);
            }
            catch (Exception ex)
            {
                log.AppendLine("create_level: could not set name: " + ex.Message);
            }

            log.AppendLine("create_level id=" + level.Id + " elevation~=" + Math.Round(elevM, 3) + "m");
        }

        private static string UniqueLevelName(Document doc, string desired)
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
            {
                if (!string.IsNullOrEmpty(l.Name))
                {
                    existing.Add(l.Name);
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

            return desired + " " + Guid.NewGuid().ToString("N").Substring(0, 6);
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
