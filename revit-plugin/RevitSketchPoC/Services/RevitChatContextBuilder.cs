using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RevitSketchPoC.Services
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
        public static string BuildSelectionSnapshot(UIDocument uidoc, int maxElements = 24, int maxParamsPerElement = 14)
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
                payload["note"] = "Lista truncada; pede ao utilizador para reduzir a seleção ou filtrar por categoria se precisares de todos.";
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

            row["parameters"] = CollectParameters(el, maxParams);
            return row;
        }

        private static List<Dictionary<string, string?>> CollectParameters(Element el, int maxParams)
        {
            var list = new List<Dictionary<string, string?>>();
            foreach (Parameter p in el.Parameters)
            {
                if (list.Count >= maxParams)
                {
                    break;
                }

                if (p == null || !p.HasValue)
                {
                    continue;
                }

                if (p.StorageType == StorageType.ElementId)
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

                string? value = p.AsValueString();
                if (string.IsNullOrWhiteSpace(value))
                {
                    value = p.AsString();
                }

                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (value.Length > 160)
                {
                    value = value.Substring(0, 157) + "...";
                }

                list.Add(new Dictionary<string, string?> { ["name"] = name, ["value"] = value });
            }

            return list;
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
