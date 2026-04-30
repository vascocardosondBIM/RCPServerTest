using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Newtonsoft.Json.Linq;
using RevitSketchPoC.Sketch.Contracts;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace RevitSketchPoC.RevitOperations.CreateElements
{
    /// <summary>Doors on walls from placement points or JSON <c>create_door</c>.</summary>
    public static class RevitDoorCreationOps
    {
        /// <summary>Creates doors hosted on the given walls (nearest wall to each placement point).</summary>
        public static int CreateDoorsFromPlacements(
            Document doc,
            Level level,
            IReadOnlyCollection<Wall> walls,
            IEnumerable<DoorPlacement> doors)
        {
            if (walls.Count == 0) return 0;

            var doorSymbol = ResolveDefaultDoorSymbol(doc);
            if (doorSymbol == null) return 0;

            if (!doorSymbol.IsActive)
            {
                doorSymbol.Activate();
                doc.Regenerate();
            }

            var created = 0;
            foreach (var door in doors)
            {
                var point = new XYZ(
                    RevitWallCreationOps.MetersToFeet(door.Location.X),
                    RevitWallCreationOps.MetersToFeet(door.Location.Y),
                    level.Elevation);
                var host = FindNearestWall(walls, point);
                if (host == null) continue;

                var locationCurve = host.Location as LocationCurve;
                if (locationCurve == null) continue;
                var projected = locationCurve.Curve.Project(point);
                if (projected == null) continue;

                try
                {
                    doc.Create.NewFamilyInstance(
                        projected.XYZPoint,
                        doorSymbol,
                        host,
                        level,
                        StructuralType.NonStructural);
                    created++;
                }
                catch
                {
                    // Keep going even if one placement fails.
                }
            }

            return created;
        }

        /// <summary>JSON op <c>create_door</c>: location in metres; optional hostWallId, else nearest wall on level.</summary>
        public static void RunCreateDoorJsonOp(Document doc, JObject op, StringBuilder log)
        {
            if (!TryReadLocationMeters(op, out var lx, out var ly))
            {
                throw new InvalidOperationException(
                    "create_door requires locationX/locationY (metres) or location object { x, y }.");
            }

            var levelName = op["levelName"]?.ToString();
            var level = RevitWallCreationOps.ResolveLevel(doc, string.IsNullOrWhiteSpace(levelName) ? null : levelName);
            var point = new XYZ(RevitWallCreationOps.MetersToFeet(lx), RevitWallCreationOps.MetersToFeet(ly), level.Elevation);

            Wall? host = null;
            var hostIdToken = op["hostWallId"];
            if (hostIdToken != null && hostIdToken.Type != JTokenType.Null)
            {
                var idVal = hostIdToken.Value<long?>() ?? hostIdToken.Value<int?>();
                if (idVal != null)
                {
                    host = doc.GetElement(new ElementId((long)idVal)) as Wall;
                }
            }

            if (host == null)
            {
                var wallsOnLevel = CollectWallsOnLevel(doc, level);
                host = FindNearestWall(wallsOnLevel, point);
            }

            if (host == null)
            {
                throw new InvalidOperationException("create_door: no suitable host wall found.");
            }

            var doorSymbol = ResolveDefaultDoorSymbol(doc);
            if (doorSymbol == null)
            {
                throw new InvalidOperationException("create_door: no door family type in the project.");
            }

            if (!doorSymbol.IsActive)
            {
                doorSymbol.Activate();
                doc.Regenerate();
            }

            var locationCurve = host.Location as LocationCurve;
            if (locationCurve == null)
            {
                throw new InvalidOperationException("create_door: host wall has no location curve.");
            }

            var projected = locationCurve.Curve.Project(point);
            if (projected == null)
            {
                throw new InvalidOperationException("create_door: could not project point onto host wall.");
            }

            FamilyInstance instance;
            try
            {
                instance = doc.Create.NewFamilyInstance(
                    projected.XYZPoint,
                    doorSymbol,
                    host,
                    level,
                    StructuralType.NonStructural);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("create_door: " + ex.Message);
            }

            log.AppendLine("create_door id=" + instance.Id + " hostWallId=" + host.Id);
        }

        public static FamilySymbol? ResolveDefaultDoorSymbol(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Doors)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .FirstOrDefault();
        }

        public static List<Wall> CollectWallsOnLevel(Document doc, Level level)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(Wall))
                .Cast<Wall>()
                .Where(w => w.LevelId == level.Id)
                .ToList();
        }

        public static Wall? FindNearestWall(IEnumerable<Wall> walls, XYZ point)
        {
            var bestDistance = double.MaxValue;
            Wall? bestWall = null;
            foreach (var wall in walls)
            {
                var curve = (wall.Location as LocationCurve)?.Curve;
                if (curve == null) continue;
                var projection = curve.Project(point);
                if (projection == null) continue;
                if (projection.Distance < bestDistance)
                {
                    bestDistance = projection.Distance;
                    bestWall = wall;
                }
            }

            return bestWall;
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
