using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitSketchPoC.RevitOperations.Shared
{
    /// <summary>
    /// Builds horizontal <see cref="CurveLoop"/>s from a placed <see cref="Room"/>'s computed boundary (lines + arcs; other curves tessellated).
    /// </summary>
    public static class RevitRoomBoundaryLoops
    {
        /// <summary>
        /// Default: <see cref="SpatialElementBoundaryLocation.Finish"/> — room boundary at finish faces (inside the room),
        /// not at the wall location line. Use <c>boundaryLocation: \"center\"</c> in JSON for centreline slabs.
        /// </summary>
        public static SpatialElementBoundaryLocation DefaultBoundaryLocation => SpatialElementBoundaryLocation.Finish;

        public static SpatialElementBoundaryLocation ParseBoundaryLocation(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return DefaultBoundaryLocation;
            }

            var s = raw.Trim().ToLowerInvariant().Replace(" ", string.Empty).Replace("_", string.Empty);
            return s switch
            {
                "finish" => SpatialElementBoundaryLocation.Finish,
                "finishwithoutcw" => SpatialElementBoundaryLocation.Finish,
                "center" or "centre" or "wallcenter" or "wallcenterline" or "locationline" =>
                    SpatialElementBoundaryLocation.Center,
                "coreboundary" or "core" => SpatialElementBoundaryLocation.CoreBoundary,
                "corecenter" => SpatialElementBoundaryLocation.CoreCenter,
                _ => DefaultBoundaryLocation
            };
        }

        /// <summary>
        /// For footprint analyze/repair: use <paramref name="explicitRoomId"/> when it is a placed <see cref="Room"/> on <paramref name="levelId"/>,
        /// otherwise pick the largest room on that level whose location point lies inside the slab outline (metres XY).
        /// </summary>
        public static Room? TryAssociateRoomForSlabOnLevel(
            Document doc,
            ElementId levelId,
            long? explicitRoomId,
            IReadOnlyList<(double X, double Y)>? slabOutlineMeters)
        {
            if (explicitRoomId != null)
            {
                if (doc.GetElement(new ElementId((long)explicitRoomId)) is Room r &&
                    r.LevelId == levelId &&
                    r.Area > 1e-9)
                {
                    return r;
                }
            }

            if (slabOutlineMeters == null || slabOutlineMeters.Count < 3)
            {
                return null;
            }

            Room? best = null;
            var bestArea = 0.0;
            foreach (var el in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_Rooms)
                         .WhereElementIsNotElementType())
            {
                if (el is not Room room || room.LevelId != levelId || room.Area <= 1e-9)
                {
                    continue;
                }

                if (!TryRoomLocationPointMeters(room, out var cx, out var cy))
                {
                    continue;
                }

                if (!PointInPolygon2D(slabOutlineMeters, cx, cy))
                {
                    continue;
                }

                if (room.Area > bestArea)
                {
                    bestArea = room.Area;
                    best = room;
                }
            }

            return best;
        }

        private static bool TryRoomLocationPointMeters(Room room, out double x, out double y)
        {
            x = y = 0;
            if (room.Location is not LocationPoint lp)
            {
                return false;
            }

            var p = lp.Point;
            x = UnitUtils.ConvertFromInternalUnits(p.X, UnitTypeId.Meters);
            y = UnitUtils.ConvertFromInternalUnits(p.Y, UnitTypeId.Meters);
            return true;
        }

        private static bool PointInPolygon2D(IReadOnlyList<(double X, double Y)> poly, double x, double y)
        {
            var inside = false;
            var n = poly.Count;
            for (var i = 0; i < n; i++)
            {
                var j = (i + 1) % n;
                var pi = poly[i];
                var pj = poly[j];
                if ((pi.Y > y) != (pj.Y > y))
                {
                    var denom = pj.Y - pi.Y;
                    if (Math.Abs(denom) < 1e-15)
                    {
                        continue;
                    }

                    var xInt = (pj.X - pi.X) * (y - pi.Y) / denom + pi.X;
                    if (x < xInt)
                    {
                        inside = !inside;
                    }
                }
            }

            return inside;
        }

        /// <summary>
        /// Outer loop first (largest plan area), then remaining loops (openings / islands) in arbitrary order.
        /// </summary>
        public static List<CurveLoop> BuildCurveLoopsForSlab(
            Room room,
            double zElevation,
            SpatialElementBoundaryLocation location = SpatialElementBoundaryLocation.Center)
        {
            if (room == null)
            {
                throw new ArgumentNullException(nameof(room));
            }

            var options = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = location };
            IList<IList<BoundarySegment>>? raw;
            try
            {
                raw = room.GetBoundarySegments(options);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Room.GetBoundarySegments failed: " + ex.Message);
            }

            if (raw == null || raw.Count == 0)
            {
                throw new InvalidOperationException(
                    "Room has no boundary segments (room may be unplaced, not enclosed, or needs regeneration). " +
                    "Close the loop with walls and ensure the room is computed.");
            }

            var loops = new List<CurveLoop>();
            foreach (var segLoop in raw)
            {
                if (segLoop == null || segLoop.Count == 0)
                {
                    continue;
                }

                var curves = new List<Curve>();
                foreach (var seg in segLoop)
                {
                    var c = seg?.GetCurve();
                    if (c == null || c.Length < 1e-9)
                    {
                        continue;
                    }

                    foreach (var flat in FlattenCurveToPlanZ(c, zElevation))
                    {
                        if (flat != null && flat.Length >= 1e-6)
                        {
                            curves.Add(flat);
                        }
                    }
                }

                if (curves.Count < 2)
                {
                    continue;
                }

                CurveLoop cl;
                try
                {
                    cl = CurveLoop.Create(curves);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("Invalid room boundary loop: " + ex.Message);
                }

                loops.Add(cl);
            }

            if (loops.Count == 0)
            {
                throw new InvalidOperationException("Could not build any closed curve loop from room boundary.");
            }

            loops.Sort((a, b) => Math.Abs(PlanLoopAreaInternal(a)).CompareTo(Math.Abs(PlanLoopAreaInternal(b))));
            loops.Reverse();
            return loops;
        }

        /// <summary>
        /// Read-only tessellation of the primary (largest) room loop in plan metres — for footprint compare / analyze.
        /// </summary>
        public static bool TryTessellatePrimaryRoomBoundaryMeters(
            Room room,
            double zElevation,
            SpatialElementBoundaryLocation location,
            int maxPointsPerCurve,
            out List<(double X, double Y)> boundaryMeters,
            out double? areaSquareMeters,
            out string? errorMessage)
        {
            boundaryMeters = new List<(double X, double Y)>();
            areaSquareMeters = null;
            errorMessage = null;
            try
            {
                var loops = BuildCurveLoopsForSlab(room, zElevation, location);
                if (loops.Count == 0)
                {
                    errorMessage = "no boundary loops";
                    return false;
                }

                boundaryMeters = TessellateCurveLoopToMetersXY(loops[0], maxPointsPerCurve);
                if (boundaryMeters.Count < 3)
                {
                    errorMessage = "boundary tessellation too small";
                    return false;
                }

                areaSquareMeters = Math.Abs(SignedArea2DMeters(boundaryMeters));
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        private static List<(double X, double Y)> TessellateCurveLoopToMetersXY(CurveLoop loop, int maxPerCurve)
        {
            var pts = new List<(double X, double Y)>();
            foreach (var curve in loop)
            {
                IList<XYZ> t;
                try
                {
                    t = curve.Tessellate();
                }
                catch
                {
                    continue;
                }

                if (t == null || t.Count == 0)
                {
                    continue;
                }

                var step = Math.Max(1, (t.Count - 1) / Math.Max(1, maxPerCurve));
                for (var i = 0; i < t.Count; i += step)
                {
                    var p = t[i];
                    var xm = UnitUtils.ConvertFromInternalUnits(p.X, UnitTypeId.Meters);
                    var ym = UnitUtils.ConvertFromInternalUnits(p.Y, UnitTypeId.Meters);
                    if (pts.Count == 0 ||
                        (pts[pts.Count - 1].X - xm) * (pts[pts.Count - 1].X - xm) +
                        (pts[pts.Count - 1].Y - ym) * (pts[pts.Count - 1].Y - ym) > 1e-12)
                    {
                        pts.Add((xm, ym));
                    }
                }
            }

            if (pts.Count >= 2 &&
                (pts[0].X - pts[pts.Count - 1].X) * (pts[0].X - pts[pts.Count - 1].X) +
                (pts[0].Y - pts[pts.Count - 1].Y) * (pts[0].Y - pts[pts.Count - 1].Y) < 1e-12)
            {
                pts.RemoveAt(pts.Count - 1);
            }

            return pts;
        }

        private static double SignedArea2DMeters(List<(double X, double Y)> poly)
        {
            double s = 0;
            var n = poly.Count;
            for (var i = 0; i < n; i++)
            {
                var j = (i + 1) % n;
                s += poly[i].X * poly[j].Y - poly[j].X * poly[i].Y;
            }

            return s * 0.5;
        }

        private static IEnumerable<Curve> FlattenCurveToPlanZ(Curve c, double z)
        {
            switch (c)
            {
                case Line ln:
                    yield return Line.CreateBound(XyZ(ln.GetEndPoint(0), z), XyZ(ln.GetEndPoint(1), z));
                    yield break;
                case Arc arc:
                {
                    var p0 = XyZ(arc.GetEndPoint(0), z);
                    var p1 = XyZ(arc.GetEndPoint(1), z);
                    var pm = arc.Evaluate(0.5, true);
                    var mid = XyZ(pm, z);
                    Curve? a = null;
                    try
                    {
                        a = Arc.Create(p0, p1, mid);
                    }
                    catch
                    {
                        a = null;
                    }

                    if (a != null)
                    {
                        yield return a;
                        yield break;
                    }

                    foreach (var q in TessellateAsLines(arc, z))
                    {
                        yield return q;
                    }

                    yield break;
                }
                default:
                    foreach (var q in TessellateAsLines(c, z))
                    {
                        yield return q;
                    }

                    yield break;
            }
        }

        private static IEnumerable<Line> TessellateAsLines(Curve c, double z)
        {
            IList<XYZ> pts;
            try
            {
                pts = c.Tessellate();
            }
            catch
            {
                yield break;
            }

            if (pts == null || pts.Count < 2)
            {
                yield break;
            }

            for (var i = 0; i < pts.Count - 1; i++)
            {
                var a = XyZ(pts[i], z);
                var b = XyZ(pts[i + 1], z);
                if (a.DistanceTo(b) >= 1e-6)
                {
                    yield return Line.CreateBound(a, b);
                }
            }
        }

        private static XYZ XyZ(XYZ p, double z) => new XYZ(p.X, p.Y, z);

        private static double PlanLoopAreaInternal(CurveLoop loop)
        {
            var pts = new List<XYZ>();
            foreach (var curve in loop)
            {
                IList<XYZ> t;
                try
                {
                    t = curve.Tessellate();
                }
                catch
                {
                    continue;
                }

                if (t == null || t.Count == 0)
                {
                    continue;
                }

                var step = Math.Max(1, (t.Count - 1) / 48);
                for (var i = 0; i < t.Count; i += step)
                {
                    var p = t[i];
                    if (pts.Count == 0 || pts[pts.Count - 1].DistanceTo(p) > 1e-6)
                    {
                        pts.Add(p);
                    }
                }
            }

            if (pts.Count < 3)
            {
                return 0;
            }

            double s = 0;
            var n = pts.Count;
            for (var i = 0; i < n; i++)
            {
                var j = (i + 1) % n;
                s += pts[i].X * pts[j].Y - pts[j].X * pts[i].Y;
            }

            return s * 0.5;
        }
    }
}
