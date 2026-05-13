using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitSketchPoC.Core.Configuration;
using RevitSketchPoC.RevitOperations.DeleteElements;
using RevitSketchPoC.RevitOperations.SelectElements;
using RevitSketchPoC.RevitOperations.CreateElements;
using RevitSketchPoC.RevitOperations.ChangeElements;
using RevitSketchPoC.RevitOperations.ReviewElements;
using RevitSketchPoC.RevitOperations.Shared;
using System;
using System.Collections.Generic;
using System.Text;

namespace RevitSketchPoC.RevitOperations.JsonOps
{
    /// <summary>Runs Revit mutations from LLM-produced JSON ops (shared by chat and other features). All array entries are attempted.</summary>
    public static class RevitJsonOpsExecutor
    {
        public const int MaxIdsPerOp = 50;

        public static string Execute(UIDocument uidoc, JArray ops, PluginSettings? pluginSettings = null)
        {
            var settings = pluginSettings ?? new PluginSettings();
            var doc = uidoc.Document;
            var log = new StringBuilder();
            var n = ops.Count;
            var ok = 0;
            var fail = 0;
            var selectOps = new List<JObject>();
            var placementBatch = new List<(double x, double y, ElementId levelId, PlanPlacementBatchKind kind)>();
            var wallBatchKeys = new HashSet<string>(StringComparer.Ordinal);
            var openingBatchKeys = new HashSet<string>(StringComparer.Ordinal);

            Transaction? tx = new Transaction(doc, "AI Chat — revitOps");
            try
            {
                tx.Start();
                for (var i = 0; i < n; i++)
                {
                    if (ops[i] is not JObject opObj)
                    {
                        continue;
                    }

                    var op = opObj["op"]?.ToString()?.Trim()?.ToLowerInvariant();
                    if (string.Equals(op, "create stairs", StringComparison.OrdinalIgnoreCase))
                    {
                        op = "create_stairs";
                    }

                    if (string.IsNullOrEmpty(op))
                    {
                        fail++;
                        log.AppendLine("Op " + i + ": missing \"op\".");
                        continue;
                    }

                    if (string.Equals(op, "select_elements", StringComparison.Ordinal))
                    {
                        selectOps.Add(opObj);
                        continue;
                    }

                    if (string.Equals(op, "create_wall_roman_arch_profile", StringComparison.Ordinal) ||
                        string.Equals(op, "create_wall_custom_profile_void", StringComparison.Ordinal) ||
                        string.Equals(op, "create_stairs", StringComparison.Ordinal))
                    {
                        try
                        {
                            if (tx != null && tx.GetStatus() == TransactionStatus.Started)
                            {
                                doc.Regenerate();
                                tx.Commit();
                            }

                            tx?.Dispose();
                            tx = null;
                            if (string.Equals(op, "create_wall_roman_arch_profile", StringComparison.Ordinal))
                            {
                                RevitWallArchProfileOps.RunCreateWallRomanArchProfileJsonOp(doc, opObj, log);
                            }
                            else if (string.Equals(op, "create_wall_custom_profile_void", StringComparison.Ordinal))
                            {
                                RevitWallCustomProfileVoidOps.RunCreateWallCustomProfileVoidJsonOp(doc, opObj, log);
                            }
                            else if (string.Equals(op, "create_stairs", StringComparison.Ordinal))
                            {
                                RevitStairsCreationOps.RunCreateStairsJsonOp(doc, opObj, log);
                            }

                            ok++;

                            tx = new Transaction(doc, "AI Chat — revitOps");
                            tx.Start();
                        }
                        catch (Exception ex)
                        {
                            fail++;
                            log.AppendLine("Op " + i + " (" + op + "): " + ex.Message);
                            if (tx == null)
                            {
                                tx = new Transaction(doc, "AI Chat — revitOps");
                            }

                            if (tx.GetStatus() != TransactionStatus.Started)
                            {
                                tx.Start();
                            }
                        }

                        continue;
                    }

                    try
                    {
                        switch (op)
                        {
                            case "set_parameter":
                                RevitSetParameterOps.Run(doc, opObj, log);
                                ok++;
                                break;
                            case "delete_elements":
                                RevitDeleteElementsOps.Run(doc, opObj, log, MaxIdsPerOp);
                                ok++;
                                break;
                            case "create_wall":
                                RevitWallCreationOps.RunCreateWallJsonOp(doc, opObj, settings, log, wallBatchKeys);
                                ok++;
                                break;
                            case "create_wall_arc":
                                RevitWallCreationOps.RunCreateWallArcJsonOp(doc, opObj, settings, log, wallBatchKeys);
                                ok++;
                                break;
                            case "create_room":
                                RevitRoomCreationOps.RunCreateRoomJsonOp(doc, opObj, log);
                                ok++;
                                break;
                            case "create_door":
                                RevitDoorCreationOps.RunCreateDoorJsonOp(doc, opObj, log, placementBatch);
                                ok++;
                                break;
                            case "create_window":
                                RevitWindowCreationOps.RunCreateWindowJsonOp(doc, opObj, log, placementBatch);
                                ok++;
                                break;
                            case "create_floor":
                                RevitFloorCreationOps.RunCreateFloorJsonOp(doc, opObj, log);
                                ok++;
                                break;
                            case "create_floor_from_room":
                                RevitFloorCreationOps.RunCreateFloorFromRoomJsonOp(doc, opObj, log);
                                ok++;
                                break;
                            case "analyze_floor_wall_footprint":
                                RevitFloorWallFootprintOps.RunAnalyzeFloorWallFootprintJsonOp(doc, opObj, log);
                                ok++;
                                break;
                            case "repair_floor_to_wall_footprint":
                                RevitFloorWallFootprintOps.RunRepairFloorToWallFootprintJsonOp(doc, opObj, log);
                                ok++;
                                break;
                            case "repair_floor_to_room_footprint":
                                RevitFloorWallFootprintOps.RunRepairFloorToRoomFootprintJsonOp(doc, opObj, log);
                                ok++;
                                break;
                            case "analyze_ceiling_wall_footprint":
                                RevitCeilingWallFootprintOps.RunAnalyzeCeilingWallFootprintJsonOp(doc, opObj, log);
                                ok++;
                                break;
                            case "repair_ceiling_to_wall_footprint":
                                RevitCeilingWallFootprintOps.RunRepairCeilingToWallFootprintJsonOp(doc, opObj, log);
                                ok++;
                                break;
                            case "repair_ceiling_to_room_footprint":
                                RevitCeilingWallFootprintOps.RunRepairCeilingToRoomFootprintJsonOp(doc, opObj, log);
                                ok++;
                                break;
                            case "create_ceiling":
                                RevitCeilingCreationOps.RunCreateCeilingJsonOp(doc, opObj, log, settings);
                                ok++;
                                break;
                            case "create_ceiling_from_room":
                                RevitCeilingCreationOps.RunCreateCeilingFromRoomJsonOp(doc, opObj, log, settings);
                                ok++;
                                break;
                            case "change_ceiling_kind":
                                RevitCeilingKindOps.RunChangeCeilingKindJsonOp(doc, opObj, settings, log);
                                ok++;
                                break;
                            case "create_wall_opening":
                                RevitWallOpeningOps.RunCreateWallOpeningJsonOp(doc, opObj, log, openingBatchKeys);
                                ok++;
                                break;
                            case "create_wall_arch_opening":
                                RevitWallOpeningOps.RunCreateWallArchOpeningJsonOp(doc, opObj, log, openingBatchKeys);
                                ok++;
                                break;
                            case "flip_wall":
                                RevitWallModifyOps.RunFlipWallJsonOp(doc, opObj, log);
                                ok++;
                                break;
                            case "create_family_instance":
                                RevitFamilyInstanceCreationOps.RunCreateFamilyInstanceJsonOp(doc, opObj, log, placementBatch);
                                ok++;
                                break;
                            case "create_pillar":
                                RevitColumnCreationOps.RunCreatePillarJsonOp(doc, opObj, log, placementBatch);
                                ok++;
                                break;
                            case "create_beam":
                                RevitBeamCreationOps.RunCreateBeamJsonOp(doc, opObj, log);
                                ok++;
                                break;
                            case "create_guardrail":
                            case "create_railing":
                                RevitGuardrailCreationOps.RunCreateGuardrailJsonOp(doc, opObj, log);
                                ok++;
                                break;
                            case "create_level":
                                RevitLevelCreationOps.RunCreateLevelJsonOp(doc, opObj, log);
                                ok++;
                                break;
                            case "create_grid":
                                RevitGridCreationOps.RunCreateGridJsonOp(doc, opObj, log);
                                ok++;
                                break;
                            case "change_element_level":
                                RevitChangeLevelPreserveOps.Run(
                                    doc,
                                    opObj,
                                    log,
                                    MaxIdsPerOp,
                                    RevitChangeLevelPreserveOps.ReadPreserveWorldPosition(opObj));
                                ok++;
                                break;
                            case "change_level_preserve_position":
                                RevitChangeLevelPreserveOps.Run(doc, opObj, log, MaxIdsPerOp, preserveWorldPosition: true);
                                ok++;
                                break;
                            default:
                                fail++;
                                log.AppendLine("Op " + i + ": unknown op \"" + op + "\".");
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        log.AppendLine("Op " + i + " (" + op + "): " + ex.Message);
                    }
                }

                doc.Regenerate();
                if (tx != null && tx.GetStatus() == TransactionStatus.Started)
                {
                    tx.Commit();
                }
            }
            catch (Exception ex)
            {
                if (tx != null && tx.HasStarted() && tx.GetStatus() == TransactionStatus.Started)
                {
                    tx.RollBack();
                }

                return "Transação revertida: " + ex.Message;
            }
            finally
            {
                tx?.Dispose();
            }

            foreach (var opObj in selectOps)
            {
                try
                {
                    RevitSelectElementsOps.Run(uidoc, opObj, log, MaxIdsPerOp);
                    ok++;
                }
                catch (Exception ex)
                {
                    fail++;
                    log.AppendLine("select_elements: " + ex.Message);
                }
            }

            return "Operações aplicadas: " + ok + " ok, " + fail + " falharam." +
                   (log.Length > 0 ? "\n" + log : string.Empty);
        }
    }
}
