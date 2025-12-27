using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Shaderc;
using Silk.NET.SPIRV;
using Silk.NET.SPIRV.Cross;

namespace Veldrid.SPIRV;

using CrossCompiler = Silk.NET.SPIRV.Cross.Compiler;
using SpvcConstant = Silk.NET.SPIRV.Cross.SpecializationConstant;

using ShaderCompiler = Silk.NET.Shaderc.Compiler;

readonly record struct BindingInfo(uint Set, uint Binding);

struct ResourceInfo
{
    public InteropArray<byte> Name;
    public ResourceKind Kind;
    public IdArray IDs; // 0 == VS/CS, 1 == FS

    [InlineArray(2)]
    public struct IdArray
    {
        private uint _e0;
    }
}

internal static unsafe class LibVeldridSpirv
{
    private static readonly Cross api1 = Cross.GetApi();
    private static readonly Shaderc api2 = Shaderc.GetApi();

    private static ResourceKind ClassifyResource(CrossCompiler* compiler, in ReflectedResource resource, bool image, bool storage)
    {
        CrossType* type = api1.CompilerGetTypeHandle(compiler, resource.TypeId);

        // TODO: what's this for? was it meant for CompilerGetBufferBlockDecorations?
        uint nonWritable = api1.CompilerGetDecoration(compiler, resource.Id, Decoration.NonWritable);

        switch (api1.TypeGetBasetype(type))
        {
            case Basetype.Struct:
                if (storage)
                {
                    Decoration* decors;
                    nuint num_decors;
                    api1.CompilerGetBufferBlockDecorations(compiler, resource.Id, &decors, &num_decors);

                    for (nuint i = 0; i < num_decors; i++)
                    {
                        if (decors[i] == Decoration.NonWritable)
                        {
                            return ResourceKind.StructuredBufferReadOnly;
                        }
                    }
                    return ResourceKind.StructuredBufferReadWrite;
                }
                else
                {
                    return ResourceKind.UniformBuffer;
                }

            case Basetype.Image:
                return storage ? ResourceKind.TextureReadWrite : ResourceKind.TextureReadOnly;

            case Basetype.Sampler:
                return ResourceKind.Sampler;

            default:
                throw new Exception("Unhandled SPIR-V data type.");
        }
    }

    private static void AddResources(
        Resources* resources,
        ResourceType type,
        CrossCompiler* compiler,
        Dictionary<BindingInfo, ResourceInfo> allResources,
        int idIndex,
        bool normalizeResourceNames,
        bool image,
        bool storage)
    {
        ReflectedResource* resourceList;
        nuint resource_size;
        api1.ResourcesGetResourceListForType(resources, type, &resourceList, &resource_size);

        for (nuint i = 0; i < resource_size; i++)
        {
            ref ReflectedResource res = ref resourceList[i];
            ResourceKind kind = ClassifyResource(compiler, res, image, storage);
            BindingInfo bi = new(
                api1.CompilerGetDecoration(compiler, res.Id, Decoration.DescriptorSet),
                api1.CompilerGetDecoration(compiler, res.Id, Decoration.Binding)
            );

            ResourceInfo ri = new();
            if (normalizeResourceNames)
            {
                InteropArray<byte> name = InteropArray.ToUtf8($"vdspv_{bi.Set}_{bi.Binding}");
                uint id = kind == ResourceKind.UniformBuffer ? res.BaseTypeId : res.Id;
                api1.CompilerSetName(compiler, id, name.Data);
                ri.Name = name;
            }
            else
            {
                ri.Name = InteropArray.FromNullTerminated(res.Name).Clone();
            }

            ri.IDs[idIndex] = res.Id;
            ri.Kind = kind;

            if (allResources.TryAdd(bi, ri))
            {
                continue;
            }
            ref ResourceInfo actualRi = ref CollectionsMarshal.GetValueRefOrNullRef(allResources, bi);

            if (actualRi.IDs[idIndex] != 0)
            {
                string msg =
                    $"The same binding slot ({bi.Set}, {bi.Binding}) was used by multiple distinct resources. " +
                    $"First resource: {InteropArray.ToString(actualRi.Name)}. Second resource: {InteropArray.ToString(ri.Name)}";
                throw new Exception(msg);
            }

            actualRi.IDs[idIndex] = res.Id;
            if (actualRi.Kind != kind)
            {
                string msg =
                    $"The same binding slot ({bi.Set}, {bi.Binding}) was used by multiple resources " +
                    $"with incompatible types: \"{actualRi.Kind}\" and \"{kind}\".";
                throw new Exception(msg);
            }
        }
    }

    private static void AddAllResources(
        Resources* resources,
        CrossCompiler* compiler,
        Dictionary<BindingInfo, ResourceInfo> map,
        int idIndex,
        bool normalizeResourceNames)
    {
        AddResources(resources, ResourceType.UniformBuffer, compiler, map, idIndex, normalizeResourceNames, false, false);
        AddResources(resources, ResourceType.StorageBuffer, compiler, map, idIndex, normalizeResourceNames, false, true);
        AddResources(resources, ResourceType.SeparateImage, compiler, map, idIndex, normalizeResourceNames, true, false);
        AddResources(resources, ResourceType.StorageImage, compiler, map, idIndex, normalizeResourceNames, true, true);
        AddResources(resources, ResourceType.SeparateSamplers, compiler, map, idIndex, normalizeResourceNames, false, false);
    }

    private struct ResourceCounter
    {
        public uint BufferIndex;
        public uint TextureIndex;
        public uint UavIndex;
        public uint SamplerIndex;
    }

    private static uint GetResourceIndex(
        CrossCompileTarget target,
        ResourceKind resourceKind,
        ref ResourceCounter counter)
    {
        bool isMSL = target == CrossCompileTarget.MSL;
        return resourceKind switch
        {
            ResourceKind.UniformBuffer => counter.BufferIndex++,
            ResourceKind.StructuredBufferReadWrite => isMSL ? counter.BufferIndex++ : counter.UavIndex++,
            ResourceKind.TextureReadWrite => isMSL ? counter.TextureIndex++ : counter.UavIndex++,
            ResourceKind.TextureReadOnly => counter.TextureIndex++,
            ResourceKind.StructuredBufferReadOnly => isMSL ? counter.BufferIndex++ : counter.TextureIndex++,
            ResourceKind.Sampler => counter.SamplerIndex++,
            _ => throw new ArgumentException("Invalid ResourceKind."),
        };
    }

    private static CrossCompiler* GetCompiler(Context* context, ParsedIr* ir, in CrossCompileInfo info)
    {
        CrossCompiler* compiler;
        CompilerOptions* options;

        switch (info.Target)
        {
            case CrossCompileTarget.HLSL:
            {
                api1.ContextCreateCompiler(context, Backend.Hlsl, ir, CaptureMode.TakeOwnership, &compiler).Check(context);
                api1.CompilerCreateCompilerOptions(compiler, &options);

                api1.CompilerOptionsSetUint(options, CompilerOption.HlslShaderModel, 50);
                api1.CompilerOptionsSetBool(options, CompilerOption.HlslPointSizeCompat, 1);
                break;
            }

            case CrossCompileTarget.GLSL:
            case CrossCompileTarget.ESSL:
            {
                api1.ContextCreateCompiler(context, Backend.Glsl, ir, CaptureMode.TakeOwnership, &compiler).Check(context);
                api1.CompilerCreateCompilerOptions(compiler, &options);

                api1.CompilerOptionsSetBool(options, CompilerOption.GlslES, (byte)(info.Target == CrossCompileTarget.ESSL ? 1 : 0));
                api1.CompilerOptionsSetBool(options, CompilerOption.GlslEnable420PackExtension, 0);

                uint version;
                if (!info.ComputeShader.IsEmpty)
                {
                    version = info.Target == CrossCompileTarget.GLSL ? 430u : 310;
                }
                else
                {
                    version = info.Target == CrossCompileTarget.GLSL ? 330u : 300;
                }
                api1.CompilerOptionsSetUint(options, CompilerOption.GlslVersion, version);
                break;
            }

            case CrossCompileTarget.MSL:
            {
                api1.ContextCreateCompiler(context, Backend.Msl, ir, CaptureMode.TakeOwnership, &compiler).Check(context);
                api1.CompilerCreateCompilerOptions(compiler, &options);
                break;
            }

            default:
                throw new ArgumentException("Invalid OutputKind.");
        }

        api1.CompilerOptionsSetBool(options, CompilerOption.FlipVertexY, (byte)(info.InvertY ? 1 : 0));
        api1.CompilerOptionsSetBool(options, CompilerOption.FixupDepthConvention, (byte)(info.FixClipSpaceZ ? 1 : 0));

        api1.CompilerInstallCompilerOptions(compiler, options).Check(context);
        return compiler;
    }

    private static void SetSpecializations(CrossCompiler* compiler, in CrossCompileInfo info)
    {
        SpvcConstant* specConstants;
        nuint num_constants;
        api1.CompilerGetSpecializationConstants(compiler, &specConstants, &num_constants);

        foreach (SpecializationConstant spec in info.Specializations)
        {
            uint varID = 0;

            for (nuint j = 0; j < num_constants; j++)
            {
                SpvcConstant constant = specConstants[j];
                if (constant.ConstantId == spec.ID)
                {
                    varID = constant.Id;
                }
            }

            if (varID != 0)
            {
                Constant* constVar = api1.CompilerGetConstantHandle(compiler, varID);
                api1.ConstantSetScalarU64(constVar, 0, 0, spec.Data);
            }
        }
    }

    private static InteropArray<NativeResourceLayoutDescription> CreateResourceLayoutArray(
        Dictionary<BindingInfo, ResourceInfo> resources,
        bool compute)
    {
        List<uint> setSizes = [];
        foreach (BindingInfo it in resources.Keys)
        {
            uint set = it.Set;
            while (setSizes.Count <= set)
            {
                setSizes.Add(0);
            }
            setSizes[(int)set] = Math.Max(setSizes[(int)set], it.Binding + 1);
        }

        int setCount = setSizes.Count;
        InteropArray<NativeResourceLayoutDescription> ret = new((uint)setCount);

        for (int i = 0; i < setCount; i++)
        {
            ret[i].ResourceElements = new InteropArray<NativeResourceElementDescription>(setSizes[i]);
            for (int j = 0; j < setSizes[i]; j++)
            {
                ret[i].ResourceElements[j] = new NativeResourceElementDescription()
                {
                    Name = default,
                    Kind = ResourceKind.UniformBuffer,
                    Stages = ShaderStages.None,
                    Options = ResourceLayoutElementOptions.Unused,
                };
            }
        }

        foreach (KeyValuePair<BindingInfo, ResourceInfo> it in resources)
        {
            ShaderStages stages = ShaderStages.None;
            if (it.Value.IDs[0] != 0)
            {
                stages |= compute ? ShaderStages.Compute : ShaderStages.Vertex;
            }
            if (it.Value.IDs[1] != 0)
            {
                stages |= ShaderStages.Fragment;
            }

            ret[it.Key.Set].ResourceElements[it.Key.Binding] = new NativeResourceElementDescription()
            {
                Name = it.Value.Name,
                Kind = it.Value.Kind,
                Stages = stages,
                Options = 0,
            };
        }

        return ret;
    }

    private static CompilationResult CompileVertexFragment(Context* context, in CrossCompileInfo info)
    {
        ParsedIr* vsBytes;
        api1.ContextParseSpirv(context, info.VertexShader, (uint)info.VertexShader.Length, &vsBytes).Check(context);
        CrossCompiler* vsCompiler = GetCompiler(context, vsBytes, info);

        ParsedIr* fsBytes;
        api1.ContextParseSpirv(context, info.FragmentShader, (uint)info.FragmentShader.Length, &fsBytes).Check(context);
        CrossCompiler* fsCompiler = GetCompiler(context, fsBytes, info);

        SetSpecializations(vsCompiler, info);
        SetSpecializations(fsCompiler, info);

        Resources* vsResources;
        api1.CompilerCreateShaderResources(vsCompiler, &vsResources);
        Resources* fsResources;
        api1.CompilerCreateShaderResources(fsCompiler, &fsResources);

        using OwnedMap<BindingInfo, ResourceInfo> allResources = new([]);

        AddAllResources(vsResources, vsCompiler, allResources.Map, 0, info.NormalizeResourceNames);
        AddAllResources(fsResources, fsCompiler, allResources.Map, 1, info.NormalizeResourceNames);

        if (info.Target == CrossCompileTarget.HLSL || info.Target == CrossCompileTarget.MSL)
        {
            ResourceCounter counter = new();
            foreach (ResourceInfo it in allResources.Map.Values)
            {
                uint index = GetResourceIndex(info.Target, it.Kind, ref counter);
                uint vsID = it.IDs[0];
                if (vsID != 0)
                {
                    api1.CompilerSetDecoration(vsCompiler, vsID, Decoration.Binding, index);
                }

                uint fsID = it.IDs[1];
                if (fsID != 0)
                {
                    api1.CompilerSetDecoration(fsCompiler, fsID, Decoration.Binding, index);
                }
            }
        }

        if (info.Target == CrossCompileTarget.GLSL || info.Target == CrossCompileTarget.ESSL)
        {
            uint vsId;
            api1.CompilerBuildDummySamplerForCombinedImages(vsCompiler, &vsId);
            api1.CompilerBuildCombinedImageSamplers(vsCompiler);

            CombinedImageSampler* vsSamplers;
            nuint vsSamplerSize;
            api1.CompilerGetCombinedImageSamplers(vsCompiler, &vsSamplers, &vsSamplerSize);
            for (nuint i = 0; i < vsSamplerSize; i++)
            {
                CombinedImageSampler* remap = &vsSamplers[i];
                api1.CompilerSetName(vsCompiler, remap->CombinedId, api1.CompilerGetName(vsCompiler, remap->ImageId));
            }

            uint fsId;
            api1.CompilerBuildDummySamplerForCombinedImages(fsCompiler, &fsId);
            api1.CompilerBuildCombinedImageSamplers(fsCompiler);

            CombinedImageSampler* fsSamplers;
            nuint fsSamplerSize;
            api1.CompilerGetCombinedImageSamplers(fsCompiler, &fsSamplers, &fsSamplerSize);
            for (nuint i = 0; i < fsSamplerSize; i++)
            {
                CombinedImageSampler* remap = &fsSamplers[i];
                api1.CompilerSetName(fsCompiler, remap->CombinedId, api1.CompilerGetName(fsCompiler, remap->ImageId));
            }

            ReflectedResource* resList;
            nuint resSize;
            api1.ResourcesGetResourceListForType(vsResources, ResourceType.StageOutput, &resList, &resSize);
            for (nuint i = 0; i < resSize; i++)
            {
                ReflectedResource* output = &resList[i];

                uint location = api1.CompilerGetDecoration(vsCompiler, output->Id, Decoration.Location);
                using InteropArray<byte> newName = InteropArray.ToUtf8($"vdspv_fsin{location}");
                api1.CompilerSetName(vsCompiler, output->Id, newName.Data);
            }

            api1.ResourcesGetResourceListForType(fsResources, ResourceType.StageInput, &resList, &resSize);
            for (nuint i = 0; i < resSize; i++)
            {
                ReflectedResource* input = &resList[i];

                uint location = api1.CompilerGetDecoration(fsCompiler, input->Id, Decoration.Location);
                using InteropArray<byte> newName = InteropArray.ToUtf8($"vdspv_fsin{location}");
                api1.CompilerSetName(fsCompiler, input->Id, newName.Data);
            }
        }

        if (info.Target == CrossCompileTarget.ESSL)
        {
            ReflectedResource* resList;
            nuint resSize;
            api1.ResourcesGetResourceListForType(vsResources, ResourceType.UniformBuffer, &resList, &resSize);
            for (nuint i = 0; i < resSize; i++)
            {
                ReflectedResource* uniformBuffer = &resList[i];
                api1.CompilerUnsetDecoration(vsCompiler, uniformBuffer->Id, Decoration.Binding);
            }

            uint bufferIndex = 0;
            uint imageIndex = 0;
            foreach (ResourceInfo it in allResources.Map.Values)
            {
                if (it.Kind == ResourceKind.StructuredBufferReadOnly || it.Kind == ResourceKind.StructuredBufferReadWrite)
                {
                    uint id = bufferIndex++;
                    if (it.IDs[0] != 0)
                    {
                        api1.CompilerSetDecoration(vsCompiler, it.IDs[0], Decoration.Binding, id);
                    }

                    if (it.IDs[1] != 0)
                    {
                        api1.CompilerSetDecoration(fsCompiler, it.IDs[1], Decoration.Binding, id);
                    }
                }
                else if (it.Kind == ResourceKind.TextureReadWrite)
                {
                    uint id = imageIndex++;
                    if (it.IDs[0] != 0)
                    {
                        api1.CompilerSetDecoration(vsCompiler, it.IDs[0], Decoration.Binding, id);
                    }

                    if (it.IDs[1] != 0)
                    {
                        api1.CompilerSetDecoration(fsCompiler, it.IDs[1], Decoration.Binding, id);
                    }
                }
            }
        }

        byte* vsText;
        api1.CompilerCompile(vsCompiler, &vsText).Check(context);
        string vsStr = Util.UTF8.GetString(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(vsText));

        ReflectedResource* tmpList;
        nuint bufCount;
        nuint imgCount;
        api1.ResourcesGetResourceListForType(vsResources, ResourceType.StorageBuffer, &tmpList, &bufCount);
        api1.ResourcesGetResourceListForType(vsResources, ResourceType.StorageImage, &tmpList, &imgCount);
        bool usesStorageResource = bufCount > 0 || imgCount > 0;

        if (info.Target == CrossCompileTarget.GLSL && usesStorageResource)
        {
            string key = "#version 330";
            vsStr = vsStr.Replace(key, "#version 430");
        }
        else if (info.Target == CrossCompileTarget.ESSL && usesStorageResource)
        {
            string key = "#version 300";
            vsStr = vsStr.Replace(key, "#version 310");
        }

        byte* fsText;
        api1.CompilerCompile(fsCompiler, &fsText).Check(context);
        string fsStr = Util.UTF8.GetString(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(fsText));

        api1.ResourcesGetResourceListForType(fsResources, ResourceType.StorageBuffer, &tmpList, &bufCount);
        api1.ResourcesGetResourceListForType(fsResources, ResourceType.StorageImage, &tmpList, &imgCount);
        usesStorageResource = bufCount > 0 || imgCount > 0;

        if (info.Target == CrossCompileTarget.GLSL && usesStorageResource)
        {
            string key = "#version 330";
            fsStr = fsStr.Replace(key, "#version 430");
        }
        else if (info.Target == CrossCompileTarget.ESSL && usesStorageResource)
        {
            string key = "#version 300";
            fsStr = fsStr.Replace(key, "#version 310");
        }

        CompilationResult result = new()
        {
            Succeeded = true,
            DataBuffers = new InteropArray<InteropArray<byte>>(2)
        };
        result.DataBuffers[0] = InteropArray.ToUtf8(vsStr);
        result.DataBuffers[1] = InteropArray.ToUtf8(fsStr);

        ReflectVertexInfo(vsCompiler, vsResources, ref result.Reflection);
        result.Reflection.ResourceLayouts = CreateResourceLayoutArray(allResources.Map, false);

        return result;
    }

    private static CompilationResult CompileCompute(Context* context, in CrossCompileInfo info)
    {
        ParsedIr* csBytes;
        api1.ContextParseSpirv(context, info.ComputeShader, (uint)info.ComputeShader.Length, &csBytes).Check(context);
        CrossCompiler* csCompiler = GetCompiler(context, csBytes, info);

        SetSpecializations(csCompiler, info);

        Resources* csResources;
        api1.CompilerCreateShaderResources(csCompiler, &csResources);

        using OwnedMap<BindingInfo, ResourceInfo> allResources = new([]);

        AddAllResources(csResources, csCompiler, allResources.Map, 0, info.NormalizeResourceNames);

        if (info.Target == CrossCompileTarget.HLSL || info.Target == CrossCompileTarget.MSL)
        {
            ResourceCounter counter = new();
            foreach (ResourceInfo it in allResources.Map.Values)
            {
                uint index = GetResourceIndex(info.Target, it.Kind, ref counter);
                uint csID = it.IDs[0];
                if (csID != 0)
                {
                    api1.CompilerSetDecoration(csCompiler, csID, Decoration.Binding, index);
                }
            }
        }

        if (info.Target == CrossCompileTarget.GLSL || info.Target == CrossCompileTarget.ESSL)
        {
            uint csId;
            api1.CompilerBuildDummySamplerForCombinedImages(csCompiler, &csId);
            api1.CompilerBuildCombinedImageSamplers(csCompiler);

            CombinedImageSampler* csSamplers;
            nuint csSamplerSize;
            api1.CompilerGetCombinedImageSamplers(csCompiler, &csSamplers, &csSamplerSize);
            for (nuint i = 0; i < csSamplerSize; i++)
            {
                CombinedImageSampler* remap = &csSamplers[i];
                api1.CompilerSetName(csCompiler, remap->CombinedId, api1.CompilerGetName(csCompiler, remap->ImageId));
            }
        }

        if (info.Target == CrossCompileTarget.ESSL)
        {
            ReflectedResource* bufferList;
            nuint bufferCount;
            api1.ResourcesGetResourceListForType(csResources, ResourceType.UniformBuffer, &bufferList, &bufferCount);
            for (nuint i = 0; i < bufferCount; i++)
            {
                ReflectedResource* uniformBuffer = &bufferList[i];
                api1.CompilerUnsetDecoration(csCompiler, uniformBuffer->Id, Decoration.Binding);
            }

            uint bufferIndex = 0;
            uint imageIndex = 0;
            foreach (ResourceInfo it in allResources.Map.Values)
            {
                // TODO: check if IDs is zero like elsewhere?
                if (it.Kind == ResourceKind.StructuredBufferReadOnly || it.Kind == ResourceKind.StructuredBufferReadWrite)
                {
                    api1.CompilerSetDecoration(csCompiler, it.IDs[0], Decoration.Binding, bufferIndex++);
                }
                else if (it.Kind == ResourceKind.TextureReadWrite)
                {
                    api1.CompilerSetDecoration(csCompiler, it.IDs[0], Decoration.Binding, imageIndex++);
                }
            }
        }

        byte* csText;
        api1.CompilerCompile(csCompiler, &csText).Check(context);

        CompilationResult result = new(InteropArray.FromNullTerminated(csText))
        {
            Succeeded = true,
        };
        result.Reflection.ResourceLayouts = CreateResourceLayoutArray(allResources.Map, true);

        return result;
    }

    public static CompilationResult Compile(Context* context, in CrossCompileInfo info)
    {
        if (!info.VertexShader.IsEmpty && !info.FragmentShader.IsEmpty)
        {
            return CompileVertexFragment(context, info);
        }
        else if (!info.ComputeShader.IsEmpty)
        {
            return CompileCompute(context, info);
        }

        return new CompilationResult("The given combination of shaders was not valid.");
    }

    public static CompilationResult CompileGLSLToSPIRV(in GlslCompileInfo info, CompileOptions* options)
    {
        ShaderCompiler* compiler = api2.CompilerInitialize();
        Silk.NET.Shaderc.CompilationResult* res = api2.CompileIntoSpv(
            compiler,
            info.SourceText,
            (uint)info.SourceText.Length,
            info.Kind,
            info.FileName,
            "main\0"u8,
            options);
        try
        {
            if (api2.ResultGetCompilationStatus(res) != CompilationStatus.Success)
            {
                // TODO: differentiate error types/results
                InteropArray<byte> msg = InteropArray.FromNullTerminated(api2.ResultGetErrorMessage(res));
                return new CompilationResult(msg);
            }

            InteropArray<byte> array = new(
                api2.ResultGetLength(res),
                api2.ResultGetBytes(res));

            return new CompilationResult(array) { Succeeded = true };
        }
        finally
        {
            api2.ResultRelease(res);
            api2.CompilerRelease(compiler);
        }
    }

    public static CompilationResult CrossCompile(in CrossCompileInfo info)
    {
        Context* context;
        api1.ContextCreate(&context);
        try
        {
            return Compile(context, info);
        }
        catch (Exception ex)
        {
            return new CompilationResult(ex.ToString());
        }
        finally
        {
            api1.ContextDestroy(context);
        }
    }

    public static CompilationResult CompileGlslToSpirv(in GlslCompileInfo info)
    {
        CompileOptions* options = api2.CompileOptionsInitialize();
        try
        {
            api2.CompileOptionsSetSourceLanguage(options, Silk.NET.Shaderc.SourceLanguage.Glsl);

            if (info.Debug)
            {
                api2.CompileOptionsSetGenerateDebugInfo(options);
            }
            else
            {
                api2.CompileOptionsSetOptimizationLevel(options, OptimizationLevel.Performance);
            }

            foreach (ref readonly NativeMacroDefinition macro in info.Macros)
            {
                api2.CompileOptionsAddMacroDefinition(
                    options,
                    macro.Name.Data, macro.Name.Count,
                    macro.Value.Data, macro.Value.Count);
            }

            return CompileGLSLToSPIRV(info, options);
        }
        catch (Exception ex)
        {
            return new CompilationResult(ex.ToString());
        }
        finally
        {
            api2.CompileOptionsRelease(options);
        }
    }

    private static ReadOnlySpan<VertexElementFormat> FloatFormats =>
    [
        VertexElementFormat.Float1,
        VertexElementFormat.Float1,
        VertexElementFormat.Float2,
        VertexElementFormat.Float3,
        VertexElementFormat.Float4
    ];

    private static ReadOnlySpan<VertexElementFormat> IntFormats =>
    [
        VertexElementFormat.Int1,
        VertexElementFormat.Int1,
        VertexElementFormat.Int2,
        VertexElementFormat.Int3,
        VertexElementFormat.Int4,
    ];

    private static ReadOnlySpan<VertexElementFormat> UIntFormats =>
    [
        VertexElementFormat.UInt1,
        VertexElementFormat.UInt1,
        VertexElementFormat.UInt2,
        VertexElementFormat.UInt3,
        VertexElementFormat.UInt4,
    ];

    private static void ReflectVertexInfo(CrossCompiler* compiler, Resources* resources, ref ReflectionInfo info)
    {
        uint elementCount = 0;
        ReflectedResource* resList;
        nuint resSize;
        api1.ResourcesGetResourceListForType(resources, ResourceType.StageInput, &resList, &resSize);
        for (nuint i = 0; i < resSize; i++)
        {
            ReflectedResource* input = &resList[i];

            uint location = api1.CompilerGetDecoration(compiler, input->Id, Decoration.Location);
            elementCount = Math.Max(location + 1, elementCount);
        }

        info.VertexElements = new InteropArray<NativeVertexElementDescription>(elementCount);

        for (nuint i = 0; i < resSize; i++)
        {
            ReflectedResource* input = &resList[i];

            uint location = api1.CompilerGetDecoration(compiler, input->Id, Decoration.Location);
            info.VertexElements[location].Semantic = VertexElementSemantic.TextureCoordinate;
            InteropArray<byte> name = InteropArray.FromNullTerminated(api1.CompilerGetName(compiler, input->Id));
            info.VertexElements[location].Name = name.Count == 0 ? InteropArray.ToUtf8("_" + input->Id) : name.Clone();

            CrossType* baseType = api1.CompilerGetTypeHandle(compiler, input->BaseTypeId);
            CrossType* type = api1.CompilerGetTypeHandle(compiler, input->TypeId);
            int vecsize = (int)api1.TypeGetVectorSize(baseType);
            info.VertexElements[location].Format = api1.TypeGetBasetype(baseType) switch
            {
                Basetype.FP32 => FloatFormats[vecsize],
                Basetype.Int32 => IntFormats[vecsize],
                Basetype.Uint32 => UIntFormats[vecsize],
                _ => throw new Exception("Unhandled SPIR-V vertex input data type."),
            };
        }
    }

    private static void Check(this Result result, Context* context)
    {
        if (result != Result.Success)
        {
            byte* msg = api1.ContextGetLastErrorString(context);
            throw new Exception(Util.UTF8.GetString(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(msg)));
        }
    }
}
