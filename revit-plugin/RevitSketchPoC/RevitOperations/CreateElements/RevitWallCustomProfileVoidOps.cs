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
    /// <para><c>shape.kind</c> (metros no plano da parede): suporta polilinhas paramétricas — <c>star</c>, <c>regularPolygon</c>, <c>isoscelesTriangle</c>/<c>triangle</c>,
    /// <c>diamond</c>/<c>rhombus</c>, <c>cross</c>/<c>plus</c>, <c>heart</c> — e também curvas nativas <c>circle</c>/<c>ellipse</c>.</para>
    /// <para>Também aceita loops por <c>segments[]</c> com <c>line</c>, <c>arc</c>, <c>circle</c> e <c>ellipse</c>.</para>
    /// <para>Vãos em arco romano (com semicírculo clássico) continuam em <c>create_wall_roman_arch_profile</c> (<see cref="RevitWallArchProfileOps"/>).</para>
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

        private static double ComputeAdaptiveShrinkFeet(VoidLoopSpec pl)
        {
            double shrinkM;
            if (pl.PolylinePoints == null || pl.PolylinePoints.Count == 0)
            {
                shrinkM = ShrinkTowardCentroidMeters;
            }
            else
            {
                var cx = pl.PolylinePoints.Average(p => p.alongM);
                var cy = pl.PolylinePoints.Average(p => p.heightFromBaseM);
                var dMin = pl.PolylinePoints.Min(p =>
                {
                    var dx = p.alongM - cx;
                    var dy = p.heightFromBaseM - cy;
                    return Math.Sqrt(dx * dx + dy * dy);
                });
                var minA = pl.PolylinePoints.Min(p => p.alongM);
                var maxA = pl.PolylinePoints.Max(p => p.alongM);
                var minH = pl.PolylinePoints.Min(p => p.heightFromBaseM);
                var maxH = pl.PolylinePoints.Max(p => p.heightFromBaseM);
                var span = Math.Min(maxA - minA, maxH - minH);
                var fromSpan = Math.Min(ShrinkTowardCentroidMeters, Math.Max(0.01, span * 0.2));
                var fromRadius = Math.Min(ShrinkTowardCentroidMeters, Math.Max(0.008, dMin * 0.32));
                shrinkM = Math.Min(fromSpan, fromRadius);
            }

            return RevitWallCreationOps.MetersToFeet(shrinkM);
        }

        private sealed class VoidLoopSpec
        {
            public VoidLoopSpec(List<(double alongM, double heightFromBaseM)> polylinePoints)
            {
                PolylinePoints = polylinePoints;
                SegmentDefs = null;
                ValidationPoints = polylinePoints;
            }

            public VoidLoopSpec(
                List<JObject> segmentDefs,
                List<(double alongM, double heightFromBaseM)> validationPoints)
            {
                PolylinePoints = null;
                SegmentDefs = segmentDefs;
                ValidationPoints = validationPoints;
            }

            public List<(double alongM, double heightFromBaseM)>? PolylinePoints { get; }

            public List<JObject>? SegmentDefs { get; }

            public List<(double alongM, double heightFromBaseM)> ValidationPoints { get; }

            public bool IsSegmentBased => SegmentDefs != null;
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

            var clampShell = ReadOptionalBool(op, "clampToWallShell") ??
                             ReadOptionalBool(op, "autoClamp") ??
                             true;
            var wallLengthMeters = lenFt * 0.3048;
            var wallHeightMeters = (topZ - baseZ) * 0.3048;
            ApplyProfileVoidPlacementHints(op, wallLengthMeters, wallHeightMeters, log);
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
                List<Curve> innerCurves;
                if (voidLoopSpecs[loopIdx].IsSegmentBased)
                {
                    innerCurves = BuildInnerCurvesFromSegments(
                        voidLoopSpecs[loopIdx],
                        loopIdx,
                        WallPoint,
                        baseZ,
                        topZ,
                        lenFt,
                        insetFt,
                        clampShell,
                        gc0,
                        dir,
                        minEdgeFt);
                }
                else
                {
                    var shrinkFt = ComputeAdaptiveShrinkFeet(voidLoopSpecs[loopIdx]);
                    innerCurves = BuildInnerCurvesFromPolyline(
                        voidLoopSpecs[loopIdx].PolylinePoints ?? new List<(double alongM, double heightFromBaseM)>(),
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
                }

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

            // Regenerate must run inside a transaction in many Revit builds; calling it with no open
            // transaction can throw "Modification of the document is forbidden."
            using (var txRegen = new Transaction(doc, "Custom void — regenerate"))
            {
                txRegen.Start();
                doc.Regenerate();
                txRegen.Commit();
            }

            var edgeCount = allInnerCurves.Sum(l => l.Count);
            log.AppendLine(
                "create_wall_custom_profile_void ok hostWallId=" + wall.Id + " sketchId=" + sketchId +
                " innerLoops=" + allInnerCurves.Count + " innerEdges=" + edgeCount);
        }

        /// <summary>One or many voids: top-level shape/boundary, or <c>voids</c> array of { shape } / { boundary } / parametric root.</summary>
        private static List<VoidLoopSpec> CollectVoidLoops(JObject op, StringBuilder log)
        {
            if (op["voids"] is JArray voidsArr && voidsArr.Count > 0)
            {
                var list = new List<VoidLoopSpec>();
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

            return new List<VoidLoopSpec> { ResolveSingleVoidLoop(op, log, 0) };
        }

        private static readonly HashSet<string> VoidItemKeysNotMergedIntoShape = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            "shape",
            "boundary",
            "op",
            "hostWallId",
            "voids",
            "clampToWallShell",
            "autoClamp",
            "positionRatio",
            "positionAlongWallMeters",
            "heightPositionRatio"
        };

        /// <summary>
        /// Fills <c>centerAlongMeters</c> / height center from <c>positionRatio</c> (0–1 along wall length) when the LLM omits explicit centers.
        /// </summary>
        private static void ApplyProfileVoidPlacementHints(
            JObject op,
            double wallLengthMeters,
            double wallHeightMeters,
            StringBuilder log)
        {
            void Visit(JObject node)
            {
                if (node["shape"] is JObject sh)
                {
                    EnsureShapeCentersFromPlacement(sh, node, wallLengthMeters, wallHeightMeters, log);
                }
                else if (node["kind"] != null && node["kind"].Type != JTokenType.Null)
                {
                    EnsureShapeCentersFromPlacement(node, node, wallLengthMeters, wallHeightMeters, log);
                }
            }

            Visit(op);
            if (op["voids"] is JArray arr)
            {
                foreach (var t in arr)
                {
                    if (t is JObject vo)
                    {
                        Visit(vo);
                    }
                }
            }
        }

        private static void EnsureShapeCentersFromPlacement(
            JObject shape,
            JObject voidItem,
            double wallLengthMeters,
            double wallHeightMeters,
            StringBuilder log)
        {
            var needAlong = !TryReadNumber(shape["centerAlongMeters"], out _) && !TryReadNumber(shape["centerAlong"], out _);
            var needHeight = !TryReadNumber(shape["centerHeightFromWallBaseMeters"], out _) &&
                             !TryReadNumber(shape["centerHeightFromBaseMeters"], out _) &&
                             !TryReadNumber(shape["centerHeight"], out _);

            if (needAlong)
            {
                if (TryReadNumber(voidItem["centerAlongMeters"], out var ca))
                {
                    shape["centerAlongMeters"] = ca;
                    needAlong = false;
                }
                else if (TryReadNumber(voidItem["positionAlongWallMeters"], out var pal))
                {
                    shape["centerAlongMeters"] = pal;
                    needAlong = false;
                }
                else if (TryReadNumber(voidItem["positionRatio"], out var pr) && pr >= 0 && pr <= 1)
                {
                    shape["centerAlongMeters"] = pr * wallLengthMeters;
                    needAlong = false;
                    log.AppendLine(
                        "create_wall_custom_profile_void: derived centerAlongMeters from positionRatio × wall length.");
                }
            }

            if (needHeight)
            {
                if (TryReadNumber(voidItem["centerHeightFromWallBaseMeters"], out var ch))
                {
                    shape["centerHeightFromWallBaseMeters"] = ch;
                }
                else if (TryReadNumber(voidItem["heightPositionRatio"], out var hr) && hr >= 0 && hr <= 1)
                {
                    shape["centerHeightFromWallBaseMeters"] = hr * wallHeightMeters;
                    log.AppendLine(
                        "create_wall_custom_profile_void: derived center height from heightPositionRatio × wall height.");
                }
            }
        }

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

        private static VoidLoopSpec ResolveSingleVoidLoop(
            JObject jo,
            StringBuilder log,
            int voidIndex)
        {
            if (jo["shape"] is JObject shapeNested)
            {
                var mergedShape = MergeSiblingParamsIntoShape(jo, shapeNested);
                return FinalizeLoopSpec(BuildParametricVoidSpec(mergedShape, log), voidIndex);
            }

            if (jo["kind"] != null && jo["kind"].Type != JTokenType.Null)
            {
                return FinalizeLoopSpec(BuildParametricVoidSpec(jo, log), voidIndex);
            }

            if (jo["segments"] is JArray segmentsArr && segmentsArr.Count > 0)
            {
                return FinalizeLoopSpec(BuildSegmentBasedSpec(segmentsArr), voidIndex);
            }

            if (jo["boundary"] is JArray boundaryArr && boundaryArr.Count >= 3)
            {
                var pts = ReadBoundaryPointsMeters(boundaryArr);
                if (pts.Count < 3)
                {
                    throw new InvalidOperationException(
                        "create_wall_custom_profile_void: void " + voidIndex + " boundary invalid.");
                }

                return new VoidLoopSpec(pts);
            }

            if (jo["boundary"] is JObject boundaryObj &&
                boundaryObj["segments"] is JArray boundarySegments &&
                boundarySegments.Count > 0)
            {
                return FinalizeLoopSpec(BuildSegmentBasedSpec(boundarySegments), voidIndex);
            }

            throw new InvalidOperationException(
                "create_wall_custom_profile_void: void " + voidIndex +
                " needs \"shape\", \"kind\"+params, or \"boundary\" (>=3 points).");
        }

        private static VoidLoopSpec FinalizeLoopSpec(VoidLoopSpec spec, int voidIndex)
        {
            if (spec.ValidationPoints.Count < 3)
            {
                throw new InvalidOperationException(
                    "create_wall_custom_profile_void: void " + voidIndex + " produced < 3 points.");
            }

            return spec;
        }

        private static double SpecMinHeightMeters(VoidLoopSpec spec) =>
            spec.ValidationPoints.Min(x => x.heightFromBaseM);

        /// <summary>Parametric shapes supported here are closed polylines only (arches: <c>create_wall_roman_arch_profile</c>).</summary>
        private static VoidLoopSpec BuildParametricVoidSpec(JObject shape, StringBuilder log)
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
                case "circle":
                    var radiusM = ReadRequiredPositiveMeters(shape, "radiusMeters");
                    log.AppendLine("create_wall_custom_profile_void parametric: circle");
                    return BuildEllipseLoopSpec(cu, ch, radiusM, radiusM, rot, shape);
                case "ellipse":
                    var rx = ReadRequiredPositiveMeters(shape, "radiusAlongMeters");
                    var ry = ReadRequiredPositiveMeters(shape, "radiusHeightMeters");
                    log.AppendLine("create_wall_custom_profile_void parametric: ellipse");
                    return BuildEllipseLoopSpec(cu, ch, rx, ry, rot, shape);
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
                    return new VoidLoopSpec(BuildStarPoints(cu, ch, outer, inner, tips, rot));
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
                    return new VoidLoopSpec(BuildRegularPolygonPoints(cu, ch, pr, sides, rot));
                case "isoscelestriangle":
                case "isosceles_triangle":
                case "triangle":
                    var tw = ReadRequiredPositiveMeters(shape, "baseWidthMeters");
                    var th = ReadRequiredPositiveMeters(shape, "heightMeters");
                    var pointUp = shape["pointUp"]?.Value<bool?>() ?? true;
                    log.AppendLine("create_wall_custom_profile_void parametric: isoscelesTriangle");
                    return new VoidLoopSpec(BuildIsoscelesTrianglePoints(cu, ch, tw, th, pointUp, rot));
                case "diamond":
                case "rhombus":
                    var dw = ReadRequiredPositiveMeters(shape, "widthAlongMeters");
                    var dh = ReadRequiredPositiveMeters(shape, "heightMeters");
                    log.AppendLine("create_wall_custom_profile_void parametric: diamond");
                    return new VoidLoopSpec(BuildDiamondPoints(cu, ch, dw, dh, rot));
                case "cross":
                case "plus":
                    var hSpan = ReadRequiredPositiveMeters(shape, "horizontalSpanMeters");
                    var vSpan = ReadRequiredPositiveMeters(shape, "verticalSpanMeters");
                    var armT = ReadRequiredPositiveMeters(shape, "armThicknessMeters");
                    log.AppendLine("create_wall_custom_profile_void parametric: cross");
                    return new VoidLoopSpec(BuildCrossPoints(cu, ch, hSpan, vSpan, armT, rot));
                case "heart":
                    var hs = ReadRequiredPositiveMeters(shape, "scaleMeters");
                    log.AppendLine("create_wall_custom_profile_void parametric: heart (adaptive chords)");
                    return new VoidLoopSpec(BuildHeartPoints(cu, ch, hs, rot));
                default:
                    throw new InvalidOperationException(
                        "create_wall_custom_profile_void: unknown shape.kind \"" + kind + "\". " +
                        "For arches use create_wall_roman_arch_profile. Supported: circle, ellipse, star, regularPolygon, isoscelesTriangle, triangle, " +
                        "diamond, rhombus, cross, plus, heart; or \"boundary\" with point coordinates.");
            }
        }

        private static VoidLoopSpec BuildEllipseLoopSpec(
            double centerAlongM,
            double centerHeightM,
            double radiusAlongM,
            double radiusHeightM,
            double rotationRad,
            JObject shape)
        {
            var startDeg = ReadOptionalNumber(shape, "startAngleDegrees") ?? 0.0;
            var endDeg = ReadOptionalNumber(shape, "endAngleDegrees") ?? 360.0;
            var rotDeg = rotationRad * 180.0 / Math.PI;
            var segments = BuildEllipseSegmentObjects(
                centerAlongM,
                centerHeightM,
                radiusAlongM,
                radiusHeightM,
                rotDeg,
                startDeg,
                endDeg);

            var validation = SampleEllipseProfilePoints(centerAlongM, centerHeightM, radiusAlongM, radiusHeightM, rotationRad, startDeg, endDeg);
            return new VoidLoopSpec(segments, validation);
        }

        private static List<JObject> BuildEllipseSegmentObjects(
            double centerAlongM,
            double centerHeightM,
            double radiusAlongM,
            double radiusHeightM,
            double rotationDeg,
            double startDeg,
            double endDeg)
        {
            var startRad = startDeg * Math.PI / 180.0;
            var endRad = endDeg * Math.PI / 180.0;
            var sweep = NormalizePositiveAngle(endRad - startRad);
            var fullLoop = sweep < 1e-6 || Math.Abs(sweep - 2 * Math.PI) < 1e-6;

            JObject CreateSegment(double segStartDeg, double segEndDeg)
            {
                return new JObject
                {
                    ["kind"] = "ellipse",
                    ["center"] = new JObject
                    {
                        ["alongMeters"] = centerAlongM,
                        ["heightFromWallBaseMeters"] = centerHeightM
                    },
                    ["radiusAlongMeters"] = radiusAlongM,
                    ["radiusHeightMeters"] = radiusHeightM,
                    ["rotationDegrees"] = rotationDeg,
                    ["startAngleDegrees"] = segStartDeg,
                    ["endAngleDegrees"] = segEndDeg
                };
            }

            if (!fullLoop)
            {
                return new List<JObject> { CreateSegment(startDeg, endDeg) };
            }

            var midDeg = startDeg + 180.0;
            return new List<JObject>
            {
                CreateSegment(startDeg, midDeg),
                CreateSegment(midDeg, startDeg + 360.0)
            };
        }

        private static VoidLoopSpec BuildSegmentBasedSpec(JArray segmentsArr)
        {
            var segments = new List<JObject>();
            var validation = new List<(double alongM, double heightFromBaseM)>();
            foreach (var token in segmentsArr)
            {
                if (token is not JObject segment)
                {
                    continue;
                }

                var cloned = (JObject)segment.DeepClone();
                segments.Add(cloned);
                AddSegmentValidationPoints(cloned, validation);
            }

            if (segments.Count == 0)
            {
                throw new InvalidOperationException("create_wall_custom_profile_void: no valid segments were provided.");
            }

            if (validation.Count < 3)
            {
                throw new InvalidOperationException(
                    "create_wall_custom_profile_void: segment-based boundary needs enough points to form a closed loop.");
            }

            return new VoidLoopSpec(segments, validation);
        }

        private static void AddSegmentValidationPoints(
            JObject segment,
            List<(double alongM, double heightFromBaseM)> validation)
        {
            var kind = NormalizeSegmentKind(segment["kind"]?.ToString());
            switch (kind)
            {
                case "line":
                    if (!TryReadSegmentPoint(segment, "start", out var l0, out var h0) ||
                        !TryReadSegmentPoint(segment, "end", out var l1, out var h1))
                    {
                        throw new InvalidOperationException(
                            "create_wall_custom_profile_void: line segment needs start/end points.");
                    }

                    validation.Add((l0, h0));
                    validation.Add((l1, h1));
                    break;
                case "arc":
                    if (TryReadSegmentPoint(segment, "start", out l0, out h0) &&
                        TryReadSegmentPoint(segment, "end", out l1, out h1))
                    {
                        validation.Add((l0, h0));
                        if (TryReadSegmentPoint(segment, "mid", out var lm, out var hm))
                        {
                            validation.Add((lm, hm));
                        }

                        validation.Add((l1, h1));
                        break;
                    }

                    AddCenterArcValidationPoints(segment, validation);
                    break;
                case "circle":
                case "ellipse":
                    if (!TryReadEllipseParams(segment, out var ca, out var ch, out var ra, out var rh, out var rotRad, out var startDeg, out var endDeg))
                    {
                        throw new InvalidOperationException(
                            "create_wall_custom_profile_void: ellipse/circle segment parameters are invalid.");
                    }

                    validation.AddRange(SampleEllipseProfilePoints(ca, ch, ra, rh, rotRad, startDeg, endDeg));
                    break;
                default:
                    throw new InvalidOperationException(
                        "create_wall_custom_profile_void: unsupported segment kind \"" + kind + "\". Use line, arc, circle or ellipse.");
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

        /// <summary>
        /// Heart curve in local metres (classic parametric). Uniform Δt yields very short edges near cusps — step in <c>t</c>
        /// so each chord in wall profile space is ≥ min edge (same idea as ellipse tessellation).
        /// </summary>
        private static (double lx, double ly) HeartLocalPoint(double t, double scale)
        {
            var lx = scale * Math.Pow(Math.Sin(t), 3);
            var ly = (scale / 16.0) * (
                13 * Math.Cos(t) -
                5 * Math.Cos(2 * t) -
                2 * Math.Cos(3 * t) -
                Math.Cos(4 * t));
            return (lx, ly);
        }

        private static (double alongM, double heightFromBaseM) HeartWorldAt(
            double t,
            double scale,
            double cu,
            double ch,
            double rotRad)
        {
            var w = HeartLocalPoint(t, scale);
            return LocalToWorld(w.lx, w.ly, cu, ch, rotRad);
        }

        private static double NextHeartParamForMinChord(
            (double al, double ht) lastW,
            double tLo,
            double tHi,
            double scale,
            double cu,
            double ch,
            double rotRad,
            double minChordM)
        {
            double DistLast(double t)
            {
                var w = HeartWorldAt(t, scale, cu, ch, rotRad);
                var dx = w.alongM - lastW.al;
                var dy = w.heightFromBaseM - lastW.ht;
                return Math.Sqrt(dx * dx + dy * dy);
            }

            if (DistLast(tHi) < minChordM - 1e-12 && (tHi - tLo) < 0.02)
            {
                return tHi;
            }

            var lo = tLo;
            var hi = tHi;
            for (var i = 0; i < 56; i++)
            {
                var mid = (lo + hi) * 0.5;
                if (DistLast(mid) >= minChordM)
                {
                    hi = mid;
                }
                else
                {
                    lo = mid;
                }
            }

            return hi;
        }

        private static List<(double, double)> BuildHeartPoints(
            double cu,
            double ch,
            double scale,
            double rotRad)
        {
            var minChordM = MinEdgeLengthMeters * 1.14;
            var tFull = 2 * Math.PI;
            var p0 = HeartWorldAt(0, scale, cu, ch, rotRad);
            var list = new List<(double alongM, double heightFromBaseM)> { p0 };
            var last = p0;
            var t = 0.0;
            var iterations = 0;
            const int maxIter = 100000;

            // Do not stop when |last − p0| is small mid-loop: this curve passes near the start point
            // again before t reaches 2π; that falsely triggered "early close" and collapsed the ring.
            while (t < tFull - 1e-10 && iterations < maxIter)
            {
                iterations++;
                var tNext = NextHeartParamForMinChord(last, t, tFull, scale, cu, ch, rotRad, minChordM);
                if (tNext <= t + 1e-14)
                {
                    tNext = Math.Min(tFull, t + 1e-4);
                }

                var pNext = HeartWorldAt(tNext, scale, cu, ch, rotRad);
                var step = Math.Sqrt(
                    Math.Pow(pNext.alongM - last.alongM, 2) + Math.Pow(pNext.heightFromBaseM - last.heightFromBaseM, 2));
                if (step < 1e-9)
                {
                    t = Math.Min(tFull, tNext + 1e-4);
                    continue;
                }

                list.Add(pNext);
                last = pNext;
                t = tNext;
            }

            if (iterations >= maxIter)
            {
                throw new InvalidOperationException(
                    "create_wall_custom_profile_void: heart tessellation exceeded iteration limit.");
            }

            if (list.Count >= 2)
            {
                var f = list[0];
                var L = list[list.Count - 1];
                var dupLen = Math.Sqrt(
                    Math.Pow(f.alongM - L.alongM, 2) + Math.Pow(f.heightFromBaseM - L.heightFromBaseM, 2));
                if (dupLen < 1e-6)
                {
                    list.RemoveAt(list.Count - 1);
                }
            }

            while (list.Count > 1)
            {
                var a = list[list.Count - 1];
                var dClose = Math.Sqrt(
                    Math.Pow(a.alongM - p0.alongM, 2) + Math.Pow(a.heightFromBaseM - p0.heightFromBaseM, 2));
                if (dClose >= minChordM - 1e-9)
                {
                    break;
                }

                list.RemoveAt(list.Count - 1);
            }

            var lastPt = list[list.Count - 1];
            var dFinal = Math.Sqrt(
                Math.Pow(lastPt.alongM - p0.alongM, 2) + Math.Pow(lastPt.heightFromBaseM - p0.heightFromBaseM, 2));
            var minClose = MinEdgeLengthMeters * 1.02;
            if (list.Count < 3 || dFinal < minClose - 1e-9)
            {
                throw new InvalidOperationException(
                    "create_wall_custom_profile_void: heart scale too small or tessellation collapsed — increase scaleMeters.");
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

        private static List<Curve> BuildInnerCurvesFromSegments(
            VoidLoopSpec loop,
            int loopIdx,
            Func<double, double, XYZ> wallPt,
            double baseZ,
            double topZ,
            double lenFt,
            double insetFt,
            bool clampShell,
            XYZ gc0,
            XYZ dir,
            double minEdgeFt)
        {
            var defs = loop.SegmentDefs ?? new List<JObject>();
            if (defs.Count == 0)
            {
                throw new InvalidOperationException(
                    "create_wall_custom_profile_void: void " + loopIdx + " has no curve segments.");
            }

            var curves = new List<Curve>(defs.Count);
            XYZ? firstStart = null;
            XYZ? prevEnd = null;
            var joinTolFt = Math.Max(1e-4, minEdgeFt * 0.4);
            foreach (var seg in defs)
            {
                var segCurves = BuildSegmentCurves(seg, wallPt, baseZ, topZ, lenFt, insetFt, clampShell, dir);
                if (segCurves.Count == 0)
                {
                    continue;
                }

                foreach (var curve in segCurves)
                {
                    if (curve.Length < minEdgeFt - 1e-10)
                    {
                        throw new InvalidOperationException(
                            "create_wall_custom_profile_void: void " + loopIdx + " has a curve segment shorter than minimum edge length.");
                    }

                    var start = curve.GetEndPoint(0);
                    var end = curve.GetEndPoint(1);
                    if (firstStart == null)
                    {
                        firstStart = start;
                    }

                    if (prevEnd != null && prevEnd.DistanceTo(start) > joinTolFt)
                    {
                        throw new InvalidOperationException(
                            "create_wall_custom_profile_void: void " + loopIdx +
                            " segment chain is not continuous (end/start mismatch).");
                    }

                    prevEnd = end;
                    curves.Add(curve);
                }
            }

            if (firstStart == null || prevEnd == null || prevEnd.DistanceTo(firstStart) > joinTolFt)
            {
                throw new InvalidOperationException(
                    "create_wall_custom_profile_void: void " + loopIdx + " segment chain is not closed.");
            }

            EnsureCurveLoopOppositeWindingToOuter(curves, gc0, dir, lenFt, baseZ, topZ);

            var samples = SampleCurveLoop(curves, 16);
            if (samples.Count < 3)
            {
                throw new InvalidOperationException(
                    "create_wall_custom_profile_void: void " + loopIdx + " could not be sampled for validation.");
            }

            foreach (var p in samples)
            {
                var along = (p - gc0).DotProduct(dir);
                var z = p.Z;
                if (along < insetFt - 1e-6 || along > lenFt - insetFt + 1e-6 ||
                    z < baseZ + insetFt - 1e-6 || z > topZ - insetFt + 1e-6)
                {
                    throw new InvalidOperationException(
                        "create_wall_custom_profile_void: void " + loopIdx +
                        " curve extends outside wall shell inset. Reduce radii or move control points.");
                }
            }

            if (HasSelfIntersection2D(samples, gc0, dir))
            {
                throw new InvalidOperationException(
                    "create_wall_custom_profile_void: void " + loopIdx + " self-intersects.");
            }

            return curves;
        }

        private static List<Curve> BuildSegmentCurves(
            JObject seg,
            Func<double, double, XYZ> wallPt,
            double baseZ,
            double topZ,
            double lenFt,
            double insetFt,
            bool clampShell,
            XYZ dir)
        {
            var kind = NormalizeSegmentKind(seg["kind"]?.ToString());
            switch (kind)
            {
                case "line":
                    if (!TryReadSegmentPoint(seg, "start", out var l0, out var h0) ||
                        !TryReadSegmentPoint(seg, "end", out var l1, out var h1))
                    {
                        throw new InvalidOperationException(
                            "create_wall_custom_profile_void: line segment requires start/end.");
                    }

                    return new List<Curve>
                    {
                        Line.CreateBound(
                            MapProfilePointToWall(wallPt, l0, h0, baseZ, topZ, lenFt, insetFt, clampShell),
                            MapProfilePointToWall(wallPt, l1, h1, baseZ, topZ, lenFt, insetFt, clampShell))
                    };
                case "arc":
                    return new List<Curve> { BuildArcCurve(seg, wallPt, baseZ, topZ, lenFt, insetFt, clampShell) };
                case "circle":
                case "ellipse":
                    return BuildEllipseCurves(seg, wallPt, baseZ, topZ, lenFt, insetFt, clampShell, dir);
                default:
                    throw new InvalidOperationException(
                        "create_wall_custom_profile_void: unsupported segment kind \"" + kind + "\".");
            }
        }

        private static Curve BuildArcCurve(
            JObject seg,
            Func<double, double, XYZ> wallPt,
            double baseZ,
            double topZ,
            double lenFt,
            double insetFt,
            bool clampShell)
        {
            if (TryReadSegmentPoint(seg, "start", out var l0, out var h0) &&
                TryReadSegmentPoint(seg, "end", out var l1, out var h1))
            {
                if (!TryReadSegmentPoint(seg, "mid", out var lm, out var hm))
                {
                    throw new InvalidOperationException(
                        "create_wall_custom_profile_void: arc by points requires start, mid and end.");
                }

                var p0 = MapProfilePointToWall(wallPt, l0, h0, baseZ, topZ, lenFt, insetFt, clampShell);
                var p1 = MapProfilePointToWall(wallPt, l1, h1, baseZ, topZ, lenFt, insetFt, clampShell);
                var pm = MapProfilePointToWall(wallPt, lm, hm, baseZ, topZ, lenFt, insetFt, clampShell);
                return Arc.Create(p0, p1, pm);
            }

            if (!TryReadSegmentPoint(seg, "center", out var ca, out var ch) ||
                !TryReadNumber(seg["radiusMeters"], out var rM) || rM <= 1e-6 ||
                !TryReadNumber(seg["startAngleDegrees"], out var startDeg) ||
                !TryReadNumber(seg["endAngleDegrees"], out var endDeg))
            {
                throw new InvalidOperationException(
                    "create_wall_custom_profile_void: arc segment requires start/mid/end OR center+radiusMeters+startAngleDegrees+endAngleDegrees.");
            }

            var rotRad = (ReadOptionalNumber(seg, "rotationDegrees") ?? 0.0) * Math.PI / 180.0;
            var startRad = startDeg * Math.PI / 180.0;
            var endRad = endDeg * Math.PI / 180.0;
            var sweep = NormalizePositiveAngle(endRad - startRad);
            if (sweep < 1e-6 || sweep > (2 * Math.PI) - 1e-6)
            {
                throw new InvalidOperationException(
                    "create_wall_custom_profile_void: arc sweep must be in (0, 360) degrees.");
            }

            var midRad = startRad + sweep / 2.0;
            var pStart = MapProfilePointToWall(
                wallPt,
                ca + rM * Math.Cos(startRad + rotRad),
                ch + rM * Math.Sin(startRad + rotRad),
                baseZ,
                topZ,
                lenFt,
                insetFt,
                clampShell);
            var pEnd = MapProfilePointToWall(
                wallPt,
                ca + rM * Math.Cos(endRad + rotRad),
                ch + rM * Math.Sin(endRad + rotRad),
                baseZ,
                topZ,
                lenFt,
                insetFt,
                clampShell);
            var pMid = MapProfilePointToWall(
                wallPt,
                ca + rM * Math.Cos(midRad + rotRad),
                ch + rM * Math.Sin(midRad + rotRad),
                baseZ,
                topZ,
                lenFt,
                insetFt,
                clampShell);

            return Arc.Create(pStart, pEnd, pMid);
        }

        private static List<Curve> BuildEllipseCurves(
            JObject seg,
            Func<double, double, XYZ> wallPt,
            double baseZ,
            double topZ,
            double lenFt,
            double insetFt,
            bool clampShell,
            XYZ dir)
        {
            if (!TryReadEllipseParams(seg, out var centerAlongM, out var centerHeightM, out var radiusAlongM, out var radiusHeightM, out var rotRad, out var startDeg, out var endDeg))
            {
                throw new InvalidOperationException(
                    "create_wall_custom_profile_void: ellipse/circle segment needs center and radii.");
            }

            var center = MapProfilePointToWall(wallPt, centerAlongM, centerHeightM, baseZ, topZ, lenFt, insetFt, clampShell);
            var xAxis = (dir * Math.Cos(rotRad) + XYZ.BasisZ * Math.Sin(rotRad)).Normalize();
            var yAxis = (dir * (-Math.Sin(rotRad)) + XYZ.BasisZ * Math.Cos(rotRad)).Normalize();
            var rxFt = RevitWallCreationOps.MetersToFeet(radiusAlongM);
            var ryFt = RevitWallCreationOps.MetersToFeet(radiusHeightM);
            var startRad = startDeg * Math.PI / 180.0;
            var endRad = endDeg * Math.PI / 180.0;

            var sweep = NormalizePositiveAngle(endRad - startRad);
            if (sweep < 1e-6 || Math.Abs(sweep - 2 * Math.PI) < 1e-6)
            {
                startRad = 0.0;
                sweep = 2 * Math.PI;
            }
            else if (endRad <= startRad)
            {
                endRad += 2 * Math.PI;
                sweep = endRad - startRad;
            }

            // Ellipse.CreateCurve often yields curves that NewModelCurve rejects as "not bound" in wall
            // profile sketches. Use true circular Arc segments for circles; line chains for real ellipses.
            var maxR = Math.Max(radiusAlongM, radiusHeightM);
            var isCircle = maxR > 1e-9 && Math.Abs(radiusAlongM - radiusHeightM) / maxR < 1e-9;
            if (isCircle)
            {
                return BuildCircularSweepAsArcs(center, xAxis, yAxis, rxFt, startRad, sweep);
            }

            var closeAsRing = Math.Abs(sweep - 2 * Math.PI) < 0.02;
            return BuildEllipseSweepAsAdaptiveMinChordPolyline(
                center, xAxis, yAxis, rxFt, ryFt, startRad, sweep, closeAsRing);
        }

        /// <summary>
        /// Steps in parameter <paramref name="t"/> so each chord is ≥ min edge length. Equal Δt fails on ellipses
        /// (arc-length varies), which produced “could not tessellate…” even for large radii.
        /// When <paramref name="closeAsRing"/> is false (typical half-ellipse JSON segment), do not add a closing chord to
        /// <c>p0</c> — that chord was a flat diameter and broke two-segment full ellipses (vesica / wrong outline + short edges).
        /// </summary>
        private static List<Curve> BuildEllipseSweepAsAdaptiveMinChordPolyline(
            XYZ c,
            XYZ xa,
            XYZ ya,
            double rxFt,
            double ryFt,
            double startRad,
            double sweepRad,
            bool closeAsRing)
        {
            var minChordFt = RevitWallCreationOps.MetersToFeet(MinEdgeLengthMeters);
            if (minChordFt < 1e-12)
            {
                throw new InvalidOperationException("create_wall_custom_profile_void: invalid min chord.");
            }

            var tStart = startRad;
            var tEnd = startRad + sweepRad;
            var p0 = EllipsePointOnPlane(c, xa, ya, rxFt, ryFt, tStart);
            var points = new List<XYZ> { p0 };
            var last = p0;
            var t = tStart;
            var iterations = 0;
            const int maxIter = 100000;

            while (t < tEnd - 1e-10 && iterations < maxIter)
            {
                iterations++;
                var pAtEnd = EllipsePointOnPlane(c, xa, ya, rxFt, ryFt, tEnd);
                var distRemain = last.DistanceTo(pAtEnd);
                if (t > tStart + 1e-8 && distRemain < minChordFt + 1e-9)
                {
                    break;
                }

                var tNext = NextEllipseParamForMinChord(last, t, tEnd, c, xa, ya, rxFt, ryFt, minChordFt);
                if (tNext <= t + 1e-14)
                {
                    tNext = Math.Min(tEnd, t + 1e-4);
                }

                var pNext = EllipsePointOnPlane(c, xa, ya, rxFt, ryFt, tNext);
                if (pNext.DistanceTo(last) < 1e-9)
                {
                    t = Math.Min(tEnd, tNext + 1e-5);
                    continue;
                }

                points.Add(pNext);
                last = pNext;
                t = tNext;
            }

            if (iterations >= maxIter)
            {
                throw new InvalidOperationException(
                    "create_wall_custom_profile_void: ellipse tessellation exceeded iteration limit.");
            }

            if (!closeAsRing)
            {
                var pEndOpen = EllipsePointOnPlane(c, xa, ya, rxFt, ryFt, tEnd);
                FinalizeOpenEllipsePointChain(points, pEndOpen, minChordFt);

                if (points.Count < 2)
                {
                    throw new InvalidOperationException("create_wall_custom_profile_void: ellipse arc tessellation collapsed.");
                }

                var openCurves = new List<Curve>();
                for (var i = 0; i < points.Count - 1; i++)
                {
                    var a = points[i];
                    var b = points[i + 1];
                    var d = a.DistanceTo(b);
                    if (d < 1e-9)
                    {
                        continue;
                    }

                    if (d < minChordFt - 1e-9)
                    {
                        throw new InvalidOperationException(
                            "create_wall_custom_profile_void: ellipse arc has a segment shorter than minimum edge length.");
                    }

                    openCurves.Add(Line.CreateBound(a, b));
                }

                if (openCurves.Count == 0)
                {
                    throw new InvalidOperationException(
                        "create_wall_custom_profile_void: ellipse arc produced no edges.");
                }

                return openCurves;
            }

            // Full ring in one JSON segment (2π): closing edge is last point → p0 (p0 not duplicated in list)
            FinalizeClosedEllipsePointChain(points, p0, minChordFt);

            if (points.Count < 2)
            {
                throw new InvalidOperationException("create_wall_custom_profile_void: ellipse tessellation collapsed.");
            }

            var closeLen = points[points.Count - 1].DistanceTo(p0);
            if (closeLen > 1e-9 && closeLen < minChordFt - 1e-9)
            {
                throw new InvalidOperationException(
                    "create_wall_custom_profile_void: ellipse closing edge too short — reduce radii slightly or raise center so the loop fits the wall.");
            }

            var curves = new List<Curve>();
            for (var i = 0; i < points.Count; i++)
            {
                var a = points[i];
                var b = i < points.Count - 1 ? points[i + 1] : p0;
                if (a.DistanceTo(b) < 1e-9)
                {
                    continue;
                }

                curves.Add(Line.CreateBound(a, b));
            }

            if (curves.Count < 3)
            {
                throw new InvalidOperationException(
                    "create_wall_custom_profile_void: ellipse needs at least 3 edges after tessellation.");
            }

            return curves;
        }

        /// <summary>
        /// Drop trailing tessellation vertices that sit too close to <paramref name="pEnd"/>, then append <paramref name="pEnd"/>
        /// so the last chord is never shorter than <paramref name="minChordFt"/> (avoids invalid Revit edges).
        /// </summary>
        private static void FinalizeOpenEllipsePointChain(List<XYZ> points, XYZ pEnd, double minChordFt)
        {
            if (points.Count == 0)
            {
                throw new InvalidOperationException("create_wall_custom_profile_void: ellipse tessellation empty.");
            }

            while (points.Count > 1 && points[points.Count - 1].DistanceTo(pEnd) < minChordFt - 1e-9)
            {
                points.RemoveAt(points.Count - 1);
            }

            var d = points[points.Count - 1].DistanceTo(pEnd);
            if (d <= 1e-9)
            {
                return;
            }

            if (d < minChordFt - 1e-9)
            {
                if (points.Count == 1)
                {
                    throw new InvalidOperationException(
                        "create_wall_custom_profile_void: ellipse arc span smaller than minimum edge length.");
                }

                throw new InvalidOperationException(
                    "create_wall_custom_profile_void: could not tessellate ellipse arc with minimum edge length.");
            }

            points.Add(pEnd);
        }

        /// <summary>Trim near-duplicate trailing points so the implicit closing edge to <paramref name="p0"/> is long enough.</summary>
        private static void FinalizeClosedEllipsePointChain(List<XYZ> points, XYZ p0, double minChordFt)
        {
            if (points.Count == 0)
            {
                throw new InvalidOperationException("create_wall_custom_profile_void: ellipse tessellation empty.");
            }

            while (points.Count > 1 && points[points.Count - 1].DistanceTo(p0) < minChordFt * 0.25)
            {
                points.RemoveAt(points.Count - 1);
            }

            while (points.Count > 2 && points[points.Count - 1].DistanceTo(p0) < minChordFt - 1e-9)
            {
                points.RemoveAt(points.Count - 1);
            }
        }

        /// <summary>Smallest t in (tLo, tHi] with chord from <paramref name="last"/> to E(t) ≥ minChord, or tHi if remainder is shorter.</summary>
        private static double NextEllipseParamForMinChord(
            XYZ last,
            double tLo,
            double tHi,
            XYZ c,
            XYZ xa,
            XYZ ya,
            double rxFt,
            double ryFt,
            double minChordFt)
        {
            var pHi = EllipsePointOnPlane(c, xa, ya, rxFt, ryFt, tHi);
            if (last.DistanceTo(pHi) < minChordFt - 1e-10)
            {
                return tHi;
            }

            var lo = tLo;
            var hi = tHi;
            for (var i = 0; i < 56; i++)
            {
                var mid = (lo + hi) * 0.5;
                var pMid = EllipsePointOnPlane(c, xa, ya, rxFt, ryFt, mid);
                if (last.DistanceTo(pMid) >= minChordFt)
                {
                    hi = mid;
                }
                else
                {
                    lo = mid;
                }
            }

            return hi;
        }

        /// <summary>Point on axis-aligned ellipse in wall profile plane: C + cos(t)*rx*xAxis + sin(t)*ry*yAxis.</summary>
        private static XYZ EllipsePointOnPlane(XYZ c, XYZ xAxis, XYZ yAxis, double rxFt, double ryFt, double t)
        {
            return c + xAxis * (rxFt * Math.Cos(t)) + yAxis * (ryFt * Math.Sin(t));
        }

        /// <summary>
        /// Circular arcs only (each sweep ≤ π) — always bounded; safe for <see cref="Autodesk.Revit.Creation.Document.NewModelCurve"/>.
        /// Revit’s UI “Circle” tool does not map to a separate <c>Circle</c> curve class: native circular geometry in the API is
        /// <see cref="Arc"/> (same as drawing a circle in a sketch). Full 360° is typically two semicircular arcs — one arc for the
        /// whole revolution is often rejected or unbound in profile sketches; see also why we avoid <see cref="Ellipse"/> here.
        /// </summary>
        private static List<Curve> BuildCircularSweepAsArcs(XYZ c, XYZ xa, XYZ ya, double rFt, double startRad, double sweepRad)
        {
            if (rFt < 1e-9 || sweepRad < 1e-9)
            {
                throw new InvalidOperationException("create_wall_custom_profile_void: circle radius or sweep invalid.");
            }

            var curves = new List<Curve>();
            var n = Math.Max(1, (int)Math.Ceiling(sweepRad / (Math.PI - 1e-6)));
            var d = sweepRad / n;
            for (var i = 0; i < n; i++)
            {
                var t0 = startRad + i * d;
                var tm = startRad + (i + 0.5) * d;
                var t1 = startRad + (i + 1) * d;
                var p0 = EllipsePointOnPlane(c, xa, ya, rFt, rFt, t0);
                var pm = EllipsePointOnPlane(c, xa, ya, rFt, rFt, tm);
                var p1 = EllipsePointOnPlane(c, xa, ya, rFt, rFt, t1);
                curves.Add(Arc.Create(p0, p1, pm));
            }

            return curves;
        }

        private static XYZ MapProfilePointToWall(
            Func<double, double, XYZ> wallPt,
            double alongM,
            double heightFromBaseM,
            double baseZ,
            double topZ,
            double lenFt,
            double insetFt,
            bool clampShell)
        {
            var alongFt = RevitWallCreationOps.MetersToFeet(alongM);
            var z = baseZ + RevitWallCreationOps.MetersToFeet(heightFromBaseM);
            if (clampShell)
            {
                alongFt = Math.Max(insetFt, Math.Min(lenFt - insetFt, alongFt));
                z = Math.Max(baseZ + insetFt, Math.Min(topZ - insetFt, z));
            }
            else if (alongFt < insetFt - 1e-6 || alongFt > lenFt - insetFt + 1e-6 ||
                     z < baseZ + insetFt - 1e-6 || z > topZ - insetFt + 1e-6)
            {
                throw new InvalidOperationException(
                    "create_wall_custom_profile_void: segment point outside wall face (use clampToWallShell true or move points).");
            }

            return wallPt(alongFt, z);
        }

        private static void EnsureCurveLoopOppositeWindingToOuter(
            List<Curve> curves,
            XYZ gc0,
            XYZ dir,
            double lenFt,
            double baseZ,
            double topZ)
        {
            var points = curves.Select(c => c.GetEndPoint(0)).ToList();
            if (points.Count < 3)
            {
                return;
            }

            var outerUv = new List<(double u, double v)>
            {
                (0, baseZ),
                (lenFt, baseZ),
                (lenFt, topZ),
                (0, topZ)
            };
            var outerA = SignedAreaUv(outerUv);
            var innerUv = points.Select(p => ((p - gc0).DotProduct(dir), p.Z)).ToList();
            var innerA = SignedAreaUv(innerUv);
            if (outerA * innerA <= 0)
            {
                return;
            }

            curves.Reverse();
            for (var i = 0; i < curves.Count; i++)
            {
                curves[i] = curves[i].CreateReversed();
            }
        }

        private static List<XYZ> SampleCurveLoop(IReadOnlyList<Curve> curves, int samplesPerCurve)
        {
            var output = new List<XYZ>();
            foreach (var c in curves)
            {
                var count = Math.Max(2, samplesPerCurve);
                for (var i = 0; i < count; i++)
                {
                    var t = (double)i / count;
                    var p = c.Evaluate(t, true);
                    if (output.Count == 0 || output[output.Count - 1].DistanceTo(p) > 1e-6)
                    {
                        output.Add(p);
                    }
                }
            }

            return output;
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

        private static string NormalizeSegmentKind(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().ToLowerInvariant().Replace("-", "_");
        }

        private static bool TryReadSegmentPoint(JObject segment, string key, out double alongM, out double heightM)
        {
            alongM = heightM = 0;
            if (segment[key] is not JObject point)
            {
                return false;
            }

            return TryReadAlongAndHeight(point, out alongM, out heightM) || TryReadXY(point, out alongM, out heightM);
        }

        private static bool TryReadXY(JObject jo, out double x, out double y)
        {
            x = y = 0;
            return TryReadNumber(jo["x"], out x) && TryReadNumber(jo["y"], out y);
        }

        private static bool TryReadEllipseParams(
            JObject segment,
            out double centerAlongM,
            out double centerHeightM,
            out double radiusAlongM,
            out double radiusHeightM,
            out double rotationRad,
            out double startDeg,
            out double endDeg)
        {
            centerAlongM = centerHeightM = radiusAlongM = radiusHeightM = rotationRad = startDeg = endDeg = 0;
            if (!TryReadSegmentPoint(segment, "center", out centerAlongM, out centerHeightM))
            {
                return false;
            }

            var kind = NormalizeSegmentKind(segment["kind"]?.ToString());
            if (kind == "circle")
            {
                if (!TryReadNumber(segment["radiusMeters"], out var radiusM) || radiusM <= 1e-6)
                {
                    return false;
                }

                radiusAlongM = radiusM;
                radiusHeightM = radiusM;
            }
            else
            {
                if (!TryReadNumber(segment["radiusAlongMeters"], out radiusAlongM) || radiusAlongM <= 1e-6 ||
                    !TryReadNumber(segment["radiusHeightMeters"], out radiusHeightM) || radiusHeightM <= 1e-6)
                {
                    return false;
                }
            }

            var rotDeg = ReadOptionalNumber(segment, "rotationDegrees") ?? 0.0;
            rotationRad = rotDeg * Math.PI / 180.0;
            startDeg = ReadOptionalNumber(segment, "startAngleDegrees") ?? 0.0;
            endDeg = ReadOptionalNumber(segment, "endAngleDegrees") ?? 360.0;
            return true;
        }

        private static List<(double alongM, double heightFromBaseM)> SampleEllipseProfilePoints(
            double centerAlongM,
            double centerHeightM,
            double radiusAlongM,
            double radiusHeightM,
            double rotationRad,
            double startDeg,
            double endDeg)
        {
            var startRad = startDeg * Math.PI / 180.0;
            var endRad = endDeg * Math.PI / 180.0;
            var sweep = NormalizePositiveAngle(endRad - startRad);
            if (sweep < 1e-6 || Math.Abs(sweep - 2 * Math.PI) < 1e-6)
            {
                sweep = 2 * Math.PI;
                startRad = 0;
            }

            var segments = Math.Max(16, (int)Math.Ceiling(sweep / (Math.PI / 12.0)));
            var output = new List<(double alongM, double heightFromBaseM)>(segments);
            for (var i = 0; i < segments; i++)
            {
                var t = startRad + sweep * i / segments;
                var localU = radiusAlongM * Math.Cos(t);
                var localV = radiusHeightM * Math.Sin(t);
                var (rU, rV) = RotateLocal(localU, localV, rotationRad);
                output.Add((centerAlongM + rU, centerHeightM + rV));
            }

            return output;
        }

        private static void AddCenterArcValidationPoints(
            JObject segment,
            List<(double alongM, double heightFromBaseM)> validation)
        {
            if (!TryReadSegmentPoint(segment, "center", out var ca, out var ch) ||
                !TryReadNumber(segment["radiusMeters"], out var rM) || rM <= 1e-6 ||
                !TryReadNumber(segment["startAngleDegrees"], out var startDeg) ||
                !TryReadNumber(segment["endAngleDegrees"], out var endDeg))
            {
                throw new InvalidOperationException(
                    "create_wall_custom_profile_void: arc segment requires start/mid/end OR center+radiusMeters+angles.");
            }

            var rotRad = (ReadOptionalNumber(segment, "rotationDegrees") ?? 0.0) * Math.PI / 180.0;
            var startRad = startDeg * Math.PI / 180.0;
            var endRad = endDeg * Math.PI / 180.0;
            var sweep = NormalizePositiveAngle(endRad - startRad);
            if (sweep < 1e-6 || sweep > 2 * Math.PI - 1e-6)
            {
                throw new InvalidOperationException(
                    "create_wall_custom_profile_void: arc segment sweep must be in (0, 360) degrees.");
            }

            var midRad = startRad + sweep / 2.0;
            validation.Add((ca + rM * Math.Cos(startRad + rotRad), ch + rM * Math.Sin(startRad + rotRad)));
            validation.Add((ca + rM * Math.Cos(midRad + rotRad), ch + rM * Math.Sin(midRad + rotRad)));
            validation.Add((ca + rM * Math.Cos(endRad + rotRad), ch + rM * Math.Sin(endRad + rotRad)));
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

        private static double? ReadOptionalNumber(JObject op, string key)
        {
            return TryReadNumber(op[key], out var v) ? v : (double?)null;
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
