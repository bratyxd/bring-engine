using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BringEngine.Assets;
using BringEngine.Core.Handles;
using BringEngine.Core.Memory;
using BringEngine.Rendering.Gpu;
using SDL3;

namespace BringEngine.Rendering.Shaders;

/// <summary>
/// Cross-platform HLSL shader compiler & pipeline builder powered by SDL_shadercross.
/// Compiles a single HLSL source into SPIR-V -> auto-reflects bindings -> builds DXIL/SPIR-V/MSL
/// SDL_GPUShader + cached Opaque / AlphaBlend / Additive / NoDepth pipelines.
/// </summary>
public static unsafe class ShaderSystem
{
    public static void CompileBuiltinShaders(ref GpuContext ctx, string? shadersDir = null)
    {
        string baseDir = shadersDir ?? Path.Combine(AppContext.BaseDirectory, "Shaders");
        if (!Directory.Exists(baseDir))
            baseDir = Path.Combine(Directory.GetCurrentDirectory(), "Shaders");

        ctx.BuiltinPbrShader = CompileFromFile(ref ctx, Path.Combine(baseDir, "pbr_mesh.hlsl"), "builtin_pbr", ShaderVertexLayout.Mesh3D, SDL.GPUCullMode.Back);
        ctx.BuiltinSpriteShader = CompileFromFile(ref ctx, Path.Combine(baseDir, "sprite_3d.hlsl"), "builtin_sprite", ShaderVertexLayout.InstancedQuad, SDL.GPUCullMode.None);
        ctx.BuiltinSdfTextShader = CompileFromFile(ref ctx, Path.Combine(baseDir, "sdf_text.hlsl"), "builtin_sdf_text", ShaderVertexLayout.InstancedQuad, SDL.GPUCullMode.None);
        ctx.BuiltinParticleShader = CompileFromFile(ref ctx, Path.Combine(baseDir, "particle.hlsl"), "builtin_particle", ShaderVertexLayout.InstancedQuad, SDL.GPUCullMode.None);

        string shadowPath = Path.Combine(baseDir, "shadow_depth.hlsl");
        if (ctx.ShadowMapSupported != 0 && File.Exists(shadowPath))
            CompileShadowPipeline(ref ctx, File.ReadAllText(shadowPath));
    }

    private static void CompileShadowPipeline(ref GpuContext ctx, string hlslSource)
    {
        nint vs = CompileStage(ctx.Device, hlslSource, "VSMain", ShaderCross.ShaderStage.Vertex, "builtin_shadow_depth", out _);
        nint ps = CompileStage(ctx.Device, hlslSource, "PSMain", ShaderCross.ShaderStage.Fragment, "builtin_shadow_depth", out _);

        var depthState = new SDL.GPUDepthStencilState
        {
            CompareOp = SDL.GPUCompareOp.LessOrEqual,
            EnableDepthTest = true,
            EnableDepthWrite = true,
        };

        var raster = new SDL.GPURasterizerState
        {
            FillMode = SDL.GPUFillMode.Fill,
            CullMode = SDL.GPUCullMode.Back,
            FrontFace = SDL.GPUFrontFace.CounterClockwise,
            EnableDepthClip = true,
            EnableDepthBias = true,
            DepthBiasConstantFactor = 1.25f,
            DepthBiasSlopeFactor = 1.75f,
            DepthBiasClamp = 0.0f,
        };

        var targetInfo = new SDL.GPUGraphicsPipelineTargetInfo
        {
            NumColorTargets = 0,
            ColorTargetDescriptions = nint.Zero,
            DepthStencilFormat = ctx.DepthFormat,
            HasDepthStencilTarget = true,
        };

        var vBufDesc = new SDL.GPUVertexBufferDescription
        {
            Slot = 0,
            Pitch = (uint)sizeof(Vertex3D),
            InputRate = SDL.GPUVertexInputRate.Vertex,
        };

        SDL.GPUVertexAttribute* attrs = stackalloc SDL.GPUVertexAttribute[7];
        attrs[0] = new SDL.GPUVertexAttribute { Location = 0, BufferSlot = 0, Format = SDL.GPUVertexElementFormat.Float3, Offset = 0 };
        attrs[1] = new SDL.GPUVertexAttribute { Location = 1, BufferSlot = 0, Format = SDL.GPUVertexElementFormat.Float3, Offset = 12 };
        attrs[2] = new SDL.GPUVertexAttribute { Location = 2, BufferSlot = 0, Format = SDL.GPUVertexElementFormat.Float2, Offset = 24 };
        attrs[3] = new SDL.GPUVertexAttribute { Location = 3, BufferSlot = 0, Format = SDL.GPUVertexElementFormat.Ubyte4Norm, Offset = 32 };
        attrs[4] = new SDL.GPUVertexAttribute { Location = 4, BufferSlot = 0, Format = SDL.GPUVertexElementFormat.Uint, Offset = 36 };
        attrs[5] = new SDL.GPUVertexAttribute { Location = 5, BufferSlot = 0, Format = SDL.GPUVertexElementFormat.Float4, Offset = 40 };
        attrs[6] = new SDL.GPUVertexAttribute { Location = 6, BufferSlot = 0, Format = SDL.GPUVertexElementFormat.Float2, Offset = 56 };

        var vertInput = new SDL.GPUVertexInputState
        {
            NumVertexBuffers = 1,
            VertexBufferDescriptions = (nint)(&vBufDesc),
            NumVertexAttributes = 7,
            VertexAttributes = (nint)attrs,
        };

        var pipeInfo = new SDL.GPUGraphicsPipelineCreateInfo
        {
            VertexShader = vs,
            FragmentShader = ps,
            VertexInputState = vertInput,
            PrimitiveType = SDL.GPUPrimitiveType.TriangleList,
            RasterizerState = raster,
            DepthStencilState = depthState,
            TargetInfo = targetInfo,
        };

        nint pipe = SDL.CreateGPUGraphicsPipeline(ctx.Device, in pipeInfo);
        if (pipe == nint.Zero)
        {
            SDL.ReleaseGPUShader(ctx.Device, vs);
            SDL.ReleaseGPUShader(ctx.Device, ps);
            throw new InvalidOperationException($"SDL_CreateGPUGraphicsPipeline(shadow_depth) failed: {SDL.GetError()}");
        }

        ctx.ShadowVertexShader = vs;
        ctx.ShadowFragmentShader = ps;
        ctx.ShadowPipeline = pipe;
    }

    public static ShaderHandle CompileFromFile(
        ref GpuContext ctx,
        string hlslFilePath,
        string shaderName,
        ShaderVertexLayout layout = ShaderVertexLayout.Mesh3D,
        SDL.GPUCullMode cullMode = SDL.GPUCullMode.Back,
        string vsEntry = "VSMain",
        string psEntry = "PSMain")
    {
        string source = File.ReadAllText(hlslFilePath);
        return CompileFromSource(ref ctx, source, shaderName, layout, cullMode, vsEntry, psEntry);
    }

    public static ShaderHandle CompileFromSource(
        ref GpuContext ctx,
        string hlslSource,
        string shaderName,
        ShaderVertexLayout layout = ShaderVertexLayout.Mesh3D,
        SDL.GPUCullMode cullMode = SDL.GPUCullMode.Back,
        string vsEntry = "VSMain",
        string psEntry = "PSMain")
    {
        int slot = -1;
        for (int i = 0; i < ctx.MaxShaders; i++)
        {
            if (ctx.Shaders[i].Alive == 0) { slot = i; break; }
        }
        if (slot < 0) throw new OutOfMemoryException("GpuContext shader slots exhausted.");

        nint vs = CompileStage(ctx.Device, hlslSource, vsEntry, ShaderCross.ShaderStage.Vertex, shaderName, out _);
        nint ps = CompileStage(ctx.Device, hlslSource, psEntry, ShaderCross.ShaderStage.Fragment, shaderName, out uint psSamplers);

        nint pipeOpaque = CreatePipeline(ref ctx, vs, ps, layout, cullMode, blendMode: 0, depthTest: true, depthWrite: true);
        nint pipeAlpha = CreatePipeline(ref ctx, vs, ps, layout, cullMode, blendMode: 1, depthTest: true, depthWrite: false);
        nint pipeAdd = CreatePipeline(ref ctx, vs, ps, layout, cullMode, blendMode: 2, depthTest: true, depthWrite: false);
        nint pipeOverlay = CreatePipeline(ref ctx, vs, ps, layout, cullMode, blendMode: 1, depthTest: false, depthWrite: false);

        var handle = ShaderHandle.Create((uint)slot, ctx.ShaderGens[slot]);
        ctx.Shaders[slot] = new GpuShaderSlot
        {
            Handle = handle,
            Name = StringId.Hash(shaderName),
            VertexShader = vs,
            FragmentShader = ps,
            PipelineOpaque = pipeOpaque,
            PipelineAlphaBlend = pipeAlpha,
            PipelineAdditive = pipeAdd,
            PipelineOverlayNoDepth = pipeOverlay,
            Layout = layout,
            FragmentSamplerCount = (byte)(psSamplers > 255u ? 255u : psSamplers),
            Alive = 1,
        };
        return handle;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static GpuShaderSlot* TryGetShader(in GpuContext ctx, ShaderHandle handle)
    {
        if (!handle.IsValid || handle.Index >= (uint)ctx.MaxShaders) return null;
        if (ctx.Shaders[handle.Index].Alive == 0 || ctx.ShaderGens[handle.Index] != handle.Generation) return null;
        return ctx.Shaders + handle.Index;
    }

    private static nint CompileStage(nint device, string hlslSource, string entryPoint, ShaderCross.ShaderStage stage, string debugName, out uint numSamplers)
    {
        numSamplers = 0;
        nint spirvPtr = ShaderCross.CompileSPIRVFromHLSL(hlslSource, entryPoint, stage, out nuint spirvSize);
        if (spirvPtr == nint.Zero || spirvSize == 0)
            throw new InvalidOperationException($"ShaderCross.CompileSPIRVFromHLSL failed for '{debugName}' ({entryPoint}): {SDL.GetError()}");

        try
        {
            nint metaPtr = ShaderCross.ReflectGraphicsSPIRV(spirvPtr, spirvSize, 0);
            if (metaPtr == nint.Zero)
                throw new InvalidOperationException($"ShaderCross.ReflectGraphicsSPIRV failed for '{debugName}' ({entryPoint}): {SDL.GetError()}");

            try
            {
                var meta = *(ShaderCross.GraphicsShaderMetadata*)metaPtr;
                var resInfo = meta.ResourceInfo;
                numSamplers = resInfo.NumSamplers;
                nint shader = ShaderCross.CompileGraphicsShaderFromSPIRV(
                    device, spirvPtr, spirvSize, entryPoint, stage, in resInfo, 0);

                if (shader == nint.Zero)
                    throw new InvalidOperationException($"ShaderCross.CompileGraphicsShaderFromSPIRV failed for '{debugName}' ({entryPoint}): {SDL.GetError()}");

                return shader;
            }
            finally
            {
                SDL.Free(metaPtr);
            }
        }
        finally
        {
            SDL.Free(spirvPtr);
        }
    }

    private static nint CreatePipeline(
        ref GpuContext ctx,
        nint vs,
        nint ps,
        ShaderVertexLayout layout,
        SDL.GPUCullMode cullMode,
        int blendMode, // 0 = opaque, 1 = alpha blend, 2 = additive
        bool depthTest,
        bool depthWrite)
    {
        var blend = new SDL.GPUColorTargetBlendState();
        if (blendMode == 1)
        {
            blend.EnableBlend = true;
            blend.SrcColorBlendFactor = SDL.GPUBlendFactor.SrcAlpha;
            blend.DstColorBlendFactor = SDL.GPUBlendFactor.OneMinusSrcAlpha;
            blend.ColorBlendOp = SDL.GPUBlendOp.Add;
            blend.SrcAlphaBlendFactor = SDL.GPUBlendFactor.One;
            blend.DstAlphaBlendFactor = SDL.GPUBlendFactor.OneMinusSrcAlpha;
            blend.AlphaBlendOp = SDL.GPUBlendOp.Add;
        }
        else if (blendMode == 2)
        {
            blend.EnableBlend = true;
            blend.SrcColorBlendFactor = SDL.GPUBlendFactor.SrcAlpha;
            blend.DstColorBlendFactor = SDL.GPUBlendFactor.One;
            blend.ColorBlendOp = SDL.GPUBlendOp.Add;
            blend.SrcAlphaBlendFactor = SDL.GPUBlendFactor.One;
            blend.DstAlphaBlendFactor = SDL.GPUBlendFactor.One;
            blend.AlphaBlendOp = SDL.GPUBlendOp.Add;
        }

        var colorTarget = new SDL.GPUColorTargetDescription
        {
            Format = ctx.SwapchainFormat,
            BlendState = blend,
        };

        var depthState = new SDL.GPUDepthStencilState
        {
            CompareOp = SDL.GPUCompareOp.LessOrEqual,
            EnableDepthTest = depthTest,
            EnableDepthWrite = depthWrite,
        };

        var raster = new SDL.GPURasterizerState
        {
            FillMode = SDL.GPUFillMode.Fill,
            CullMode = cullMode,
            FrontFace = SDL.GPUFrontFace.CounterClockwise,
            EnableDepthClip = true,
        };

        var targetInfo = new SDL.GPUGraphicsPipelineTargetInfo
        {
            NumColorTargets = 1,
            ColorTargetDescriptions = (nint)(&colorTarget),
            DepthStencilFormat = ctx.DepthFormat,
            HasDepthStencilTarget = true,
        };

        var vBufDesc = new SDL.GPUVertexBufferDescription
        {
            Slot = 0,
            Pitch = (uint)sizeof(Vertex3D),
            InputRate = SDL.GPUVertexInputRate.Vertex,
        };

        SDL.GPUVertexAttribute* attrs = stackalloc SDL.GPUVertexAttribute[7];
        attrs[0] = new SDL.GPUVertexAttribute { Location = 0, BufferSlot = 0, Format = SDL.GPUVertexElementFormat.Float3, Offset = 0 };
        attrs[1] = new SDL.GPUVertexAttribute { Location = 1, BufferSlot = 0, Format = SDL.GPUVertexElementFormat.Float3, Offset = 12 };
        attrs[2] = new SDL.GPUVertexAttribute { Location = 2, BufferSlot = 0, Format = SDL.GPUVertexElementFormat.Float2, Offset = 24 };
        attrs[3] = new SDL.GPUVertexAttribute { Location = 3, BufferSlot = 0, Format = SDL.GPUVertexElementFormat.Ubyte4Norm, Offset = 32 };
        attrs[4] = new SDL.GPUVertexAttribute { Location = 4, BufferSlot = 0, Format = SDL.GPUVertexElementFormat.Uint, Offset = 36 };
        attrs[5] = new SDL.GPUVertexAttribute { Location = 5, BufferSlot = 0, Format = SDL.GPUVertexElementFormat.Float4, Offset = 40 };
        attrs[6] = new SDL.GPUVertexAttribute { Location = 6, BufferSlot = 0, Format = SDL.GPUVertexElementFormat.Float2, Offset = 56 };

        var vertInput = layout == ShaderVertexLayout.Mesh3D
            ? new SDL.GPUVertexInputState
            {
                NumVertexBuffers = 1,
                VertexBufferDescriptions = (nint)(&vBufDesc),
                NumVertexAttributes = 7,
                VertexAttributes = (nint)attrs,
            }
            : default;

        var pipeInfo = new SDL.GPUGraphicsPipelineCreateInfo
        {
            VertexShader = vs,
            FragmentShader = ps,
            VertexInputState = vertInput,
            PrimitiveType = SDL.GPUPrimitiveType.TriangleList,
            RasterizerState = raster,
            DepthStencilState = depthState,
            TargetInfo = targetInfo,
        };

        nint pipe = SDL.CreateGPUGraphicsPipeline(ctx.Device, in pipeInfo);
        if (pipe == nint.Zero)
            throw new InvalidOperationException($"SDL_CreateGPUGraphicsPipeline failed: {SDL.GetError()}");
        return pipe;
    }
}
