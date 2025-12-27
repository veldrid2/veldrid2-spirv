using Silk.NET.Shaderc;

namespace Veldrid.SPIRV
{
    internal unsafe struct GlslCompileInfo
    {
        public InteropArray<byte> SourceText;
        public InteropArray<byte> FileName;
        public ShaderKind Kind;
        public Bool32 Debug;
        public InteropArray<NativeMacroDefinition> Macros;
    };
}
