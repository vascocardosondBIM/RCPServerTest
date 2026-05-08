using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json.Linq;
using RevitSketchPoC.RevitOperations.Shared;
using RevitSketchPoC.Sketch.Contracts;
using System;
using System.Collections.Generic;
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
                    var loop = RevitOpJsonGeometry.TryBuildCurveLoop(level, f.Boundary);
                    if (loop == null)
                    {
                        continue;
                    }

                    var floor = Floor.Create(doc, new List<CurveLoop> { loop }, floorType.Id, level.Id);
                    if (floor != null)
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
            var loop = RevitOpJsonGeometry.TryBuildCurveLoop(level, op, out var loopError);
            if (loop == null)
            {
                throw new InvalidOperationException("create_floor: " + loopError);
            }

            Floor? floor;
            try
            {
                floor = Floor.Create(doc, new List<CurveLoop> { loop }, floorType.Id, level.Id);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("create_floor: " + ex.Message);
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

        /// <summary>JSON <c>create_floor_from_room</c> — slab from a placed room's computed boundary (works for curved walls / circular rooms).</summary>
        public static void RunCreateFloorFromRoomJsonOp(Document doc, JObject op, StringBuilder log)
        {
            var roomId = op["roomId"]?.Value<long?>() ?? op["elementId"]?.Value<long?>();
            if (roomId == null)
            {
                throw new InvalidOperationException("create_floor_from_room requires roomId (or elementId for a Room).");
            }

            if (doc.GetElement(new ElementId((long)roomId)) is not Room room)
            {
                throw new InvalidOperationException("create_floor_from_room: element is not a Room.");
            }

            var level = doc.GetElement(room.LevelId) as Level
                        ?? throw new InvalidOperationException("create_floor_from_room: room has no valid level.");

            var z = level.Elevation;
            var boundaryLoc = RevitRoomBoundaryLoops.ParseBoundaryLocation(op["boundaryLocation"]?.ToString());

            var loops = RevitRoomBoundaryLoops.BuildCurveLoopsForSlab(room, z, boundaryLoc);
            var floorTypeName = op["floorTypeName"]?.ToString();
            var floorType = ResolveFloorType(doc, string.IsNullOrWhiteSpace(floorTypeName) ? null : floorTypeName);

            Floor? floor;
            try
            {
                floor = Floor.Create(doc, loops, floorType.Id, level.Id);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("create_floor_from_room: " + ex.Message);
            }

            if (floor == null)
            {
                throw new InvalidOperationException("create_floor_from_room: Revit returned null.");
            }

            var label = op["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(label))
            {
                label = string.IsNullOrWhiteSpace(room.Name) ? null : room.Name.Trim();
            }

            if (!string.IsNullOrWhiteSpace(label))
            {
                try
                {
                    floor.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(label);
                }
                catch
                {
                    // optional
                }
            }

            log.AppendLine("create_floor_from_room id=" + floor.Id + " roomId=" + roomId);
        }
    }
}
