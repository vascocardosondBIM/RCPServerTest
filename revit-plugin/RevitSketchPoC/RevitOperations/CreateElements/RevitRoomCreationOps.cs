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
    /// <summary>Rooms from region centroids or JSON <c>create_room</c> (placement in metres on level).</summary>
    public static class RevitRoomCreationOps
    {
        /// <summary>Creates rooms from closed regions (centroid of boundary in metres).</summary>
        public static int CreateRoomsFromRegions(Document doc, Level level, IEnumerable<RoomRegion> rooms)
        {
            var created = 0;
            foreach (var room in rooms)
            {
                if (room.Boundary.Count == 0) continue;
                var centroid = ComputeCentroid(room.Boundary);
                var uv = new UV(RevitWallCreationOps.MetersToFeet(centroid.X), RevitWallCreationOps.MetersToFeet(centroid.Y));
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

        /// <summary>JSON op <c>create_room</c>: centre in metres on level plane.</summary>
        public static void RunCreateRoomJsonOp(Document doc, JObject op, StringBuilder log)
        {
            if (!TryReadCenterMeters(op, out var cx, out var cy))
            {
                throw new InvalidOperationException(
                    "create_room requires centerX/centerY (metres) or center object { x, y }.");
            }

            var levelName = op["levelName"]?.ToString();
            var level = RevitWallCreationOps.ResolveLevel(doc, string.IsNullOrWhiteSpace(levelName) ? null : levelName);
            var uv = new UV(RevitWallCreationOps.MetersToFeet(cx), RevitWallCreationOps.MetersToFeet(cy));
            var name = op["name"]?.ToString();

            Element roomEl;
            try
            {
                roomEl = doc.Create.NewRoom(level, uv);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("create_room: Revit refused placement (" + ex.Message + ").");
            }

            var roomName = string.IsNullOrWhiteSpace(name) ? "Room" : name.Trim();
            if (roomEl is SpatialElement spatial)
            {
                spatial.Name = roomName;
            }

            log.AppendLine("create_room id=" + roomEl.Id + " name=\"" + roomName + "\"");
        }

        private static Point2D ComputeCentroid(IReadOnlyCollection<Point2D> points)
        {
            return new Point2D
            {
                X = points.Average(x => x.X),
                Y = points.Average(x => x.Y)
            };
        }

        private static bool TryReadCenterMeters(JObject op, out double cx, out double cy)
        {
            cx = cy = 0;
            if (op["center"] is JObject c)
            {
                if (TryReadNumber(c["x"], out cx) && TryReadNumber(c["y"], out cy))
                {
                    return true;
                }
            }

            return TryReadNumber(op["centerX"], out cx) && TryReadNumber(op["centerY"], out cy);
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
