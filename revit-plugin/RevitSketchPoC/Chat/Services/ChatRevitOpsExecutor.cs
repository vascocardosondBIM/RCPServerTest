using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RevitSketchPoC.Chat.Services
{
    /// <summary>Runs a bounded set of Revit mutations from LLM-produced JSON ops.</summary>
    public static class ChatRevitOpsExecutor
    {
        private const int MaxOps = 40;
        private const int MaxIdsPerOp = 50;

        public static string Execute(UIDocument uidoc, JArray ops)
        {
            var doc = uidoc.Document;
            var log = new StringBuilder();
            var n = Math.Min(ops.Count, MaxOps);
            var ok = 0;
            var fail = 0;
            var selectOps = new List<JObject>();

            using (var tx = new Transaction(doc, "AI Chat â€” revitOps"))
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
                                    RunSetParameter(doc, opObj, log);
                                    ok++;
                                    break;
                                case "delete_elements":
                                    RunDelete(doc, opObj, log);
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

                    return "TransaÃ§Ã£o revertida: " + ex.Message;
                }
            }

            foreach (var opObj in selectOps)
            {
                try
                {
                    RunSelect(uidoc, opObj, log);
                    ok++;
                }
                catch (Exception ex)
                {
                    fail++;
                    log.AppendLine("select_elements: " + ex.Message);
                }
            }

            return "OperaÃ§Ãµes aplicadas: " + ok + " ok, " + fail + " falharam." +
                   (log.Length > 0 ? "\n" + log : string.Empty);
        }

        /// <summary>
        /// Resolves parameter on instance, then on element type; supports optional <c>builtInParameter</c> on the op
        /// or <c>parameterName</c> like <c>BuiltIn:WALL_BASE_CONSTRAINT</c>.
        /// </summary>
        private static Parameter? ResolveWritableParameter(Document doc, Element el, JObject op, string? displayName)
        {
            var builtInToken = op["builtInParameter"]?.ToString()?.Trim();
            if (!string.IsNullOrEmpty(builtInToken) &&
                Enum.TryParse(builtInToken, ignoreCase: true, out BuiltInParameter bipFromField))
            {
                var fromInstance = el.get_Parameter(bipFromField);
                if (fromInstance != null)
                {
                    return fromInstance;
                }

                return ParameterOnType(doc, el, e => e.get_Parameter(bipFromField));
            }

            if (!string.IsNullOrWhiteSpace(displayName) &&
                displayName.StartsWith("BuiltIn:", StringComparison.OrdinalIgnoreCase))
            {
                var rest = displayName.Substring("BuiltIn:".Length).Trim();
                if (Enum.TryParse(rest, ignoreCase: true, out BuiltInParameter bipFromName))
                {
                    var fromInstance = el.get_Parameter(bipFromName);
                    if (fromInstance != null)
                    {
                        return fromInstance;
                    }

                    return ParameterOnType(doc, el, e => e.get_Parameter(bipFromName));
                }
            }

            if (!string.IsNullOrWhiteSpace(displayName))
            {
                var byName = el.LookupParameter(displayName);
                if (byName != null)
                {
                    return byName;
                }

                return ParameterOnType(doc, el, e => e.LookupParameter(displayName));
            }

            return null;
        }

        private static Parameter? ParameterOnType(Document doc, Element el, Func<Element, Parameter?> pick)
        {
            if (el.GetTypeId() == ElementId.InvalidElementId)
            {
                return null;
            }

            var typeEl = doc.GetElement(el.GetTypeId());
            return typeEl == null ? null : pick(typeEl);
        }

        private static void RunSetParameter(Document doc, JObject op, StringBuilder log)
        {
            var idVal = op["elementId"]?.Value<long?>() ?? op["elementId"]?.Value<int?>();
            if (idVal == null)
            {
                throw new InvalidOperationException("set_parameter requires elementId.");
            }

            var name = op["parameterName"]?.ToString();
            var builtInField = op["builtInParameter"]?.ToString();
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(builtInField))
            {
                throw new InvalidOperationException("set_parameter requires parameterName and/or builtInParameter.");
            }

            var value = op["value"]?.ToString() ?? string.Empty;
            var el = doc.GetElement(new ElementId((long)idVal));
            if (el == null)
            {
                throw new InvalidOperationException("Elemento " + idVal + " nÃ£o encontrado.");
            }

            var p = ResolveWritableParameter(doc, el, op, name);
            if (p == null)
            {
                throw new InvalidOperationException(
                    "ParÃ¢metro nÃ£o encontrado (nome localizado ou builtInParameter). Nome pedido: \"" +
                    (name ?? string.Empty) + "\".");
            }

            if (p.IsReadOnly)
            {
                throw new InvalidOperationException("ParÃ¢metro \"" + p.Definition?.Name + "\" Ã© sÃ³ leitura.");
            }

            try
            {
                ApplyParameterValue(doc, p, value);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Falha ao escrever \"" + p.Definition?.Name + "\" (" + p.StorageType + "): " + ex.Message);
            }

            log.AppendLine("set_parameter id=" + idVal + " " + p.Definition?.Name + "=\"" + value + "\"");
        }

        /// <summary>
        /// Revit often ignores <see cref="Parameter.SetValueString"/> for <see cref="StorageType.ElementId"/> (levels, phases, etc.).
        /// Use type-aware setters and resolve ids from names when needed.
        /// </summary>
        private static void ApplyParameterValue(Document doc, Parameter p, string rawValue)
        {
            var value = rawValue ?? string.Empty;

            switch (p.StorageType)
            {
                case StorageType.ElementId:
                {
                    var id = ResolveElementIdValue(doc, value);
                    if (id == ElementId.InvalidElementId)
                    {
                        throw new InvalidOperationException(
                            "NÃ£o foi possÃ­vel resolver referÃªncia (nÃ­vel/elemento) a partir de: \"" + value + "\".");
                    }

                    p.Set(id);
                    return;
                }
                case StorageType.String:
                    p.Set(value);
                    return;
                case StorageType.Integer:
                    if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var iv))
                    {
                        p.Set(iv);
                        return;
                    }

                    if (p.SetValueString(value))
                    {
                        return;
                    }

                    throw new InvalidOperationException("Valor inteiro invÃ¡lido: \"" + value + "\".");
                case StorageType.Double:
                    if (p.SetValueString(value))
                    {
                        return;
                    }

                    throw new InvalidOperationException("Valor numÃ©rico invÃ¡lido (unidades): \"" + value + "\".");
                default:
                    if (!p.SetValueString(value))
                    {
                        throw new InvalidOperationException("SetValueString nÃ£o aceitou o valor.");
                    }

                    return;
            }
        }

        private static ElementId ResolveElementIdValue(Document doc, string raw)
        {
            var value = (raw ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(value))
            {
                return ElementId.InvalidElementId;
            }

            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var direct))
            {
                var id = new ElementId(direct);
                if (doc.GetElement(id) != null)
                {
                    return id;
                }
            }

            var idInParens = Regex.Match(value, @"\(id:\s*(\d+)\s*\)\s*$", RegexOptions.IgnoreCase);
            if (idInParens.Success &&
                long.TryParse(idInParens.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fromCtx))
            {
                var id2 = new ElementId(fromCtx);
                if (doc.GetElement(id2) != null)
                {
                    return id2;
                }
            }

            var nameBeforeId = Regex.Match(value, @"^(.+?)\s*\(id:\s*\d+", RegexOptions.IgnoreCase);
            var nameCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { value };
            if (nameBeforeId.Success)
            {
                nameCandidates.Add(nameBeforeId.Groups[1].Value.Trim());
            }

            foreach (var candidate in nameCandidates)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                foreach (Level lvl in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
                {
                    if (string.Equals(lvl.Name, candidate, StringComparison.OrdinalIgnoreCase))
                    {
                        return lvl.Id;
                    }
                }
            }

            return ElementId.InvalidElementId;
        }

        private static void RunDelete(Document doc, JObject op, StringBuilder log)
        {
            var ids = ReadElementIds(op["elementIds"]);
            if (ids.Count == 0)
            {
                throw new InvalidOperationException("delete_elements requires elementIds.");
            }

            doc.Delete(ids);
            log.AppendLine("delete_elements count=" + ids.Count);
        }

        private static void RunSelect(UIDocument uidoc, JObject op, StringBuilder log)
        {
            var ids = ReadElementIds(op["elementIds"]);
            if (ids.Count == 0)
            {
                throw new InvalidOperationException("select_elements requires elementIds.");
            }

            uidoc.Selection.SetElementIds(ids);
            log.AppendLine("select_elements count=" + ids.Count);
        }

        private static ICollection<ElementId> ReadElementIds(JToken? token)
        {
            var list = new List<ElementId>();
            if (token is not JArray arr)
            {
                return list;
            }

            foreach (var t in arr.Take(MaxIdsPerOp))
            {
                long? v = t.Type == JTokenType.Integer ? t.Value<long>() : null;
                if (v == null && long.TryParse(t.ToString(), out var parsed))
                {
                    v = parsed;
                }

                if (v != null)
                {
                    list.Add(new ElementId((long)v));
                }
            }

            return list;
        }
    }
}
