using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitSketchPoC.RevitOperations.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RevitSketchPoC.RevitOperations.ChangeElements
{
    /// <summary>JSON ops <c>change_element_level</c> (default: mudar para o nível) e <c>change_level_preserve_position</c> (sempre preservar Z).</summary>
    public static class RevitChangeLevelPreserveOps
    {
        /// <summary>Runs level change for one op. <paramref name="preserveWorldPosition"/> true = manter cota no mundo.</summary>
        public static void Run(
            Document doc,
            JObject op,
            StringBuilder log,
            int maxIdsPerOp,
            bool preserveWorldPosition)
        {
            var targetLevel = ResolveTargetLevel(doc, op);
            if (targetLevel == null)
            {
                throw new InvalidOperationException(
                    "Operação de nível: indica targetLevelName (string) ou targetLevelId (integer).");
            }

            var ids = CollectElementIds(op, maxIdsPerOp);
            if (ids.Count == 0)
            {
                throw new InvalidOperationException(
                    "Operação de nível: indica elementIds (array) e/ou elementId (inteiro).");
            }

            var ok = 0;
            var fail = 0;
            foreach (var id in ids)
            {
                var el = doc.GetElement(id);
                if (el == null)
                {
                    fail++;
                    log.AppendLine("id=" + id + ": elemento não encontrado.");
                    continue;
                }

                if (RevitChangeLevelPreserveService.TryReassignLevel(doc, el, targetLevel, preserveWorldPosition, out var reason))
                {
                    ok++;
                    var mode = preserveWorldPosition ? "preservar Z" : "nível simples";
                    log.AppendLine("change_level id=" + id + " -> " + targetLevel.Name + " (" + mode + ")");
                }
                else
                {
                    fail++;
                    log.AppendLine("id=" + id + ": " + (reason ?? "falhou."));
                }
            }

            var label = preserveWorldPosition ? "change_level_preserve_position" : "change_element_level";
            log.Insert(0, label + ": " + ok + " ok, " + fail + " falharam.\n");
        }

        /// <summary>For <c>change_element_level</c>: optional preserveWorldPosition / preservePosition (default false).</summary>
        public static bool ReadPreserveWorldPosition(JObject op)
        {
            var t = op["preserveWorldPosition"] ?? op["preservePosition"];
            if (t == null || t.Type == JTokenType.Null)
            {
                return false;
            }

            if (t.Type == JTokenType.Boolean)
            {
                return t.Value<bool>();
            }

            var s = t.ToString().Trim();
            if (bool.TryParse(s, out var b))
            {
                return b;
            }

            if (string.Equals(s, "yes", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s, "1", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(s, "no", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s, "false", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s, "0", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return false;
        }

        private static Level? ResolveTargetLevel(Document doc, JObject op)
        {
            var idVal = op["targetLevelId"]?.Value<long?>() ?? op["targetLevelId"]?.Value<int?>();
            if (idVal != null)
            {
                return doc.GetElement(new ElementId((long)idVal)) as Level;
            }

            var name = op["targetLevelName"]?.ToString()?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            return new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        private static List<ElementId> CollectElementIds(JObject op, int maxIdsPerOp)
        {
            var fromArray = RevitOpsElementIdList.Read(op["elementIds"], maxIdsPerOp);
            var single = op["elementId"]?.Value<long?>() ?? op["elementId"]?.Value<int?>();
            if (single != null && fromArray.Count < maxIdsPerOp)
            {
                var id = new ElementId((long)single);
                if (!fromArray.Any(x => x == id))
                {
                    fromArray.Add(id);
                }
            }

            return fromArray;
        }
    }
}
