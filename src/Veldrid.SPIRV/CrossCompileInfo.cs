using System;

namespace Veldrid.SPIRV
{
    internal ref struct CrossCompileInfo
    {
        public CrossCompileTarget Target;
        public bool FixClipSpaceZ;
        public bool InvertY;
        public bool NormalizeResourceNames;
        public ReadOnlySpan<SpecializationConstant> Specializations;
        public ReadOnlySpan<uint> VertexShader;
        public ReadOnlySpan<uint> FragmentShader;
        public ReadOnlySpan<uint> ComputeShader;
    }
}
