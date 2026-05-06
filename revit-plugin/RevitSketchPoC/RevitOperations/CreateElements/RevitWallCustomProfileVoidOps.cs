using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace RevitSketchPoC.RevitOperations.CreateElements
{
    /// <summary>
    /// <para><b>Buraco através da espessura</b> (loop <i>interior</i> no sketch de perfil): a parede mantém o
    /// <b>retângulo exterior</b> (base → topo, todo o comprimento); só o polígono indicado fica <b>vazio</b>,
    /// como um recorte na massa — equivalente a um vão com forma livre, não a alterar o “desenho” exterior
    /// da parede (ex.: topo em zigzag só com bordo exterior).</para>
    /// <para>Não é “passagem ao solo” por defeito: se <c>heightFromWallBaseMeters</c> começar perto de 0,
    /// o vão encosta ao piso (efeito porta); para estrela/janela no <b>meio</b> da parede, use alturas
    /// todas acima do piso (ex. 1,2–2,8 m).</para>
    /// <para>JSON <c>create_wall_custom_profile_void</c>: <c>hostWallId</c> (parede recta). Um vão: <c>shape</c> ou <c>boundary</c> no root.
    /// Vários vãos: <c>voids</c> array de objectos, cada um com <c>shape</c>, ou <c>kind</c>+parâmetros no mesmo nível, ou <c>boundary</c>.</para>
    /// <para><c>shape.kind</c> (metros no plano da parede): apenas polilinhas paramétricas — <c>star</c>, <c>regularPolygon</c>, <c>isoscelesTriangle</c>/<c>triangle</c>,
    /// <c>diamond</c>/<c>rhombus</c>, <c>cross</c>/<c>plus</c>, <c>heart</c>. Vãos em arco romano: usar <c>create_wall_roman_arch_profile</c> (<see cref="RevitWallArchProfileOps"/>).</para>
    /// <para>Centrados usam <c>centerAlongMeters</c> e <c>centerHeightFromWallBaseMeters</c> (ou aliases no código) e <c>rotationDegrees</c> opcional.</para>
    /// Callers must not hold an open outer <see cref="Transaction"/> around <see cref="RunCreateWallCustomProfileVoidJsonOp"/>.
    /// </summary>
    public static class RevitWallCustomProfileVoidOps
    {
        /// <summary>Larger than roman arch default: stars / concave voids graze outer shell easily.</summary>
        private const double MinVoidShellInsetMeters = 0.08;

        private const double MinEdgeLengthMeters = 0.035;
        private const double ShrinkTowardCentroidMeters = 0.04;

        private static int ClampInt(int v, int min, int max) => v < min ? min : (v > max ? max : v);

        private static double ComputeAdaptiveShrinkFeet(PolylineVoidLoop pl)
        {
            double shrinkM;
            if (pl.Points.Count == 0)
            {
                shrinkM = ShrinkTowardCentroidMeters;
            }
            else
            {
                var cx = pl.Points.Average(p => p.alongM);
                var cy = pl.Points.Average(p => p.heightFromBaseM);
                var dMin = pl.Points.Min(p =>
                {
                    var dx = p.alongM - cx;
                    var dy = p.heightFromBaseM - cy;
                    return Math.Sqrt(dx * dx + dy * dy);
                });
                var minA = pl.Points.Min(p => p.alongM);
                var maxA = pl.Points.Max(p => p.alongM);
                var minH = pl.Points.Min(p => p.heightFromBaseM);
                var maxH = pl.Points.Max(p => p.heightFromBaseM);
                var span = Math.Min(maxA - minA, maxH - minH);
                var fromSpan = Math.Min(ShrinkTowardCentroidMeters, Math.Max(0.01, span * 0.2));
                var fromRadius = Math.Min(ShrinkTowardCentroidMeters, Math.Max(0.008, dMin * 0.32));
                shrinkM = Math.Min(fromSpan, fromRadius);
            }

            return RevitWallCreationOps.MetersToFeet(shrinkM);
        }

        private sealed class PolylineVoidLoop
        {
            public PolylineVoidLoop(List<(double alongM, double heightFromBaseM)> points) => Points = points;

            public List<(double alongM, double heightFromBaseM)> Points { get; }
        }

        public static void RunCreateWallCustomProfileVoidJsonOp(Document doc, JObject op, StringBuilder log)
        {
            var hostIdTok = op["hostWallId"];
            if (hostIdTok == null || hostIdTok.Type == JTokenType.Null)
            {
                throw new InvalidOperationException(
                    "create_wall_custom_profile_void requires hostWallId (straight wall).");
            }

            var idVal = hostIdTok.Value<long?>() ?? hostIdTok.Value<int?>();
            if (idVal == null)
            {
                throw new InvalidOperationException("create_wall_custom_profile_void: invalid hostWallId.");
            }

            var wall = doc.GetElement(new ElementId((long)idVal)) as Wall;
            if (wall == null)
            {
                throw new InvalidOperationException("create_wall_custom_profile_void: host is not a wall.");
            }

            if (wall.Location is not LocationCurve locCurve || locCurve.Curve is not Line wallLine)
            {
                throw new InvalidOperationException(
                    "create_wall_custom_profile_void: only straight walls (line location) are supported.");
            }

            var level = doc.GetElement(wall.LevelId) as Level
                        ?? throw new InvalidOperationException("create_wall_custom_profile_void: wall has no level.");
            var baseZ = level.Elevation + (wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0.0);
            var heightFt = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble()
                           ?? throw new InvalidOperationException(
                               "create_wall_custom_profile_void: could not read wall height.");
            var topZ = baseZ + heightFt;
            var lenFt = wallLine.Length;

            var insetFt = RevitWallCreationOps.MetersToFeet(MinVoidShellInsetMeters);
            if (lenFt < insetFt * 4 || (topZ - baseZ) < insetFt * 4)
            {
                throw new InvalidOperationException(
                    "create_wall_custom_profile_void: wall too small for shell inset (~0.08 m from edges).");
            }

            var clampShell = ReadOptionalBool(op, "clampToWallShell") ?? true;
            var voidLoopSpecs = CollectVoidLoops(op, log);
            if (voidLoopSpecs.Count == 0)
            {
                throw new InvalidOperationException(
                    "create_wall_custom_profile_void: need at least one void (shape, boundary, or voids[]).");
            }

            var globalMinHeightM = voidLoopSpecs.Min(SpecMinHeightMeters);
            if (globalMinHeightM < 0.15)
            {
                log.AppendLine(
                    "create_wall_custom_profile_void note: some void includes points near the wall base — " +
                    "may look like a floor opening; raise heights for mid-wall holes only.");
            }

            var gc0 = wallLine.GetEndPoint(0);
            var gc1 = wallLine.GetEndPoint(1);
            var dir = new XYZ(gc1.X - gc0.X, gc1.Y - gc0.Y, 0).Normalize();

            XYZ WallPoint(double alongFromStartFt, double zAbs)
            {
                return new XYZ(gc0.X, gc0.Y, zAbs) + dir * alongFromStartFt;
            }

            var minEdgeFt = RevitWallCreationOps.MetersToFeet(MinEdgeLengthMeters);
            var allInnerCurves = new List<List<Curve>>();
            for (var loopIdx = 0; loopIdx < voidLoopSpecs.Count; loopIdx++)
            {
                var shrinkFt = ComputeAdaptiveShrinkFeet(voidLoopSpecs[loopIdx]);
                var innerCurves = BuildInnerCurvesFromPolyline(
                    voidLoopSpecs[loopIdx].Points,
                    loopIdx,
                    WallPoint,
                    baseZ,
                    topZ,
                    lenFt,
                    insetFt,
                    clampShell,
                    gc0,
                    dir,
                    minEdgeFt,
                    shrinkFt);
                allInnerCurves.Add(innerCurves);
            }

            var outerCurves = BuildOuterCurves(WallPoint, lenFt, baseZ, topZ);

            ElementId sketchId;
            using (var tg = new TransactionGroup(doc, "Custom void — wall profile"))
            {
                tg.Start();
                try
                {
                    using (var txSketch = new Transaction(doc, "Custom void — wall profile sketch"))
                    {
                        txSketch.Start();
                        if (wall.SketchId != ElementId.InvalidElementId)
                        {
                            wall.RemoveProfileSketch();
                            doc.Regenerate();
                        }

                        var sketch = wall.CreateProfileSketch();
                        doc.Regenerate();
                        sketchId = sketch.Id;
                        txSketch.Commit();
                    }

                    using (var ses = new SketchEditScope(doc, "Custom void — edit wall profile"))
                    {
                        ses.Start(sketchId);
                        using (var txCurves = new Transaction(doc, "Custom void — profile curves"))
                        {
                            txCurves.Start();
                            var sketchEl = doc.GetElement(sketchId) as global::Autodesk.Revit.DB.Sketch
                                           ?? throw new InvalidOperationException(
                                               "create_wall_custom_profile_void: sketch not found after create.");
                            var plane = sketchEl.SketchPlane
                                        ?? throw new InvalidOperationException(
                                            "create_wall_custom_profile_void: sketch has no SketchPlane.");

                            foreach (var depId in sketchEl.GetAllElements())
                            {
                                if (doc.GetElement(depId) is ModelCurve)
                                {
                                    doc.Delete(depId);
                                }
                            }

                            foreach (var c in outerCurves)
                            {
                                doc.Create.NewModelCurve(c, plane);
                            }

                            foreach (var loop in allInnerCurves)
                            {
                                foreach (var c in loop)
                                {
                                    doc.Create.NewModelCurve(c, plane);
                                }
                            }

                            txCurves.Commit();
                        }

                        ses.Commit(new ContinueFailuresPreprocessor());
                    }

                    tg.Assimilate();
                }
                catch
                {
                    tg.RollBack();
                    throw;
                }
            }

            doc.Regenerate();
            var edgeCount = allInnerCurves.Sum(l => l.Count);
            log.AppendLine(
                "create_wall_custom_profile_void ok hostWallId=" + wall.Id + " sketchId=" + sketchId +
                " innerLoops=" + allInnerCurves.Count + " innerEdges=" + edgeCount);
        }

        /// <summary>One or many voids: top-level shape/boundary, or <c>voids</c> array of { shape } / { boundary } / parametric root.</summary>
        private static List<PolylineVoidLoop> CollectVoidLoops(JObject op, StringBuilder log)
        {
            if (op["voids"] is JArray voidsArr && voidsArr.Count > 0)
            {
                var list = new List<PolylineVoidLoop>();
                for (var i = 0; i < voidsArr.Count; i++)
                {
                    if (voidsArr[i] is not JObject item)
                    {
                        continue;
                    }

                    list.Add(ResolveSingleVoidLoop(item, log, i));
                }

                if (list.Count == 0)
                {
                    throw new InvalidOperationException(
                        "create_wall_custom_profile_void: \"voids\" array has no valid objects.");
                }

                log.AppendLine("create_wall_custom_profile_void: voids count=" + list.Count);
                return list;
            }

            return new List<PolylineVoidLoop> { ResolveSingleVoidLoop(op, log, 0) };
        }

        private static readonly HashSet<string> VoidItemKeysNotMergedIntoShape = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            "shape",
            "boundary",
            "op",
            "hostWallId",
            "voids",
            "clampToWallShell"
        };

        /// <summary>LLMs often put <c>centerAlongMeters</c> next to <c>shape</c> in the same void object — merge into shape for parsing.</summary>
        private static JObject MergeSiblingParamsIntoShape(JObject voidItem, JObject shape)
        {
            var merged = (JObject)shape.DeepClone();
            foreach (var prop in voidItem.Properties())
            {
                if (VoidItemKeysNotMergedIntoShape.Contains(prop.Name))
                {
                    continue;
                }

                var existing = merged[prop.Name];
                if (existing == null || existing.Type == JTokenType.Null)
                {
                    merged[prop.Name] = prop.Value.DeepClone();
                }
            }

            return merged;
        }

        private static PolylineVoidLoop ResolveSingleVoidLoop(
            JObject jo,
            StringBuilder log,
            int voidIndex)
        {
            if (jo["shape"] is JObject shapeNested)
            {
                var mergedShape = MergeSiblingParamsIntoShape(jo, shapeNested);
                return FinalizePolylineOrSpec(BuildParametricVoidSpec(mergedShape, log), voidIndex);
            }

            if (jo["kind"] != null && jo["kind"].Type != JTokenType.Null)
            {
                return FinalizePolylineOrSpec(BuildParametricVoidSpec(jo, log), voidIndex);
            }

            if (jo["boundary"] is JArray boundaryArr && boundaryArr.Count >= 3)
            {
                var pts = ReadBoundaryPointsMeters(boundaryArr);
                if (pts.Count < 3)
                {
                    throw new InvalidOperationException(
                        "create_wall_custom_profile_void: void " + voidIndex + " boundary invalid.");
                }

                return new PolylineVoidLoop(pts);
            }

            throw new InvalidOperationException(
                "create_wall_custom_profile_void: void " + voidIndex +
                " needs \"shape\", \"kind\"+params, or \"boundary\" (>=3 points).");
        }

        private static PolylineVoidLoop FinalizePolylineOrSpec(PolylineVoidLoop spec, int voidIndex)
        {
            if (spec.Points.Count < 3)
            {
                throw new InvalidOperationException(
                    "create_wall_custom_profile_void: void " + voidIndex + " produced < 3 points.");
            }

            return spec;
        }

        private static double SpecMinHeightMeters(PolylineVoidLoop spec) =>
            spec.Points.Min(x => x.heightFromBaseM);

        /// <summary>Parametric shapes supported here are closed polylines only (arches: <c>create_wall_roman_arch_profile</c>).</summary>
        private static PolylineVoidLoop BuildParametricVoidSpec(JObject shape, StringBuilder log)
        {
            var kind = shape["kind"]?.ToString().Trim().ToLowerInvariant().Replace("-", "_");
            if (string.IsNullOrEmpty(kind))
            {
                throw new InvalidOperationException(
                    "create_wall_custom_profile_void: shape.kind is required.");
            }

            switch (kind)
            {
                case "arch":
                case "lunette":
                    throw new InvalidOperationException(
                        "create_wall_custom_profile_void: arch/lunette is not supported here — use JSON op " +
                        "create_wall_roman_arch_profile (RevitWallArchProfileOps) for roman arch wall profile openings.");
                case "circle":
                case "ellipse":
                case "capsule":
                case "stadium":
                case "superellipse":
                case "squircle":
                case "roundedrectangle":
                case "rounded_rectangle":
                case "slot":
                    throw new InvalidOperationException(
                        "create_wall_custom_profile_void: shape.kind \"" + kind + "\" is not supported — use \"boundary\" " +
                        "with { alongMeters, heightFromWallBaseMeters } points (>=3), or a supported kind: star, regularPolygon, " +
                        "isoscelesTriangle/triangle, diamond/rhombus, cross/plus, heart.");
            }

            ReadCenterRequired(shape, out var cu, out var ch);
            var rot = ReadRotationRad(shape);
            switch (kind)
            {
                case "star":
                    var outer = ReadRequiredPositiveMeters(shape, "outerRadiusMeters");
                    var tips = ClampInt(shape["points"]?.Value<int?>() ?? shape["tips"]?.Value<int?>() ?? 5, 3, 24);
                    double inner;
                    if (TryReadNumber(shape["innerRadiusMeters"], out var inr) && inr > 0 && inr < outer)
                    {
                        inner = inr;
                    }
                    else
                    {
                        inner = outer * DefaultStarInnerRadiusRatio(tips);
                    }

                    if (inner <= 0.02 || inner >= outer - 0.02)
                    {
                        throw new InvalidOperationException(
                            "create_wall_custom_profile_void: star innerRadius invalid.");
                    }

                    log.AppendLine("create_wall_custom_profile_void parametric: star tips=" + tips);
                    return new PolylineVoidLoop(BuildStarPoints(cu, ch, outer, inner, tips, rot));
                case "regularpolygon":
                case "regular_polygon":
                case "polygon":
                    var pr = ReadRequiredPositiveMeters(shape, "radiusMeters");
                    var sides = shape["sides"]?.Value<int?>() ?? 0;
                    if (sides < 3)
                    {
                        throw new InvalidOperationException(
                            "create_wall_custom_profile_void: regularPolygon requires sides >= 3.");
                    }

                    sides = Math.Min(64, sides);
                    log.AppendLine("create_wall_custom_profile_void parametric: polygon sides=" + sides);
                    return new PolylineVoidLoop(BuildRegularPolygonPoints(cu, ch, pr, sides, rot));
                case "isoscelestriangle":
                case "isosceles_triangle":
                case "triangle":
                    var tw = ReadRequiredPositiveMeters(shape, "baseWidthMeters");
                    var th = ReadRequiredPositiveMeters(shape, "heightMeters");
                    var pointUp = shape["pointUp"]?.Value<bool?>() ?? true;
                    log.AppendLine("create_wall_custom_profile_void parametric: isoscelesTriangle");
                    return new PolylineVoidLoop(BuildIsoscelesTrianglePoints(cu, ch, tw, th, pointUp, rot));
                case "diamond":
                case "rhombus":
                    var dw = ReadRequiredPositiveMeters(shape, "widthAlongMeters");
                    var dh = ReadRequiredPositiveMeters(shape, "heightMeters");
                    log.AppendLine("create_wall_custom_profile_void parametric: diamond");
                    return new PolylineVoidLoop(BuildDiamondPoints(cu, ch, dw, dh, rot));
                case "cross":
                case "plus":
                    var hSpan = ReadRequiredPositiveMeters(shape, "horizontalSpanMeters");
                    var vSpan = ReadRequiredPositiveMeters(shape, "verticalSpanMeters");
                    var armT = ReadRequiredPositiveMeters(shape, "armThicknessMeters");
                    log.AppendLine("create_wall_custom_profile_void parametric: cross");
                    return new PolylineVoidLoop(BuildCrossPoints(cu, ch, hSpan, vSpan, armT, rot));
                case "heart":
                    var hs = ReadRequiredPositiveMeters(shape, "scaleMeters");
                    var hSeg = ClampInt(shape["segments"]?.Value<int?>() ?? 72, 32, 144);
                    log.AppendLine("create_wall_custom_profile_void parametric: heart");
                    return new PolylineVoidLoop(BuildHeartPoints(cu, ch, hs, hSeg, rot));
                default:
                    throw new InvalidOperationException(
                        "create_wall_custom_profile_void: unknown shape.kind \"" + kind + "\". " +
                        "For arches use create_wall_roman_arch_profile. Supported: star, regularPolygon, isoscelesTriangle, triangle, " +
                        "diamond, rhombus, cross, plus, heart; or \"boundary\" with point coordinates.");
            }
        }

        private static void ReadCenterRequired(JObject shape, out double cu, out double ch)
        {
            if (!TryReadNumber(shape["centerAlongMeters"], out cu) && !TryReadNumber(shape["centerAlong"], out cu))
            {
                throw new InvalidOperationException(
                    "create_wall_custom_profile_void: shape requires centerAlongMeters.");
            }

            if (!TryReadNumber(shape["centerHeightFromWallBaseMeters"], out ch) &&
                !TryReadNumber(shape["centerHeightFromBaseMeters"], out ch) &&
                !TryReadNumber(shape["centerHeight"], out ch))
            {
                throw new InvalidOperationException(
                    "create_wall_custom_profile_void: shape requires centerHeightFromWallBaseMeters.");
            }
        }

        private static double ReadRotationRad(JObject shape)
        {
            return TryReadNumber(shape["rotationDegrees"], out var rd) ? rd * (Math.PI / 180.0) : 0.0;
        }

        private static (double u, double v) RotateLocal(double lu, double lv, double rotRad)
        {
            var cos = Math.Cos(rotRad);
            var sin = Math.Sin(rotRad);
            return (lu * cos - lv * sin, lu * sin + lv * cos);
        }

        private static (double alongM, double heightFromBaseM) LocalToWorld(
            double lu,
            double lv,
            double cu,
            double ch,
            double rotRad)
        {
            var (r0, r1) = RotateLocal(lu, lv, rotRad);
            return (cu + r0, ch + r1);
        }

        private static List<(double, double)> BuildIsoscelesTrianglePoints(
            double cu,
            double ch,
            double baseW,
            double h,
            bool pointUp,
            double rotRad)
        {
            var half = baseW * 0.5;
            var sign = pointUp ? 1.0 : -1.0;
            var b0 = (-half, -sign * h * 0.5);
            var b1 = (half, -sign * h * 0.5);
            var apex = (0.0, sign * h * 0.5);
            return new List<(double, double)>
            {
                LocalToWorld(b0.Item1, b0.Item2, cu, ch, rotRad),
                LocalToWorld(b1.Item1, b1.Item2, cu, ch, rotRad),
                LocalToWorld(apex.Item1, apex.Item2, cu, ch, rotRad)
            };
        }

        private static List<(double, double)> BuildDiamondPoints(
            double cu,
            double ch,
            double w,
            double h,
            double rotRad)
        {
            return new List<(double, double)>
            {
                LocalToWorld(0, h * 0.5, cu, ch, rotRad),
                LocalToWorld(w * 0.5, 0, cu, ch, rotRad),
                LocalToWorld(0, -h * 0.5, cu, ch, rotRad),
                LocalToWorld(-w * 0.5, 0, cu, ch, rotRad)
            };
        }

        private static List<(double, double)> BuildCrossPoints(
            double cu,
            double ch,
            double w,
            double h,
            double t,
            double rotRad)
        {
            var halfW = w * 0.5;
            var halfH = h * 0.5;
            var halfT = t * 0.5;
            var pts = new (double lu, double lv)[]
            {
                (-halfT, -halfH),
                (halfT, -halfH),
                (halfT, -halfT),
                (halfW, -halfT),
                (halfW, halfT),
                (halfT, halfT),
                (halfT, halfH),
                (-halfT, halfH),
                (-halfT, halfT),
                (-halfW, halfT),
                (-halfW, -halfT),
                (-halfT, -halfT)
            };

            return pts.Select(p => LocalToWorld(p.lu, p.lv, cu, ch, rotRad)).ToList();
        }

        private static List<(double, double)> BuildHeartPoints(
            double cu,
            double ch,
            double scale,
            int segments,
            double rotRad)
        {
            var list = new List<(double, double)>(segments);
            for (var i = 0; i < segments; i++)
            {
                var t = 2 * Math.PI * i / segments;
                var lx = 16 * Math.Pow(Math.Sin(t), 3);
                var ly =
                    13 * Math.Cos(t) -
                    5 * Math.Cos(2 * t) -
                    2 * Math.Cos(3 * t) -
                    Math.Cos(4 * t);
                list.Add(LocalToWorld(lx * scale / 16.0, ly * scale / 16.0, cu, ch, rotRad));
            }

            return list;
        }

        /// <summary>Default inner/outer for regular star; 5 tips uses golden-ratio pentagram (~0.382).</summary>
        private static double DefaultStarInnerRadiusRatio(int tips)
        {
            if (tips == 5)
            {
                var phi = (1 + Math.Sqrt(5)) / 2;
                return 1 / (phi * phi);
            }

            var n = tips;
            return Math.Sin(Math.PI / n) / (1 + Math.Cos(Math.PI / n));
        }

        private static double ReadRequiredPositiveMeters(JObject jo, string key)
        {
            if (!TryReadNumber(jo[key], out var v) || v <= 1e-6)
            {
                throw new InvalidOperationException(
                    "create_wall_custom_profile_void: shape." + key + " must be a positive number (metres).");
            }

            return v;
        }

        private static List<(double alongM, double heightFromBaseM)> BuildStarPoints(
            double cu,
            double ch,
            double outerMeters,
            double innerMeters,
            int tips,
            double rotationRad)
        {
            var list = new List<(double, double)>(2 * tips);
            for (var k = 0; k < 2 * tips; k++)
            {
                var r = (k % 2 == 0) ? outerMeters : innerMeters;
                var theta = (Math.PI / 2) - (k * Math.PI / tips) + rotationRad;
                list.Add((cu + r * Math.Cos(theta), ch + r * Math.Sin(theta)));
            }

            return list;
        }

        private static List<(double alongM, double heightFromBaseM)> BuildRegularPolygonPoints(
            double cu,
            double ch,
            double radiusMeters,
            int sides,
            double rotationRad)
        {
            var list = new List<(double, double)>(sides);
            for (var i = 0; i < sides; i++)
            {
                var theta = (Math.PI / 2) - (i * 2 * Math.PI / sides) + rotationRad;
                list.Add((cu + radiusMeters * Math.Cos(theta), ch + radiusMeters * Math.Sin(theta)));
            }

            return list;
        }

        private static List<(double alongM, double heightFromBaseM)> ReadBoundaryPointsMeters(JArray arr)
        {
            var list = new List<(double alongM, double heightFromBaseM)>();
            foreach (var t in arr)
            {
                if (t is not JObject jo)
                {
                    continue;
                }

                if (!TryReadAlongAndHeight(jo, out var a, out var h))
                {
                    continue;
                }

                list.Add((alongM: a, heightFromBaseM: h));
            }

            return list;
        }

        private static bool TryReadAlongAndHeight(JObject jo, out double alongM, out double heightM)
        {
            alongM = heightM = 0;
            if (TryReadNumber(jo["alongMeters"], out alongM) || TryReadNumber(jo["along"], out alongM))
            {
                if (TryReadNumber(jo["heightFromWallBaseMeters"], out heightM) ||
                    TryReadNumber(jo["heightFromBaseMeters"], out heightM) ||
                    TryReadNumber(jo["height"], out heightM))
                {
                    return true;
                }
            }

            return false;
        }

        private static List<XYZ> RemoveNearDuplicateVertices(List<XYZ> pts, double minDistFt)
        {
            if (pts.Count == 0)
            {
                return pts;
            }

            var keep = new List<XYZ> { pts[0] };
            for (var i = 1; i < pts.Count; i++)
            {
                if (pts[i].DistanceTo(keep[keep.Count - 1]) >= minDistFt)
                {
                    keep.Add(pts[i]);
                }
            }

            if (keep.Count >= 2 && keep[0].DistanceTo(keep[keep.Count - 1]) < minDistFt)
            {
                keep.RemoveAt(keep.Count - 1);
            }

            return keep;
        }

        private static List<Curve> BuildClosedPolylineAllEdges(IReadOnlyList<XYZ> verts)
        {
            var curves = new List<Curve>();
            var n = verts.Count;
            for (var i = 0; i < n; i++)
            {
                curves.Add(Line.CreateBound(verts[i], verts[(i + 1) % n]));
            }

            return curves;
        }

        private static List<XYZ> ShrinkLoopTowardCentroid(List<XYZ> verts, double shrinkFt)
        {
            if (verts.Count < 3 || shrinkFt < 1e-12)
            {
                return verts;
            }

            var cx = verts.Average(p => p.X);
            var cy = verts.Average(p => p.Y);
            var cz = verts.Average(p => p.Z);
            var c = new XYZ(cx, cy, cz);
            var @out = new List<XYZ>(verts.Count);
            foreach (var p in verts)
            {
                var d = c - p;
                var len = d.GetLength();
                if (len < 1e-12)
                {
                    @out.Add(p);
                    continue;
                }

                var step = Math.Min(shrinkFt, len * 0.45);
                @out.Add(p + d.Normalize() * step);
            }

            return @out;
        }

        private static bool AllEdgesMeetMinimumLength(IReadOnlyList<XYZ> verts, double minFt, out double shortestFt)
        {
            shortestFt = double.MaxValue;
            var n = verts.Count;
            for (var i = 0; i < n; i++)
            {
                var d = verts[i].DistanceTo(verts[(i + 1) % n]);
                if (d < shortestFt)
                {
                    shortestFt = d;
                }

                if (d < minFt - 1e-10)
                {
                    return false;
                }
            }

            return true;
        }

        private static void EnsureInnerOppositeWindingToOuter(
            List<XYZ> innerWorld,
            XYZ gc0,
            XYZ dir,
            double lenFt,
            double baseZ,
            double topZ)
        {
            var outerUv = new List<(double u, double v)>
            {
                (0, baseZ),
                (lenFt, baseZ),
                (lenFt, topZ),
                (0, topZ)
            };
            var outerA = SignedAreaUv(outerUv);
            if (Math.Abs(outerA) < 1e-12)
            {
                return;
            }

            var innerUv = innerWorld.Select(p => ((p - gc0).DotProduct(dir), p.Z)).ToList();
            var innerA = SignedAreaUv(innerUv);
            if (outerA * innerA > 0)
            {
                innerWorld.Reverse();
            }
        }

        private static double SignedAreaUv(List<(double u, double v)> poly)
        {
            double s = 0;
            var n = poly.Count;
            for (var i = 0; i < n; i++)
            {
                var j = (i + 1) % n;
                s += poly[i].u * poly[j].v - poly[j].u * poly[i].v;
            }

            return s * 0.5;
        }

        private static bool HasSelfIntersection2D(List<XYZ> world, XYZ gc0, XYZ dir)
        {
            var n = world.Count;
            if (n < 4)
            {
                return false;
            }

            var u = new double[n];
            var v = new double[n];
            for (var i = 0; i < n; i++)
            {
                u[i] = (world[i] - gc0).DotProduct(dir);
                v[i] = world[i].Z;
            }

            for (var i = 0; i < n; i++)
            {
                var i1 = (i + 1) % n;
                for (var j = i + 1; j < n; j++)
                {
                    var j1 = (j + 1) % n;
                    if (EdgesShareVertex(i, i1, j, j1))
                    {
                        continue;
                    }

                    if (SegmentsIntersectOpen(u[i], v[i], u[i1], v[i1], u[j], v[j], u[j1], v[j1]))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool EdgesShareVertex(int a0, int a1, int b0, int b1)
        {
            return a0 == b0 || a0 == b1 || a1 == b0 || a1 == b1;
        }

        private static bool SegmentsIntersectOpen(
            double x1,
            double y1,
            double x2,
            double y2,
            double x3,
            double y3,
            double x4,
            double y4)
        {
            const double eps = 1e-9;
            var o1 = Orient(x1, y1, x2, y2, x3, y3);
            var o2 = Orient(x1, y1, x2, y2, x4, y4);
            var o3 = Orient(x3, y3, x4, y4, x1, y1);
            var o4 = Orient(x3, y3, x4, y4, x2, y2);
            if (o1 * o2 < -eps && o3 * o4 < -eps)
            {
                return true;
            }

            if (Math.Abs(o1) < eps && OnSegment(x1, y1, x3, y3, x2, y2))
            {
                return true;
            }

            if (Math.Abs(o2) < eps && OnSegment(x1, y1, x4, y4, x2, y2))
            {
                return true;
            }

            if (Math.Abs(o3) < eps && OnSegment(x3, y3, x1, y1, x4, y4))
            {
                return true;
            }

            if (Math.Abs(o4) < eps && OnSegment(x3, y3, x2, y2, x4, y4))
            {
                return true;
            }

            return false;
        }

        private static double Orient(double ax, double ay, double bx, double by, double cx, double cy)
        {
            return (by - ay) * (cx - bx) - (bx - ax) * (cy - by);
        }

        private static bool OnSegment(double ax, double ay, double px, double py, double bx, double by)
        {
            return px >= Math.Min(ax, bx) - 1e-9 && px <= Math.Max(ax, bx) + 1e-9 &&
                   py >= Math.Min(ay, by) - 1e-9 && py <= Math.Max(ay, by) + 1e-9;
        }

        private static List<Curve> BuildInnerCurvesFromPolyline(
            List<(double alongM, double heightFromBaseM)> rawPts,
            int loopIdx,
            Func<double, double, XYZ> wallPt,
            double baseZ,
            double topZ,
            double lenFt,
            double insetFt,
            bool clampShell,
            XYZ gc0,
            XYZ dir,
            double minEdgeFt,
            double shrinkFt)
        {
            var innerWorld = new List<XYZ>();
            foreach (var (alongM, hM) in rawPts)
            {
                var alongFt = RevitWallCreationOps.MetersToFeet(alongM);
                var z = baseZ + RevitWallCreationOps.MetersToFeet(hM);
                if (clampShell)
                {
                    alongFt = Math.Max(insetFt, Math.Min(lenFt - insetFt, alongFt));
                    z = Math.Max(baseZ + insetFt, Math.Min(topZ - insetFt, z));
                }
                else if (alongFt < insetFt - 1e-6 || alongFt > lenFt - insetFt + 1e-6 ||
                         z < baseZ + insetFt - 1e-6 || z > topZ - insetFt + 1e-6)
                {
                    throw new InvalidOperationException(
                        "create_wall_custom_profile_void: void " + loopIdx +
                        " outside wall face (use clampToWallShell true or move points).");
                }

                innerWorld.Add(wallPt(alongFt, z));
            }

            innerWorld = RemoveNearDuplicateVertices(innerWorld, minEdgeFt * 0.85);
            if (innerWorld.Count < 3)
            {
                throw new InvalidOperationException(
                    "create_wall_custom_profile_void: void " + loopIdx +
                    " collapses to fewer than 3 corners after cleanup.");
            }

            innerWorld = ShrinkLoopTowardCentroid(innerWorld, shrinkFt);
            innerWorld = RemoveNearDuplicateVertices(innerWorld, minEdgeFt * 0.85);
            if (innerWorld.Count < 3)
            {
                throw new InvalidOperationException(
                    "create_wall_custom_profile_void: void " + loopIdx +
                    " too tight after safety shrink.");
            }

            if (HasSelfIntersection2D(innerWorld, gc0, dir))
            {
                throw new InvalidOperationException(
                    "create_wall_custom_profile_void: void " + loopIdx +
                    " self-intersects — fix vertex order or simplify the polygon.");
            }

            if (!AllEdgesMeetMinimumLength(innerWorld, minEdgeFt, out var badLen))
            {
                throw new InvalidOperationException(
                    "create_wall_custom_profile_void: void " + loopIdx + " edge too short (~" +
                    Math.Round(badLen * 0.3048, 3) + " m).");
            }

            EnsureInnerOppositeWindingToOuter(innerWorld, gc0, dir, lenFt, baseZ, topZ);

            var innerCurves = BuildClosedPolylineAllEdges(innerWorld);
            if (innerCurves.Count < 3)
            {
                throw new InvalidOperationException(
                    "create_wall_custom_profile_void: void " + loopIdx + " could not build closed loop.");
            }

            return innerCurves;
        }

        private static List<Curve> BuildOuterCurves(Func<double, double, XYZ> wallPt, double lenFt, double baseZ, double topZ)
        {
            var a = wallPt(0, baseZ);
            var b = wallPt(lenFt, baseZ);
            var c = wallPt(lenFt, topZ);
            var d = wallPt(0, topZ);
            return new List<Curve>
            {
                Line.CreateBound(a, b),
                Line.CreateBound(b, c),
                Line.CreateBound(c, d),
                Line.CreateBound(d, a)
            };
        }

        private static bool? ReadOptionalBool(JObject op, string key)
        {
            var token = op[key];
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            if (token.Type == JTokenType.Boolean)
            {
                return token.Value<bool>();
            }

            return bool.TryParse(token.ToString().Trim(), out var b) ? b : null;
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

        private sealed class ContinueFailuresPreprocessor : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
            {
                return FailureProcessingResult.Continue;
            }
        }
    }
}
