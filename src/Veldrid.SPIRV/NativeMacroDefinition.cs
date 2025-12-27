using System.Runtime.CompilerServices;
using System.Text;

namespace Veldrid.SPIRV
{
    internal struct NativeMacroDefinition
    {
        public uint NameLength;
        public NameArray Name;
        public uint ValueLength;
        public NameArray Value;

        public NativeMacroDefinition(MacroDefinition macroDefinition)
        {
            if (string.IsNullOrEmpty(macroDefinition.Name))
            {
                throw new SpirvCompilationException($"MacroDefinition Name must be non-null.");
            }
            if (macroDefinition.Name.Length > 128)
            {
                throw new SpirvCompilationException($"Macro names must be less than or equal to 128 characters.");
            }

            NameLength = (uint)Encoding.ASCII.GetBytes(macroDefinition.Name, Name);

            if (!string.IsNullOrEmpty(macroDefinition.Value))
            {
                if (macroDefinition.Value.Length > 128)
                {
                    throw new SpirvCompilationException($"Macro values must be less than or equal to 128 characters.");
                }

                ValueLength = (uint)Encoding.ASCII.GetBytes(macroDefinition.Value, Value);
            }
            else
            {
                ValueLength = 0;
            }
        }

        [InlineArray(128)]
        internal struct NameArray
        {
            private byte _e0;
        }
    }
}
