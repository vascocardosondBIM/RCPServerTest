namespace RevitSketchPoC.RevitOperations.Shared
{
    /// <summary>Internal Revit length units are feet.</summary>
    public static class RevitInternalUnits
    {
        public static double MetersToFeet(double meters) => meters / 0.3048;
    }
}
