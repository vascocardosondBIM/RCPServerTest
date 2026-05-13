using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json.Linq;
using RevitSketchPoC.RevitOperations.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RevitSketchPoC.RevitOperations.CreateElements
{
    /// <summary>JSON <c>create_stairs</c> for chat revitOps.</summary>
    public static class RevitStairsCreationOps
    {
        public static StairsType ResolveStairsType(Document doc, string? requestedName)
        {
            var types = new FilteredElementCollector(doc)
                .OfClass(typeof(StairsType))
                .Cast<StairsType>()
                .ToList();

            if (types.Count == 0)
            {
                throw new InvalidOperationException("No stair types found in the Revit model.");
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

        /// <summary>
        /// Straight run between bottomLevelName and topLevelName; path (startX,startY)-(endX,endY) in metres on bottom level.
        /// </summary>
        public static void RunCreateStairsJsonOp(Document doc, JObject op, StringBuilder log)
        {
            RunCreateStraightStairRunJsonOp(
                doc,
                op,
                log,
                "create_stairs");
        }

        private static void RunCreateStraightStairRunJsonOp(
            Document doc,
            JObject op,
            StringBuilder log,
            string opLabel)
        {
            var bottomName = op["bottomLevelName"]?.ToString() ?? op["baseLevelName"]?.ToString();
            var topName = op["topLevelName"]?.ToString();
            if (string.IsNullOrWhiteSpace(bottomName) || string.IsNullOrWhiteSpace(topName))
            {
                throw new InvalidOperationException(opLabel + " requires bottomLevelName (or baseLevelName) and topLevelName.");
            }

            var bottom = RevitWallCreationOps.ResolveLevel(doc, bottomName);
            var top = RevitWallCreationOps.ResolveLevel(doc, topName);
            if (top.Elevation <= bottom.Elevation)
            {
                throw new InvalidOperationException(opLabel + ": topLevel must be above bottomLevel.");
            }

            if (!RevitOpJsonGeometry.TryReadNumber(op["startX"], out var sx) ||
                !RevitOpJsonGeometry.TryReadNumber(op["startY"], out var sy) ||
                !RevitOpJsonGeometry.TryReadNumber(op["endX"], out var ex) ||
                !RevitOpJsonGeometry.TryReadNumber(op["endY"], out var ey))
            {
                throw new InvalidOperationException(opLabel + " requires startX, startY, endX, endY (metres on bottom level).");
            }

            var z = bottom.Elevation;
            var p0 = new XYZ(RevitInternalUnits.MetersToFeet(sx), RevitInternalUnits.MetersToFeet(sy), z);
            var p1 = new XYZ(RevitInternalUnits.MetersToFeet(ex), RevitInternalUnits.MetersToFeet(ey), z);
            if (p0.DistanceTo(p1) < RevitInternalUnits.MetersToFeet(0.05))
            {
                throw new InvalidOperationException(opLabel + ": path length is too small.");
            }

            var stairsType = ResolveStairsType(doc, op["stairsTypeName"]?.ToString());
            var justification = ReadStairsRunJustification(op);

            // StairsEditScope.Start must run with NO active Transaction (API remarks). After Start, open a
            // Transaction for ChangeTypeId / StairsRun.CreateStraightRun, then commit the edit scope.
            ElementId stairsId = ElementId.InvalidElementId;
            using var scope = new StairsEditScope(doc, "AI Chat — " + opLabel);
            try
            {
                stairsId = scope.Start(bottom.Id, top.Id);

                using (var tx = new Transaction(doc, "AI Chat — " + opLabel))
                {
                    tx.Start();
                    try
                    {
                        doc.Regenerate();

                        var stairsEl = doc.GetElement(stairsId) as Stairs;
                        if (stairsEl != null)
                        {
                            stairsEl.ChangeTypeId(stairsType.Id);
                            doc.Regenerate();
                        }

                        var locationLine = Line.CreateBound(p0, p1);
                        var run = StairsRun.CreateStraightRun(doc, stairsId, locationLine, justification);
                        if (run == null)
                        {
                            throw new InvalidOperationException(opLabel + ": StairsRun.CreateStraightRun returned null.");
                        }

                        tx.Commit();
                    }
                    catch
                    {
                        if (tx.GetStatus() == TransactionStatus.Started)
                        {
                            tx.RollBack();
                        }

                        throw;
                    }
                }

                scope.Commit(new StairsEditScopeFailuresPreprocessor());
            }
            catch
            {
                try
                {
                    scope.Cancel();
                }
                catch
                {
                    // ignore cancel failures
                }

                throw;
            }

            ProcessStairRailingsAfterCreate(doc, stairsId, op, log);

            log.AppendLine(opLabel + " id=" + stairsId.IntegerValue + " (stairs component)");
        }

        /// <summary>Optional railing type for stair-hosted railings: same keys as <c>create_guardrail</c>.</summary>
        private static string? ReadOptionalStairRailingTypeName(JObject op)
        {
            var s = op["stairRailingTypeName"]?.ToString()?.Trim()
                    ?? op["railingTypeName"]?.ToString()?.Trim()
                    ?? op["guardrailTypeName"]?.ToString()?.Trim();
            return string.IsNullOrEmpty(s) ? null : s;
        }

        private static RailingPlacementPosition ReadStairRailingPlacementPosition(JObject op)
        {
            var raw = (op["stairRailingPlacement"]?.ToString()
                       ?? op["railingPlacementPosition"]?.ToString()
                       ?? "treads").Trim().ToLowerInvariant();
            return raw switch
            {
                "stringer" => RailingPlacementPosition.Stringer,
                "treads" or "tread" or "" => RailingPlacementPosition.Treads,
                _ => RailingPlacementPosition.Treads
            };
        }

        /// <summary>
        /// Default: remove auto railings. <see cref="ReadKeepStairsRailings"/> true: keep Revit defaults unless a railing type is set.
        /// If a railing type name is set: with keep=true, retipe existing railings; otherwise remove then <see cref="Railing.Create"/> with that type.
        /// </summary>
        private static void ProcessStairRailingsAfterCreate(Document doc, ElementId stairsId, JObject op, StringBuilder log)
        {
            var typeName = ReadOptionalStairRailingTypeName(op);
            var keep = ReadKeepStairsRailings(op);

            if (!string.IsNullOrWhiteSpace(typeName))
            {
                var railingType = RevitGuardrailCreationOps.ResolveRailingType(doc, typeName, log);
                var placement = ReadStairRailingPlacementPosition(op);
                var existing = CollectStairRailingIds(doc, stairsId);

                if (keep && existing.Count > 0)
                {
                    ApplyRailingTypeToRailings(doc, existing, railingType.Id, log);
                    log.AppendLine(
                        "create_stairs: applied stair railing type \"" + railingType.Name + "\" to " + existing.Count + " railing(s).");
                }
                else
                {
                    RemoveRailingsByIds(doc, existing, log);
                    RemoveAutomaticRailingsHostedOnStairs(doc, stairsId, log);
                    CreateAutomaticStairRailings(doc, stairsId, railingType.Id, placement, log);
                }

                return;
            }

            if (!keep)
            {
                RemoveAutomaticRailingsHostedOnStairs(doc, stairsId, log);
            }
        }

        private static HashSet<ElementId> CollectStairRailingIds(Document doc, ElementId stairsId)
        {
            var ids = new HashSet<ElementId>();
            if (doc.GetElement(stairsId) is Stairs stairs)
            {
                try
                {
                    foreach (var rid in stairs.GetAssociatedRailings())
                    {
                        if (rid != ElementId.InvalidElementId)
                        {
                            ids.Add(rid);
                        }
                    }
                }
                catch
                {
                    // ignore
                }
            }

            foreach (var el in new FilteredElementCollector(doc).OfClass(typeof(Railing)).ToElements())
            {
                if (el is Railing railing && IsRailingHostedByStairs(doc, railing, stairsId))
                {
                    ids.Add(railing.Id);
                }
            }

            return ids;
        }

        private static void RemoveRailingsByIds(Document doc, HashSet<ElementId> ids, StringBuilder log)
        {
            if (ids.Count == 0)
            {
                return;
            }

            using var tx = new Transaction(doc, "AI Chat — remove stair railings before replace");
            tx.Start();
            try
            {
                foreach (var id in ids)
                {
                    try
                    {
                        if (doc.GetElement(id) is Railing)
                        {
                            doc.Delete(id);
                        }
                    }
                    catch (Exception ex)
                    {
                        log.AppendLine("create_stairs: could not delete railing id=" + id + ": " + ex.Message);
                    }
                }

                tx.Commit();
            }
            catch
            {
                if (tx.GetStatus() == TransactionStatus.Started)
                {
                    tx.RollBack();
                }

                throw;
            }
        }

        private static void ApplyRailingTypeToRailings(Document doc, HashSet<ElementId> railingIds, ElementId railingTypeId, StringBuilder log)
        {
            using var tx = new Transaction(doc, "AI Chat — stair railing type");
            tx.Start();
            try
            {
                foreach (var id in railingIds)
                {
                    if (doc.GetElement(id) is not Railing railing)
                    {
                        continue;
                    }

                    if (!railing.IsValidType(railingTypeId))
                    {
                        log.AppendLine(
                            "create_stairs: railing type not valid for railing id=" + id + "; skipped.");
                        continue;
                    }

                    railing.ChangeTypeId(railingTypeId);
                }

                tx.Commit();
            }
            catch
            {
                if (tx.GetStatus() == TransactionStatus.Started)
                {
                    tx.RollBack();
                }

                throw;
            }
        }

        private static void CreateAutomaticStairRailings(
            Document doc,
            ElementId stairsId,
            ElementId railingTypeId,
            RailingPlacementPosition placement,
            StringBuilder log)
        {
            using var tx = new Transaction(doc, "AI Chat — stair railings from type");
            tx.Start();
            try
            {
                doc.Regenerate();
                ICollection<ElementId>? created;
                try
                {
                    created = Railing.Create(doc, stairsId, railingTypeId, placement);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("create_stairs: could not create stair railings: " + ex.Message);
                }

                tx.Commit();
                var n = created?.Count ?? 0;
                log.AppendLine("create_stairs: created " + n + " stair railing(s) (Railing.Create).");
            }
            catch
            {
                if (tx.GetStatus() == TransactionStatus.Started)
                {
                    tx.RollBack();
                }

                throw;
            }
        }

        /// <summary>When <c>true</c>, leaves Revit-created railings on the stair. Default <c>false</c>: removes automatic railings after creation — use <c>create_guardrail</c> when the user wants railings.
        /// </summary>
        private static bool ReadKeepStairsRailings(JObject op)
        {
            if (op["keepStairsRailings"]?.Value<bool?>() is bool b)
            {
                return b;
            }

            if (op["keepStairRailings"]?.Value<bool?>() is bool b2)
            {
                return b2;
            }

            foreach (var key in new[] { "keepStairsRailings", "keepStairRailings", "retainStairsRailings" })
            {
                var s = op[key]?.ToString()?.Trim();
                if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s, "yes", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s, "1", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Deletes <see cref="Railing"/> elements hosted on this stair or on its runs/landings.</summary>
        private static void RemoveAutomaticRailingsHostedOnStairs(Document doc, ElementId stairsId, StringBuilder log)
        {
            var toDelete = CollectStairRailingIds(doc, stairsId);
            if (toDelete.Count == 0)
            {
                return;
            }

            using var tx = new Transaction(doc, "AI Chat — remove automatic stair railings");
            tx.Start();
            try
            {
                foreach (var id in toDelete)
                {
                    try
                    {
                        doc.Delete(id);
                    }
                    catch (Exception ex)
                    {
                        log.AppendLine("create_stairs: could not delete railing id=" + id + ": " + ex.Message);
                    }
                }

                tx.Commit();
            }
            catch
            {
                if (tx.GetStatus() == TransactionStatus.Started)
                {
                    tx.RollBack();
                }

                throw;
            }

            log.AppendLine(
                "create_stairs: removed " + toDelete.Count + " automatic railing(s) (omit keepStairsRailings or set false; use create_guardrail for custom railings).");
        }

        private static bool IsRailingHostedByStairs(Document doc, Railing railing, ElementId stairsId)
        {
            var hid = railing.HostId;
            if (hid == null || hid == ElementId.InvalidElementId)
            {
                return false;
            }

            if (hid == stairsId)
            {
                return true;
            }

            var host = doc.GetElement(hid);
            switch (host)
            {
                case StairsRun run:
                    return run.GetStairs()?.Id == stairsId;
                case StairsLanding landing:
                    return landing.GetStairs()?.Id == stairsId;
                default:
                    return false;
            }
        }

        private static StairsRunJustification ReadStairsRunJustification(JObject op)
        {
            var j = (op["justification"]?.ToString() ?? op["runJustification"]?.ToString() ?? "center").Trim().ToLowerInvariant();
            return j switch
            {
                "left" => StairsRunJustification.Left,
                "right" => StairsRunJustification.Right,
                "center" or "centre" or "middle" => StairsRunJustification.Center,
                _ => StairsRunJustification.Center
            };
        }

        private sealed class StairsEditScopeFailuresPreprocessor : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
            {
                var failList = failuresAccessor.GetFailureMessages();
                foreach (var fm in failList)
                {
                    if (fm.GetSeverity() == FailureSeverity.Warning)
                    {
                        failuresAccessor.DeleteWarning(fm);
                    }
                }

                return FailureProcessingResult.Continue;
            }
        }
    }
}
