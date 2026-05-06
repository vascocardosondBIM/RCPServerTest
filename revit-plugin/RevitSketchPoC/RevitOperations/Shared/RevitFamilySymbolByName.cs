using Autodesk.Revit.DB;
using System;
using System.Linq;

namespace RevitSketchPoC.RevitOperations.Shared
{
    /// <summary>Resolves <see cref="FamilySymbol"/> by type name for LLM-driven ops.</summary>
    public static class RevitFamilySymbolByName
    {
        public static FamilySymbol? ResolveInCategory(Document doc, BuiltInCategory category, string? requestedTypeName)
        {
            var symbols = new FilteredElementCollector(doc)
                .OfCategory(category)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .ToList();

            if (symbols.Count == 0)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(requestedTypeName))
            {
                return symbols[0];
            }

            var req = requestedTypeName.Trim();
            var byType = symbols.FirstOrDefault(s => s.Name.Equals(req, StringComparison.OrdinalIgnoreCase));
            if (byType != null)
            {
                return byType;
            }

            return symbols.FirstOrDefault(s =>
                ((s.Family?.Name ?? "") + " : " + s.Name).Equals(req, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Match <c>familyTypeName</c> to type name or <c>Family : Type</c> across all loadable symbols.</summary>
        public static FamilySymbol? ResolveAny(Document doc, string familyTypeName)
        {
            if (string.IsNullOrWhiteSpace(familyTypeName))
            {
                return null;
            }

            var req = familyTypeName.Trim();
            var all = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .ToList();

            var hit = all.FirstOrDefault(s => s.Name.Equals(req, StringComparison.OrdinalIgnoreCase));
            if (hit != null)
            {
                return hit;
            }

            return all.FirstOrDefault(s =>
                ((s.Family?.Name ?? "") + " : " + s.Name).Equals(req, StringComparison.OrdinalIgnoreCase));
        }
    }
}
