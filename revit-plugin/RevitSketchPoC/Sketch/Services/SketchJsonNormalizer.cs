using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using RevitSketchPoC.Sketch.Contracts;

namespace RevitSketchPoC.Sketch.Services
{
    /// <summary>
    /// Maps common alternate LLM JSON shapes (gpt-oss, etc.) into the strict schema expected by <see cref="SketchInterpretation"/>.
    /// </summary>
    internal static class SketchJsonNormalizer
    {
        public static void Apply(JObject root)
        {
            HoistNestedSketchObject(root);
            PromoteAlternateWallArrays(root);
            NormalizeWallsArray(root);
            NormalizeRoomsArray(root);
            NormalizeDoorsArray(root);
        }

        /// <summary>If walls are empty but room polygons exist, build wall segments from shared edges.</summary>
        public static void DeriveWallsFromRoomBoundariesIfNeeded(SketchInterpretation interpretation)
        {
            if (interpretation == null || interpretation.Walls.Count > 0)
            {
                return;
            }

            if (interpretation.Rooms == null || interpretation.Rooms.Count == 0)
            {
                return;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var room in interpretation.Rooms)
            {
                var b = room.Boundary;
                if (b == null || b.Count < 2)
                {
                    continue;
                }

                var n = b.Count;
                for (var i = 0; i < n; i++)
                {
                    var a = b[i];
                    var c = b[(i + 1) % n];
                    var key = EdgeKey(a.X, a.Y, c.X, c.Y);
                    if (!seen.Add(key))
                    {
                        continue;
                    }

                    var dx = c.X - a.X;
                    var dy = c.Y - a.Y;
                    var len = Math.Sqrt(dx * dx + dy * dy);
                    if (len < 0.35)
                    {
                        continue;
                    }

                    interpretation.Walls.Add(new WallSegment
                    {
                        Start = new Point2D { X = a.X, Y = a.Y },
                        End = new Point2D { X = c.X, Y = c.Y }
                    });
                }
            }
        }

        private static string EdgeKey(double x1, double y1, double x2, double y2)
        {
            var k1 = Round6(x1) + "," + Round6(y1) + "|" + Round6(x2) + "," + Round6(y2);
            var k2 = Round6(x2) + "," + Round6(y2) + "|" + Round6(x1) + "," + Round6(y1);
            return string.CompareOrdinal(k1, k2) <= 0 ? k1 : k2;
        }

        private static string Round6(double v)
        {
            return Math.Round(v, 6).ToString(CultureInfo.InvariantCulture);
        }

        private static void HoistNestedSketchObject(JObject root)
        {
            if (HasNonEmptyWallArray(root))
            {
                return;
            }

            if (root.Properties().Count() == 1)
            {
                var only = root.Properties().First().Value as JObject;
                if (only != null && HasNonEmptyWallArray(only))
                {
                    MergeSketchFieldsInto(root, only);
                    return;
                }
            }

            foreach (var prop in root.Properties())
            {
                if (prop.Value is not JObject child)
                {
                    continue;
                }

                if (!HasNonEmptyWallArray(child))
                {
                    continue;
                }

                MergeSketchFieldsInto(root, child);
                return;
            }
        }

        private static void MergeSketchFieldsInto(JObject target, JObject source)
        {
            foreach (var c in source.Properties())
            {
                var n = c.Name.ToLowerInvariant();
                if (n is not ("walls" or "rooms" or "doors" or "notes"))
                {
                    continue;
                }

                var key = n;
                RemovePropertyIgnoreCase(target, key);
                target[key] = c.Value is JToken t ? t.DeepClone() : c.Value;
            }
        }

        private static void RemovePropertyIgnoreCase(JObject o, string name)
        {
            var p = o.Properties().FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            p?.Remove();
        }

        private static void PromoteAlternateWallArrays(JObject root)
        {
            if (HasNonEmptyWallArray(root))
            {
                return;
            }

            foreach (var key in new[]
                     {
                         "wall_segments", "wallSegments", "lines", "line_segments", "segments", "exterior_walls", "exteriorWalls",
                         "walls_list", "wall"
                     })
            {
                var arr = FindTokenIgnoreCase(root, key) as JArray;
                if (arr == null || arr.Count == 0)
                {
                    continue;
                }

                RemovePropertyIgnoreCase(root, key);
                root["walls"] = arr;
                return;
            }
        }

        private static void NormalizeWallsArray(JObject root)
        {
            var walls = GetArrayIgnoreCase(root, "walls");
            if (walls == null)
            {
                return;
            }

            var mapped = new JArray();
            foreach (var item in walls)
            {
                var seg = NormalizeWallObject(item as JObject);
                if (seg != null)
                {
                    mapped.Add(seg);
                }
            }

            if (mapped.Count == 0 && walls.Count > 0)
            {
                return;
            }

            RemovePropertyIgnoreCase(root, "walls");
            root["walls"] = mapped;
        }

        private static JObject? NormalizeWallObject(JObject? o)
        {
            if (o == null)
            {
                return null;
            }

            var start = GetPointObject(o, "start", "from", "a", "p0", "begin", "startPoint", "start_point");
            var end = GetPointObject(o, "end", "to", "b", "p1", "finish", "endPoint", "end_point");
            if (start != null && end != null)
            {
                return BuildWallJObject(start, end, o);
            }

            var x1 = GetNumber(o, "x1", "X1");
            var y1 = GetNumber(o, "y1", "Y1");
            var x2 = GetNumber(o, "x2", "X2");
            var y2 = GetNumber(o, "y2", "Y2");
            if (x1.HasValue && y1.HasValue && x2.HasValue && y2.HasValue)
            {
                return new JObject
                {
                    ["start"] = new JObject { ["x"] = x1.Value, ["y"] = y1.Value },
                    ["end"] = new JObject { ["x"] = x2.Value, ["y"] = y2.Value },
                    ["heightMeters"] = GetNumber(o, "heightMeters", "height_meters", "height") ?? 3.0
                };
            }

            return null;
        }

        private static JObject BuildWallJObject(JObject start, JObject end, JObject source)
        {
            var h = GetNumber(source, "heightMeters", "height_meters", "height", "HeightMeters");
            return new JObject
            {
                ["start"] = start,
                ["end"] = end,
                ["heightMeters"] = h ?? 3.0
            };
        }

        private static void NormalizeRoomsArray(JObject root)
        {
            var rooms = GetArrayIgnoreCase(root, "rooms");
            if (rooms == null)
            {
                return;
            }

            var mapped = new JArray();
            foreach (var item in rooms)
            {
                var room = NormalizeRoomObject(item as JObject);
                if (room != null)
                {
                    mapped.Add(room);
                }
            }

            if (mapped.Count == 0 && rooms.Count > 0)
            {
                return;
            }

            RemovePropertyIgnoreCase(root, "rooms");
            root["rooms"] = mapped;
        }

        private static JObject? NormalizeRoomObject(JObject? o)
        {
            if (o == null)
            {
                return null;
            }

            var boundary = FindTokenIgnoreCase(o, "boundary") as JArray
                           ?? FindTokenIgnoreCase(o, "vertices") as JArray
                           ?? FindTokenIgnoreCase(o, "polygon") as JArray
                           ?? FindTokenIgnoreCase(o, "corners") as JArray
                           ?? FindTokenIgnoreCase(o, "points") as JArray;

            var nameTok = FindTokenIgnoreCase(o, "name") ?? FindTokenIgnoreCase(o, "label");
            var name = nameTok?.Type == JTokenType.String ? nameTok.Value<string>() ?? "Room" : nameTok?.ToString() ?? "Room";

            if (boundary == null || boundary.Count < 2)
            {
                return null;
            }

            var pts = new JArray();
            foreach (var p in boundary)
            {
                var pt = NormalizePointToken(p);
                if (pt != null)
                {
                    pts.Add(pt);
                }
            }

            if (pts.Count < 2)
            {
                return null;
            }

            return new JObject
            {
                ["name"] = name,
                ["boundary"] = pts
            };
        }

        private static void NormalizeDoorsArray(JObject root)
        {
            var doors = GetArrayIgnoreCase(root, "doors");
            if (doors == null)
            {
                return;
            }

            var mapped = new JArray();
            foreach (var item in doors)
            {
                var d = NormalizeDoorObject(item as JObject);
                if (d != null)
                {
                    mapped.Add(d);
                }
            }

            if (mapped.Count == 0 && doors.Count > 0)
            {
                return;
            }

            RemovePropertyIgnoreCase(root, "doors");
            root["doors"] = mapped;
        }

        private static JObject? NormalizeDoorObject(JObject? o)
        {
            if (o == null)
            {
                return null;
            }

            var loc = GetPointObject(o, "location", "position", "point", "pt", "center", "place");
            if (loc == null)
            {
                return null;
            }

            return new JObject { ["location"] = loc };
        }

        private static JArray? GetArrayIgnoreCase(JObject root, string propName)
        {
            var p = root.Properties().FirstOrDefault(x => string.Equals(x.Name, propName, StringComparison.OrdinalIgnoreCase));
            return p?.Value as JArray;
        }

        private static JObject? GetPointObject(JObject o, params string[] names)
        {
            foreach (var n in names)
            {
                var t = FindTokenIgnoreCase(o, n);
                if (t is JObject jo)
                {
                    var pt = NormalizePointObject(jo);
                    if (pt != null)
                    {
                        return pt;
                    }
                }
            }

            return null;
        }

        private static JObject? NormalizePointToken(JToken? p)
        {
            if (p is JObject jo)
            {
                return NormalizePointObject(jo);
            }

            if (p is JArray ja && ja.Count >= 2)
            {
                return new JObject
                {
                    ["x"] = ja[0].Value<double>(),
                    ["y"] = ja[1].Value<double>()
                };
            }

            return null;
        }

        private static JObject? NormalizePointObject(JObject jo)
        {
            var x = GetNumber(jo, "x", "X", "lon", "lng");
            var y = GetNumber(jo, "y", "Y", "lat");
            if (!x.HasValue || !y.HasValue)
            {
                return null;
            }

            return new JObject { ["x"] = x.Value, ["y"] = y.Value };
        }

        private static double? GetNumber(JObject o, params string[] names)
        {
            foreach (var n in names)
            {
                var t = FindTokenIgnoreCase(o, n);
                if (t == null || t.Type == JTokenType.Null)
                {
                    continue;
                }

                try
                {
                    return t.Value<double>();
                }
                catch
                {
                    if (double.TryParse(t.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    {
                        return d;
                    }
                }
            }

            return null;
        }

        private static JToken? FindTokenIgnoreCase(JObject o, string name)
        {
            var p = o.Properties().FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            return p?.Value;
        }

        private static bool HasNonEmptyWallArray(JObject root)
        {
            var w = GetArrayIgnoreCase(root, "walls");
            return w != null && w.Count > 0;
        }
    }
}
