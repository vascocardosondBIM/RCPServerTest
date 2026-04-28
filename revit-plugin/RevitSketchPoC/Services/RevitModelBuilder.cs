using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitSketchPoC.Contracts;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitSketchPoC.Services
{
    public sealed class RevitModelBuilder
    {
        private readonly PluginSettings _settings;

        public RevitModelBuilder(PluginSettings settings)
        {
            _settings = settings;
        }

        public BuildResult Build(UIDocument uiDoc, SketchToBimRequest request, SketchInterpretation interpretation)
        {
            SketchInterpretationSanitizer.SanitizeInPlace(interpretation);
            if (interpretation.Walls.Count == 0)
            {
                throw new InvalidOperationException(
                    "Depois de filtrar paredes demasiado curtas, não sobrou nenhuma parede válida. " +
                    "O modelo provavelmente devolveu coordenadas erradas ou fora de escala; tenta outro modelo Ollama (ex. maior) ou refina o prompt.");
            }

            var doc = uiDoc.Document;
            var result = new BuildResult { Ok = true, Notes = interpretation.Notes };

            using (var tx = new Transaction(doc, "Sketch to BIM"))
            {
                tx.Start();

                var level = ResolveLevel(doc, request.TargetLevelName);
                var wallType = ResolveWallType(doc, request.WallTypeName);
                var createdWalls = CreateWalls(doc, level, wallType, interpretation.Walls);
                result.WallsCreated = createdWalls.Count;

                if (request.AutoCreateRooms)
                {
                    result.RoomsCreated = CreateRooms(doc, level, interpretation.Rooms);
                }

                if (request.AutoCreateDoors)
                {
                    result.DoorsCreated = CreateDoors(doc, level, createdWalls, interpretation.Doors);
                }

                tx.Commit();
            }

            return result;
        }

        private List<Wall> CreateWalls(Document doc, Level level, WallType wallType, IEnumerable<WallSegment> walls)
        {
            var output = new List<Wall>();
            foreach (var wall in walls)
            {
                var p0 = new XYZ(ToFeet(wall.Start.X), ToFeet(wall.Start.Y), level.Elevation);
                var p1 = new XYZ(ToFeet(wall.End.X), ToFeet(wall.End.Y), level.Elevation);
                var lenFt = p0.DistanceTo(p1);
                if (lenFt < ToFeet(0.25)) continue;

                var line = Line.CreateBound(p0, p1);
                var height = wall.HeightMeters > 0 ? wall.HeightMeters : _settings.DefaultWallHeightMeters;
                var revitWall = Wall.Create(doc, line, wallType.Id, level.Id, ToFeet(height), 0.0, false, false);
                output.Add(revitWall);
            }
            return output;
        }

        private int CreateRooms(Document doc, Level level, IEnumerable<RoomRegion> rooms)
        {
            var created = 0;
            foreach (var room in rooms)
            {
                if (room.Boundary.Count == 0) continue;
                var centroid = ComputeCentroid(room.Boundary);
                var uv = new UV(ToFeet(centroid.X), ToFeet(centroid.Y));
                try
                {
                    var createdRoom = doc.Create.NewRoom(level, uv);
                    createdRoom.Name = string.IsNullOrWhiteSpace(room.Name) ? "Room" : room.Name;
                    created++;
                }
                catch
                {
                    // Ignore non-enclosed room placement.
                }
            }
            return created;
        }

        private int CreateDoors(Document doc, Level level, IReadOnlyCollection<Wall> walls, IEnumerable<DoorPlacement> doors)
        {
            if (walls.Count == 0) return 0;

            var doorSymbol = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Doors)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .FirstOrDefault();

            if (doorSymbol == null) return 0;

            if (!doorSymbol.IsActive)
            {
                doorSymbol.Activate();
                doc.Regenerate();
            }

            var created = 0;
            foreach (var door in doors)
            {
                var point = new XYZ(ToFeet(door.Location.X), ToFeet(door.Location.Y), level.Elevation);
                var host = FindNearestWall(walls, point);
                if (host == null) continue;

                var locationCurve = host.Location as LocationCurve;
                if (locationCurve == null) continue;
                var projected = locationCurve.Curve.Project(point);
                if (projected == null) continue;

                try
                {
                    doc.Create.NewFamilyInstance(projected.XYZPoint, doorSymbol, host, level, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                    created++;
                }
                catch
                {
                    // Keep going even if one placement fails.
                }
            }
            return created;
        }

        private static Level ResolveLevel(Document doc, string? requestedLevelName)
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

        private static WallType ResolveWallType(Document doc, string? wallTypeName)
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

        private static Wall? FindNearestWall(IEnumerable<Wall> walls, XYZ point)
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

        private static Point2D ComputeCentroid(IReadOnlyCollection<Point2D> points)
        {
            return new Point2D
            {
                X = points.Average(x => x.X),
                Y = points.Average(x => x.Y)
            };
        }

        private static double ToFeet(double meters) => meters / 0.3048;
    }
}
