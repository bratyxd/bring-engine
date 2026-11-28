using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BringEngine.Assets;
using BringEngine.Core.Handles;
using BringEngine.Core.Logging;
using BringEngine.Core.Memory;
using BringEngine.Rendering.Gpu;
using SDL3;

namespace BringEngine.Rendering.Shaders;

/// <summary>
/// Cross-platform HLSL shader compiler &amp; pipeline builder powered by SDL_shadercross.
/// Compiles a single HLSL source into SPIR-V -&gt; auto-reflects bindings -&gt; builds DXIL/SPIR-V/MSL
/// SDL_GPUShader + cached Opaque / AlphaBlend / Additive / NoDepth / CullNone pipelines.
/// </summary>
public static unsafe class ShaderSystem
{
    public static void CompileBuiltinShaders(ref GpuContext ctx, string? shadersDir = null, LogState* log = null)
    {
        LogState* activeLog = log != null ? log : ctx.Log;
        if (ctx.Log == null && log != null) ctx.Log = log;

        string baseDir;
        if (shadersDir != null)
        {
            baseDir = shadersDir;
        }
        else
        {
            baseDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
            if (!Directory.Exists(baseDir))
                baseDir = Path.Combine(Directory.GetCurrentDirectory(), "Shaders");
        }

        ctx.BuiltinPbrShader = CompileFromFile(ref ctx, Path.Combine(baseDir, "pbr_mesh.hlsl"), "builtin_pbr", ShaderVertexLayout.Mesh3D, SDL.GPUCullMode.Back);
        ctx.BuiltinSpriteShader = CompileFromFile(ref ctx, Path.Combine(baseDir, "sprite_3d.hlsl"), "builtin_sprite", ShaderVertexLayout.InstancedQuad, SDL.GPUCullMode.None);
        ctx.BuiltinRig2DShader = CompileFromFile(ref ctx, Path.Combine(baseDir, "rig_2d.hlsl"), "builtin_rig_2d", ShaderVertexLayout.Rig2DMesh, SDL.GPUCullMode.None);
        ctx.BuiltinSdfTextShader = CompileFromFile(ref ctx, Path.Combine(baseDir, "sdf_text.hlsl"), "builtin_sdf_text", ShaderVertexLayout.InstancedQuad, SDL.GPUCullMode.None);
        ctx.BuiltinParticleShader = CompileFromFile(ref ctx, Path.Combine(baseDir, "particle.hlsl"), "builtin_particle", ShaderVertexLayout.InstancedQuad, SDL.GPUCullMode.None);

        if (ctx.ShadowMapSupported == 0)
        {
            if (activeLog != null)
                LogSystem.Warn(activeLog, "Directional shadow mapping disabled: D32Float DepthStencil+Sampler format unsupported on this GPU");
        }
        else
        {
            string shadowPath = Path.Combine(baseDir, "shadow_depth.hlsl");
            if (File.Exists(shadowPath))
            {
                CompileShadowVariant(ref ctx, File.ReadAllText(shadowPath), "builtin_shadow_depth",
                    out ctx.ShadowVertexShader, out ctx.ShadowFragmentShader,
                    out ctx.ShadowPipeline, out ctx.ShadowPipelineCullNone);
            }
            else if (activeLog != null)
            {
                LogSystem.Warn(activeLog, $"Builtin shadow shader missing at '{shadowPath}'; directional shadow pass disabled");
            }

            string shadowMaskPath = Path.Combine(baseDir, "shadow_depth_mask.hlsl");
            if (File.Exists(shadowMaskPath))
            {
                CompileShadowVariant(ref ctx, File.ReadAllText(shadowMaskPath), "builtin_shadow_depth_mask",
                    out ctx.ShadowMaskVertexShader, out ctx.ShadowMaskFragmentShader,
                    out ctx.ShadowMaskPipeline, out ctx.ShadowMaskPipelineCullNone);
            }
            else if (activeLog != null)
            {
                LogSystem.Warn(activeLog, $"Masked shadow shader missing at '{shadowMaskPath}'; alpha-masked shadow cutouts disabled");
            }
        }

        if (activeLog != null)
        {
            LogSystem.Info(activeLog, $"Builtin shaders compiled from '{baseDir}' (shadows={(ctx.ShadowPipeline != nint.Zero ? "ON" : "OFF")}, maskShadows={(ctx.ShadowMaskPipeline != nint.Zero ? "ON" : "OFF")})");
        }
    }

    private static void CompileShadowVariant(
        ref GpuContext ctx,
        string hlslSource,
        string debugName,
        out nint outVs,
        out nint outPs,
        out nint outPipeBack,
        out nint outPipeCullNone)
    {
        nint vs = nint.Zero, ps = nint.Zero, pipeBack = nint.Zero, pipeCullNone = nint.Zero;
        try
        {
            vs = CompileStage(ctx.Device, hlslSource, "VSMain", ShaderCross.ShaderStage.Vertex, debugName, out _, ctx.Log);
            ps = CompileStage(ctx.Device, hlslSource, "PSMain", ShaderCross.ShaderStage.Fragment, debugName, out _, ctx.Log);

            var depthState = new SDL.GPUDepthStencilState
            {
                CompareOp = SDL.GPUCompareOp.LessOrEqual,
                EnableDepthTest = true,
                EnableDepthWrite = true,
            };

            var rasterBack = new SDL.GPURasterizerState
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

            var rasterNone = rasterBack;
            rasterNone.CullMode = SDL.GPUCullMode.None;

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
                RasterizerState = rasterBack,
                DepthStencilState = depthState,
                TargetInfo = targetInfo,
            };

            pipeBack = SDL.CreateGPUGraphicsPipeline(ctx.Device, in pipeInfo);
            if (pipeBack == nint.Zero)
            {
                string err = SDL.GetError();
                if (ctx.Log != null) LogSystem.Error(ctx.Log, $"SDL_CreateGPUGraphicsPipeline({debugName}, CullBack) failed: {err}");
                throw new InvalidOperationException($"SDL_CreateGPUGraphicsPipeline({debugName}) failed: {err}");
            }

            pipeInfo.RasterizerState = rasterNone;
            pipeCullNone = SDL.CreateGPUGraphicsPipeline(ctx.Device, in pipeInfo);
            if (pipeCullNone == nint.Zero)
            {
                string err = SDL.GetError();
                if (ctx.Log != null) LogSystem.Error(ctx.Log, $"SDL_CreateGPUGraphicsPipeline({debugName}, CullNone) failed: {err}");
                throw new InvalidOperationException($"SDL_CreateGPUGraphicsPipeline({debugName} CullNone) failed: {err}");
            }

            outVs = vs;
            outPs = ps;
            outPipeBack = pipeBack;
            outPipeCullNone = pipeCullNone;
        }
        catch
        {
            if (pipeBack != nint.Zero) SDL.ReleaseGPUGraphicsPipeline(ctx.Device, pipeBack);
            if (pipeCullNone != nint.Zero) SDL.ReleaseGPUGraphicsPipeline(ctx.Device, pipeCullNone);
            if (vs != nint.Zero) SDL.ReleaseGPUShader(ctx.Device, vs);
            if (ps != nint.Zero) SDL.ReleaseGPUShader(ctx.Device, ps);
            throw;
        }
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
        if (!File.Exists(hlslFilePath))
        {
            if (ctx.Log != null) LogSystem.Error(ctx.Log, $"Shader file not found for '{shaderName}': '{hlslFilePath}'");
            throw new FileNotFoundException($"Shader file not found for '{shaderName}': '{hlslFilePath}'.", hlslFilePath);
        }
        string source = File.ReadAllText(hlslFilePath);
        try
        {
            return CompileFromSource(ref ctx, source, shaderName, layout, cullMode, vsEntry, psEntry);
        }
        catch (Exception ex)
        {
            if (ctx.Log != null) LogSystem.Error(ctx.Log, $"Shader '{shaderName}' failed compiling file '{hlslFilePath}': {ex.Message}");
            throw;
        }
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
        if (slot < 0)
        {
            if (ctx.Log != null) LogSystem.Error(ctx.Log, $"Shader slots exhausted (MaxShaders={ctx.MaxShaders}) compiling '{shaderName}'");
            throw new OutOfMemoryException("GpuContext shader slots exhausted.");
        }

        nint vs = nint.Zero, ps = nint.Zero;
        nint pipeOpaque = nint.Zero, pipeAlpha = nint.Zero, pipeAdd = nint.Zero;
        nint pipeOverlay = nint.Zero, pipeAddOverlay = nint.Zero;
        nint pipeMultiply = nint.Zero, pipeMultiplyOverlay = nint.Zero;
        nint pipeOpaqueCullNone = nint.Zero, pipeAlphaCullNone = nint.Zero;

        try
        {
            vs = CompileStage(ctx.Device, hlslSource, vsEntry, ShaderCross.ShaderStage.Vertex, shaderName, out _, ctx.Log);
            ps = CompileStage(ctx.Device, hlslSource, psEntry, ShaderCross.ShaderStage.Fragment, shaderName, out uint psSamplers, ctx.Log);

            pipeOpaque = CreatePipeline(ref ctx, vs, ps, layout, cullMode, blendMode: 0, depthTest: true, depthWrite: true);
            pipeAlpha = CreatePipeline(ref ctx, vs, ps, layout, cullMode, blendMode: 1, depthTest: true, depthWrite: false);
            pipeAdd = CreatePipeline(ref ctx, vs, ps, layout, cullMode, blendMode: 2, depthTest: true, depthWrite: false);
            pipeOverlay = CreatePipeline(ref ctx, vs, ps, layout, cullMode, blendMode: 1, depthTest: false, depthWrite: false);
            pipeAddOverlay = CreatePipeline(ref ctx, vs, ps, layout, cullMode, blendMode: 2, depthTest: false, depthWrite: false);

            if (layout == ShaderVertexLayout.Rig2DMesh)
            {
                pipeMultiply = CreatePipeline(ref ctx, vs, ps, layout, cullMode, blendMode: 3, depthTest: true, depthWrite: false);
                pipeMultiplyOverlay = CreatePipeline(ref ctx, vs, ps, layout, cullMode, blendMode: 3, depthTest: false, depthWrite: false);
            }

            if (layout == ShaderVertexLayout.Mesh3D && cullMode != SDL.GPUCullMode.None)
            {
                pipeOpaqueCullNone = CreatePipeline(ref ctx, vs, ps, layout, SDL.GPUCullMode.None, blendMode: 0, depthTest: true, depthWrite: true);
                pipeAlphaCullNone = CreatePipeline(ref ctx, vs, ps, layout, SDL.GPUCullMode.None, blendMode: 1, depthTest: true, depthWrite: false);
            }

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
                PipelineAdditiveNoDepth = pipeAddOverlay,
                PipelineMultiply = pipeMultiply,
                PipelineMultiplyNoDepth = pipeMultiplyOverlay,
                PipelineOpaqueCullNone = pipeOpaqueCullNone,
                PipelineAlphaBlendCullNone = pipeAlphaCullNone,
                Layout = layout,
                FragmentSamplerCount = (byte)(psSamplers > 255u ? 255u : psSamplers),
                Alive = 1,
            };
            return handle;
        }
        catch
        {
            if (pipeOpaque != nint.Zero) SDL.ReleaseGPUGraphicsPipeline(ctx.Device, pipeOpaque);
            if (pipeAlpha != nint.Zero) SDL.ReleaseGPUGraphicsPipeline(ctx.Device, pipeAlpha);
            if (pipeAdd != nint.Zero) SDL.ReleaseGPUGraphicsPipeline(ctx.Device, pipeAdd);
            if (pipeOverlay != nint.Zero) SDL.ReleaseGPUGraphicsPipeline(ctx.Device, pipeOverlay);
            if (pipeAddOverlay != nint.Zero) SDL.ReleaseGPUGraphicsPipeline(ctx.Device, pipeAddOverlay);
            if (pipeMultiply != nint.Zero) SDL.ReleaseGPUGraphicsPipeline(ctx.Device, pipeMultiply);
            if (pipeMultiplyOverlay != nint.Zero) SDL.ReleaseGPUGraphicsPipeline(ctx.Device, pipeMultiplyOverlay);
            if (pipeOpaqueCullNone != nint.Zero) SDL.ReleaseGPUGraphicsPipeline(ctx.Device, pipeOpaqueCullNone);
            if (pipeAlphaCullNone != nint.Zero) SDL.ReleaseGPUGraphicsPipeline(ctx.Device, pipeAlphaCullNone);
            if (vs != nint.Zero) SDL.ReleaseGPUShader(ctx.Device, vs);
            if (ps != nint.Zero) SDL.ReleaseGPUShader(ctx.Device, ps);
            throw;
        }
    }

    /// <summary>
    /// Waits for GPU idle and releases a compiled shader and all of its cached pipelines,
    /// freeing the slot and advancing its generational counter.
    /// </summary>
    public static bool ReleaseShader(ref GpuContext ctx, ShaderHandle handle)
    {
        if (ctx.Initialized == 0 || ctx.Device == nint.Zero) return false;
        if (handle.IsValid && (handle == ctx.BuiltinPbrShader ||
                               handle == ctx.BuiltinSpriteShader ||
                               handle == ctx.BuiltinRig2DShader ||
                               handle == ctx.BuiltinSdfTextShader ||
                               handle == ctx.BuiltinParticleShader))
        {
            if (ctx.Log != null)
                LogSystem.Warn(ctx.Log, $"ShaderSystem.ReleaseShader refused to release built-in shader {handle}");
            return false;
        }
        GpuShaderSlot* s = TryGetShader(in ctx, handle);
        if (s == null) return false;

        SDL.WaitForGPUIdle(ctx.Device);

        if (s->PipelineOpaque != nint.Zero) SDL.ReleaseGPUGraphicsPipeline(ctx.Device, s->PipelineOpaque);
        if (s->PipelineAlphaBlend != nint.Zero) SDL.ReleaseGPUGraphicsPipeline(ctx.Device, s->PipelineAlphaBlend);
        if (s->PipelineAdditive != nint.Zero) SDL.ReleaseGPUGraphicsPipeline(ctx.Device, s->PipelineAdditive);
        if (s->PipelineOverlayNoDepth != nint.Zero) SDL.ReleaseGPUGraphicsPipeline(ctx.Device, s->PipelineOverlayNoDepth);
        if (s->PipelineAdditiveNoDepth != nint.Zero) SDL.ReleaseGPUGraphicsPipeline(ctx.Device, s->PipelineAdditiveNoDepth);
        if (s->PipelineMultiply != nint.Zero) SDL.ReleaseGPUGraphicsPipeline(ctx.Device, s->PipelineMultiply);
        if (s->PipelineMultiplyNoDepth != nint.Zero) SDL.ReleaseGPUGraphicsPipeline(ctx.Device, s->PipelineMultiplyNoDepth);
        if (s->PipelineOpaqueCullNone != nint.Zero) SDL.ReleaseGPUGraphicsPipeline(ctx.Device, s->PipelineOpaqueCullNone);
        if (s->PipelineAlphaBlendCullNone != nint.Zero) SDL.ReleaseGPUGraphicsPipeline(ctx.Device, s->PipelineAlphaBlendCullNone);
        if (s->VertexShader != nint.Zero) SDL.ReleaseGPUShader(ctx.Device, s->VertexShader);
        if (s->FragmentShader != nint.Zero) SDL.ReleaseGPUShader(ctx.Device, s->FragmentShader);

        uint idx = handle.Index;
        *s = default;
        ctx.ShaderGens[idx] = HandlePacking.NextGeneration(ctx.ShaderGens[idx]);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static GpuShaderSlot* TryGetShader(in GpuContext ctx, ShaderHandle handle)
    {
        if (!handle.IsValid || handle.Index >= (uint)ctx.MaxShaders) return null;
        if (ctx.Shaders[handle.Index].Alive == 0 || ctx.ShaderGens[handle.Index] != handle.Generation) return null;
        return ctx.Shaders + handle.Index;
    }

    private static nint CompileStage(nint device, string hlslSource, string entryPoint, ShaderCross.ShaderStage stage, string debugName, out uint numSamplers, LogState* log)
    {
        numSamplers = 0;
        nint spirvPtr = ShaderCross.CompileSPIRVFromHLSL(hlslSource, entryPoint, stage, out nuint spirvSize);
        if (spirvPtr == nint.Zero || spirvSize == 0)
        {
            string err = SDL.GetError();
            if (log != null) LogSystem.Error(log, $"ShaderCross.CompileSPIRVFromHLSL failed for '{debugName}' ({entryPoint}): {err}");
            throw new InvalidOperationException($"ShaderCross.CompileSPIRVFromHLSL failed for '{debugName}' ({entryPoint}): {err}");
        }

        try
        {
            nint metaPtr = ShaderCross.ReflectGraphicsSPIRV(spirvPtr, spirvSize, 0);
            if (metaPtr == nint.Zero)
            {
                string err = SDL.GetError();
                if (log != null) LogSystem.Error(log, $"ShaderCross.ReflectGraphicsSPIRV failed for '{debugName}' ({entryPoint}): {err}");
                throw new InvalidOperationException($"ShaderCross.ReflectGraphicsSPIRV failed for '{debugName}' ({entryPoint}): {err}");
            }

            try
            {
                var meta = *(ShaderCross.GraphicsShaderMetadata*)metaPtr;
                var resInfo = meta.ResourceInfo;
                numSamplers = resInfo.NumSamplers;
                nint shader = ShaderCross.CompileGraphicsShaderFromSPIRV(
                    device, spirvPtr, spirvSize, entryPoint, stage, in resInfo, 0);

                if (shader == nint.Zero)
                {
                    string err = SDL.GetError();
                    if (log != null) LogSystem.Error(log, $"ShaderCross.CompileGraphicsShaderFromSPIRV failed for '{debugName}' ({entryPoint}): {err}");
                    throw new InvalidOperationException($"ShaderCross.CompileGraphicsShaderFromSPIRV failed for '{debugName}' ({entryPoint}): {err}");
                }

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
        else if (blendMode == 3)
        {
            // Alpha-safe Multiply blend: shader outputs (rgb * alpha, alpha)
            blend.EnableBlend = true;
            blend.SrcColorBlendFactor = SDL.GPUBlendFactor.DstColor;
            blend.DstColorBlendFactor = SDL.GPUBlendFactor.OneMinusSrcAlpha;
            blend.ColorBlendOp = SDL.GPUBlendOp.Add;
            blend.SrcAlphaBlendFactor = SDL.GPUBlendFactor.One;
            blend.DstAlphaBlendFactor = SDL.GPUBlendFactor.OneMinusSrcAlpha;
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
            Pitch = layout == ShaderVertexLayout.Rig2DMesh ? (uint)sizeof(Rig2DVertexGpu) : (uint)sizeof(Vertex3D),
            InputRate = SDL.GPUVertexInputRate.Vertex,
        };

        SDL.GPUVertexAttribute* attrs = stackalloc SDL.GPUVertexAttribute[7];
        SDL.GPUVertexInputState vertInput = default;
        if (layout == ShaderVertexLayout.Mesh3D)
        {
            attrs[0] = new SDL.GPUVertexAttribute { Location = 0, BufferSlot = 0, Format = SDL.GPUVertexElementFormat.Float3, Offset = 0 };
            attrs[1] = new SDL.GPUVertexAttribute { Location = 1, BufferSlot = 0, Format = SDL.GPUVertexElementFormat.Float3, Offset = 12 };
            attrs[2] = new SDL.GPUVertexAttribute { Location = 2, BufferSlot = 0, Format = SDL.GPUVertexElementFormat.Float2, Offset = 24 };
            attrs[3] = new SDL.GPUVertexAttribute { Location = 3, BufferSlot = 0, Format = SDL.GPUVertexElementFormat.Ubyte4Norm, Offset = 32 };
            attrs[4] = new SDL.GPUVertexAttribute { Location = 4, BufferSlot = 0, Format = SDL.GPUVertexElementFormat.Uint, Offset = 36 };
            attrs[5] = new SDL.GPUVertexAttribute { Location = 5, BufferSlot = 0, Format = SDL.GPUVertexElementFormat.Float4, Offset = 40 };
            attrs[6] = new SDL.GPUVertexAttribute { Location = 6, BufferSlot = 0, Format = SDL.GPUVertexElementFormat.Float2, Offset = 56 };
            vertInput = new SDL.GPUVertexInputState
            {
                NumVertexBuffers = 1,
                VertexBufferDescriptions = (nint)(&vBufDesc),
                NumVertexAttributes = 7,
                VertexAttributes = (nint)attrs,
            };
        }
        else if (layout == ShaderVertexLayout.Rig2DMesh)
        {
            attrs[0] = new SDL.GPUVertexAttribute { Location = 0, BufferSlot = 0, Format = SDL.GPUVertexElementFormat.Float3, Offset = 0 };
            attrs[1] = new SDL.GPUVertexAttribute { Location = 1, BufferSlot = 0, Format = SDL.GPUVertexElementFormat.Float2, Offset = 12 };
            attrs[2] = new SDL.GPUVertexAttribute { Location = 2, BufferSlot = 0, Format = SDL.GPUVertexElementFormat.Ubyte4Norm, Offset = 20 };
            attrs[3] = new SDL.GPUVertexAttribute { Location = 3, BufferSlot = 0, Format = SDL.GPUVertexElementFormat.Ubyte4Norm, Offset = 24 };
            attrs[4] = new SDL.GPUVertexAttribute { Location = 4, BufferSlot = 0, Format = SDL.GPUVertexElementFormat.Float, Offset = 28 };
            vertInput = new SDL.GPUVertexInputState
            {
                NumVertexBuffers = 1,
                VertexBufferDescriptions = (nint)(&vBufDesc),
                NumVertexAttributes = 5,
                VertexAttributes = (nint)attrs,
            };
        }

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
