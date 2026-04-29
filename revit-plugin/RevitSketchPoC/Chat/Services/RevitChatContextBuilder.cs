using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RevitSketchPoC.Chat.Services
{
    /// <summary>
    /// Builds compact JSON/text snapshots of the active document and selection for LLM chat context.
    /// </summary>
    public static class RevitChatContextBuilder
    {
        private static readonly BuiltInCategory[] CountCategories =
        {
            BuiltInCategory.OST_Walls,
            BuiltInCategory.OST_Doors,
            BuiltInCategory.OST_Windows,
            BuiltInCategory.OST_Floors,
            BuiltInCategory.OST_Roofs,
            BuiltInCategory.OST_Rooms,
            BuiltInCategory.OST_Furniture,
            BuiltInCategory.OST_GenericModel,
            BuiltInCategory.OST_StructuralColumns,
            BuiltInCategory.OST_StructuralFraming
        };

        /// <summary>Project-wide snapshot (no geometry dump).</summary>
        public static string BuildProjectSnapshot(UIDocument uidoc)
        {
            var doc = uidoc.Document;
            var view = uidoc.ActiveView;

            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .Take(20)
                .Select(l => new Dictionary<string, object?>
                {
                    ["name"] = l.Name,
                    ["elevation"] = FormatLength(doc, l.Elevation)
                })
                .ToList();

            var counts = new Dictionary<string, int>();
            foreach (var bic in CountCategories)
            {
                var n = CountCategory(doc, bic);
                if (n > 0)
                {
                    counts[bic.ToString()] = n;
                }
            }

            var projectInfo = doc.ProjectInformation;
            var projectNumber = projectInfo?.get_Parameter(BuiltInParameter.PROJECT_NUMBER)?.AsString();
            var projectName = projectInfo?.get_Parameter(BuiltInParameter.PROJECT_NAME)?.AsString();

            var payload = new Dictionary<string, object?>
            {
                ["documentTitle"] = doc.Title,
                ["documentPath"] = string.IsNullOrEmpty(doc.PathName) ? null : doc.PathName,
                ["displayUnitSystem"] = doc.DisplayUnitSystem.ToString(),
                ["activeView"] = view == null
                    ? null
                    : new Dictionary<string, object?>
                    {
                        ["name"] = view.Name,
                        ["viewType"] = view.ViewType.ToString(),
                        ["discipline"] = view.Discipline.ToString()
                    },
                ["projectInfo"] = string.IsNullOrEmpty(projectName) && string.IsNullOrEmpty(projectNumber)
                    ? null
                    : new Dictionary<string, string?>
                    {
                        ["projectName"] = projectName,
                        ["projectNumber"] = projectNumber
                    },
                ["levels"] = levels,
                ["elementCountsByBuiltInCategory"] = counts,
                ["note"] = "Counts are non-type instances only. Use element ids from selection context when provided."
            };

            return JsonConvert.SerializeObject(payload, Formatting.Indented);
        }

        /// <summary>Detailed snapshot of current selection (capped).</summary>
        public static string BuildSelectionSnapshot(UIDocument uidoc, int maxElements = 24, int maxParamsPerElement = 120)
        {
            var doc = uidoc.Document;
            var ids = uidoc.Selection.GetElementIds();
            var total = ids.Count;
            var elements = new List<Dictionary<string, object?>>();
            var n = 0;
            foreach (var id in ids)
            {
                if (n >= maxElements)
                {
                    break;
                }

                var el = doc.GetElement(id);
                if (el == null)
                {
                    continue;
                }

                elements.Add(SerializeElement(doc, el, maxParamsPerElement));
                n++;
            }

            var payload = new Dictionary<string, object?>
            {
                ["totalSelected"] = total,
                ["elementsReturned"] = elements.Count,
                ["elements"] = elements
            };

            if (total > elements.Count)
            {
                payload["truncated"] = true;
                payload["note"] = "Lista truncada; pede ao utilizador para reduzir a seleÃ§Ã£o ou filtrar por categoria se precisares de todos.";
            }

            return JsonConvert.SerializeObject(payload, Formatting.Indented);
        }

        private static Dictionary<string, object?> SerializeElement(Document doc, Element el, int maxParams)
        {
            var typeId = el.GetTypeId();
            var typeEl = typeId != ElementId.InvalidElementId ? doc.GetElement(typeId) as ElementType : null;

            var row = new Dictionary<string, object?>
            {
                ["id"] = el.Id.IntegerValue,
                ["uniqueId"] = el.UniqueId,
                ["category"] = el.Category?.Name,
                ["name"] = string.IsNullOrEmpty(el.Name) ? null : el.Name,
                ["typeName"] = typeEl?.Name
            };

            if (el is FamilyInstance fi)
            {
                if (fi.Host != null)
                {
                    row["hostId"] = fi.Host.Id.IntegerValue;
                    row["hostCategory"] = fi.Host.Category?.Name;
                }

                if (fi.LevelId != null && fi.LevelId != ElementId.InvalidElementId)
                {
                    var lvl = doc.GetElement(fi.LevelId) as Level;
                    row["level"] = lvl?.Name;
                }
            }
            else if (el is Wall w)
            {
                if (w.LevelId != ElementId.InvalidElementId)
                {
                    var lvl = doc.GetElement(w.LevelId) as Level;
                    row["level"] = lvl?.Name;
                }

                if (w.Location is LocationCurve lc)
                {
                    row["length"] = FormatLength(doc, lc.Curve.Length);
                }
            }

            row["parameters"] = CollectParameters(doc, el, typeEl, maxParams);
            return row;
        }

        /// <summary>
        /// Collects instance + type parameters, including ElementId (levels, work planes), empty strings when writable,
        /// and built-in ids for stable <c>set_parameter</c> calls.
        /// </summary>
        private static List<Dictionary<string, object?>> CollectParameters(
            Document doc,
            Element instance,
            ElementType? type,
            int maxParams)
        {
            var list = new List<Dictionary<string, object?>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddFrom(Element source, string scope)
            {
                foreach (Parameter p in source.Parameters)
                {
                    if (list.Count >= maxParams)
                    {
                        return;
                    }

                    if (p == null)
                    {
                        continue;
                    }

                    var def = p.Definition;
                    if (def == null)
                    {
                        continue;
                    }

                    var name = def.Name;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var dedupeKey = scope + "|" + name;
                    if (!seen.Add(dedupeKey))
                    {
                        continue;
                    }

                    if (p.StorageType == StorageType.None)
                    {
                        continue;
                    }

                    if (!p.HasValue)
                    {
                        if (p.StorageType == StorageType.String)
                        {
                            // include empty user-editable strings (e.g. Comments)
                        }
                        else if (p.StorageType == StorageType.ElementId)
                        {
                            continue;
                        }
                        else
                        {
                            continue;
                        }
                    }

                    var formatted = FormatParameterValue(doc, p);
                    if (formatted == null && p.StorageType != StorageType.String)
                    {
                        continue;
                    }

                    var row = new Dictionary<string, object?>
                    {
                        ["scope"] = scope,
                        ["name"] = name,
                        ["value"] = formatted ?? string.Empty,
                        ["readOnly"] = p.IsReadOnly,
                        ["storage"] = p.StorageType.ToString()
                    };

                    if (def is InternalDefinition idef && idef.BuiltInParameter != BuiltInParameter.INVALID)
                    {
                        row["builtInParameter"] = idef.BuiltInParameter.ToString();
                    }

                    list.Add(row);
                }
            }

            AddFrom(instance, "instance");
            if (type != null)
            {
                AddFrom(type, "type");
            }

            return list;
        }

        private static string? FormatParameterValue(Document doc, Parameter p)
        {
            try
            {
                switch (p.StorageType)
                {
                    case StorageType.ElementId:
                    {
                        var id = p.AsElementId();
                        if (id == ElementId.InvalidElementId)
                        {
                            return null;
                        }

                        var refEl = doc.GetElement(id);
                        if (refEl != null)
                        {
                            var nm = refEl.Name;
                            return string.IsNullOrWhiteSpace(nm)
                                ? refEl.Id.IntegerValue.ToString(CultureInfo.InvariantCulture)
                                : nm + " (id:" + refEl.Id.IntegerValue + ")";
                        }

                        return id.IntegerValue.ToString(CultureInfo.InvariantCulture);
                    }
                    case StorageType.String:
                        return p.AsString() ?? string.Empty;
                    case StorageType.Double:
                        return p.AsValueString() ?? p.AsDouble().ToString(CultureInfo.InvariantCulture);
                    case StorageType.Integer:
                        return p.AsValueString() ?? p.AsInteger().ToString(CultureInfo.InvariantCulture);
                    default:
                        return p.AsValueString();
                }
            }
            catch
            {
                return p.AsValueString();
            }
        }

        private static int CountCategory(Document doc, BuiltInCategory bic)
        {
            try
            {
                return new FilteredElementCollector(doc)
                    .OfCategory(bic)
                    .WhereElementIsNotElementType()
                    .ToElementIds()
                    .Count;
            }
            catch
            {
                return 0;
            }
        }

        private static string FormatLength(Document doc, double internalFeet)
        {
            try
            {
                return UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Length, internalFeet, false);
            }
            catch
            {
                var m = UnitUtils.ConvertFromInternalUnits(internalFeet, UnitTypeId.Meters);
                return Math.Round(m, 3).ToString(CultureInfo.InvariantCulture) + " m (approx)";
            }
        }
    }
}
