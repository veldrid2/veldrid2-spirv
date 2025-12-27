
namespace Veldrid.SPIRV
{
    internal struct CrossCompileInfo
    {
        public CrossCompileTarget Target;
        public Bool32 FixClipSpaceZ;
        public Bool32 InvertY;
        public Bool32 NormalizeResourceNames;
        public InteropArray<SpecializationConstant> Specializations;
        public InteropArray<uint> VertexShader;
        public InteropArray<uint> FragmentShader;
        public InteropArray<uint> ComputeShader;
    }
}
