using System.Threading.Tasks;
using RevitSketchPoC.Contracts;

namespace RevitSketchPoC.Services
{
    public interface ISketchInterpreter
    {
        Task<SketchInterpretation> InterpretAsync(SketchToBimRequest request);
    }
}
