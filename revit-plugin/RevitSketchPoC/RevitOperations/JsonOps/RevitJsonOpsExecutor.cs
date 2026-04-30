using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitSketchPoC.Core.Configuration;
using RevitSketchPoC.RevitOperations.DeleteElements;
using RevitSketchPoC.RevitOperations.SelectElements;
using RevitSketchPoC.RevitOperations.CreateElements;
using RevitSketchPoC.RevitOperations.ChangeElements;
using System;
using System.Collections.Generic;
using System.Text;

namespace RevitSketchPoC.RevitOperations.JsonOps
{
    /// <summary>Runs a bounded set of Revit mutations from LLM-produced JSON ops (shared by chat and other features).</summary>
    public static class RevitJsonOpsExecutor
    {
        public const int MaxOps = 40;
        public const int MaxIdsPerOp = 50;

        public static string Execute(UIDocument uidoc, JArray ops, PluginSettings? pluginSettings = null)
        {
            var settings = pluginSettings ?? new PluginSettings();
            var doc = uidoc.Document;
            var log = new StringBuilder();
            var n = Math.Min(ops.Count, MaxOps);
            var ok = 0;
            var fail = 0;
            var selectOps = new List<JObject>();

            using (var tx = new Transaction(doc, "AI Chat — revitOps"))
            {
                tx.Start();
                try
                {
                    for (var i = 0; i < n; i++)
                    {
                        if (ops[i] is not JObject opObj)
                        {
                            continue;
                        }

                        var op = opObj["op"]?.ToString()?.Trim()?.ToLowerInvariant();
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
                                    RevitWallCreationOps.RunCreateWallJsonOp(doc, opObj, settings, log);
                                    ok++;
                                    break;
                                case "create_room":
                                    RevitRoomCreationOps.RunCreateRoomJsonOp(doc, opObj, log);
                                    ok++;
                                    break;
                                case "create_door":
                                    RevitDoorCreationOps.RunCreateDoorJsonOp(doc, opObj, log);
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
                    tx.Commit();
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && tx.GetStatus() == TransactionStatus.Started)
                    {
                        tx.RollBack();
                    }

                    return "Transação revertida: " + ex.Message;
                }
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
