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
        /// <summary>Default: wall centre — good match for slab footprint vs location curves.</summary>
        public static SpatialElementBoundaryLocation DefaultBoundaryLocation => SpatialElementBoundaryLocation.Center;

        public static SpatialElementBoundaryLocation ParseBoundaryLocation(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return SpatialElementBoundaryLocation.Center;
            }

            var s = raw.Trim().ToLowerInvariant().Replace(" ", string.Empty).Replace("_", string.Empty);
            return s switch
            {
                "finish" => SpatialElementBoundaryLocation.Finish,
                "finishwithoutcw" => SpatialElementBoundaryLocation.Finish,
                "coreboundary" or "core" => SpatialElementBoundaryLocation.CoreBoundary,
                "corecenter" => SpatialElementBoundaryLocation.CoreCenter,
                _ => SpatialElementBoundaryLocation.Center
            };
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
