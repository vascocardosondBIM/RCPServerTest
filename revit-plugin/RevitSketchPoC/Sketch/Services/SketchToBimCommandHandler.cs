using Autodesk.Revit.UI;
using RevitSketchPoC.RevitOperations.SketchBuild;
using RevitSketchPoC.Sketch.Contracts;
using System.Threading.Tasks;

namespace RevitSketchPoC.Sketch.Services
{
    public sealed class SketchToBimCommandHandler
    {
        private readonly ISketchInterpreter _interpreter;
        private readonly RevitModelBuilder _builder;

        public SketchToBimCommandHandler(ISketchInterpreter interpreter, RevitModelBuilder builder)
        {
            _interpreter = interpreter;
            _builder = builder;
        }

        public RevitModelBuilder Builder => _builder;

        /// <summary>HTTP / LLM only — safe to run on a thread-pool thread.</summary>
        public Task<SketchInterpretation> InterpretOnlyAsync(SketchToBimRequest request)
        {
            return _interpreter.InterpretAsync(request);
        }

        /// <summary>Revit DB changes only — must run on the Revit API thread.</summary>
        public BuildResult BuildOnly(UIDocument uiDoc, SketchToBimRequest request, SketchInterpretation interpretation)
        {
            return _builder.Build(uiDoc, request, interpretation);
        }

        /// <summary>
        /// Use from Revit API thread (e.g. <see cref="IExternalEventHandler.Execute"/>). Keeps LLM await on the same sync context so <see cref="RevitModelBuilder.Build"/> runs on the Revit main thread.
        /// </summary>
        public BuildResult HandleSync(UIDocument uiDoc, SketchToBimRequest request)
        {
            var interpretation = _interpreter.InterpretAsync(request).ConfigureAwait(true).GetAwaiter().GetResult();
            return _builder.Build(uiDoc, request, interpretation);
        }

        public async Task<BuildResult> HandleAsync(UIDocument uiDoc, SketchToBimRequest request)
        {
            var interpretation = await _interpreter.InterpretAsync(request).ConfigureAwait(true);
            return _builder.Build(uiDoc, request, interpretation);
        }
    }
}
