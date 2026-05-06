using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using System;
using System.Globalization;
using System.Text;

namespace RevitSketchPoC.RevitOperations.CreateElements
{
    /// <summary>Rectangular opening in a wall — JSON <c>create_wall_opening</c>.</summary>
    public static class RevitWallOpeningOps
    {
        /// <summary>
        /// hostWallId required. openingBaseOffsetMeters / openingHeightMeters from wall base;
        /// positionAlongWallMeters = distance along wall curve from start (metres).
        /// openingWidthMeters = width of opening in plan along wall.
        /// </summary>
        public static void RunCreateWallOpeningJsonOp(Document doc, JObject op, StringBuilder log)
        {
            var hostIdTok = op["hostWallId"];
            if (hostIdTok == null || hostIdTok.Type == JTokenType.Null)
            {
                throw new InvalidOperationException("create_wall_opening requires hostWallId (wall element id).");
            }

            var idVal = hostIdTok.Value<long?>() ?? hostIdTok.Value<int?>();
            if (idVal == null)
            {
                throw new InvalidOperationException("create_wall_opening: invalid hostWallId.");
            }

            var wall = doc.GetElement(new ElementId((long)idVal)) as Wall;
            if (wall == null)
            {
                throw new InvalidOperationException("create_wall_opening: host is not a wall.");
            }

            if (!TryReadNumber(op["positionAlongWallMeters"], out var alongM) ||
                !TryReadNumber(op["openingWidthMeters"], out var widthM) ||
                !TryReadNumber(op["openingBaseOffsetMeters"], out var baseOffM) ||
                !TryReadNumber(op["openingHeightMeters"], out var heightM))
            {
                throw new InvalidOperationException(
                    "create_wall_opening requires positionAlongWallMeters, openingWidthMeters, openingBaseOffsetMeters, openingHeightMeters (metres).");
            }

            if (widthM <= 0.05 || heightM <= 0.05)
            {
                throw new InvalidOperationException("create_wall_opening: width and height must be positive.");
            }

            var locCurve = wall.Location as LocationCurve;
            if (locCurve?.Curve == null)
            {
                throw new InvalidOperationException("create_wall_opening: wall has no location curve.");
            }

            var curve = locCurve.Curve;
            var lenFt = curve.Length;
            var alongFt = RevitWallCreationOps.MetersToFeet(alongM);
            if (alongFt < 1e-4 || alongFt > lenFt - 1e-4)
            {
                throw new InvalidOperationException(
                    "create_wall_opening: positionAlongWallMeters must be strictly inside the wall length (0 < s < length).");
            }

            var pCenter = curve.Evaluate(alongFt / lenFt, true);
            var tangent = curve.ComputeDerivatives(alongFt / lenFt, true).BasisX.Normalize();
            var halfW = RevitWallCreationOps.MetersToFeet(widthM) / 2.0;
            var p0 = pCenter - tangent * halfW;
            var p1 = pCenter + tangent * halfW;

            var lvlWall = doc.GetElement(wall.LevelId) as Level;
            var wallBaseElev = lvlWall?.Elevation ?? 0;
            var wallBaseOffsetFt = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0.0;
            var zLow = wallBaseElev + wallBaseOffsetFt + RevitWallCreationOps.MetersToFeet(baseOffM);
            var zHigh = zLow + RevitWallCreationOps.MetersToFeet(heightM);

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

            log.AppendLine("create_wall_opening id=" + opening.Id);
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
