using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Shaderc;
using Silk.NET.SPIRV;
using Silk.NET.SPIRV.Cross;

namespace Veldrid.SPIRV;

using CrossCompiler = Silk.NET.SPIRV.Cross.Compiler;
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
        
        // TODO: what's this for?
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
        bool image = false,
        bool storage = false)
    {
        ReflectedResource* resourceList;
        nuint resource_size;
        api1.ResourcesGetResourceListForType(resources, type, &resourceList, &resource_size);

        for (nuint i = 0; i < resource_size; i++)
        {
            ReflectedResource res = resourceList[i];
            ResourceKind kind = ClassifyResource(compiler, res, image, storage);
            BindingInfo bi = new(
                api1.CompilerGetDecoration(compiler, res.Id, Decoration.DescriptorSet),
                api1.CompilerGetDecoration(compiler, res.Id, Decoration.Binding)
            );

            ResourceInfo ri = new();
            if (normalizeResourceNames)
            {
                InteropArray<byte> name = InteropArray.ToUtf8($"vdspv_{bi.Set}_{bi.Binding}");
                if (kind == ResourceKind.UniformBuffer)
                {
                    api1.CompilerSetName(compiler, res.BaseTypeId, name.Data);
                }
                else
                {
                    api1.CompilerSetName(compiler, res.Id, name.Data);
                }
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
                string msg = $"The same binding slot ({bi.Set}, {bi.Binding}) was used by multiple distinct resources. First resource: {InteropArray.ToString(actualRi.Name)}. Second resource: {InteropArray.ToString(ri.Name)}";
                throw new Exception(msg);
            }

            actualRi.IDs[idIndex] = res.Id;
            if (actualRi.Kind != kind)
            {
                string msg = $"The same binding slot ({bi.Set}, {bi.Binding}) was used by multiple resources with incompatible types: \"{actualRi.Kind}\" and \"{kind}\".";
                throw new Exception(msg);
            }
        }
    }


    private static uint GetResourceIndex(
        CrossCompileTarget target,
        ResourceKind resourceKind,
        ref uint bufferIndex,
        ref uint textureIndex,
        ref uint uavIndex,
        ref uint samplerIndex)
    {
        switch (resourceKind)
        {
            case ResourceKind.UniformBuffer:
                return bufferIndex++;

            case ResourceKind.StructuredBufferReadWrite:
                if (target == CrossCompileTarget.MSL)
                {
                    return bufferIndex++;
                }
                else
                {
                    return uavIndex++;
                }

            case ResourceKind.TextureReadWrite:
                if (target == CrossCompileTarget.MSL)
                {
                    return textureIndex++;
                }
                else
                {
                    return uavIndex++;
                }

            case ResourceKind.TextureReadOnly:
                return textureIndex++;

            case ResourceKind.StructuredBufferReadOnly:
                if (target == CrossCompileTarget.MSL)
                {
                    return bufferIndex++;
                }
                else
                {
                    return textureIndex++;
                }

            case ResourceKind.Sampler:
                return samplerIndex++;

            default:
                throw new ArgumentException("Invalid ResourceKind.");
        }
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
                if (info.ComputeShader.Count > 0)
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
        Silk.NET.SPIRV.Cross.SpecializationConstant* specConstants;
        nuint num_constants;
        api1.CompilerGetSpecializationConstants(compiler, &specConstants, &num_constants);

        for (nuint i = 0; i < info.Specializations.Count; i++)
        {
            uint constID = info.Specializations[i].ID;
            uint varID = 0;

            for (nuint j = 0; j < num_constants; j++)
            {
                var constant = specConstants[j];
                if (constant.ConstantId == constID)
                {
                    varID = constant.Id;
                }
            }

            if (varID != 0)
            {
                var constVar = api1.CompilerGetConstantHandle(compiler, varID);
                api1.ConstantSetScalarU64(constVar, 0, 0, info.Specializations[i].Data);
            }
        }
    }

    private static InteropArray<NativeResourceLayoutDescription> CreateResourceLayoutArray(
        Dictionary<BindingInfo, ResourceInfo> resources,
        bool compute)
    {
        List<uint> setSizes = new();
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
            ret[i].ResourceElements = new(setSizes[i]);
            for (int j = 0; j < setSizes[i]; j++)
            {
                ret[i].ResourceElements[j].Name = new InteropArray<byte>();
                ret[i].ResourceElements[j].Kind = ResourceKind.UniformBuffer;
                ret[i].ResourceElements[j].Stages = ShaderStages.None;
                ret[i].ResourceElements[j].Options = ResourceLayoutElementOptions.Unused;
            }
        }

        foreach (KeyValuePair<BindingInfo, ResourceInfo> it in resources)
        {
            ShaderStages stages = ShaderStages.None;
            if (it.Value.IDs[0] != 0)
            {
                if (compute)
                {
                    stages |= ShaderStages.Compute;
                }
                else
                {
                    stages |= ShaderStages.Vertex;
                }
            }
            if (it.Value.IDs[1] != 0)
            {
                stages |= ShaderStages.Fragment;
            }

            ret[it.Key.Set].ResourceElements[it.Key.Binding].Name = it.Value.Name;
            ret[it.Key.Set].ResourceElements[it.Key.Binding].Kind = it.Value.Kind;
            ret[it.Key.Set].ResourceElements[it.Key.Binding].Stages = stages;
            ret[it.Key.Set].ResourceElements[it.Key.Binding].Options = 0;
        }

        return ret;
    }

    private static CompilationResult CompileVertexFragment(in CrossCompileInfo info)
    {
        Context* context;
        api1.ContextCreate(&context);

        ParsedIr* vsBytes;
        api1.ContextParseSpirv(context, info.VertexShader.Data, info.VertexShader.Count, &vsBytes).Check(context);
        CrossCompiler* vsCompiler = GetCompiler(context, vsBytes, info);

        ParsedIr* fsBytes;
        api1.ContextParseSpirv(context, info.FragmentShader.Data, info.FragmentShader.Count, &fsBytes).Check(context);
        CrossCompiler* fsCompiler = GetCompiler(context, fsBytes, info);

        SetSpecializations(vsCompiler, info);
        SetSpecializations(fsCompiler, info);

        Resources* vsResources;
        api1.CompilerCreateShaderResources(vsCompiler, &vsResources);
        Resources* fsResources;
        api1.CompilerCreateShaderResources(fsCompiler, &fsResources);

        Dictionary<BindingInfo, ResourceInfo> allResources = new();

        AddResources(vsResources, ResourceType.UniformBuffer, vsCompiler, allResources, 0, info.NormalizeResourceNames);
        AddResources(vsResources, ResourceType.StorageBuffer, vsCompiler, allResources, 0, info.NormalizeResourceNames, false, true);
        AddResources(vsResources, ResourceType.SeparateImage, vsCompiler, allResources, 0, info.NormalizeResourceNames, true, false);
        AddResources(vsResources, ResourceType.StorageImage, vsCompiler, allResources, 0, info.NormalizeResourceNames, true, true);
        AddResources(vsResources, ResourceType.SeparateSamplers, vsCompiler, allResources, 0, info.NormalizeResourceNames);

        AddResources(fsResources, ResourceType.UniformBuffer, fsCompiler, allResources, 1, info.NormalizeResourceNames);
        AddResources(fsResources, ResourceType.StorageBuffer, fsCompiler, allResources, 1, info.NormalizeResourceNames, false, true);
        AddResources(fsResources, ResourceType.SeparateImage, fsCompiler, allResources, 1, info.NormalizeResourceNames, true, false);
        AddResources(fsResources, ResourceType.StorageImage, fsCompiler, allResources, 1, info.NormalizeResourceNames, true, true);
        AddResources(fsResources, ResourceType.SeparateSamplers, fsCompiler, allResources, 1, info.NormalizeResourceNames);

        if (info.Target == CrossCompileTarget.HLSL || info.Target == CrossCompileTarget.MSL)
        {
            uint bufferIndex = 0;
            uint textureIndex = 0;
            uint uavIndex = 0;
            uint samplerIndex = 0;
            foreach (ResourceInfo it in allResources.Values)
            {
                uint index = GetResourceIndex(
                    info.Target, it.Kind, ref bufferIndex, ref textureIndex, ref uavIndex, ref samplerIndex);

                uint vsID = it.IDs[0];
                if (vsID != 0)
                {
                    api1.CompilerSetDecoration(vsCompiler, vsID, Silk.NET.SPIRV.Decoration.Binding, index);
                }

                uint fsID = it.IDs[1];
                if (fsID != 0)
                {
                    api1.CompilerSetDecoration(fsCompiler, fsID, Silk.NET.SPIRV.Decoration.Binding, index);
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
            api1.CompilerBuildDummySamplerForCombinedImages(vsCompiler, &fsId);
            api1.CompilerBuildCombinedImageSamplers(vsCompiler);

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
                using var newName = InteropArray.ToUtf8($"vdspv_fsin{location}");
                api1.CompilerSetName(vsCompiler, output->Id, newName.Data);
            }

            api1.ResourcesGetResourceListForType(vsResources, ResourceType.StageInput, &resList, &resSize);
            for (nuint i = 0; i < resSize; i++)
            {
                ReflectedResource* input = &resList[i];

                uint location = api1.CompilerGetDecoration(fsCompiler, input->Id, Decoration.Location);
                using var newName = InteropArray.ToUtf8($"vdspv_fsin{location}");
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
            foreach (var it in allResources.Values)
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
        api1.CompilerCompile(fsCompiler, &fsText);
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

        CompilationResult result = new CompilationResult();
        result.Succeeded = true;

        result.DataBuffers = new InteropArray<InteropArray<byte>>(2);
        result.DataBuffers[0] = InteropArray.ToUtf8(vsStr);
        result.DataBuffers[1] = InteropArray.ToUtf8(fsStr);

        ReflectVertexInfo(vsCompiler, vsResources, ref result.Reflection);
        result.Reflection.ResourceLayouts = CreateResourceLayoutArray(allResources, false);

        //delete vsCompiler;
        //delete fsCompiler;

        return result;
    }

    private static CompilationResult CompileCompute(in CrossCompileInfo info)
    {
        Context* context;
        api1.ContextCreate(&context);

        ParsedIr* csBytes;
        api1.ContextParseSpirv(context, info.ComputeShader.Data, info.ComputeShader.Count, &csBytes).Check(context);
        CrossCompiler* csCompiler = GetCompiler(context, csBytes, info);

        SetSpecializations(csCompiler, info);

        Resources* csResources;
        api1.CompilerCreateShaderResources(csCompiler, &csResources);

        Dictionary<BindingInfo, ResourceInfo> allResources = new();

        AddResources(csResources, ResourceType.UniformBuffer, csCompiler, allResources, 0, info.NormalizeResourceNames);
        AddResources(csResources, ResourceType.StorageBuffer, csCompiler, allResources, 0, info.NormalizeResourceNames, false, true);
        AddResources(csResources, ResourceType.SeparateImage, csCompiler, allResources, 0, info.NormalizeResourceNames, true, false);
        AddResources(csResources, ResourceType.StorageImage, csCompiler, allResources, 0, info.NormalizeResourceNames, true, true);
        AddResources(csResources, ResourceType.SeparateSamplers, csCompiler, allResources, 0, info.NormalizeResourceNames);

        if (info.Target == CrossCompileTarget.HLSL || info.Target == CrossCompileTarget.MSL)
        {
            uint bufferIndex = 0;
            uint textureIndex = 0;
            uint uavIndex = 0;
            uint samplerIndex = 0;
            foreach (ResourceInfo it in allResources.Values)
            {
                uint index = GetResourceIndex(info.Target, it.Kind, ref bufferIndex, ref textureIndex, ref uavIndex, ref samplerIndex);

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
            foreach (ResourceInfo it in allResources.Values)
            {
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

        CompilationResult result = new(InteropArray.FromNullTerminated(csText).Clone())
        {
            Succeeded = true,
        };
        result.Reflection.ResourceLayouts = CreateResourceLayoutArray(allResources, true);

        return result;
    }

    public static CompilationResult Compile(in CrossCompileInfo info)
    {
        if (info.VertexShader.Count > 0 && info.FragmentShader.Count > 0)
        {
            return CompileVertexFragment(info);
        }
        else if (info.ComputeShader.Count > 0)
        {
            return CompileCompute(info);
        }

        return new CompilationResult("The given combination of shaders was not valid.");
    }

    /*
    std::vector<uint32_t> ReadFile(std::string path)
    {
        std::ifstream is(path, std::ios::binary | std::ios::in | std::ios::ate);
        size_t size = is.tellg();
        is.seekg(0, std::ios::beg);
        char *shaderCode = new char[size];
        is.read(shaderCode, size);
        is.close();

        std::vector<uint32_t> ret(size / 4);
        memcpy(ret.data(), shaderCode, size);

        delete[] shaderCode;
        return ret;
    }

    void WriteToFile(const std::string &path, const std::string &text)
    {
        auto outFile = std::ofstream(path);
        outFile << text;
        outFile.close();
    }
    */

    public static CompilationResult CompileGLSLToSPIRV(in GlslCompileInfo info, CompileOptions* options)
    {
        ShaderCompiler* compiler = api2.CompilerInitialize();
        Silk.NET.Shaderc.CompilationResult* res = api2.CompileIntoSpv(
            compiler,
            info.SourceText.Data,
            info.SourceText.Count,
            info.Kind,
            info.FileName.Data,
            "main\0"u8,
            options);

        if (api2.ResultGetCompilationStatus(res) != CompilationStatus.Success)
        {
            InteropArray<byte> msg = InteropArray.FromNullTerminated(api2.ResultGetErrorMessage(res));
            return new CompilationResult(msg.Clone());
        }

        InteropArray<byte> array = new(
            api2.ResultGetLength(res),
            api2.ResultGetBytes(res));

        return new CompilationResult(array.Clone()) { Succeeded = true };

        //api2.ResultRelease(res);
        //api2.CompilerRelease(compiler);
    }

    public static CompilationResult CrossCompile(in CrossCompileInfo info)
    {
        try
        {
            return Compile(info);
        }
        catch (Exception ex)
        {
            return new CompilationResult(ex.ToString());
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

            for (uint i = 0; i < info.Macros.Count; i++)
            {
                ref NativeMacroDefinition macro = ref info.Macros[i];
                api2.CompileOptionsAddMacroDefinition(
                    options,
                    macro.Name.Data, macro.Name.Count,
                    macro.Value.Data, macro.Value.Count);
            }

            return CompileGLSLToSPIRV(info, options);
        }
        catch (Exception e)
        {
            return new CompilationResult(e.ToString());
        }
        //finally
        //{
        //    api2.CompileOptionsRelease(options);
        //}
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
            if (name.Count == 0)
            {
                info.VertexElements[location].Name = InteropArray.ToUtf8("_" + input->Id);
            }
            else
            {
                info.VertexElements[location].Name = name.Clone();
            }

            CrossType* baseType = api1.CompilerGetTypeHandle(compiler, input->BaseTypeId);
            CrossType* type = api1.CompilerGetTypeHandle(compiler, input->TypeId);
            int vecsize = (int)api1.TypeGetVectorSize(baseType);
            switch (api1.TypeGetBasetype(baseType))
            {
                case Basetype.FP32:
                    info.VertexElements[location].Format = FloatFormats[vecsize];
                    break;
                case Basetype.Int32:
                    info.VertexElements[location].Format = IntFormats[vecsize];
                    break;
                case Basetype.Uint32:
                    info.VertexElements[location].Format = UIntFormats[vecsize];
                    break;
                default:
                    throw new Exception("Unhandled SPIR-V vertex input data type.");
            }
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
