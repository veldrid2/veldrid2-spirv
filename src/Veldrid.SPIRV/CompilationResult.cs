using System;

namespace Veldrid.SPIRV
{
    internal unsafe struct CompilationResult : IDisposable
    {
        public Bool32 Succeeded;
        public InteropArray<InteropArray<byte>> DataBuffers;
        public ReflectionInfo Reflection;

        public CompilationResult(InteropArray<byte> buffer)
        {
            DataBuffers = new InteropArray<InteropArray<byte>>(1);
            DataBuffers[0] = buffer;
        }

        public CompilationResult(string value) : this(InteropArray.ToUtf8(value))
        {
        }

        public readonly nuint GetLength(uint index) => DataBuffers[index].Count;

        public readonly byte* GetData(uint index) => DataBuffers[index].Data;

        public void Dispose()
        {
            DataBuffers.Dispose();
            Reflection.Dispose();
        }
    }
}
