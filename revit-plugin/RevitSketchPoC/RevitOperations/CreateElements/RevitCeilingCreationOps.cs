using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json.Linq;
using RevitSketchPoC.Core.Configuration;
using RevitSketchPoC.RevitOperations.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RevitSketchPoC.RevitOperations.CreateElements
{
    /// <summary>Ceilings from closed 2D boundaries (metres) — JSON <c>create_ceiling</c>.</summary>
    public static class RevitCeilingCreationOps
    {
        public static CeilingType ResolveCeilingType(Document doc, string? requestedName)
        {
            var types = new FilteredElementCollector(doc)
                .OfClass(typeof(CeilingType))
                .Cast<CeilingType>()
                .ToList();

            if (types.Count == 0)
            {
                throw new InvalidOperationException("No ceiling types found in the Revit model.");
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

        public static void RunCreateCeilingJsonOp(
            Document doc,
            JObject op,
            StringBuilder log,
            PluginSettings? pluginSettings = null)
        {
            var settings = pluginSettings ?? new PluginSettings();
            var levelName = op["levelName"]?.ToString();
            var level = RevitWallCreationOps.ResolveLevel(doc, string.IsNullOrWhiteSpace(levelName) ? null : levelName);
            var typeName = op["ceilingTypeName"]?.ToString();
            var ceilingType = ResolveCeilingType(doc, string.IsNullOrWhiteSpace(typeName) ? null : typeName);

            var loop = RevitOpJsonGeometry.TryBuildCurveLoop(level, op, out var loopError);
            if (loop == null)
            {
                throw new InvalidOperationException("create_ceiling: " + loopError);
            }

            Ceiling? ceiling;
            try
            {
                ceiling = Ceiling.Create(doc, new List<CurveLoop> { loop }, ceilingType.Id, level.Id);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("create_ceiling: " + ex.Message);
            }

            if (ceiling == null)
            {
                throw new InvalidOperationException("create_ceiling: Revit returned null.");
            }

            var label = op["name"]?.ToString();
            if (!string.IsNullOrWhiteSpace(label))
            {
                try
                {
                    ceiling.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(label);
                }
                catch
                {
                    // optional
                }
            }

            var kind = RevitCeilingVerticalPlacement.ParseKind(op);
            var drop = RevitCeilingVerticalPlacement.ReadFalseCeilingDropMeters(op, settings);
            var wallsHint = RevitCeilingVerticalPlacement.CollectWallsNearCurveLoop(doc, level.Id, loop, level.Elevation);
            RevitCeilingVerticalPlacement.ApplyAfterCreate(doc, ceiling, level, wallsHint, kind, drop, log);

            log.AppendLine("create_ceiling id=" + ceiling.Id);
        }

        /// <summary>JSON <c>create_ceiling_from_room</c> — ceiling from a placed room's boundary (same geometry as floor-from-room).</summary>
        public static void RunCreateCeilingFromRoomJsonOp(
            Document doc,
            JObject op,
            StringBuilder log,
            PluginSettings? pluginSettings = null)
        {
            var settings = pluginSettings ?? new PluginSettings();
            var roomId = op["roomId"]?.Value<long?>() ?? op["elementId"]?.Value<long?>();
            if (roomId == null)
            {
                throw new InvalidOperationException("create_ceiling_from_room requires roomId (or elementId for a Room).");
            }

            if (doc.GetElement(new ElementId((long)roomId)) is not Room room)
            {
                throw new InvalidOperationException("create_ceiling_from_room: element is not a Room.");
            }

            var level = doc.GetElement(room.LevelId) as Level
                        ?? throw new InvalidOperationException("create_ceiling_from_room: room has no valid level.");

            var z = level.Elevation;
            var boundaryLoc = RevitRoomBoundaryLoops.ParseBoundaryLocation(op["boundaryLocation"]?.ToString());
            var loops = RevitRoomBoundaryLoops.BuildCurveLoopsForSlab(room, z, boundaryLoc);
            var typeName = op["ceilingTypeName"]?.ToString();
            var ceilingType = ResolveCeilingType(doc, string.IsNullOrWhiteSpace(typeName) ? null : typeName);

            Ceiling? ceiling;
            try
            {
                ceiling = Ceiling.Create(doc, loops, ceilingType.Id, level.Id);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("create_ceiling_from_room: " + ex.Message);
            }

            if (ceiling == null)
            {
                throw new InvalidOperationException("create_ceiling_from_room: Revit returned null.");
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
                    ceiling.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(label);
                }
                catch
                {
                    // optional
                }
            }

            var kind = RevitCeilingVerticalPlacement.ParseKind(op);
            var drop = RevitCeilingVerticalPlacement.ReadFalseCeilingDropMeters(op, settings);
            var wallsHint = CollectWallsHintForRoomLoops(doc, level, loops);
            RevitCeilingVerticalPlacement.ApplyAfterCreate(doc, ceiling, level, wallsHint, kind, drop, log);

            log.AppendLine("create_ceiling_from_room id=" + ceiling.Id + " roomId=" + roomId);
        }

        private static List<Wall> CollectWallsHintForRoomLoops(Document doc, Level level, List<CurveLoop> loops)
        {
            var map = new Dictionary<int, Wall>();
            var z = level.Elevation;
            foreach (var loop in loops)
            {
                foreach (var w in RevitCeilingVerticalPlacement.CollectWallsNearCurveLoop(doc, level.Id, loop, z))
                {
                    map[w.Id.IntegerValue] = w;
                }
            }

            if (map.Count > 0)
            {
                return map.Values.ToList();
            }

            return new List<Wall>();
        }
    }
}
