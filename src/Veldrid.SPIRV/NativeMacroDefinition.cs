using System;

namespace Veldrid.SPIRV
{
    internal struct NativeMacroDefinition : IDisposable
    {
        public InteropArray<byte> Name;
        public InteropArray<byte> Value;

        public NativeMacroDefinition(MacroDefinition macroDefinition)
        {
            if (string.IsNullOrEmpty(macroDefinition.Name))
            {
                throw new SpirvCompilationException($"MacroDefinition Name must be non-null.");
            }

            Name = InteropArray.ToUtf8(macroDefinition.Name);

            if (!string.IsNullOrEmpty(macroDefinition.Value))
            {
                Value = InteropArray.ToUtf8(macroDefinition.Value);
            }
        }

        public void Dispose()
        {
            Name.Dispose();
            Value.Dispose();
        }
    }
}
