using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RevitSketchPoC.RevitOperations.ChangeElements
{
    /// <summary>Applies <c>set_parameter</c> JSON ops against a <see cref="Document"/>.</summary>
    public static class RevitSetParameterOps
    {
        public static void Run(Document doc, JObject op, StringBuilder log)
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
                throw new InvalidOperationException("Elemento " + idVal + " não encontrado.");
            }

            var p = ResolveWritableParameter(doc, el, op, name);
            if (p == null)
            {
                throw new InvalidOperationException(
                    "Parâmetro não encontrado (nome localizado ou builtInParameter). Nome pedido: \"" +
                    (name ?? string.Empty) + "\".");
            }

            if (p.IsReadOnly)
            {
                throw new InvalidOperationException("Parâmetro \"" + p.Definition?.Name + "\" é só leitura.");
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

            if (displayName is { Length: > 0 } dn &&
                dn.StartsWith("BuiltIn:", StringComparison.OrdinalIgnoreCase))
            {
                var rest = dn.Substring("BuiltIn:".Length).Trim();
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
                            "Não foi possível resolver referência (nível/elemento) a partir de: \"" + value + "\".");
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

                    throw new InvalidOperationException("Valor inteiro inválido: \"" + value + "\".");
                case StorageType.Double:
                    if (p.SetValueString(value))
                    {
                        return;
                    }

                    throw new InvalidOperationException("Valor numérico inválido (unidades): \"" + value + "\".");
                default:
                    if (!p.SetValueString(value))
                    {
                        throw new InvalidOperationException("SetValueString não aceitou o valor.");
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
    }
}
