using System;
using Silk.NET.Shaderc;

namespace Veldrid.SPIRV
{
    internal ref struct GlslCompileInfo
    {
        public ReadOnlySpan<byte> SourceText;
        public ReadOnlySpan<byte> FileName;
        public ReadOnlySpan<NativeMacroDefinition> Macros;
        public ShaderKind Kind;
        public bool Debug;
    };
}
