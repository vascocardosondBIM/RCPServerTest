using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitSketchPoC.Core.Geometry;
using RevitSketchPoC.RevitOperations.CreateElements;
using RevitSketchPoC.RevitOperations.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RevitSketchPoC.RevitOperations.ReviewElements
{
    /// <summary>
    /// Review / repair: detects slab outlines that disagree with a ring of walls (e.g. smooth circle vs faceted wall polygon)
    /// and can rebuild the floor from a closed chain of wall location curves.
    /// </summary>
    public static class RevitFloorWallFootprintOps
    {
        private const double DefaultMismatchDistanceMeters = 0.08;
        private const double DefaultAreaRatioTolerance = 0.06;
        private const int MaxTessellationPointsPerCurve = 24;
        private const int MaxSamplePointsForMetric = 400;
        /// <summary>Keep in sync with MaxIdsPerOp on the JSON ops executor (duplicated to avoid project reference cycles).</summary>
        private const int MaxElementIdsPerOp = 50;

        /// <summary>
        /// Emits one JSON line per floor: metrics, flags, and suggested repair op payload (read-only).
        /// </summary>
        public static void RunAnalyzeFloorWallFootprintJsonOp(Document doc, JObject op, StringBuilder log)
        {
            var floorIds = ReadFloorIds(op, MaxElementIdsPerOp);
            if (floorIds.Count == 0)
            {
                throw new InvalidOperationException(
                    "analyze_floor_wall_footprint requires floorId (integer) or floorIds (array).");
            }

            var tolM = op["toleranceMeters"]?.Value<double?>() ?? DefaultMismatchDistanceMeters;
            var areaTol = op["areaRatioTolerance"]?.Value<double?>() ?? DefaultAreaRatioTolerance;
            var explicitRoomId = op["roomId"]?.Value<long?>();
            var boundaryLocRaw = op["boundaryLocation"]?.ToString();
            var roomBoundaryLocation = RevitRoomBoundaryLoops.ParseBoundaryLocation(boundaryLocRaw);

            foreach (var fid in floorIds)
            {
                if (doc.GetElement(fid) is not Floor floor)
                {
                    log.AppendLine(JsonConvert.SerializeObject(new
                    {
                        op = "analyze_floor_wall_footprint_result",
                        floorId = fid.IntegerValue,
                        error = "not_a_floor"
                    }));
                    continue;
                }

                var level = doc.GetElement(floor.LevelId) as Level;
                var wallIds = ReadOptionalWallIds(op);
                var walls = ResolveWallsForFloor(doc, floor, wallIds);
                var report = BuildReport(
                    doc,
                    floor,
                    level,
                    walls,
                    tolM,
                    areaTol,
                    explicitRoomId,
                    roomBoundaryLocation,
                    boundaryLocRaw);
                log.AppendLine(BuildHumanAnalyzeSummary(report));
                if (op["includeJson"]?.Value<bool?>() == true)
                {
                    log.AppendLine(report.ToString(Formatting.None));
                }
            }
        }

        /// <summary>
        /// Deletes the floor and recreates it with a boundary derived from wall location curves on the same level.
        /// </summary>
        public static void RunRepairFloorToWallFootprintJsonOp(Document doc, JObject op, StringBuilder log)
        {
            var floorIds = ReadFloorIds(op, 1);
            if (floorIds.Count != 1)
            {
                throw new InvalidOperationException("repair_floor_to_wall_footprint requires a single floorId.");
            }

            var floorId = floorIds[0];
            if (doc.GetElement(floorId) is not Floor floor)
            {
                throw new InvalidOperationException("repair_floor_to_wall_footprint: element is not a Floor.");
            }

            var align = (op["alignTo"]?.ToString() ?? "wall_centerline").Trim().ToLowerInvariant();
            if (align is not ("wall_centerline" or "wall_inside" or "wall_outside"))
            {
                throw new InvalidOperationException(
                    "repair_floor_to_wall_footprint: alignTo must be wall_centerline, wall_inside, or wall_outside.");
            }

            var level = doc.GetElement(floor.LevelId) as Level
                        ?? throw new InvalidOperationException("Floor has no valid level.");

            var wallIds = ReadOptionalWallIds(op);
            var walls = ResolveWallsForFloor(doc, floor, wallIds);
            if (walls.Count < 3)
            {
                throw new InvalidOperationException(
                    "repair_floor_to_wall_footprint: need at least 3 walls on the floor level (or pass wallIds).");
            }

            var tolFt = RevitWallCreationOps.MetersToFeet(PlanGeometryRules.EndpointJoinToleranceMeters);
            var z = level.Elevation;
            var wallCurves = CollectWallLocationCurves(walls, z);
            if (!TryBuildClosedCurveChain(wallCurves, tolFt, out var orderedWallCurves, out var closed))
            {
                throw new InvalidOperationException(
                    "repair_floor_to_wall_footprint: could not build a single closed loop from wall location curves.");
            }

            if (!closed)
            {
                throw new InvalidOperationException(
                    "repair_floor_to_wall_footprint: wall chain is not closed within join tolerance.");
            }

            var profileCurves = orderedWallCurves.ToList();

            if (align is "wall_inside" or "wall_outside")
            {
                var rawSegs = CollectWallSegments(walls, z);
                if (!TryBuildClosedPolygon(rawSegs, tolFt, out var loopMeters, out _))
                {
                    throw new InvalidOperationException(
                        "repair_floor_to_wall_footprint: could not offset wall chain for alignTo mode.");
                }

                var vertsInternal = loopMeters
                    .Select(p => new XYZ(RevitWallCreationOps.MetersToFeet(p.X), RevitWallCreationOps.MetersToFeet(p.Y), z))
                    .ToList();
                var avgW = walls.Average(w => w.Width);
                var d = avgW * 0.5 * (align == "wall_inside" ? 1.0 : -1.0);
                vertsInternal = OffsetPolygonInPlan(vertsInternal, d);
                if (vertsInternal.Count < 3)
                {
                    throw new InvalidOperationException("repair_floor_to_wall_footprint: offset produced degenerate boundary.");
                }

                profileCurves = PolylineToCurves(vertsInternal, tolFt * 0.5).Cast<Curve>().ToList();
            }

            var newLoop = RevitOpJsonGeometry.TryCreateCurveLoopFromCurves(profileCurves, tolFt * 0.5);
            if (newLoop == null)
            {
                throw new InvalidOperationException("repair_floor_to_wall_footprint: boundary has too few edges after cleanup.");
            }

            var floorTypeId = floor.FloorType.Id;
            var isStructural = floor.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)?.AsInteger() == 1;
            var offsetInternal = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.AsDouble() ?? 0.0;
            var comment = floor.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();
            var oldId = floor.Id.IntegerValue;
            Floor? created = null;
            var subTx = new SubTransaction(doc);
            subTx.Start();
            try
            {
                doc.Delete(floorId);
                created = Floor.Create(doc, new List<CurveLoop> { newLoop }, floorTypeId, level.Id);
                subTx.Commit();
            }
            catch (Exception ex)
            {
                subTx.RollBack();
                throw new InvalidOperationException(
                    "repair_floor_to_wall_footprint: Floor.Create failed (rollback applied, original floor preserved): " + ex.Message);
            }

            if (created == null)
            {
                throw new InvalidOperationException("repair_floor_to_wall_footprint: Floor.Create returned null.");
            }

            try
            {
                if (isStructural)
                {
                    created.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)?.Set(1);
                }

                created.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.Set(offsetInternal);
                if (!string.IsNullOrWhiteSpace(comment))
                {
                    created.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(comment);
                }
            }
            catch
            {
                // optional parameter copy
            }

            log.AppendLine(
                "repair_floor_to_wall_footprint oldId=" + oldId + " newId=" + created.Id.IntegerValue +
                " wallsUsed=" + walls.Count + " alignTo=" + align);
        }

        /// <summary>
        /// Rebuilds the floor from the placed room boundary (same geometry as <c>create_floor_from_room</c>).
        /// </summary>
        public static void RunRepairFloorToRoomFootprintJsonOp(Document doc, JObject op, StringBuilder log)
        {
            var floorIds = ReadFloorIds(op, 1);
            if (floorIds.Count != 1)
            {
                throw new InvalidOperationException("repair_floor_to_room_footprint requires a single floorId.");
            }

            var floorId = floorIds[0];
            if (doc.GetElement(floorId) is not Floor floor)
            {
                throw new InvalidOperationException("repair_floor_to_room_footprint: element is not a Floor.");
            }

            var roomIdLong = op["roomId"]?.Value<long?>()
                             ?? throw new InvalidOperationException("repair_floor_to_room_footprint requires roomId.");
            if (doc.GetElement(new ElementId((long)roomIdLong)) is not Room room)
            {
                throw new InvalidOperationException("repair_floor_to_room_footprint: element is not a Room.");
            }

            if (room.LevelId != floor.LevelId)
            {
                throw new InvalidOperationException(
                    "repair_floor_to_room_footprint: Room and Floor must be on the same level.");
            }

            var level = doc.GetElement(floor.LevelId) as Level
                        ?? throw new InvalidOperationException("Floor has no valid level.");

            var z = level.Elevation;
            var boundaryLoc = RevitRoomBoundaryLoops.ParseBoundaryLocation(op["boundaryLocation"]?.ToString());
            var loops = RevitRoomBoundaryLoops.BuildCurveLoopsForSlab(room, z, boundaryLoc);
            if (loops.Count == 0)
            {
                throw new InvalidOperationException("repair_floor_to_room_footprint: Room produced no boundary loops.");
            }

            var floorTypeId = floor.FloorType.Id;
            var isStructural = floor.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)?.AsInteger() == 1;
            var offsetInternal = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.AsDouble() ?? 0.0;
            var comment = floor.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();
            var oldId = floor.Id.IntegerValue;
            Floor? created = null;
            var subTx = new SubTransaction(doc);
            subTx.Start();
            try
            {
                doc.Delete(floorId);
                created = Floor.Create(doc, loops, floorTypeId, level.Id);
                subTx.Commit();
            }
            catch (Exception ex)
            {
                subTx.RollBack();
                throw new InvalidOperationException(
                    "repair_floor_to_room_footprint: Floor.Create failed (rollback applied, original floor preserved): " +
                    ex.Message);
            }

            if (created == null)
            {
                throw new InvalidOperationException("repair_floor_to_room_footprint: Floor.Create returned null.");
            }

            try
            {
                if (isStructural)
                {
                    created.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)?.Set(1);
                }

                created.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.Set(offsetInternal);
                if (!string.IsNullOrWhiteSpace(comment))
                {
                    created.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(comment);
                }
            }
            catch
            {
                // optional parameter copy
            }

            log.AppendLine(
                "repair_floor_to_room_footprint oldId=" + oldId + " newId=" + created.Id.IntegerValue +
                " roomId=" + roomIdLong + " loops=" + loops.Count);
        }

        private static List<ElementId> ReadFloorIds(JObject op, int max)
        {
            var fromArr = RevitOpsElementIdList.Read(op["floorIds"], max)
                .Concat(RevitOpsElementIdList.Read(op["elementIds"], max))
                .ToList();
            if (fromArr.Count > 0)
            {
                return fromArr.Distinct().Take(max).ToList();
            }

            var single = op["floorId"]?.Value<long?>() ?? op["elementId"]?.Value<long?>();
            if (single != null)
            {
                return new List<ElementId> { new ElementId((long)single) };
            }

            return new List<ElementId>();
        }

        private static List<ElementId>? ReadOptionalWallIds(JObject op)
        {
            var ids = RevitOpsElementIdList.Read(op["wallIds"], MaxElementIdsPerOp);
            return ids.Count > 0 ? ids : null;
        }

        private static List<Wall> ResolveWallsForFloor(Document doc, Floor floor, List<ElementId>? wallIds)
        {
            if (wallIds != null && wallIds.Count > 0)
            {
                var list = new List<Wall>();
                foreach (var id in wallIds)
                {
                    if (doc.GetElement(id) is Wall w && w.LevelId == floor.LevelId)
                    {
                        list.Add(w);
                    }
                }

                return list;
            }

            var levelWalls = new FilteredElementCollector(doc)
                .OfClass(typeof(Wall))
                .Cast<Wall>()
                .Where(w => w.LevelId == floor.LevelId)
                .ToList();

            return FilterWallsNearFloorBoundary(levelWalls, floor);
        }

        private static JObject BuildReport(
            Document doc,
            Floor floor,
            Level? level,
            List<Wall> walls,
            double tolMeters,
            double areaRatioTolerance,
            long? explicitRoomId,
            SpatialElementBoundaryLocation roomBoundaryLocation,
            string? boundaryLocationRaw)
        {
            var floorId = floor.Id.IntegerValue;
            var levelName = level?.Name;
            var zWork = level?.Elevation ?? 0.0;
            var boundaryLocationLabel = string.IsNullOrWhiteSpace(boundaryLocationRaw)
                ? "finish"
                : boundaryLocationRaw.Trim();

            var floorBoundary = DescribeFloorBoundary(floor);
            var tolFt = RevitWallCreationOps.MetersToFeet(PlanGeometryRules.EndpointJoinToleranceMeters);
            var rawSegs = CollectWallSegments(walls, zWork);
            var wallFootprint = DescribeWallFootprint(rawSegs, tolFt);
            wallFootprint.WallCount = walls.Count;
            var wallCurves = CollectWallLocationCurves(walls, zWork);
            var wallCurveChainClosed = TryBuildClosedCurveChain(wallCurves, tolFt, out _, out var chainClosed) && chainClosed;

            var wallMismatch = false;
            double? areaRatioWall = null;
            double? maxDistWall = null;

            if (floorBoundary.AreaSquareMeters is double aFwall &&
                wallFootprint.AreaSquareMeters is double aW &&
                aFwall > 1e-6 && aW > 1e-6)
            {
                areaRatioWall = aFwall / aW;
                if (Math.Abs(1.0 - areaRatioWall.Value) > areaRatioTolerance)
                {
                    wallMismatch = true;
                }
            }

            if (floorBoundary.TessellatedBoundaryMeters is { Count: >= 3 } tessW &&
                wallFootprint.ClosedLoop == true &&
                wallFootprint.VerticesMeters is { Count: >= 3 } wVerts)
            {
                var tessPts = tessW.Select(p => (X: p.X, Y: p.Y)).ToList();
                var wPts = wVerts.Select(p => (X: p.X, Y: p.Y)).ToList();
                maxDistWall = MaxDistancePolygonToSegments(tessPts, wPts);
                if (maxDistWall.Value > tolMeters)
                {
                    wallMismatch = true;
                }
            }
            else if (floorBoundary.ArcLengthRatio is double ar && ar > 0.55 && wallFootprint.StraightSegmentCount > 0 &&
                     wallFootprint.ArcHeavy == false)
            {
                wallMismatch = true;
            }

            if (walls.Count >= 3 && (!wallCurveChainClosed || wallFootprint.ClosedLoop != true))
            {
                wallMismatch = true;
            }

            IReadOnlyList<(double X, double Y)>? outline = floorBoundary.TessellatedBoundaryMeters is { Count: >= 3 } ob
                ? ob.Select(p => (p.X, p.Y)).ToList()
                : null;
            var associatedRoom = RevitRoomBoundaryLoops.TryAssociateRoomForSlabOnLevel(
                doc,
                floor.LevelId,
                explicitRoomId,
                outline);

            JObject? roomFootprintJO = null;
            double? areaRatioFloorToRoom = null;
            double? maxDistFloorToRoom = null;
            var roomMismatch = false;
            var roomReferenceReady = false;

            if (associatedRoom != null)
            {
                if (RevitRoomBoundaryLoops.TryTessellatePrimaryRoomBoundaryMeters(
                        associatedRoom,
                        zWork,
                        roomBoundaryLocation,
                        MaxTessellationPointsPerCurve,
                        out var roomTess,
                        out var roomAreaM2,
                        out var roomErr))
                {
                    roomReferenceReady = true;
                    roomFootprintJO = new JObject
                    {
                        ["roomId"] = associatedRoom.Id.IntegerValue,
                        ["roomName"] = associatedRoom.Name ?? string.Empty,
                        ["roomNumber"] = associatedRoom.Number ?? string.Empty,
                        ["areaSquareMeters"] = roomAreaM2 != null ? JToken.FromObject(roomAreaM2.Value) : JValue.CreateNull(),
                        ["tessPointCount"] = roomTess.Count,
                        ["boundaryOk"] = true
                    };

                    if (floorBoundary.AreaSquareMeters is double aFroom && roomAreaM2 is double aR && aR > 1e-6)
                    {
                        areaRatioFloorToRoom = aFroom / aR;
                        if (Math.Abs(1.0 - areaRatioFloorToRoom.Value) > areaRatioTolerance)
                        {
                            roomMismatch = true;
                        }
                    }

                    if (floorBoundary.TessellatedBoundaryMeters is { Count: >= 3 } ftess)
                    {
                        var fpts = ftess.Select(p => (X: p.X, Y: p.Y)).ToList();
                        maxDistFloorToRoom = MaxDistancePolygonToSegments(fpts, roomTess);
                        if (maxDistFloorToRoom.Value > tolMeters)
                        {
                            roomMismatch = true;
                        }
                    }
                }
                else
                {
                    roomFootprintJO = new JObject
                    {
                        ["roomId"] = associatedRoom.Id.IntegerValue,
                        ["roomName"] = associatedRoom.Name ?? string.Empty,
                        ["roomNumber"] = associatedRoom.Number ?? string.Empty,
                        ["boundaryOk"] = false,
                        ["error"] = roomErr ?? "tessellation failed"
                    };
                }
            }

            var comparisonReference = roomReferenceReady ? "room" : "wall_chain";
            var likelyMismatch = roomReferenceReady ? roomMismatch : wallMismatch;

            var result = new JObject
            {
                ["op"] = "analyze_floor_wall_footprint_result",
                ["floorId"] = floorId,
                ["levelName"] = levelName,
                ["floorBoundary"] = JObject.FromObject(floorBoundary),
                ["wallFootprint"] = JObject.FromObject(wallFootprint),
                ["metrics"] = new JObject
                {
                    ["comparisonReference"] = comparisonReference,
                    ["boundaryLocation"] = boundaryLocationLabel,
                    ["areaRatio"] = areaRatioWall != null ? JToken.FromObject(areaRatioWall.Value) : JValue.CreateNull(),
                    ["areaRatioFloorToRoom"] = areaRatioFloorToRoom != null
                        ? JToken.FromObject(areaRatioFloorToRoom.Value)
                        : JValue.CreateNull(),
                    ["maxDistanceFloorToWallChainMeters"] = maxDistWall != null
                        ? JToken.FromObject(maxDistWall.Value)
                        : JValue.CreateNull(),
                    ["maxDistanceFloorToRoomBoundaryMeters"] = maxDistFloorToRoom != null
                        ? JToken.FromObject(maxDistFloorToRoom.Value)
                        : JValue.CreateNull(),
                    ["wallCurveChainClosed"] = wallCurveChainClosed,
                    ["likelyMismatch"] = likelyMismatch,
                    ["toleranceMeters"] = tolMeters,
                    ["areaRatioTolerance"] = areaRatioTolerance
                }
            };

            if (roomFootprintJO != null)
            {
                result["roomFootprint"] = roomFootprintJO;
            }

            if (likelyMismatch)
            {
                if (comparisonReference == "room" && associatedRoom != null)
                {
                    result["suggestedRepair"] = new JObject
                    {
                        ["op"] = "repair_floor_to_room_footprint",
                        ["floorId"] = floorId,
                        ["roomId"] = associatedRoom.Id.IntegerValue,
                        ["boundaryLocation"] = boundaryLocationLabel
                    };
                }
                else
                {
                    result["suggestedRepair"] = new JObject
                    {
                        ["op"] = "repair_floor_to_wall_footprint",
                        ["floorId"] = floorId,
                        ["wallIds"] = new JArray(walls.Select(w => w.Id.IntegerValue)),
                        ["alignTo"] = "wall_centerline"
                    };
                }
            }
            else
            {
                result["suggestedRepair"] = JValue.CreateNull();
            }

            return result;
        }

        private sealed class FloorBoundaryInfo
        {
            public double? AreaSquareMeters { get; set; }
            public int TessellatedPointCount { get; set; }
            public double? ArcLengthRatio { get; set; }
            public bool? IsMostlyArcs { get; set; }
            [JsonIgnore]
            public List<XY>? TessellatedBoundaryMeters { get; set; }
        }

        private sealed class WallFootprintInfo
        {
            public int WallCount { get; set; }
            public bool? ClosedLoop { get; set; }
            public int? VertexCount { get; set; }
            public double? AreaSquareMeters { get; set; }
            public bool? ArcHeavy { get; set; }
            public int StraightSegmentCount { get; set; }
            [JsonIgnore]
            public List<XY>? VerticesMeters { get; set; }
        }

        private sealed class XY
        {
            public double X { get; set; }
            public double Y { get; set; }
        }

        private static FloorBoundaryInfo DescribeFloorBoundary(Floor floor)
        {
            var info = new FloorBoundaryInfo();
            if (!TryGetPrimaryFootprintLoop(floor, out var loop, out var arcLenRatio) || loop == null)
            {
                return info;
            }

            info.ArcLengthRatio = arcLenRatio;
            info.IsMostlyArcs = arcLenRatio > 0.45;
            var tess = TessellateLoopMeters(loop, MaxTessellationPointsPerCurve);
            info.TessellatedBoundaryMeters = tess.Select(p => new XY { X = p.X, Y = p.Y }).ToList();
            info.TessellatedPointCount = tess.Count;
            info.AreaSquareMeters = Math.Abs(SignedArea2D(tess));
            return info;
        }

        private static WallFootprintInfo DescribeWallFootprint(List<(XYZ a, XYZ b)> rawSegs, double tolFt)
        {
            var info = new WallFootprintInfo { WallCount = 0, StraightSegmentCount = rawSegs.Count };
            if (!TryBuildClosedPolygon(rawSegs, tolFt, out var loopM, out var closed))
            {
                info.ClosedLoop = false;
                return info;
            }

            info.ClosedLoop = closed;
            info.VertexCount = loopM.Count;
            info.AreaSquareMeters = Math.Abs(SignedArea2D(loopM));
            info.ArcHeavy = false;
            info.VerticesMeters = loopM.Select(p => new XY { X = p.X, Y = p.Y }).ToList();
            return info;
        }

        private static List<(XYZ a, XYZ b)> CollectWallSegments(IReadOnlyList<Wall> walls, double zElevation)
        {
            var list = new List<(XYZ, XYZ)>();
            foreach (var wall in walls)
            {
                if (wall.Location is not LocationCurve lc)
                {
                    continue;
                }

                var c = lc.Curve;
                IList<XYZ> pts;
                try
                {
                    pts = c.Tessellate();
                }
                catch
                {
                    continue;
                }

                if (pts == null || pts.Count < 2)
                {
                    continue;
                }

                for (var i = 0; i < pts.Count - 1; i++)
                {
                    var a = new XYZ(pts[i].X, pts[i].Y, zElevation);
                    var b = new XYZ(pts[i + 1].X, pts[i + 1].Y, zElevation);
                    if (a.DistanceTo(b) < RevitWallCreationOps.MetersToFeet(0.02))
                    {
                        continue;
                    }

                    list.Add((a, b));
                }
            }

            return list;
        }

        private static List<Curve> CollectWallLocationCurves(IReadOnlyList<Wall> walls, double zElevation)
        {
            var list = new List<Curve>();
            foreach (var wall in walls)
            {
                if (wall.Location is not LocationCurve lc || lc.Curve == null)
                {
                    continue;
                }

                var c = lc.Curve;
                switch (c)
                {
                    case Line ln:
                        list.Add(Line.CreateBound(
                            new XYZ(ln.GetEndPoint(0).X, ln.GetEndPoint(0).Y, zElevation),
                            new XYZ(ln.GetEndPoint(1).X, ln.GetEndPoint(1).Y, zElevation)));
                        break;
                    case Arc arc:
                        var p0 = new XYZ(arc.GetEndPoint(0).X, arc.GetEndPoint(0).Y, zElevation);
                        var p1 = new XYZ(arc.GetEndPoint(1).X, arc.GetEndPoint(1).Y, zElevation);
                        var pm = arc.Evaluate(0.5, true);
                        var pMid = new XYZ(pm.X, pm.Y, zElevation);
                        list.Add(Arc.Create(p0, p1, pMid));
                        break;
                    default:
                        var pts = c.Tessellate();
                        for (var i = 0; i < pts.Count - 1; i++)
                        {
                            var a = new XYZ(pts[i].X, pts[i].Y, zElevation);
                            var b = new XYZ(pts[i + 1].X, pts[i + 1].Y, zElevation);
                            if (a.DistanceTo(b) >= RevitWallCreationOps.MetersToFeet(0.02))
                            {
                                list.Add(Line.CreateBound(a, b));
                            }
                        }

                        break;
                }
            }

            return list;
        }

        private static bool TryBuildClosedCurveChain(
            IReadOnlyList<Curve> rawCurves,
            double tolFt,
            out List<Curve> ordered,
            out bool closed)
        {
            ordered = new List<Curve>();
            closed = false;
            if (rawCurves.Count < 3)
            {
                return false;
            }

            List<Curve>? best = null;
            for (var i = 0; i < rawCurves.Count; i++)
            {
                foreach (var reverseSeed in new[] { false, true })
                {
                    if (!TryBuildCurveChainFromSeed(rawCurves, i, reverseSeed, tolFt, out var trial, out var trialClosed))
                    {
                        continue;
                    }

                    if (!trialClosed)
                    {
                        continue;
                    }

                    if (best == null || trial.Count > best.Count)
                    {
                        best = trial;
                    }
                }
            }

            if (best == null || best.Count < 3)
            {
                return false;
            }

            ordered = best;
            closed = true;
            return true;
        }

        private static bool TryBuildCurveChainFromSeed(
            IReadOnlyList<Curve> rawCurves,
            int seedIndex,
            bool reverseSeed,
            double tolFt,
            out List<Curve> ordered,
            out bool closed)
        {
            ordered = new List<Curve>();
            closed = false;
            if (seedIndex < 0 || seedIndex >= rawCurves.Count)
            {
                return false;
            }

            var used = new bool[rawCurves.Count];
            var seed = reverseSeed ? rawCurves[seedIndex].CreateReversed() : rawCurves[seedIndex];
            used[seedIndex] = true;
            ordered.Add(seed);
            var start = seed.GetEndPoint(0);
            var current = seed.GetEndPoint(1);

            for (var step = 0; step < rawCurves.Count + 1; step++)
            {
                if (current.DistanceTo(start) <= tolFt && ordered.Count >= 3)
                {
                    closed = true;
                    break;
                }

                var bestIdx = -1;
                var bestDist = double.MaxValue;
                var bestReverse = false;
                for (var i = 0; i < rawCurves.Count; i++)
                {
                    if (used[i])
                    {
                        continue;
                    }

                    var c = rawCurves[i];
                    var dStart = current.DistanceTo(c.GetEndPoint(0));
                    if (dStart <= tolFt && dStart < bestDist)
                    {
                        bestDist = dStart;
                        bestIdx = i;
                        bestReverse = false;
                    }

                    var dEnd = current.DistanceTo(c.GetEndPoint(1));
                    if (dEnd <= tolFt && dEnd < bestDist)
                    {
                        bestDist = dEnd;
                        bestIdx = i;
                        bestReverse = true;
                    }
                }

                if (bestIdx < 0)
                {
                    break;
                }

                used[bestIdx] = true;
                var next = bestReverse ? rawCurves[bestIdx].CreateReversed() : rawCurves[bestIdx];
                ordered.Add(next);
                current = next.GetEndPoint(1);
            }

            return ordered.Count >= 3;
        }

        private static bool TryBuildClosedPolygon(
            List<(XYZ a, XYZ b)> segs,
            double tolFt,
            out List<(double X, double Y)> loopMeters,
            out bool closed)
        {
            loopMeters = new List<(double X, double Y)>();
            closed = false;
            if (segs.Count < 3)
            {
                return false;
            }

            var n = segs.Count;
            var uf = new UnionFind(2 * n);
            for (var i = 0; i < 2 * n; i++)
            {
                var pi = i % 2 == 0 ? segs[i / 2].a : segs[i / 2].b;
                for (var j = i + 1; j < 2 * n; j++)
                {
                    var pj = j % 2 == 0 ? segs[j / 2].a : segs[j / 2].b;
                    if (pi.DistanceTo(pj) < tolFt)
                    {
                        uf.Union(i, j);
                    }
                }
            }

            var adj = new Dictionary<int, List<(int other, int segIdx)>>(2 * n);
            void AddEdge(int u, int v, int si)
            {
                if (!adj.TryGetValue(u, out var lu))
                {
                    lu = new List<(int, int)>();
                    adj[u] = lu;
                }

                lu.Add((v, si));
            }

            for (var i = 0; i < n; i++)
            {
                var r0 = uf.Find(2 * i);
                var r1 = uf.Find(2 * i + 1);
                if (r0 == r1)
                {
                    continue;
                }

                AddEdge(r0, r1, i);
                AddEdge(r1, r0, i);
            }

            if (adj.Count == 0)
            {
                return false;
            }

            var start = adj.Keys.Min();
            var pathRoots = new List<int> { start };
            var prevNode = -1;
            var at = start;
            var maxSteps = n + 5;
            for (var step = 0; step < maxSteps; step++)
            {
                if (!adj.TryGetValue(at, out var opts))
                {
                    return false;
                }

                var nexts = opts.Where(e => e.other != prevNode).ToList();
                if (nexts.Count == 0)
                {
                    return false;
                }

                prevNode = at;
                at = nexts[0].other;
                if (at == start)
                {
                    closed = pathRoots.Count >= 2;
                    break;
                }

                pathRoots.Add(at);
            }

            if (!closed || pathRoots.Count < 3)
            {
                return false;
            }

            var rep = new Dictionary<int, XYZ>();
            for (var i = 0; i < 2 * n; i++)
            {
                var r = uf.Find(i);
                var p = i % 2 == 0 ? segs[i / 2].a : segs[i / 2].b;
                if (!rep.ContainsKey(r))
                {
                    rep[r] = p;
                }
                else
                {
                    var q = rep[r];
                    rep[r] = new XYZ((q.X + p.X) * 0.5, (q.Y + p.Y) * 0.5, (q.Z + p.Z) * 0.5);
                }
            }

            var ordered = new List<XYZ>();
            foreach (var r in pathRoots)
            {
                if (rep.TryGetValue(r, out var pt))
                {
                    ordered.Add(pt);
                }
            }

            ordered = SimplifyCollinear(ordered, tolFt);
            loopMeters = ordered
                .Select(p => (UnitUtils.ConvertFromInternalUnits(p.X, UnitTypeId.Meters),
                    UnitUtils.ConvertFromInternalUnits(p.Y, UnitTypeId.Meters)))
                .ToList();
            return loopMeters.Count >= 3;
        }

        private static List<XYZ> SimplifyCollinear(List<XYZ> pts, double tolFt)
        {
            if (pts.Count < 3)
            {
                return pts;
            }

            var z = pts[0].Z;
            var flat = pts.Select(p => new XYZ(p.X, p.Y, z)).ToList();
            var n = flat.Count;
            var keep = new List<XYZ>();
            for (var i = 0; i < n; i++)
            {
                var prev = flat[(i - 1 + n) % n];
                var cur = flat[i];
                var next = flat[(i + 1) % n];
                var v1 = (cur - prev).Normalize();
                var v2 = (next - cur).Normalize();
                if (Math.Abs(v1.DotProduct(v2)) > 0.995)
                {
                    continue;
                }

                keep.Add(cur);
            }

            return keep.Count >= 3 ? keep : flat;
        }

        private static List<Line> PolylineToCurves(List<XYZ> verts, double minLenFt)
        {
            var curves = new List<Line>();
            var n = verts.Count;
            for (var i = 0; i < n; i++)
            {
                var a = verts[i];
                var b = verts[(i + 1) % n];
                if (a.DistanceTo(b) < minLenFt)
                {
                    continue;
                }

                curves.Add(Line.CreateBound(a, b));
            }

            return curves;
        }

        private static List<XYZ> OffsetPolygonInPlan(IReadOnlyList<XYZ> poly, double offsetFeet)
        {
            if (poly.Count < 3 || Math.Abs(offsetFeet) < 1e-9)
            {
                return poly.ToList();
            }

            var z = poly[0].Z;
            var flat = poly.Select(p => new XYZ(p.X, p.Y, z)).ToList();
            var sa = SignedAreaInternal(flat);
            var sign = sa >= 0 ? 1.0 : -1.0;
            var n = flat.Count;
            var @out = new List<XYZ>();
            for (var i = 0; i < n; i++)
            {
                var prev = flat[(i - 1 + n) % n];
                var cur = flat[i];
                var next = flat[(i + 1) % n];
                var d0 = (cur - prev).Normalize();
                var d1 = (next - cur).Normalize();
                var in0 = sign * XYZ.BasisZ.CrossProduct(d0).Normalize() * offsetFeet;
                var in1 = sign * XYZ.BasisZ.CrossProduct(d1).Normalize() * offsetFeet;
                var p0 = prev + in0;
                var p1 = cur + in1;
                var inter = IntersectLinesXY(p0, d0, p1, d1, z);
                @out.Add(inter);
            }

            return @out;
        }

        private static XYZ IntersectLinesXY(XYZ p0, XYZ d0, XYZ p1, XYZ d1, double z)
        {
            var cross = d0.X * d1.Y - d0.Y * d1.X;
            if (Math.Abs(cross) < 1e-10)
            {
                return new XYZ(p1.X, p1.Y, z);
            }

            var rX = p1.X - p0.X;
            var rY = p1.Y - p0.Y;
            var t = (rX * d1.Y - rY * d1.X) / cross;
            var p = p0 + d0 * t;
            return new XYZ(p.X, p.Y, z);
        }

        private static double SignedAreaInternal(List<XYZ> poly)
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

        private static double SignedArea2D(List<(double X, double Y)> poly)
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

        private static double MaxDistancePolygonToSegments(
            IReadOnlyList<(double X, double Y)> poly,
            IReadOnlyList<(double X, double Y)> loopVerts)
        {
            var segs = new List<((double X, double Y) a, (double X, double Y) b)>();
            var m = loopVerts.Count;
            for (var i = 0; i < m; i++)
            {
                segs.Add((loopVerts[i], loopVerts[(i + 1) % m]));
            }

            double maxD = 0;
            var step = Math.Max(1, poly.Count / MaxSamplePointsForMetric);
            for (var i = 0; i < poly.Count; i += step)
            {
                var p = poly[i];
                var best = double.MaxValue;
                foreach (var (a, b) in segs)
                {
                    best = Math.Min(best, PointToSegmentDistance(p, a, b));
                }

                maxD = Math.Max(maxD, best);
            }

            return maxD;
        }

        private static double PointToSegmentDistance(
            (double X, double Y) p,
            (double X, double Y) a,
            (double X, double Y) b)
        {
            var abX = b.X - a.X;
            var abY = b.Y - a.Y;
            var apX = p.X - a.X;
            var apY = p.Y - a.Y;
            var ab2 = abX * abX + abY * abY;
            if (ab2 < 1e-18)
            {
                var dx = p.X - a.X;
                var dy = p.Y - a.Y;
                return Math.Sqrt(dx * dx + dy * dy);
            }

            var t = Math.Max(0, Math.Min(1, (apX * abX + apY * abY) / ab2));
            var qX = a.X + t * abX;
            var qY = a.Y + t * abY;
            var dX = p.X - qX;
            var dY = p.Y - qY;
            return Math.Sqrt(dX * dX + dY * dY);
        }

        private static bool TryGetPrimaryFootprintLoop(Floor floor, out EdgeArray? outerLoop, out double arcLengthRatio)
        {
            outerLoop = null;
            arcLengthRatio = 0;
            Options opt = new Options { DetailLevel = ViewDetailLevel.Fine };
            GeometryElement? ge;
            try
            {
                ge = floor.get_Geometry(opt);
            }
            catch
            {
                return false;
            }

            if (ge == null)
            {
                return false;
            }

            PlanarFace? bestFace = null;
            double bestZ = double.MaxValue;
            foreach (GeometryObject go in ge)
            {
                if (go is not Solid sol || sol.Volume <= 0)
                {
                    continue;
                }

                foreach (Face f in sol.Faces)
                {
                    if (f is not PlanarFace pf)
                    {
                        continue;
                    }

                    var normal = pf.FaceNormal;
                    if (Math.Abs(normal.Z) < 0.95)
                    {
                        continue;
                    }

                    var z = pf.Origin.Z;
                    if (z < bestZ)
                    {
                        bestZ = z;
                        bestFace = pf;
                    }
                }
            }

            if (bestFace == null)
            {
                return false;
            }

            double bestArea = 0;
            foreach (EdgeArray ea in bestFace.EdgeLoops)
            {
                double len = 0;
                double arcLen = 0;
                foreach (Edge edge in ea)
                {
                    var c = edge.AsCurve();
                    len += c.Length;
                    if (c is Arc)
                    {
                        arcLen += c.Length;
                    }
                }

                var a = Math.Abs(LoopAreaInternal(ea));
                if (a > bestArea)
                {
                    bestArea = a;
                    outerLoop = ea;
                    arcLengthRatio = len > 1e-9 ? arcLen / len : 0;
                }
            }

            return outerLoop != null;
        }

        private static double LoopAreaInternal(EdgeArray ea)
        {
            var pts = TessellateEdgeArrayInternal(ea, MaxTessellationPointsPerCurve);
            return SignedAreaInternal(pts.Select(p => new XYZ(p.X, p.Y, p.Z)).ToList());
        }

        private static List<XYZ> TessellateEdgeArrayInternal(EdgeArray ea, int maxPerCurve)
        {
            var pts = new List<XYZ>();
            foreach (Edge edge in ea)
            {
                var c = edge.AsCurve();
                IList<XYZ> t;
                try
                {
                    t = c.Tessellate();
                }
                catch
                {
                    continue;
                }

                if (t == null || t.Count == 0)
                {
                    continue;
                }

                var step = Math.Max(1, (t.Count - 1) / maxPerCurve);
                for (var i = 0; i < t.Count; i += step)
                {
                    var p = t[i];
                    if (pts.Count == 0 || pts[pts.Count - 1].DistanceTo(p) > 1e-6)
                    {
                        pts.Add(p);
                    }
                }
            }

            if (pts.Count >= 2 && pts[0].DistanceTo(pts[pts.Count - 1]) < 1e-6)
            {
                pts.RemoveAt(pts.Count - 1);
            }

            return pts;
        }

        private static List<(double X, double Y)> TessellateLoopMeters(EdgeArray ea, int maxPerCurve)
        {
            var internalPts = TessellateEdgeArrayInternal(ea, maxPerCurve);
            return internalPts
                .Select(p => (
                    UnitUtils.ConvertFromInternalUnits(p.X, UnitTypeId.Meters),
                    UnitUtils.ConvertFromInternalUnits(p.Y, UnitTypeId.Meters)))
                .ToList();
        }

        private static List<Wall> FilterWallsNearFloorBoundary(List<Wall> walls, Floor floor)
        {
            if (walls.Count <= 12)
            {
                return walls;
            }

            if (!TryGetPrimaryFootprintLoop(floor, out var loop, out _) || loop == null)
            {
                return walls;
            }

            var boundary = TessellateEdgeArrayInternal(loop, MaxTessellationPointsPerCurve);
            if (boundary.Count < 3)
            {
                return walls;
            }

            var boundarySegs = new List<(XYZ a, XYZ b)>();
            for (var i = 0; i < boundary.Count; i++)
            {
                boundarySegs.Add((boundary[i], boundary[(i + 1) % boundary.Count]));
            }

            var nearTol = RevitWallCreationOps.MetersToFeet(0.35);
            var selected = new List<Wall>();
            foreach (var w in walls)
            {
                if (w.Location is not LocationCurve lc || lc.Curve == null)
                {
                    continue;
                }

                IList<XYZ> pts;
                try
                {
                    pts = lc.Curve.Tessellate();
                }
                catch
                {
                    continue;
                }

                if (pts == null || pts.Count == 0)
                {
                    continue;
                }

                var minD = double.MaxValue;
                foreach (var p in pts)
                {
                    foreach (var (a, b) in boundarySegs)
                    {
                        minD = Math.Min(minD, PointToSegmentDistanceInternal(p, a, b));
                    }
                }

                if (minD <= nearTol)
                {
                    selected.Add(w);
                }
            }

            return selected.Count >= 3 ? selected : walls;
        }

        private static double PointToSegmentDistanceInternal(XYZ p, XYZ a, XYZ b)
        {
            var ab = b - a;
            var ap = p - a;
            var ab2 = ab.DotProduct(ab);
            if (ab2 < 1e-12)
            {
                return p.DistanceTo(a);
            }

            var t = Math.Max(0.0, Math.Min(1.0, ap.DotProduct(ab) / ab2));
            var q = a + ab * t;
            return p.DistanceTo(q);
        }

        private static string BuildHumanAnalyzeSummary(JObject report)
        {
            var floorId = report["floorId"]?.Value<int?>() ?? -1;
            var level = report["levelName"]?.ToString() ?? "?";
            var walls = report["wallFootprint"]?["WallCount"]?.Value<int?>() ?? 0;
            var closed = report["wallFootprint"]?["ClosedLoop"]?.ToString() ?? "null";
            var mismatch = report["metrics"]?["likelyMismatch"]?.Value<bool?>() ?? false;
            var chainClosed = report["metrics"]?["wallCurveChainClosed"]?.ToString() ?? "null";
            var refMode = report["metrics"]?["comparisonReference"]?.ToString() ?? "?";
            var roomId = report["roomFootprint"]?["roomId"]?.Value<long?>();
            var maxDistWall = report["metrics"]?["maxDistanceFloorToWallChainMeters"]?.Value<double?>();
            var maxDistRoom = report["metrics"]?["maxDistanceFloorToRoomBoundaryMeters"]?.Value<double?>();
            var areaRatioWall = report["metrics"]?["areaRatio"]?.Value<double?>();
            var areaRatioRoom = report["metrics"]?["areaRatioFloorToRoom"]?.Value<double?>();
            var maxWallText = maxDistWall.HasValue ? maxDistWall.Value.ToString("0.###") + "m" : "n/a";
            var maxRoomText = maxDistRoom.HasValue ? maxDistRoom.Value.ToString("0.###") + "m" : "n/a";
            var areaWallText = areaRatioWall.HasValue ? areaRatioWall.Value.ToString("0.###") : "n/a";
            var areaRoomText = areaRatioRoom.HasValue ? areaRatioRoom.Value.ToString("0.###") : "n/a";
            return "analyze_floor_wall_footprint_result floorId=" + floorId +
                   " level=\"" + level + "\"" +
                   " ref=" + refMode +
                   (roomId.HasValue ? " roomId=" + roomId.Value : string.Empty) +
                   " walls=" + walls +
                   " closedLoop=" + closed +
                   " wallCurveChainClosed=" + chainClosed +
                   " areaRatioFloorToWall=" + areaWallText +
                   " areaRatioFloorToRoom=" + areaRoomText +
                   " maxDistToWall=" + maxWallText +
                   " maxDistToRoom=" + maxRoomText +
                   " likelyMismatch=" + mismatch;
        }

        private sealed class UnionFind
        {
            private readonly int[] _p;
            private readonly int[] _r;

            public UnionFind(int n)
            {
                _p = Enumerable.Range(0, n).ToArray();
                _r = new int[n];
            }

            public int Find(int x)
            {
                if (_p[x] != x)
                {
                    _p[x] = Find(_p[x]);
                }

                return _p[x];
            }

            public void Union(int a, int b)
            {
                a = Find(a);
                b = Find(b);
                if (a == b)
                {
                    return;
                }

                if (_r[a] < _r[b])
                {
                    _p[a] = b;
                }
                else if (_r[a] > _r[b])
                {
                    _p[b] = a;
                }
                else
                {
                    _p[b] = a;
                    _r[a]++;
                }
            }
        }
    }
}
