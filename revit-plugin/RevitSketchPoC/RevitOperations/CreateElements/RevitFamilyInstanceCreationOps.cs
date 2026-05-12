using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Newtonsoft.Json.Linq;
using RevitSketchPoC.RevitOperations.Shared;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RevitSketchPoC.RevitOperations.CreateElements
{
    /// <summary>JSON <c>create_family_instance</c>: non-hosted placement on a level (generic furniture, equipment, etc.).</summary>
    public static class RevitFamilyInstanceCreationOps
    {
        public static void RunCreateFamilyInstanceJsonOp(
            Document doc,
            JObject op,
            StringBuilder log,
            List<(double x, double y, ElementId levelId, PlanPlacementBatchKind kind)>? placementBatch = null)
        {
            var typeName = op["familyTypeName"]?.ToString()?.Trim();
            if (string.IsNullOrEmpty(typeName))
            {
                throw new InvalidOperationException(
                    "create_family_instance requires familyTypeName (type name or \"Family : Type\" as in project).");
            }

            if (!TryReadLocationMeters(op, out var lx, out var ly))
            {
                throw new InvalidOperationException(
                    "create_family_instance requires locationX/locationY (metres) or location { x, y }.");
            }

            var levelName = op["levelName"]?.ToString();
            var level = RevitWallCreationOps.ResolveLevel(doc, string.IsNullOrWhiteSpace(levelName) ? null : levelName);
            if (placementBatch != null)
            {
                RevitPlanPlacementGuard.AssertNewPlanPoint(doc, level, lx, ly, placementBatch, checkExistingDoorWindow: true);
            }

            var symbol = RevitFamilySymbolByName.ResolveAny(doc, typeName);
            if (symbol == null)
            {
                throw new InvalidOperationException(
                    "create_family_instance: no FamilySymbol matches \"" + typeName + "\".");
            }

            if (!symbol.IsActive)
            {
                symbol.Activate();
                doc.Regenerate();
            }

            var point = new XYZ(
                RevitWallCreationOps.MetersToFeet(lx),
                RevitWallCreationOps.MetersToFeet(ly),
                level.Elevation);

            FamilyInstance instance;
            try
            {
                instance = doc.Create.NewFamilyInstance(
                    point,
                    symbol,
                    level,
                    StructuralType.NonStructural);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("create_family_instance: " + ex.Message);
            }

            var rotationDeg = ReadOptionalDouble(op["rotationDegrees"]);
            if (rotationDeg.HasValue && Math.Abs(rotationDeg.Value) > 1e-6)
            {
                try
                {
                    var axis = Line.CreateBound(point, point + XYZ.BasisZ);
                    instance.Location.Rotate(axis, rotationDeg.Value * Math.PI / 180.0);
                }
                catch
                {
                    // Rotation optional; ignore failure.
                }
            }

            log.AppendLine("create_family_instance id=" + instance.Id + " type=\"" + symbol.Family.Name + " : " + symbol.Name + "\"");
        }

        private static double? ReadOptionalDouble(JToken? t)
        {
            if (t == null || t.Type == JTokenType.Null)
            {
                return null;
            }

            try
            {
                return t.Value<double>();
            }
            catch
            {
                return double.TryParse(t.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
            }
        }

        private static bool TryReadLocationMeters(JObject op, out double lx, out double ly)
        {
            lx = ly = 0;
            if (op["location"] is JObject loc)
            {
                if (TryReadNumber(loc["x"], out lx) && TryReadNumber(loc["y"], out ly))
                {
                    return true;
                }
            }

            return TryReadNumber(op["locationX"], out lx) && TryReadNumber(op["locationY"], out ly);
        }

        private static bool TryReadNumber(JToken? token, out double value)
        {
            value = 0;
            if (token == null || token.Type == JTokenType.Null)
            {
                return false;
            }

            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
            {
                value = token.Value<double>();
                return true;
            }

            var s = token.ToString().Trim();
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                   double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
        }
    }
}
