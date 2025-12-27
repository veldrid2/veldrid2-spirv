using System;
using System.Text;
using Silk.NET.Shaderc;

namespace Veldrid.SPIRV
{
    /// <summary>
    /// Static functions for cross-compiling SPIR-V bytecode to various shader languages, and for compiling GLSL to SPIR-V.
    /// </summary>
    public static class SpirvCompilation
    {
        /// <summary>
        /// Cross-compiles the given vertex-fragment pair into some target language.
        /// </summary>
        /// <param name="vsBytes">The vertex shader's SPIR-V bytecode or ASCII-encoded GLSL source code.</param>
        /// <param name="fsBytes">The fragment shader's SPIR-V bytecode or ASCII-encoded GLSL source code.</param>
        /// <param name="target">The target language.</param>
        /// <returns>A <see cref="VertexFragmentCompilationResult"/> containing the compiled output.</returns>
        public static unsafe VertexFragmentCompilationResult CompileVertexFragment(
            byte[] vsBytes,
            byte[] fsBytes,
            CrossCompileTarget target) => CompileVertexFragment(vsBytes, fsBytes, target, new CrossCompileOptions());

        /// <summary>
        /// Cross-compiles the given vertex-fragment pair into some target language.
        /// </summary>
        /// <param name="vsBytes">The vertex shader's SPIR-V bytecode or ASCII-encoded GLSL source code.</param>
        /// <param name="fsBytes">The fragment shader's SPIR-V bytecode or ASCII-encoded GLSL source code.</param>
        /// <param name="target">The target language.</param>
        /// <param name="options">The options for shader translation.</param>
        /// <returns>A <see cref="VertexFragmentCompilationResult"/> containing the compiled output.</returns>
        public static unsafe VertexFragmentCompilationResult CompileVertexFragment(
            byte[] vsBytes,
            byte[] fsBytes,
            CrossCompileTarget target,
            CrossCompileOptions options)
        {
            byte[] vsSpirvBytes;
            byte[] fsSpirvBytes;

            if (Util.HasSpirvHeader(vsBytes))
            {
                vsSpirvBytes = vsBytes;
            }
            else
            {
                fixed (byte* sourceTextPtr = vsBytes)
                {
                    SpirvCompilationResult vsCompileResult = CompileGlslToSpirv(
                        (uint) vsBytes.Length,
                        sourceTextPtr,
                        string.Empty,
                        ShaderStages.Vertex,
                        target == CrossCompileTarget.GLSL || target == CrossCompileTarget.ESSL,
                        0,
                        null);
                    vsSpirvBytes = vsCompileResult.SpirvBytes;
                }
            }

            if (Util.HasSpirvHeader(fsBytes))
            {
                fsSpirvBytes = fsBytes;
            }
            else
            {
                fixed (byte* sourceTextPtr = fsBytes)
                {
                    SpirvCompilationResult fsCompileResult = CompileGlslToSpirv(
                        (uint) fsBytes.Length,
                        sourceTextPtr,
                        string.Empty,
                        ShaderStages.Fragment,
                        target == CrossCompileTarget.GLSL || target == CrossCompileTarget.ESSL,
                        0,
                        null);
                    fsSpirvBytes = fsCompileResult.SpirvBytes;
                }
            }

            CrossCompileInfo info;
            info.Target = target;
            info.FixClipSpaceZ = options.FixClipSpaceZ;
            info.InvertY = options.InvertVertexOutputY;
            info.NormalizeResourceNames = options.NormalizeResourceNames;
            fixed (byte* vsBytesPtr = vsSpirvBytes)
            fixed (byte* fsBytesPtr = fsSpirvBytes)
            fixed (SpecializationConstant* specConstantPtr = options.Specializations)
            {
                info.VertexShader = new InteropArray<uint>((uint) vsSpirvBytes.Length / 4, (uint*) vsBytesPtr);
                info.FragmentShader = new InteropArray<uint>((uint) fsSpirvBytes.Length / 4, (uint*) fsBytesPtr);
                info.ComputeShader = default;
                info.Specializations = new InteropArray<SpecializationConstant>((uint) options.Specializations.Length, specConstantPtr);

                CompilationResult result = default;
                try
                {
                    result = LibVeldridSpirv.CrossCompile(info);
                    if (!result.Succeeded)
                    {
                        throw new SpirvCompilationException(
                            "Compilation failed: " + Util.GetString(result.GetData(0), result.GetLength(0)));
                    }

                    string vsCode = Util.GetString(result.GetData(0), result.GetLength(0));
                    string fsCode = Util.GetString(result.GetData(1), result.GetLength(1));

                    ReflectionInfo* reflInfo = &result.Reflection;

                    VertexElementDescription[] vertexElements = new VertexElementDescription[reflInfo->VertexElements.Count];
                    for (uint i = 0; i < reflInfo->VertexElements.Count; i++)
                    {
                        ref NativeVertexElementDescription nativeDesc = ref reflInfo->VertexElements.Ref(i);
                        vertexElements[i] = new VertexElementDescription(
                            Util.GetString(nativeDesc.Name.Data, nativeDesc.Name.Count),
                            nativeDesc.Semantic,
                            nativeDesc.Format,
                            nativeDesc.Offset);
                    }

                    ResourceLayoutDescription[] layouts = new ResourceLayoutDescription[reflInfo->ResourceLayouts.Count];
                    for (uint i = 0; i < reflInfo->ResourceLayouts.Count; i++)
                    {
                        ref NativeResourceLayoutDescription nativeDesc = ref reflInfo->ResourceLayouts.Ref(i);
                        layouts[i].Elements = new ResourceLayoutElementDescription[nativeDesc.ResourceElements.Count];
                        for (uint j = 0; j < nativeDesc.ResourceElements.Count; j++)
                        {
                            ref NativeResourceElementDescription elemDesc = ref nativeDesc.ResourceElements.Ref(j);
                            layouts[i].Elements[j] = new ResourceLayoutElementDescription(
                                Util.GetString(elemDesc.Name.Data, elemDesc.Name.Count),
                                elemDesc.Kind,
                                elemDesc.Stages,
                                elemDesc.Options);
                        }
                    }

                    SpirvReflection reflection = new(vertexElements, layouts);

                    return new VertexFragmentCompilationResult(vsCode, fsCode, reflection);
                }
                finally
                {
                    result.Dispose();
                }
            }
        }

        /// <summary>
        /// Cross-compiles the given vertex-fragment pair into some target language.
        /// </summary>
        /// <param name="csBytes">The compute shader's SPIR-V bytecode or ASCII-encoded GLSL source code.</param>
        /// <param name="target">The target language.</param>
        /// <returns>A <see cref="ComputeCompilationResult"/> containing the compiled output.</returns>
        public static unsafe ComputeCompilationResult CompileCompute(
            byte[] csBytes,
            CrossCompileTarget target) => CompileCompute(csBytes, target, new CrossCompileOptions());

        /// <summary>
        /// Cross-compiles the given vertex-fragment pair into some target language.
        /// </summary>
        /// <param name="csBytes">The compute shader's SPIR-V bytecode or ASCII-encoded GLSL source code.</param>
        /// <param name="target">The target language.</param>
        /// <param name="options">The options for shader translation.</param>
        /// <returns>A <see cref="ComputeCompilationResult"/> containing the compiled output.</returns>
        public static unsafe ComputeCompilationResult CompileCompute(
            byte[] csBytes,
            CrossCompileTarget target,
            CrossCompileOptions options)
        {
            byte[] csSpirvBytes;

            if (Util.HasSpirvHeader(csBytes))
            {
                csSpirvBytes = csBytes;
            }
            else
            {
                fixed (byte* sourceTextPtr = csBytes)
                {
                    SpirvCompilationResult vsCompileResult = CompileGlslToSpirv(
                        (uint) csBytes.Length,
                        sourceTextPtr,
                        string.Empty,
                        ShaderStages.Compute,
                        target == CrossCompileTarget.GLSL || target == CrossCompileTarget.ESSL,
                        0,
                        null);
                    csSpirvBytes = vsCompileResult.SpirvBytes;
                }
            }

            CrossCompileInfo info;
            info.Target = target;
            info.FixClipSpaceZ = options.FixClipSpaceZ;
            info.InvertY = options.InvertVertexOutputY;
            info.NormalizeResourceNames = options.NormalizeResourceNames;
            fixed (byte* csBytesPtr = csSpirvBytes)
            fixed (SpecializationConstant* specConstants = options.Specializations)
            {
                info.VertexShader = default;
                info.FragmentShader = default;
                info.ComputeShader = new InteropArray<uint>((uint) csSpirvBytes.Length / 4, (uint*) csBytesPtr);
                info.Specializations = new InteropArray<SpecializationConstant>((uint) options.Specializations.Length, specConstants);

                CompilationResult result = default;
                try
                {
                    result = LibVeldridSpirv.CrossCompile(info);
                    if (!result.Succeeded)
                    {
                        throw new SpirvCompilationException(
                            "Compilation failed: " + Util.GetString(result.GetData(0), result.GetLength(0)));
                    }

                    string csCode = Util.GetString(result.GetData(0), result.GetLength(0));

                    ReflectionInfo* reflInfo = &result.Reflection;

                    ResourceLayoutDescription[] layouts = new ResourceLayoutDescription[reflInfo->ResourceLayouts.Count];
                    for (uint i = 0; i < reflInfo->ResourceLayouts.Count; i++)
                    {
                        ref NativeResourceLayoutDescription nativeDesc = ref reflInfo->ResourceLayouts.Ref(i);
                        layouts[i].Elements = new ResourceLayoutElementDescription[nativeDesc.ResourceElements.Count];
                        for (uint j = 0; j < nativeDesc.ResourceElements.Count; j++)
                        {
                            ref NativeResourceElementDescription elemDesc = ref nativeDesc.ResourceElements.Ref(j);
                            layouts[i].Elements[j] = new ResourceLayoutElementDescription(
                                Util.GetString(elemDesc.Name.Data, elemDesc.Name.Count),
                                elemDesc.Kind,
                                elemDesc.Stages,
                                elemDesc.Options);
                        }
                    }

                    SpirvReflection reflection = new SpirvReflection(
                        Array.Empty<VertexElementDescription>(),
                        layouts);

                    return new ComputeCompilationResult(csCode, reflection);
                }
                finally
                {
                    result.Dispose();
                }
            }
        }

        /// <summary>
        /// Compiles the given GLSL source code into SPIR-V.
        /// </summary>
        /// <param name="sourceText">The shader source code.</param>
        /// <param name="fileName">A descriptive name for the shader. May be null.</param>
        /// <param name="stage">The <see cref="ShaderStages"/> which the shader is used in.</param>
        /// <param name="options">Parameters for the GLSL compiler.</param>
        /// <returns>A <see cref="SpirvCompilationResult"/> containing the compiled SPIR-V bytecode.</returns>
        public static unsafe SpirvCompilationResult CompileGlslToSpirv(
            string sourceText,
            string fileName,
            ShaderStages stage,
            GlslCompileOptions options)
        {
            int sourceAsciiCount = Encoding.ASCII.GetByteCount(sourceText);
            byte* sourceAsciiPtr = stackalloc byte[sourceAsciiCount];
            fixed (char* sourceTextPtr = sourceText)
            {
                Encoding.ASCII.GetBytes(sourceTextPtr, sourceText.Length, sourceAsciiPtr, sourceAsciiCount);
            }

            int macroCount = options.Macros.Length;
            NativeMacroDefinition* macros = stackalloc NativeMacroDefinition[macroCount];
            for (int i = 0; i < macroCount; i++)
            {
                macros[i] = new NativeMacroDefinition(options.Macros[i]);
            }

            return CompileGlslToSpirv(
                (uint) sourceAsciiCount,
                sourceAsciiPtr,
                fileName,
                stage,
                options.Debug,
                (uint) macroCount,
                macros);
        }

        internal static unsafe SpirvCompilationResult CompileGlslToSpirv(
            uint sourceLength,
            byte* sourceTextPtr,
            string fileName,
            ShaderStages stage,
            bool debug,
            uint macroCount,
            NativeMacroDefinition* macros)
        {
            GlslCompileInfo info;
            info.Kind = GetShadercKind(stage);
            info.SourceText = new InteropArray<byte>(sourceLength, sourceTextPtr);
            info.Debug = debug;
            info.Macros = new InteropArray<NativeMacroDefinition>(macroCount, macros);

            if (string.IsNullOrEmpty(fileName)) { fileName = "<veldrid-spirv-input>"; }
            int fileNameAsciiCount = Encoding.ASCII.GetByteCount(fileName);
            byte* fileNameAsciiPtr = stackalloc byte[fileNameAsciiCount + 1];
            if (fileNameAsciiCount > 0)
            {
                fixed (char* fileNameTextPtr = fileName)
                {
                    Encoding.ASCII.GetBytes(fileNameTextPtr, fileName.Length, fileNameAsciiPtr, fileNameAsciiCount);
                }
            }
            fileNameAsciiPtr[fileNameAsciiCount] = (byte) '\0';
            info.FileName = new InteropArray<byte>((uint) fileNameAsciiCount + 1, fileNameAsciiPtr);

            CompilationResult result = default;
            try
            {
                result = LibVeldridSpirv.CompileGlslToSpirv(info);
                if (!result.Succeeded)
                {
                    throw new SpirvCompilationException(
                        "Compilation failed: " + Util.GetString(result.GetData(0), result.GetLength(0)));
                }

                nuint length = result.GetLength(0);
                byte[] spirvBytes = new byte[length];
                fixed (byte* spirvBytesPtr = &spirvBytes[0])
                {
                    Buffer.MemoryCopy(result.GetData(0), spirvBytesPtr, length, length);
                }

                return new SpirvCompilationResult(spirvBytes);
            }
            finally
            {
                result.Dispose();
            }
        }

        private static ShaderKind GetShadercKind(ShaderStages stage)
        {
            switch (stage)
            {
                case ShaderStages.Vertex: return ShaderKind.VertexShader;
                case ShaderStages.Geometry: return ShaderKind.GeometryShader;
                case ShaderStages.TessellationControl: return ShaderKind.TessControlShader;
                case ShaderStages.TessellationEvaluation: return ShaderKind.TessEvaluationShader;
                case ShaderStages.Fragment: return ShaderKind.FragmentShader;
                case ShaderStages.Compute: return ShaderKind.ComputeShader;
                default: throw new SpirvCompilationException($"Invalid shader stage: {stage}");
            }
        }
    }
}
