using System;

namespace Veldrid.SPIRV
{
    internal struct ReflectionInfo : IDisposable
    {
        public InteropArray<NativeVertexElementDescription> VertexElements;
        public InteropArray<NativeResourceLayoutDescription> ResourceLayouts;

        public void Dispose()
        {
            VertexElements.Dispose();
            ResourceLayouts.Dispose();
        }
    }

    internal struct NativeVertexElementDescription : IDisposable
    {
        public InteropArray<byte> Name;
        public VertexElementSemantic Semantic;
        public VertexElementFormat Format;
        public uint Offset;

        public void Dispose()
        {
            Name.Dispose();
        }
    }

    internal struct NativeResourceLayoutDescription : IDisposable
    {
        public InteropArray<NativeResourceElementDescription> ResourceElements;

        public void Dispose()
        {
            ResourceElements.Dispose();
        }
    }

    internal struct NativeResourceElementDescription : IDisposable
    {
        public InteropArray<byte> Name;
        public ResourceKind Kind;
        public ShaderStages Stages;
        public ResourceLayoutElementOptions Options;

        public void Dispose()
        {
            Name.Dispose();
        }
    }
}
