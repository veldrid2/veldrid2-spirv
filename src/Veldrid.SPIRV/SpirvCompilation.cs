using System;
using System.Runtime.InteropServices;
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
        public static VertexFragmentCompilationResult CompileVertexFragment(
            byte[] vsBytes,
            byte[] fsBytes,
            CrossCompileTarget target) => CompileVertexFragment(vsBytes, fsBytes, target, CrossCompileOptions.Default);

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
                SpirvCompilationResult vsCompileResult = CompileGlslToSpirv(
                    vsBytes,
                    string.Empty,
                    ShaderStages.Vertex,
                    target == CrossCompileTarget.GLSL || target == CrossCompileTarget.ESSL,
                    default);
                vsSpirvBytes = vsCompileResult.SpirvBytes;
            }

            if (Util.HasSpirvHeader(fsBytes))
            {
                fsSpirvBytes = fsBytes;
            }
            else
            {
                SpirvCompilationResult fsCompileResult = CompileGlslToSpirv(
                    fsBytes,
                    string.Empty,
                    ShaderStages.Fragment,
                    target == CrossCompileTarget.GLSL || target == CrossCompileTarget.ESSL,
                    default);
                fsSpirvBytes = fsCompileResult.SpirvBytes;
            }

            CrossCompileInfo info;
            info.Target = target;
            info.FixClipSpaceZ = options.FixClipSpaceZ;
            info.InvertY = options.InvertVertexOutputY;
            info.NormalizeResourceNames = options.NormalizeResourceNames;

            info.VertexShader = MemoryMarshal.Cast<byte, uint>(vsSpirvBytes);
            info.FragmentShader = MemoryMarshal.Cast<byte, uint>(fsSpirvBytes);
            info.ComputeShader = default;
            info.Specializations = options.Specializations;

            using CompilationResult result = LibVeldridSpirv.CrossCompile(info);
            if (!result.Succeeded)
            {
                throw new SpirvCompilationException(
                    "Compilation failed: " + Util.GetString(result.GetData(0), result.GetLength(0)));
            }

            string vsCode = Util.GetString(result.GetData(0), result.GetLength(0));
            string fsCode = Util.GetString(result.GetData(1), result.GetLength(1));

            ReflectionInfo reflInfo = result.Reflection;

            VertexElementDescription[] vertexElements = new VertexElementDescription[reflInfo.VertexElements.Count];
            for (uint i = 0; i < vertexElements.Length; i++)
            {
                ref NativeVertexElementDescription nativeDesc = ref reflInfo.VertexElements[i];
                vertexElements[i] = new VertexElementDescription(
                    Util.GetString(nativeDesc.Name.Data, nativeDesc.Name.Count),
                    nativeDesc.Semantic,
                    nativeDesc.Format,
                    nativeDesc.Offset);
            }

            ResourceLayoutDescription[] layouts = new ResourceLayoutDescription[reflInfo.ResourceLayouts.Count];
            for (int i = 0; i < layouts.Length; i++)
            {
                ref NativeResourceLayoutDescription nativeDesc = ref reflInfo.ResourceLayouts[i];
                layouts[i].Elements = new ResourceLayoutElementDescription[nativeDesc.ResourceElements.Count];
                for (uint j = 0; j < nativeDesc.ResourceElements.Count; j++)
                {
                    ref NativeResourceElementDescription elemDesc = ref nativeDesc.ResourceElements[j];
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

        /// <summary>
        /// Cross-compiles the given vertex-fragment pair into some target language.
        /// </summary>
        /// <param name="csBytes">The compute shader's SPIR-V bytecode or ASCII-encoded GLSL source code.</param>
        /// <param name="target">The target language.</param>
        /// <returns>A <see cref="ComputeCompilationResult"/> containing the compiled output.</returns>
        public static ComputeCompilationResult CompileCompute(
            byte[] csBytes,
            CrossCompileTarget target) => CompileCompute(csBytes, target, CrossCompileOptions.Default);

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
                SpirvCompilationResult vsCompileResult = CompileGlslToSpirv(
                    csBytes,
                    string.Empty,
                    ShaderStages.Compute,
                    target == CrossCompileTarget.GLSL || target == CrossCompileTarget.ESSL,
                    default);
                csSpirvBytes = vsCompileResult.SpirvBytes;
            }

            CrossCompileInfo info;
            info.Target = target;
            info.FixClipSpaceZ = options.FixClipSpaceZ;
            info.InvertY = options.InvertVertexOutputY;
            info.NormalizeResourceNames = options.NormalizeResourceNames;

            info.VertexShader = default;
            info.FragmentShader = default;
            info.ComputeShader = MemoryMarshal.Cast<byte, uint>(csSpirvBytes);
            info.Specializations = options.Specializations;

            using CompilationResult result = LibVeldridSpirv.CrossCompile(info);
            if (!result.Succeeded)
            {
                throw new SpirvCompilationException(
                    "Compilation failed: " + Util.GetString(result.GetData(0), result.GetLength(0)));
            }

            string csCode = Util.GetString(result.GetData(0), result.GetLength(0));

            ReflectionInfo reflInfo = result.Reflection;

            ResourceLayoutDescription[] layouts = new ResourceLayoutDescription[reflInfo.ResourceLayouts.Count];
            for (uint i = 0; i < reflInfo.ResourceLayouts.Count; i++)
            {
                ref NativeResourceLayoutDescription nativeDesc = ref reflInfo.ResourceLayouts[i];
                layouts[i].Elements = new ResourceLayoutElementDescription[nativeDesc.ResourceElements.Count];
                for (uint j = 0; j < nativeDesc.ResourceElements.Count; j++)
                {
                    ref NativeResourceElementDescription elemDesc = ref nativeDesc.ResourceElements[j];
                    layouts[i].Elements[j] = new ResourceLayoutElementDescription(
                        Util.GetString(elemDesc.Name.Data, elemDesc.Name.Count),
                        elemDesc.Kind,
                        elemDesc.Stages,
                        elemDesc.Options);
                }
            }

            SpirvReflection reflection = new([], layouts);

            return new ComputeCompilationResult(csCode, reflection);
        }

        /// <summary>
        /// Compiles the given GLSL source code into SPIR-V.
        /// </summary>
        /// <param name="sourceText">The shader source code.</param>
        /// <param name="fileName">A descriptive name for the shader. May be null.</param>
        /// <param name="stage">The <see cref="ShaderStages"/> which the shader is used in.</param>
        /// <param name="options">Parameters for the GLSL compiler.</param>
        /// <returns>A <see cref="SpirvCompilationResult"/> containing the compiled SPIR-V bytecode.</returns>
        public static SpirvCompilationResult CompileGlslToSpirv(
            string sourceText,
            string fileName,
            ShaderStages stage,
            GlslCompileOptions options)
        {
            using InteropArray<NativeMacroDefinition> macros = new((uint) options.Macros.Length);
            for (nuint i = 0; i < macros.Count; i++)
            {
                macros[i] = new NativeMacroDefinition(options.Macros[i]);
            }

            using InteropArray<byte> sourceTextArray = InteropArray.ToUtf8(sourceText);

            return CompileGlslToSpirv(
                sourceTextArray.AsSpan(),
                fileName,
                stage,
                options.Debug,
                macros.AsSpan());
        }

        internal static unsafe SpirvCompilationResult CompileGlslToSpirv(
            ReadOnlySpan<byte> sourceText,
            ReadOnlySpan<char> fileName,
            ShaderStages stage,
            bool debug,
            ReadOnlySpan<NativeMacroDefinition> macros)
        {
            GlslCompileInfo info;
            info.Kind = GetShadercKind(stage);
            info.SourceText = sourceText;
            info.Debug = debug;
            info.Macros = macros;

            if (fileName.IsEmpty) { fileName = "<veldrid-spirv-input>"; }
            int fileNameAsciiCount = Encoding.ASCII.GetByteCount(fileName);
            byte[] fileNameAsciiSpan = new byte[fileNameAsciiCount + 1];
            if (fileNameAsciiCount > 0)
            {
                Encoding.ASCII.GetBytes(fileName, fileNameAsciiSpan);
            }
            fileNameAsciiSpan[fileNameAsciiCount] = (byte)'\0';
            info.FileName = fileNameAsciiSpan.AsSpan(0, fileNameAsciiCount + 1);

            using CompilationResult result = LibVeldridSpirv.CompileGlslToSpirv(info);
            if (!result.Succeeded)
            {
                throw new SpirvCompilationException(
                    "Compilation failed: " + Util.GetString(result.GetData(0), result.GetLength(0)));
            }

            return new SpirvCompilationResult(result.DataBuffers[0].AsSpan().ToArray());
        }

        private static ShaderKind GetShadercKind(ShaderStages stage)
        {
            return stage switch
            {
                ShaderStages.Vertex => ShaderKind.VertexShader,
                ShaderStages.Geometry => ShaderKind.GeometryShader,
                ShaderStages.TessellationControl => ShaderKind.TessControlShader,
                ShaderStages.TessellationEvaluation => ShaderKind.TessEvaluationShader,
                ShaderStages.Fragment => ShaderKind.FragmentShader,
                ShaderStages.Compute => ShaderKind.ComputeShader,
                _ => throw new SpirvCompilationException($"Invalid shader stage: {stage}"),
            };
        }
    }
}
