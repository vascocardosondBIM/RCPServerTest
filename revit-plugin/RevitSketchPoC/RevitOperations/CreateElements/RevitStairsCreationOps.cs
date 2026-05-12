using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json.Linq;
using RevitSketchPoC.RevitOperations.Shared;
using System;
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

            log.AppendLine(opLabel + " id=" + stairsId.IntegerValue + " (stairs component)");
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
