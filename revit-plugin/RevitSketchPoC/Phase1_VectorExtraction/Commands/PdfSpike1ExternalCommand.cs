using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;

namespace RevitSketchPoC.Phase1_VectorExtraction.Commands
{
    /// <summary>
    /// Alias retrocompatível para manifests/addins antigos que referenciam PdfSpike1ExternalCommand.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class PdfSpike1ExternalCommand : IExternalCommand
    {
        private readonly Phase1VectorExtractionExternalCommand _inner = new Phase1VectorExtractionExternalCommand();

        public Result Execute(ExternalCommandData commandData, ref string message, Autodesk.Revit.DB.ElementSet elements) =>
            _inner.Execute(commandData, ref message, elements);
    }
}
