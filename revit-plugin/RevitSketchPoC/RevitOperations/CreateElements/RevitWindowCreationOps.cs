using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Newtonsoft.Json.Linq;
using RevitSketchPoC.RevitOperations.Shared;
using RevitSketchPoC.Sketch.Contracts;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace RevitSketchPoC.RevitOperations.CreateElements
{
    /// <summary>Windows on walls from placement points or JSON <c>create_window</c>.</summary>
    public static class RevitWindowCreationOps
    {
        public static int CreateWindowsFromPlacements(
            Document doc,
            Level level,
            IReadOnlyCollection<Wall> walls,
            IEnumerable<WindowPlacement> windows)
        {
            if (walls.Count == 0)
            {
                return 0;
            }

            var created = 0;
            foreach (var win in windows)
            {
                var symbol = RevitFamilySymbolByName.ResolveInCategory(
                    doc,
                    BuiltInCategory.OST_Windows,
                    win.WindowTypeName);
                if (symbol == null)
                {
                    continue;
                }

                if (!symbol.IsActive)
                {
                    symbol.Activate();
                    doc.Regenerate();
                }

                var point = new XYZ(
                    RevitWallCreationOps.MetersToFeet(win.Location.X),
                    RevitWallCreationOps.MetersToFeet(win.Location.Y),
                    level.Elevation);
                var host = RevitDoorCreationOps.FindNearestWall(walls, point);
                if (host == null)
                {
                    continue;
                }

                var locationCurve = host.Location as LocationCurve;
                if (locationCurve == null)
                {
                    continue;
                }

                var projected = locationCurve.Curve.Project(point);
                if (projected == null)
                {
                    continue;
                }

                try
                {
                    doc.Create.NewFamilyInstance(
                        projected.XYZPoint,
                        symbol,
                        host,
                        level,
                        StructuralType.NonStructural);
                    created++;
                }
                catch
                {
                    // Continue on placement failure.
                }
            }

            return created;
        }

        public static void RunCreateWindowJsonOp(
            Document doc,
            JObject op,
            StringBuilder log,
            List<(double x, double y, ElementId levelId)>? placementBatch = null)
        {
            if (!TryReadLocationMeters(op, out var lx, out var ly))
            {
                throw new InvalidOperationException(
                    "create_window requires locationX/locationY (metres) or location object { x, y }.");
            }

            var levelName = op["levelName"]?.ToString();
            var level = RevitWallCreationOps.ResolveLevel(doc, string.IsNullOrWhiteSpace(levelName) ? null : levelName);
            if (placementBatch != null)
            {
                RevitPlanPlacementGuard.AssertNewPlanPoint(doc, level, lx, ly, placementBatch, checkExistingDoorWindow: true);
            }

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
                var wallsOnLevel = RevitDoorCreationOps.CollectWallsOnLevel(doc, level);
                host = RevitDoorCreationOps.FindNearestWall(wallsOnLevel, point);
            }

            if (host == null)
            {
                throw new InvalidOperationException("create_window: no suitable host wall found.");
            }

            var typeName = op["windowTypeName"]?.ToString();
            var windowSymbol = RevitFamilySymbolByName.ResolveInCategory(
                doc,
                BuiltInCategory.OST_Windows,
                string.IsNullOrWhiteSpace(typeName) ? null : typeName);
            if (windowSymbol == null)
            {
                throw new InvalidOperationException("create_window: no window family type in the project (or name not found).");
            }

            if (!windowSymbol.IsActive)
            {
                windowSymbol.Activate();
                doc.Regenerate();
            }

            var locationCurve = host.Location as LocationCurve;
            if (locationCurve == null)
            {
                throw new InvalidOperationException("create_window: host wall has no location curve.");
            }

            var projected = locationCurve.Curve.Project(point);
            if (projected == null)
            {
                throw new InvalidOperationException("create_window: could not project point onto host wall.");
            }

            FamilyInstance instance;
            try
            {
                instance = doc.Create.NewFamilyInstance(
                    projected.XYZPoint,
                    windowSymbol,
                    host,
                    level,
                    StructuralType.NonStructural);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("create_window: " + ex.Message);
            }

            log.AppendLine("create_window id=" + instance.Id + " hostWallId=" + host.Id);
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
