using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitSketchPoC.Core.Configuration;
using RevitSketchPoC.Core.Geometry;
using RevitSketchPoC.Sketch.Contracts;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace RevitSketchPoC.RevitOperations.CreateElements
{
    /// <summary>Levels, wall types, and robust wall geometry creation from revitOps JSON or sketch segments.</summary>
    public static class RevitWallCreationOps
    {
        public static double MetersToFeet(double meters) => meters / 0.3048;

        public static Level ResolveLevel(Document doc, string? requestedLevelName)
        {
            return ResolveLevel(doc, requestedLevelName, null);
        }

        public static Level ResolveLevel(Document doc, string? requestedLevelName, StringBuilder? log)
        {
            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(x => x.Elevation)
                .ToList();

            if (!levels.Any())
            {
                throw new InvalidOperationException("No levels found in the Revit model.");
            }

            var requested = NormalizeName(requestedLevelName);
            if (!string.IsNullOrEmpty(requested))
            {
                var exact = levels.FirstOrDefault(x => string.Equals(x.Name, requested, StringComparison.OrdinalIgnoreCase));
                if (exact != null)
                {
                    return exact;
                }

                var contains = levels.FirstOrDefault(x =>
                    NormalizeName(x.Name).IndexOf(requested, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    requested.IndexOf(NormalizeName(x.Name), StringComparison.OrdinalIgnoreCase) >= 0);
                if (contains != null)
                {
                    log?.AppendLine("create_wall: levelName fallback by partial match -> \"" + contains.Name + "\".");
                    return contains;
                }
            }

            var preferred = PickPreferredLevel(levels);
            if (!string.IsNullOrEmpty(requested))
            {
                log?.AppendLine("create_wall: levelName \"" + requestedLevelName + "\" not found; fallback -> \"" + preferred.Name + "\".");
            }

            return preferred;
        }

        public static WallType ResolveWallType(Document doc, string? wallTypeName)
        {
            return ResolveWallType(doc, wallTypeName, null);
        }

        public static WallType ResolveWallType(Document doc, string? wallTypeName, StringBuilder? log)
        {
            var types = new FilteredElementCollector(doc)
                .OfClass(typeof(WallType))
                .Cast<WallType>()
                .ToList();

            if (!types.Any())
            {
                throw new InvalidOperationException("No wall types found in the Revit model.");
            }

            var requested = NormalizeName(wallTypeName);
            if (!string.IsNullOrEmpty(requested))
            {
                var exact = types.FirstOrDefault(x => string.Equals(x.Name, requested, StringComparison.OrdinalIgnoreCase));
                if (exact != null)
                {
                    return exact;
                }

                var contains = types.FirstOrDefault(x =>
                    NormalizeName(x.Name).IndexOf(requested, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    requested.IndexOf(NormalizeName(x.Name), StringComparison.OrdinalIgnoreCase) >= 0);
                if (contains != null)
                {
                    log?.AppendLine("create_wall: wallTypeName fallback by partial match -> \"" + contains.Name + "\".");
                    return contains;
                }
            }

            var preferred = PickPreferredWallType(types);
            if (!string.IsNullOrEmpty(requested))
            {
                log?.AppendLine("create_wall: wallTypeName \"" + wallTypeName + "\" not found; fallback -> \"" + preferred.Name + "\".");
            }

            return preferred;
        }

        /// <summary>Creates walls from 2D segments (coordinates in metres, plan XY) with normalization and de-duplication.</summary>
        public static List<Wall> CreateWallsFromSegments(
            Document doc,
            Level level,
            WallType wallType,
            IEnumerable<WallSegment> walls,
            double defaultWallHeightMeters)
        {
            var normalized = NormalizeWallSegments(walls);
            var output = new List<Wall>();
            foreach (var wall in normalized)
            {
                var p0 = new XYZ(MetersToFeet(wall.Start.X), MetersToFeet(wall.Start.Y), level.Elevation);
                var p1 = new XYZ(MetersToFeet(wall.End.X), MetersToFeet(wall.End.Y), level.Elevation);
                var line = Line.CreateBound(p0, p1);
                var height = wall.HeightMeters > 0 ? wall.HeightMeters : defaultWallHeightMeters;
                var revitWall = Wall.Create(doc, line, wallType.Id, level.Id, MetersToFeet(height), 0.0, false, false);
                output.Add(revitWall);
            }

            return output;
        }

        /// <summary>JSON op <c>create_wall</c>: segment in metres (plan).</summary>
        public static void RunCreateWallJsonOp(
            Document doc,
            JObject op,
            PluginSettings settings,
            StringBuilder log,
            ISet<string>? wallBatchKeys = null)
        {
            if (!TryReadPlanSegmentMeters(op, out var x0, out var y0, out var x1, out var y1))
            {
                throw new InvalidOperationException(
                    "create_wall requires startX/startY and endX/endY (numbers, metres), or start/end objects with x,y.");
            }

            var normalized = NormalizeWallSegments(new[]
            {
                new WallSegment
                {
                    Start = new Point2D { X = x0, Y = y0 },
                    End = new Point2D { X = x1, Y = y1 },
                    HeightMeters = ReadOptionalPositiveDouble(op, "heightMeters") ?? 0
                }
            });
            if (normalized.Count == 0)
            {
                throw new InvalidOperationException(
                    "create_wall: invalid geometry after normalization (too short/duplicate/intersection noise).");
            }

            var seg = normalized[0];
            RegisterLinearWallInBatch(seg.Start.X, seg.Start.Y, seg.End.X, seg.End.Y, wallBatchKeys);

            var levelName = op["levelName"]?.ToString();
            var wallTypeName = op["wallTypeName"]?.ToString();
            var level = ResolveLevel(doc, string.IsNullOrWhiteSpace(levelName) ? null : levelName, log);
            var wallType = ResolveWallType(doc, string.IsNullOrWhiteSpace(wallTypeName) ? null : wallTypeName, log);

            var defaultH = settings.DefaultWallHeightMeters > 0 ? settings.DefaultWallHeightMeters : 3.0;
            var height = seg.HeightMeters > 0 ? seg.HeightMeters : defaultH;

            var p0 = new XYZ(MetersToFeet(seg.Start.X), MetersToFeet(seg.Start.Y), level.Elevation);
            var p1 = new XYZ(MetersToFeet(seg.End.X), MetersToFeet(seg.End.Y), level.Elevation);
            var line = Line.CreateBound(p0, p1);
            var revitWall = Wall.Create(doc, line, wallType.Id, level.Id, MetersToFeet(height), 0.0, false, false);
            var lengthM = p0.DistanceTo(p1) * 0.3048;
            log.AppendLine("create_wall id=" + revitWall.Id + " length~=" + Math.Round(lengthM, 2) + "m");
        }

        /// <summary>JSON op <c>create_wall_arc</c>: curved wall by start/end/mid points in metres.</summary>
        public static void RunCreateWallArcJsonOp(
            Document doc,
            JObject op,
            PluginSettings settings,
            StringBuilder log,
            ISet<string>? wallBatchKeys = null)
        {
            if (!TryReadArcByThreePointsMeters(op, out var x0, out var y0, out var x1, out var y1, out var xm, out var ym) &&
                !TryReadArcByCenterAnglesMeters(op, out x0, out y0, out x1, out y1, out xm, out ym))
            {
                throw new InvalidOperationException(
                    "create_wall_arc requires start/end/mid points (startX,startY,endX,endY,midX,midY) or center+radius+angles.");
            }

            x0 = PlanGeometryRules.Snap(x0);
            y0 = PlanGeometryRules.Snap(y0);
            x1 = PlanGeometryRules.Snap(x1);
            y1 = PlanGeometryRules.Snap(y1);
            xm = PlanGeometryRules.Snap(xm);
            ym = PlanGeometryRules.Snap(ym);

            var chordLen = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
            if (chordLen < PlanGeometryRules.MinWallSegmentLengthMeters)
            {
                throw new InvalidOperationException("create_wall_arc: chord too short (< 0.35 m).");
            }

            RegisterArcWallInBatch(x0, y0, x1, y1, xm, ym, wallBatchKeys);

            var levelName = op["levelName"]?.ToString();
            var wallTypeName = op["wallTypeName"]?.ToString();
            var level = ResolveLevel(doc, string.IsNullOrWhiteSpace(levelName) ? null : levelName, log);
            var wallType = ResolveWallType(doc, string.IsNullOrWhiteSpace(wallTypeName) ? null : wallTypeName, log);

            var heightM = ReadOptionalPositiveDouble(op, "heightMeters");
            var defaultH = settings.DefaultWallHeightMeters > 0 ? settings.DefaultWallHeightMeters : 3.0;
            var height = heightM ?? defaultH;

            var p0 = new XYZ(MetersToFeet(x0), MetersToFeet(y0), level.Elevation);
            var p1 = new XYZ(MetersToFeet(x1), MetersToFeet(y1), level.Elevation);
            var pm = new XYZ(MetersToFeet(xm), MetersToFeet(ym), level.Elevation);
            Arc arc;
            try
            {
                arc = Arc.Create(p0, p1, pm);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("create_wall_arc: invalid arc definition (" + ex.Message + ").");
            }

            var revitWall = Wall.Create(doc, arc, wallType.Id, level.Id, MetersToFeet(height), 0.0, false, false);
            log.AppendLine("create_wall_arc id=" + revitWall.Id + " length~=" + Math.Round(arc.Length * 0.3048, 2) + "m");
        }

        public static List<WallSegment> NormalizeWallSegments(IEnumerable<WallSegment> walls)
        {
            var snapped = new List<WallSegment>();
            foreach (var wall in walls ?? Enumerable.Empty<WallSegment>())
            {
                var start = wall.Start ?? new Point2D();
                var end = wall.End ?? new Point2D();
                var x0 = PlanGeometryRules.Snap(start.X);
                var y0 = PlanGeometryRules.Snap(start.Y);
                var x1 = PlanGeometryRules.Snap(end.X);
                var y1 = PlanGeometryRules.Snap(end.Y);
                var len = DistanceMeters(x0, y0, x1, y1);
                if (len < PlanGeometryRules.MinWallSegmentLengthMeters)
                {
                    continue;
                }

                snapped.Add(new WallSegment
                {
                    Start = new Point2D { X = x0, Y = y0 },
                    End = new Point2D { X = x1, Y = y1 },
                    HeightMeters = wall.HeightMeters > 0 ? wall.HeightMeters : 0
                });
            }

            var split = SplitOrthogonalIntersections(snapped);
            var merged = MergeCollinearAxisSegments(split);
            return DeduplicateSegments(merged);
        }

        private static List<WallSegment> SplitOrthogonalIntersections(IReadOnlyList<WallSegment> input)
        {
            var output = new List<WallSegment>();
            for (var i = 0; i < input.Count; i++)
            {
                var seg = input[i];
                if (!IsHorizontal(seg) && !IsVertical(seg))
                {
                    output.Add(seg);
                    continue;
                }

                var cuts = new List<double> { 0.0, 1.0 };
                for (var j = 0; j < input.Count; j++)
                {
                    if (i == j)
                    {
                        continue;
                    }

                    var other = input[j];
                    if (IsHorizontal(seg) && IsVertical(other) &&
                        TryGetOrthogonalIntersection(seg, other, out var ix, out var iy))
                    {
                        var t = ParamAlong(seg, ix, iy);
                        if (t > 1e-6 && t < 1.0 - 1e-6)
                        {
                            cuts.Add(t);
                        }
                    }
                    else if (IsVertical(seg) && IsHorizontal(other) &&
                             TryGetOrthogonalIntersection(other, seg, out ix, out iy))
                    {
                        var t = ParamAlong(seg, ix, iy);
                        if (t > 1e-6 && t < 1.0 - 1e-6)
                        {
                            cuts.Add(t);
                        }
                    }
                }

                cuts = cuts
                    .Distinct()
                    .OrderBy(x => x)
                    .ToList();
                for (var k = 0; k < cuts.Count - 1; k++)
                {
                    var ta = cuts[k];
                    var tb = cuts[k + 1];
                    var a = PointAt(seg, ta);
                    var b = PointAt(seg, tb);
                    if (DistanceMeters(a.X, a.Y, b.X, b.Y) < PlanGeometryRules.MinWallSegmentLengthMeters)
                    {
                        continue;
                    }

                    output.Add(new WallSegment
                    {
                        Start = a,
                        End = b,
                        HeightMeters = seg.HeightMeters
                    });
                }
            }

            return output;
        }

        private static List<WallSegment> MergeCollinearAxisSegments(IReadOnlyList<WallSegment> input)
        {
            var list = input.ToList();
            var changed = true;
            while (changed)
            {
                changed = false;
                for (var i = 0; i < list.Count; i++)
                {
                    var a = list[i];
                    for (var j = i + 1; j < list.Count; j++)
                    {
                        var b = list[j];
                        if (TryMergeAxisSegments(a, b, out var merged))
                        {
                            list[i] = merged;
                            list.RemoveAt(j);
                            changed = true;
                            break;
                        }
                    }

                    if (changed)
                    {
                        break;
                    }
                }
            }

            return list;
        }

        private static bool TryMergeAxisSegments(WallSegment a, WallSegment b, out WallSegment merged)
        {
            merged = a;
            var tol = PlanGeometryRules.CollinearToleranceMeters;
            if (IsHorizontal(a) && IsHorizontal(b) &&
                Math.Abs(a.Start.Y - b.Start.Y) <= tol &&
                RangesTouchOrOverlap(a.Start.X, a.End.X, b.Start.X, b.End.X))
            {
                var y = (a.Start.Y + a.End.Y + b.Start.Y + b.End.Y) / 4.0;
                var xs = new[] { a.Start.X, a.End.X, b.Start.X, b.End.X };
                merged = new WallSegment
                {
                    Start = new Point2D { X = xs.Min(), Y = y },
                    End = new Point2D { X = xs.Max(), Y = y },
                    HeightMeters = Math.Max(a.HeightMeters, b.HeightMeters)
                };
                return true;
            }

            if (IsVertical(a) && IsVertical(b) &&
                Math.Abs(a.Start.X - b.Start.X) <= tol &&
                RangesTouchOrOverlap(a.Start.Y, a.End.Y, b.Start.Y, b.End.Y))
            {
                var x = (a.Start.X + a.End.X + b.Start.X + b.End.X) / 4.0;
                var ys = new[] { a.Start.Y, a.End.Y, b.Start.Y, b.End.Y };
                merged = new WallSegment
                {
                    Start = new Point2D { X = x, Y = ys.Min() },
                    End = new Point2D { X = x, Y = ys.Max() },
                    HeightMeters = Math.Max(a.HeightMeters, b.HeightMeters)
                };
                return true;
            }

            return false;
        }

        private static List<WallSegment> DeduplicateSegments(IReadOnlyList<WallSegment> input)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var output = new List<WallSegment>();
            foreach (var seg in input)
            {
                var len = DistanceMeters(seg.Start.X, seg.Start.Y, seg.End.X, seg.End.Y);
                if (len < PlanGeometryRules.MinWallSegmentLengthMeters)
                {
                    continue;
                }

                var key = BuildUndirectedLinearKey(seg.Start.X, seg.Start.Y, seg.End.X, seg.End.Y);
                if (!seen.Add(key))
                {
                    continue;
                }

                output.Add(seg);
            }

            return output;
        }

        private static bool TryReadPlanSegmentMeters(JObject op, out double x0, out double y0, out double x1, out double y1)
        {
            x0 = y0 = x1 = y1 = 0;
            if (op["start"] is JObject s && op["end"] is JObject e)
            {
                if (TryReadNumber(s["x"], out x0) && TryReadNumber(s["y"], out y0) &&
                    TryReadNumber(e["x"], out x1) && TryReadNumber(e["y"], out y1))
                {
                    return true;
                }
            }

            return TryReadNumber(op["startX"], out x0) && TryReadNumber(op["startY"], out y0) &&
                   TryReadNumber(op["endX"], out x1) && TryReadNumber(op["endY"], out y1);
        }

        /// <summary>Plan segment in metres — same JSON shape as <c>create_wall</c> (start/end or startX..endY).</summary>
        public static bool TryReadPlanSegmentMetersFromJson(
            JObject op,
            out double x0,
            out double y0,
            out double x1,
            out double y1) =>
            TryReadPlanSegmentMeters(op, out x0, out y0, out x1, out y1);

        private static bool TryReadArcByThreePointsMeters(
            JObject op,
            out double x0,
            out double y0,
            out double x1,
            out double y1,
            out double xm,
            out double ym)
        {
            x0 = y0 = x1 = y1 = xm = ym = 0;
            if (!TryReadPlanSegmentMeters(op, out x0, out y0, out x1, out y1))
            {
                return false;
            }

            if (op["mid"] is JObject midObj &&
                TryReadNumber(midObj["x"], out xm) &&
                TryReadNumber(midObj["y"], out ym))
            {
                return true;
            }

            return TryReadNumber(op["midX"], out xm) && TryReadNumber(op["midY"], out ym);
        }

        private static bool TryReadArcByCenterAnglesMeters(
            JObject op,
            out double x0,
            out double y0,
            out double x1,
            out double y1,
            out double xm,
            out double ym)
        {
            x0 = y0 = x1 = y1 = xm = ym = 0;
            if (!TryReadNumber(op["centerX"], out var cx) ||
                !TryReadNumber(op["centerY"], out var cy) ||
                !TryReadNumber(op["radiusMeters"], out var r) ||
                !TryReadNumber(op["startAngleDegrees"], out var a0Deg) ||
                !TryReadNumber(op["endAngleDegrees"], out var a1Deg))
            {
                return false;
            }

            if (r <= 0)
            {
                return false;
            }

            var a0 = DegreesToRadians(a0Deg);
            var a1 = DegreesToRadians(a1Deg);
            var sweep = NormalizePositiveAngle(a1 - a0);
            if (sweep < 1e-6 || Math.Abs(sweep - 2 * Math.PI) < 1e-6)
            {
                return false;
            }

            var am = a0 + (sweep / 2.0);
            x0 = cx + r * Math.Cos(a0);
            y0 = cy + r * Math.Sin(a0);
            x1 = cx + r * Math.Cos(a1);
            y1 = cy + r * Math.Sin(a1);
            xm = cx + r * Math.Cos(am);
            ym = cy + r * Math.Sin(am);
            return true;
        }

        private static double? ReadOptionalPositiveDouble(JObject op, string key)
        {
            if (!TryReadNumber(op[key], out var v) || v <= 0)
            {
                return null;
            }

            return v;
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

        private static void RegisterLinearWallInBatch(double x0, double y0, double x1, double y1, ISet<string>? batch)
        {
            if (batch == null)
            {
                return;
            }

            var key = BuildUndirectedLinearKey(x0, y0, x1, y1);
            if (!batch.Add(key))
            {
                throw new InvalidOperationException(
                    "create_wall: duplicate or overlapping segment in the same request. Adjust coordinates.");
            }
        }

        private static void RegisterArcWallInBatch(
            double x0,
            double y0,
            double x1,
            double y1,
            double xm,
            double ym,
            ISet<string>? batch)
        {
            if (batch == null)
            {
                return;
            }

            var fwd = "arc|" + Quantize(x0) + "," + Quantize(y0) + "|" + Quantize(xm) + "," + Quantize(ym) + "|" + Quantize(x1) + "," + Quantize(y1);
            var rev = "arc|" + Quantize(x1) + "," + Quantize(y1) + "|" + Quantize(xm) + "," + Quantize(ym) + "|" + Quantize(x0) + "," + Quantize(y0);
            var key = string.CompareOrdinal(fwd, rev) <= 0 ? fwd : rev;
            if (!batch.Add(key))
            {
                throw new InvalidOperationException(
                    "create_wall_arc: duplicate curved segment in the same request. Adjust coordinates.");
            }
        }

        private static string BuildUndirectedLinearKey(double x0, double y0, double x1, double y1)
        {
            var k1 = Quantize(x0) + "," + Quantize(y0) + "|" + Quantize(x1) + "," + Quantize(y1);
            var k2 = Quantize(x1) + "," + Quantize(y1) + "|" + Quantize(x0) + "," + Quantize(y0);
            return string.CompareOrdinal(k1, k2) <= 0 ? "line|" + k1 : "line|" + k2;
        }

        private static string Quantize(double v)
        {
            return Math.Round(v, 3).ToString(CultureInfo.InvariantCulture);
        }

        private static bool IsHorizontal(WallSegment seg)
        {
            return Math.Abs(seg.Start.Y - seg.End.Y) <= PlanGeometryRules.CollinearToleranceMeters;
        }

        private static bool IsVertical(WallSegment seg)
        {
            return Math.Abs(seg.Start.X - seg.End.X) <= PlanGeometryRules.CollinearToleranceMeters;
        }

        private static bool TryGetOrthogonalIntersection(WallSegment horizontal, WallSegment vertical, out double x, out double y)
        {
            x = vertical.Start.X;
            y = horizontal.Start.Y;
            var tol = PlanGeometryRules.EndpointJoinToleranceMeters;
            var hxMin = Math.Min(horizontal.Start.X, horizontal.End.X) - tol;
            var hxMax = Math.Max(horizontal.Start.X, horizontal.End.X) + tol;
            var vyMin = Math.Min(vertical.Start.Y, vertical.End.Y) - tol;
            var vyMax = Math.Max(vertical.Start.Y, vertical.End.Y) + tol;
            var vx = x;
            var hy = y;
            if (vx < hxMin || vx > hxMax || hy < vyMin || hy > vyMax)
            {
                return false;
            }

            return true;
        }

        private static bool RangesTouchOrOverlap(double a0, double a1, double b0, double b1)
        {
            var tol = PlanGeometryRules.EndpointJoinToleranceMeters;
            var amin = Math.Min(a0, a1);
            var amax = Math.Max(a0, a1);
            var bmin = Math.Min(b0, b1);
            var bmax = Math.Max(b0, b1);
            return !(amax + tol < bmin || bmax + tol < amin);
        }

        private static double ParamAlong(WallSegment seg, double x, double y)
        {
            var dx = seg.End.X - seg.Start.X;
            var dy = seg.End.Y - seg.Start.Y;
            if (Math.Abs(dx) >= Math.Abs(dy))
            {
                return Math.Abs(dx) < 1e-9 ? 0 : (x - seg.Start.X) / dx;
            }

            return Math.Abs(dy) < 1e-9 ? 0 : (y - seg.Start.Y) / dy;
        }

        private static Point2D PointAt(WallSegment seg, double t)
        {
            return new Point2D
            {
                X = seg.Start.X + ((seg.End.X - seg.Start.X) * t),
                Y = seg.Start.Y + ((seg.End.Y - seg.Start.Y) * t)
            };
        }

        private static double DistanceMeters(double x0, double y0, double x1, double y1)
        {
            var dx = x1 - x0;
            var dy = y1 - y0;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static string NormalizeName(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        private static Level PickPreferredLevel(IReadOnlyList<Level> levels)
        {
            var ranked = levels
                .Select(l => new { Level = l, Score = ScoreLevelName(l.Name) })
                .OrderByDescending(x => x.Score)
                .ThenBy(x => Math.Abs(x.Level.Elevation))
                .FirstOrDefault();
            return ranked?.Level ?? levels.First();
        }

        private static WallType PickPreferredWallType(IReadOnlyList<WallType> types)
        {
            var ranked = types
                .Select(t => new { Type = t, Score = ScoreWallTypeName(t.Name) })
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Type.Name.Length)
                .FirstOrDefault();
            return ranked?.Type ?? types.First();
        }

        private static int ScoreLevelName(string? name)
        {
            var n = NormalizeName(name).ToLowerInvariant();
            if (n.Contains("level 1") || n.Contains("nível 1") || n.Contains("nivel 1"))
            {
                return 100;
            }

            if (n.Contains("ground") || n.Contains("piso 0") || n.Contains("rés do chão") || n.Contains("res do chao"))
            {
                return 90;
            }

            if (n.Contains("level") || n.Contains("nível") || n.Contains("nivel") || n.Contains("piso"))
            {
                return 50;
            }

            return 0;
        }

        private static int ScoreWallTypeName(string? name)
        {
            var n = NormalizeName(name).ToLowerInvariant();
            if (n.Contains("generic") || n.Contains("genérica") || n.Contains("generica"))
            {
                return 100;
            }

            if (n.Contains("basic") || n.Contains("básica") || n.Contains("basica"))
            {
                return 80;
            }

            return 0;
        }

        private static double DegreesToRadians(double deg)
        {
            return deg * Math.PI / 180.0;
        }

        private static double NormalizePositiveAngle(double rad)
        {
            var twoPi = 2 * Math.PI;
            var value = rad % twoPi;
            if (value < 0)
            {
                value += twoPi;
            }

            return value;
        }
    }
}
