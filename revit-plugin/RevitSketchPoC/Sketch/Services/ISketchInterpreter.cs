using System.Threading.Tasks;
using RevitSketchPoC.Sketch.Contracts;

namespace RevitSketchPoC.Sketch.Services
{
    public interface ISketchInterpreter
    {
        Task<SketchInterpretation> InterpretAsync(SketchToBimRequest request);
    }
}
