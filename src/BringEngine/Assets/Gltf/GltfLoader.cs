using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using BringEngine.Assets.Textures;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Simulation.Animation;
using BringEngine.Simulation.RenderData;
using BringEngine.Simulation.World;

namespace BringEngine.Assets.Gltf;

/// <summary>
/// First-class glTF 2.0 / .glb loader using zero-reflection Utf8JsonReader.
/// 100% NativeAOT safe, zero OOP object graphs, allocates all asset memory inside BundleArena.
/// Supports: .glb & .gltf, base64/external buffers, PBR materials, embedded/external images,
/// node hierarchies (TRS & Matrix), skeletal skins, and Step/Linear/CubicSpline animations.
/// </summary>
public static unsafe class GltfLoader
{
    private const uint GlbMagic = 0x46546C67u;   // "glTF"
    private const uint ChunkJson = 0x4E4F534Au;  // "JSON"
    private const uint ChunkBin = 0x004E4942u;   // "BIN\0"

    private struct RawBuffer { public byte* Ptr; public int Length; }
    private struct RawBufferView { public int Buffer; public int ByteOffset; public int ByteLength; public int ByteStride; }
    private struct RawAccessor { public int BufferView; public int ByteOffset; public int ComponentType; public int Count; public byte TypeComponents; public byte Normalized; }
    private struct RawImage { public int BufferView; public FixedString256 Uri; }
    private struct RawTexture { public int Source; }
    private struct RawMat
    {
        public ColorF BaseColor;
        public int BaseColorTex;
        public float Metallic;
        public float Roughness;
        public Vec3 Emissive;
        public MaterialAlphaMode AlphaMode;
        public float AlphaCutoff;
        public byte DoubleSided;
        public FixedString64 Name;
    }
    private struct RawPrim
    {
        public int PosAcc, NormAcc, UvAcc, ColorAcc, JointsAcc, WeightsAcc, IndicesAcc, MaterialIdx;
    }
    private struct RawMesh { public int PrimStart; public int PrimCount; }
    private struct RawNode
    {
        public FixedString64 Name;
        public int MeshIndex;
        public int SkinIndex;
        public int ParentIndex;
        public Vec3 Translation;
        public Quat Rotation;
        public Vec3 Scale;
        public byte HasMatrix;
        public Mat4 Matrix;
        public int ChildStart;
        public int ChildCount;
    }
    private struct RawSkin { public int IbmAcc; public int JointStart; public int JointCount; }
    private struct RawAnimSampler { public int InputAcc; public int OutputAcc; public AnimInterpolation Interp; }
    private struct RawAnimChannel { public int SamplerIdx; public int TargetNode; public AnimTargetPath Path; }
    private struct RawAnim { public FixedString64 Name; public int SamplerStart, SamplerCount, ChannelStart, ChannelCount; }

    public static GltfHandle LoadFromFile(
        string filePath,
        ref LinearArena bundleArena,
        ref LinearArena scratchArena,
        ref AssetRegistry assets,
        ref AnimationTable animations,
        BundleId bundle,
        StringId assetName)
    {
        byte[] bytes = File.ReadAllBytes(filePath);
        string baseDir = Path.GetDirectoryName(filePath) ?? ".";
        return LoadFromMemory(bytes, baseDir, ref bundleArena, ref scratchArena, ref assets, ref animations, bundle, assetName);
    }

    public static GltfHandle LoadFromMemory(
        ReadOnlySpan<byte> fileBytes,
        string? baseDirectory,
        ref LinearArena bundleArena,
        ref LinearArena scratchArena,
        ref AssetRegistry assets,
        ref AnimationTable animations,
        BundleId bundle,
        StringId assetName)
    {
        ReadOnlySpan<byte> jsonSpan;
        ReadOnlySpan<byte> glbBinSpan = ReadOnlySpan<byte>.Empty;

        if (fileBytes.Length >= 12 && MemoryMarshal.Read<uint>(fileBytes) == GlbMagic)
        {
            uint version = MemoryMarshal.Read<uint>(fileBytes.Slice(4));
            if (version != 2) throw new InvalidDataException($"Unsupported glTF version {version}.");

            int offset = 12;
            jsonSpan = ReadOnlySpan<byte>.Empty;
            while (offset + 8 <= fileBytes.Length)
            {
                uint chunkLen = MemoryMarshal.Read<uint>(fileBytes.Slice(offset));
                uint chunkType = MemoryMarshal.Read<uint>(fileBytes.Slice(offset + 4));
                offset += 8;
                if (offset + (int)chunkLen > fileBytes.Length) break;

                if (chunkType == ChunkJson) jsonSpan = fileBytes.Slice(offset, (int)chunkLen);
                else if (chunkType == ChunkBin) glbBinSpan = fileBytes.Slice(offset, (int)chunkLen);
                offset += (int)chunkLen;
            }
            if (jsonSpan.IsEmpty) throw new InvalidDataException("GLB missing JSON chunk.");
        }
        else
        {
            jsonSpan = fileBytes;
        }

        ArenaMarker scratchMark = scratchArena.SaveMarker();
        try
        {
            // Scratch parse tables (fixed high ceilings, zero heap)
            var buffers = NativeList<RawBuffer>.CreateInArena(ref scratchArena, 32);
            var views = NativeList<RawBufferView>.CreateInArena(ref scratchArena, 512);
            var accessors = NativeList<RawAccessor>.CreateInArena(ref scratchArena, 1024);
            var images = NativeList<RawImage>.CreateInArena(ref scratchArena, 128);
            var textures = NativeList<RawTexture>.CreateInArena(ref scratchArena, 128);
            var materials = NativeList<RawMat>.CreateInArena(ref scratchArena, 128);
            var prims = NativeList<RawPrim>.CreateInArena(ref scratchArena, 512);
            var meshes = NativeList<RawMesh>.CreateInArena(ref scratchArena, 256);
            var nodes = NativeList<RawNode>.CreateInArena(ref scratchArena, 512);
            var nodeChildren = NativeList<int>.CreateInArena(ref scratchArena, 1024);
            var skins = NativeList<RawSkin>.CreateInArena(ref scratchArena, 32);
            var skinJoints = NativeList<int>.CreateInArena(ref scratchArena, 1024);
            var anims = NativeList<RawAnim>.CreateInArena(ref scratchArena, 64);
            var animSamplers = NativeList<RawAnimSampler>.CreateInArena(ref scratchArena, 512);
            var animChannels = NativeList<RawAnimChannel>.CreateInArena(ref scratchArena, 512);

            // Copy GLB BIN chunk into scratch so we have stable pointers
            byte* glbBinPtr = null;
            if (!glbBinSpan.IsEmpty)
            {
                glbBinPtr = scratchArena.AllocUninitialized<byte>(glbBinSpan.Length, 16);
                glbBinSpan.CopyTo(new Span<byte>(glbBinPtr, glbBinSpan.Length));
            }

            // Streaming zero-reflection JSON parse
            var reader = new Utf8JsonReader(jsonSpan, isFinalBlock: true, state: default);
            ParseRootJson(
                ref reader, baseDirectory, glbBinPtr, glbBinSpan.Length, ref scratchArena,
                ref buffers, ref views, ref accessors, ref images, ref textures,
                ref materials, ref prims, ref meshes, ref nodes, ref nodeChildren,
                ref skins, ref skinJoints, ref anims, ref animSamplers, ref animChannels);

            // Wire parent links from node children
            for (int n = 0; n < nodes.Count; n++)
            {
                ref RawNode rn = ref nodes[n];
                for (int c = 0; c < rn.ChildCount; c++)
                {
                    int childIdx = nodeChildren[rn.ChildStart + c];
                    if ((uint)childIdx < (uint)nodes.Count)
                        nodes[childIdx].ParentIndex = n;
                }
            }

            // 1. Decode textures into bundleArena
            TextureHandle* loadedTextures = textures.Count > 0 ? scratchArena.Alloc<TextureHandle>(textures.Count) : null;
            for (int t = 0; t < textures.Count; t++)
            {
                int src = textures[t].Source;
                if ((uint)src >= (uint)images.Count) continue;
                ref RawImage img = ref images[src];
                var texName = StringId.Hash($"{assetName.Value}_tex_{t}");

                if (img.BufferView >= 0 && img.BufferView < views.Count)
                {
                    RawBufferView bv = views[img.BufferView];
                    RawBuffer buf = buffers[bv.Buffer];
                    loadedTextures[t] = TextureLoader.LoadFromMemory(
                        new ReadOnlySpan<byte>(buf.Ptr + bv.ByteOffset, bv.ByteLength),
                        ref bundleArena, ref assets, bundle, texName);
                }
                else if (img.Uri.Length > 0 && baseDirectory != null)
                {
                    string uriStr = img.Uri.ToString();
                    if (uriStr.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        int comma = uriStr.IndexOf(',');
                        if (comma > 0)
                        {
                            byte[] decoded = Convert.FromBase64String(uriStr.Substring(comma + 1));
                            loadedTextures[t] = TextureLoader.LoadFromMemory(decoded, ref bundleArena, ref assets, bundle, texName);
                        }
                    }
                    else
                    {
                        string fullPath = Path.Combine(baseDirectory, uriStr);
                        if (File.Exists(fullPath))
                            loadedTextures[t] = TextureLoader.LoadFromFile(fullPath, ref bundleArena, ref assets, bundle, texName);
                    }
                }
            }

            // 2. Register PBR Materials
            MaterialHandle* loadedMats = materials.Count > 0 ? scratchArena.Alloc<MaterialHandle>(materials.Count) : null;
            for (int m = 0; m < materials.Count; m++)
            {
                ref RawMat rm = ref materials[m];
                TextureHandle baseTex = (rm.BaseColorTex >= 0 && rm.BaseColorTex < textures.Count)
                    ? loadedTextures[rm.BaseColorTex] : TextureHandle.Invalid;
                loadedMats[m] = AssetSystem.RegisterMaterial(ref assets, new MaterialAssetData
                {
                    OwnerBundle = bundle,
                    Name = rm.Name.Length > 0 ? rm.Name.ToStringId() : StringId.Hash($"{assetName.Value}_mat_{m}"),
                    BaseColorFactor = rm.BaseColor,
                    BaseColorTexture = baseTex,
                    MetallicFactor = rm.Metallic,
                    RoughnessFactor = rm.Roughness,
                    EmissiveFactor = rm.Emissive,
                    AlphaMode = rm.AlphaMode,
                    AlphaCutoff = rm.AlphaCutoff,
                    DoubleSided = rm.DoubleSided,
                });
            }

            // Default fallback material if a primitive has material = -1
            MaterialHandle defaultMat = MaterialHandle.Invalid;

            // 3. Build Meshes & Primitives directly in bundleArena
            int totalPrims = 0;
            for (int n = 0; n < nodes.Count; n++)
                if (nodes[n].MeshIndex >= 0 && nodes[n].MeshIndex < meshes.Count)
                    totalPrims += meshes[nodes[n].MeshIndex].PrimCount;

            // Also support glTFs with meshes but no nodes
            bool synthesizedRootNode = nodes.Count == 0 && meshes.Count > 0;
            if (synthesizedRootNode)
            {
                for (int m = 0; m < meshes.Count; m++) totalPrims += meshes[m].PrimCount;
            }

            // Build each glTF primitive mesh once
            GltfPrimitiveRef* builtPrimTable = prims.Count > 0 ? scratchArena.Alloc<GltfPrimitiveRef>(prims.Count) : null;
            Bounds3 overallBounds = Bounds3.Empty;
            bool hasBounds = false;

            for (int p = 0; p < prims.Count; p++)
            {
                ref RawPrim rp = ref prims[p];
                if (rp.PosAcc < 0 || rp.PosAcc >= accessors.Count) continue;

                MeshHandle meshHandle = BuildMeshFromPrimitive(
                    in rp, in buffers, in views, in accessors,
                    ref bundleArena, ref assets, bundle,
                    StringId.Hash($"{assetName.Value}_prim_{p}"),
                    out Bounds3 primBounds);

                MaterialHandle matHandle;
                if (rp.MaterialIdx >= 0 && rp.MaterialIdx < materials.Count)
                {
                    matHandle = loadedMats[rp.MaterialIdx];
                }
                else
                {
                    if (!defaultMat.IsValid)
                    {
                        defaultMat = AssetSystem.RegisterMaterial(ref assets, new MaterialAssetData
                        {
                            OwnerBundle = bundle,
                            Name = StringId.Hash($"{assetName.Value}_default_mat"),
                            BaseColorFactor = ColorF.White,
                            MetallicFactor = 0.1f,
                            RoughnessFactor = 0.65f,
                            AlphaCutoff = 0.5f,
                        });
                    }
                    matHandle = defaultMat;
                }

                builtPrimTable[p] = new GltfPrimitiveRef { Mesh = meshHandle, Material = matHandle };
                if (!hasBounds) { overallBounds = primBounds; hasBounds = true; }
                else
                {
                    overallBounds.Encapsulate(primBounds.Min);
                    overallBounds.Encapsulate(primBounds.Max);
                }
            }

            // 4. Build final node + primitive reference arrays in bundleArena
            int outNodeCount = synthesizedRootNode ? meshes.Count : nodes.Count;
            GltfNodeData* outNodes = outNodeCount > 0 ? bundleArena.Alloc<GltfNodeData>(outNodeCount, 64) : null;
            GltfPrimitiveRef* outPrims = totalPrims > 0 ? bundleArena.Alloc<GltfPrimitiveRef>(totalPrims, 16) : null;
            int primWrite = 0;

            if (synthesizedRootNode)
            {
                for (int m = 0; m < meshes.Count; m++)
                {
                    int start = primWrite;
                    for (int k = 0; k < meshes[m].PrimCount; k++)
                        outPrims[primWrite++] = builtPrimTable[meshes[m].PrimStart + k];
                    outNodes[m] = new GltfNodeData
                    {
                        Name = FixedString64.From($"Mesh_{m}"),
                        ParentIndex = -1,
                        LocalPosition = Vec3.Zero,
                        LocalRotation = Quat.Identity,
                        LocalScale = Vec3.One,
                        PrimitiveStart = start,
                        PrimitiveCount = meshes[m].PrimCount,
                        SkinIndex = -1,
                    };
                }
            }
            else
            {
                for (int n = 0; n < nodes.Count; n++)
                {
                    ref RawNode rn = ref nodes[n];
                    int start = primWrite;
                    int pCount = 0;
                    if (rn.MeshIndex >= 0 && rn.MeshIndex < meshes.Count)
                    {
                        ref RawMesh rm = ref meshes[rn.MeshIndex];
                        pCount = rm.PrimCount;
                        for (int k = 0; k < rm.PrimCount; k++)
                            outPrims[primWrite++] = builtPrimTable[rm.PrimStart + k];
                    }

                    Vec3 pos = rn.Translation;
                    Quat rot = rn.Rotation;
                    Vec3 scl = rn.Scale;
                    if (rn.HasMatrix != 0)
                        DecomposeMatrix(in rn.Matrix, out pos, out rot, out scl);

                    outNodes[n] = new GltfNodeData
                    {
                        Name = rn.Name,
                        ParentIndex = rn.ParentIndex,
                        LocalPosition = pos,
                        LocalRotation = rot,
                        LocalScale = scl,
                        PrimitiveStart = start,
                        PrimitiveCount = pCount,
                        SkinIndex = rn.SkinIndex,
                    };
                }
            }

            // 5. Build Skins -> SkeletonData in bundleArena
            SkeletonHandle* outSkins = skins.Count > 0 ? bundleArena.Alloc<SkeletonHandle>(skins.Count, 16) : null;
            // Map node index -> joint index for first skin (for animation channels)
            int* nodeToJoint = nodes.Count > 0 ? scratchArena.AllocUninitialized<int>(nodes.Count) : null;
            for (int n = 0; n < nodes.Count; n++) nodeToJoint[n] = -1;

            for (int s = 0; s < skins.Count; s++)
            {
                ref RawSkin rs = ref skins[s];
                int jc = rs.JointCount;
                int* parentIndices = bundleArena.AllocUninitialized<int>(jc, 16);
                Mat4* ibms = bundleArena.AllocUninitialized<Mat4>(jc, 64);
                Vec3* bindPos = bundleArena.AllocUninitialized<Vec3>(jc, 16);
                Quat* bindRot = bundleArena.AllocUninitialized<Quat>(jc, 16);
                Vec3* bindScl = bundleArena.AllocUninitialized<Vec3>(jc, 16);

                for (int j = 0; j < jc; j++)
                {
                    int nodeIdx = skinJoints[rs.JointStart + j];
                    if (s == 0 && (uint)nodeIdx < (uint)nodes.Count)
                        nodeToJoint[nodeIdx] = j;
                }

                for (int j = 0; j < jc; j++)
                {
                    int nodeIdx = skinJoints[rs.JointStart + j];
                    int parentNode = (uint)nodeIdx < (uint)nodes.Count ? nodes[nodeIdx].ParentIndex : -1;
                    int parentJoint = -1;
                    if (parentNode >= 0)
                    {
                        for (int pj = 0; pj < jc; pj++)
                        {
                            if (skinJoints[rs.JointStart + pj] == parentNode) { parentJoint = pj; break; }
                        }
                    }
                    parentIndices[j] = parentJoint;
                    bindPos[j] = (uint)nodeIdx < (uint)outNodeCount ? outNodes[nodeIdx].LocalPosition : Vec3.Zero;
                    bindRot[j] = (uint)nodeIdx < (uint)outNodeCount ? outNodes[nodeIdx].LocalRotation : Quat.Identity;
                    bindScl[j] = (uint)nodeIdx < (uint)outNodeCount ? outNodes[nodeIdx].LocalScale : Vec3.One;
                    ibms[j] = rs.IbmAcc >= 0 ? ReadAccessorMat4(in buffers, in views, in accessors, rs.IbmAcc, j) : Mat4.Identity;
                }

                outSkins[s] = AnimationSystem.RegisterSkeleton(ref animations, new SkeletonData
                {
                    OwnerBundle = bundle,
                    JointCount = jc,
                    ParentIndices = parentIndices,
                    InverseBindMatrices = ibms,
                    BindPositions = bindPos,
                    BindRotations = bindRot,
                    BindScales = bindScl,
                });
            }

            // 6. Build Animations -> AnimClipData in bundleArena
            AnimClipHandle* outAnims = anims.Count > 0 ? bundleArena.Alloc<AnimClipHandle>(anims.Count, 16) : null;
            for (int a = 0; a < anims.Count; a++)
            {
                ref RawAnim ra = ref anims[a];
                AnimSamplerData* sArr = ra.SamplerCount > 0 ? bundleArena.Alloc<AnimSamplerData>(ra.SamplerCount, 16) : null;
                AnimChannelData* cArr = ra.ChannelCount > 0 ? bundleArena.Alloc<AnimChannelData>(ra.ChannelCount, 16) : null;

                float maxDuration = 0f;
                for (int si = 0; si < ra.SamplerCount; si++)
                {
                    RawAnimSampler ras = animSamplers[ra.SamplerStart + si];
                    RawAccessor inAcc = accessors[ras.InputAcc];
                    RawAccessor outAcc = accessors[ras.OutputAcc];

                    float* times = bundleArena.AllocUninitialized<float>(inAcc.Count, 16);
                    for (int k = 0; k < inAcc.Count; k++)
                    {
                        times[k] = ReadAccessorScalarFloat(in buffers, in views, in accessors, ras.InputAcc, k);
                        if (times[k] > maxDuration) maxDuration = times[k];
                    }

                    byte comps = outAcc.TypeComponents;
                    int totalFloats = outAcc.Count * comps;
                    float* vals = bundleArena.AllocUninitialized<float>(totalFloats, 16);
                    for (int k = 0; k < outAcc.Count; k++)
                    {
                        ReadAccessorFloats(in buffers, in views, in accessors, ras.OutputAcc, k, vals + k * comps, comps);
                    }

                    sArr[si] = new AnimSamplerData
                    {
                        Times = times,
                        Values = vals,
                        KeyframeCount = inAcc.Count,
                        ComponentsPerValue = comps,
                        Interpolation = ras.Interp,
                    };
                }

                for (int ci = 0; ci < ra.ChannelCount; ci++)
                {
                    RawAnimChannel rac = animChannels[ra.ChannelStart + ci];
                    int targetJoint = (nodeToJoint != null && (uint)rac.TargetNode < (uint)nodes.Count && nodeToJoint[rac.TargetNode] >= 0)
                        ? nodeToJoint[rac.TargetNode] : rac.TargetNode;
                    cArr[ci] = new AnimChannelData
                    {
                        SamplerIndex = rac.SamplerIdx,
                        TargetJointIndex = targetJoint,
                        Path = rac.Path,
                    };
                }

                outAnims[a] = AnimationSystem.RegisterClip(ref animations, new AnimClipData
                {
                    OwnerBundle = bundle,
                    Name = ra.Name.Length > 0 ? ra.Name.ToStringId() : StringId.Hash($"{assetName.Value}_anim_{a}"),
                    Duration = maxDuration,
                    Samplers = sArr,
                    SamplerCount = ra.SamplerCount,
                    Channels = cArr,
                    ChannelCount = ra.ChannelCount,
                });
            }

            if (!hasBounds) overallBounds = Bounds3.FromCenterExtents(Vec3.Zero, Vec3.One * 0.5f);

            return AssetSystem.RegisterGltf(ref assets, new GltfModelAssetData
            {
                OwnerBundle = bundle,
                Name = assetName,
                Nodes = outNodes,
                NodeCount = outNodeCount,
                Primitives = outPrims,
                PrimitiveCount = totalPrims,
                Skins = outSkins,
                SkinCount = skins.Count,
                Animations = outAnims,
                AnimationCount = anims.Count,
                OverallBounds = overallBounds,
            });
        }
        finally
        {
            scratchArena.Rollback(scratchMark);
        }
    }

    private static MeshHandle BuildMeshFromPrimitive(
        in RawPrim rp,
        in NativeList<RawBuffer> buffers,
        in NativeList<RawBufferView> views,
        in NativeList<RawAccessor> accessors,
        ref LinearArena bundleArena,
        ref AssetRegistry assets,
        BundleId bundle,
        StringId name,
        out Bounds3 bounds)
    {
        int vertCount = accessors[rp.PosAcc].Count;
        Vertex3D* verts = bundleArena.Alloc<Vertex3D>(vertCount, 64);
        bounds = Bounds3.Empty;
        bool hasNormals = rp.NormAcc >= 0;
        bool hasUv = rp.UvAcc >= 0;
        bool hasColor = rp.ColorAcc >= 0;
        bool hasSkin = rp.JointsAcc >= 0 && rp.WeightsAcc >= 0;

        float* tmp4 = stackalloc float[4];
        for (int v = 0; v < vertCount; v++)
        {
            ReadAccessorFloats(in buffers, in views, in accessors, rp.PosAcc, v, tmp4, 3);
            Vec3 pos = new Vec3(tmp4[0], tmp4[1], tmp4[2]);
            bounds.Encapsulate(pos);

            Vec3 norm = Vec3.UnitY;
            if (hasNormals)
            {
                ReadAccessorFloats(in buffers, in views, in accessors, rp.NormAcc, v, tmp4, 3);
                norm = new Vec3(tmp4[0], tmp4[1], tmp4[2]).Normalized();
            }

            Vec2 uv = Vec2.Zero;
            if (hasUv)
            {
                ReadAccessorFloats(in buffers, in views, in accessors, rp.UvAcc, v, tmp4, 2);
                uv = new Vec2(tmp4[0], tmp4[1]);
            }

            Color32 col = Color32.White;
            if (hasColor)
            {
                int cc = accessors[rp.ColorAcc].TypeComponents;
                tmp4[3] = 1f;
                ReadAccessorFloats(in buffers, in views, in accessors, rp.ColorAcc, v, tmp4, cc);
                col = Color32.FromFloat(tmp4[0], tmp4[1], tmp4[2], tmp4[3]);
            }

            uint jointsPacked = 0;
            Vec4 weights = new Vec4(1f, 0f, 0f, 0f);
            if (hasSkin)
            {
                uint j0 = ReadAccessorComponentUInt(in buffers, in views, in accessors, rp.JointsAcc, v, 0);
                uint j1 = ReadAccessorComponentUInt(in buffers, in views, in accessors, rp.JointsAcc, v, 1);
                uint j2 = ReadAccessorComponentUInt(in buffers, in views, in accessors, rp.JointsAcc, v, 2);
                uint j3 = ReadAccessorComponentUInt(in buffers, in views, in accessors, rp.JointsAcc, v, 3);
                jointsPacked = Vertex3D.PackJoints((byte)j0, (byte)j1, (byte)j2, (byte)j3);

                ReadAccessorFloats(in buffers, in views, in accessors, rp.WeightsAcc, v, tmp4, 4);
                float sum = tmp4[0] + tmp4[1] + tmp4[2] + tmp4[3];
                if (sum > 1e-5f)
                {
                    float inv = 1f / sum;
                    weights = new Vec4(tmp4[0] * inv, tmp4[1] * inv, tmp4[2] * inv, tmp4[3] * inv);
                }
            }

            verts[v] = new Vertex3D
            {
                Position = pos,
                Normal = norm,
                Uv0 = uv,
                Color = col,
                JointIndices = jointsPacked,
                JointWeights = weights,
            };
        }

        int idxCount = rp.IndicesAcc >= 0 ? accessors[rp.IndicesAcc].Count : vertCount;
        uint* indices = bundleArena.AllocUninitialized<uint>(idxCount, 64);
        if (rp.IndicesAcc >= 0)
        {
            for (int i = 0; i < idxCount; i++)
                indices[i] = ReadAccessorComponentUInt(in buffers, in views, in accessors, rp.IndicesAcc, i, 0);
        }
        else
        {
            for (int i = 0; i < idxCount; i++) indices[i] = (uint)i;
        }

        // Auto-compute smooth normals if missing
        if (!hasNormals)
        {
            for (int v = 0; v < vertCount; v++) verts[v].Normal = Vec3.Zero;
            for (int i = 0; i + 2 < idxCount; i += 3)
            {
                uint i0 = indices[i], i1 = indices[i + 1], i2 = indices[i + 2];
                Vec3 n = Vec3.Cross(verts[i1].Position - verts[i0].Position, verts[i2].Position - verts[i0].Position);
                verts[i0].Normal = verts[i0].Normal + n;
                verts[i1].Normal = verts[i1].Normal + n;
                verts[i2].Normal = verts[i2].Normal + n;
            }
            for (int v = 0; v < vertCount; v++)
            {
                Vec3 n = verts[v].Normal;
                verts[v].Normal = n.LengthSq() > 1e-8f ? n.Normalized() : Vec3.UnitY;
            }
        }

        return AssetSystem.RegisterMesh(ref assets, new MeshAssetData
        {
            OwnerBundle = bundle,
            Name = name,
            Vertices = verts,
            VertexCount = vertCount,
            Indices = indices,
            IndexCount = idxCount,
            LocalBounds = bounds,
            HasSkinning = (byte)(hasSkin ? 1 : 0),
        });
    }

    private static byte* GetAccessorElementPtr(
        in NativeList<RawBuffer> buffers, in NativeList<RawBufferView> views, in NativeList<RawAccessor> accessors,
        int accIdx, int elemIndex, out int componentType, out bool normalized)
    {
        RawAccessor acc = accessors[accIdx];
        RawBufferView bv = views[acc.BufferView];
        RawBuffer buf = buffers[bv.Buffer];
        componentType = acc.ComponentType;
        normalized = acc.Normalized != 0;
        int compSize = componentType switch
        {
            5120 or 5121 => 1, // BYTE / UBYTE
            5122 or 5123 => 2, // SHORT / USHORT
            _ => 4,            // UINT / FLOAT
        };
        int stride = bv.ByteStride > 0 ? bv.ByteStride : (compSize * acc.TypeComponents);
        return buf.Ptr + bv.ByteOffset + acc.ByteOffset + elemIndex * stride;
    }

    private static float ReadComponentAsFloat(byte* ptr, int compType, bool norm) => compType switch
    {
        5126 => *(float*)ptr,
        5120 => norm ? DetMath.Max(*(sbyte*)ptr / 127f, -1f) : *(sbyte*)ptr,
        5121 => norm ? *(byte*)ptr / 255f : *(byte*)ptr,
        5122 => norm ? DetMath.Max(*(short*)ptr / 32767f, -1f) : *(short*)ptr,
        5123 => norm ? *(ushort*)ptr / 65535f : *(ushort*)ptr,
        5125 => *(uint*)ptr,
        _ => 0f,
    };

    private static float ReadAccessorScalarFloat(
        in NativeList<RawBuffer> buffers, in NativeList<RawBufferView> views, in NativeList<RawAccessor> accessors, int accIdx, int elem)
    {
        byte* p = GetAccessorElementPtr(in buffers, in views, in accessors, accIdx, elem, out int ct, out bool norm);
        return ReadComponentAsFloat(p, ct, norm);
    }

    private static void ReadAccessorFloats(
        in NativeList<RawBuffer> buffers, in NativeList<RawBufferView> views, in NativeList<RawAccessor> accessors,
        int accIdx, int elem, float* dst, int count)
    {
        byte* p = GetAccessorElementPtr(in buffers, in views, in accessors, accIdx, elem, out int ct, out bool norm);
        int compSize = ct switch { 5120 or 5121 => 1, 5122 or 5123 => 2, _ => 4 };
        for (int c = 0; c < count; c++)
            dst[c] = ReadComponentAsFloat(p + c * compSize, ct, norm);
    }

    private static uint ReadAccessorComponentUInt(
        in NativeList<RawBuffer> buffers, in NativeList<RawBufferView> views, in NativeList<RawAccessor> accessors,
        int accIdx, int elem, int compIdx)
    {
        byte* p = GetAccessorElementPtr(in buffers, in views, in accessors, accIdx, elem, out int ct, out _);
        return ct switch
        {
            5121 => *(p + compIdx),
            5123 => *((ushort*)p + compIdx),
            5125 => *((uint*)p + compIdx),
            _ => 0u,
        };
    }

    private static Mat4 ReadAccessorMat4(
        in NativeList<RawBuffer> buffers, in NativeList<RawBufferView> views, in NativeList<RawAccessor> accessors, int accIdx, int elem)
    {
        float* f = stackalloc float[16];
        ReadAccessorFloats(in buffers, in views, in accessors, accIdx, elem, f, 16);
        return new Mat4
        {
            C0 = new Vec4(f[0], f[1], f[2], f[3]),
            C1 = new Vec4(f[4], f[5], f[6], f[7]),
            C2 = new Vec4(f[8], f[9], f[10], f[11]),
            C3 = new Vec4(f[12], f[13], f[14], f[15]),
        };
    }

    private static void DecomposeMatrix(in Mat4 m, out Vec3 pos, out Quat rot, out Vec3 scale)
    {
        pos = new Vec3(m.C3.X, m.C3.Y, m.C3.Z);
        Vec3 c0 = new Vec3(m.C0.X, m.C0.Y, m.C0.Z);
        Vec3 c1 = new Vec3(m.C1.X, m.C1.Y, m.C1.Z);
        Vec3 c2 = new Vec3(m.C2.X, m.C2.Y, m.C2.Z);
        scale = new Vec3(c0.Length(), c1.Length(), c2.Length());
        Vec3 r = scale.X > 1e-6f ? c0 / scale.X : Vec3.UnitX;
        Vec3 u = scale.Y > 1e-6f ? c1 / scale.Y : Vec3.UnitY;
        Vec3 f = scale.Z > 1e-6f ? -c2 / scale.Z : Vec3.Forward;
        rot = Quat.LookRotation(f, u);
    }

    // --- Streaming Utf8JsonReader Parsers (100% Zero Reflection) ---

    private static void ParseRootJson(
        ref Utf8JsonReader r, string? baseDir, byte* glbBinPtr, int glbBinLen, ref LinearArena scratch,
        ref NativeList<RawBuffer> buffers, ref NativeList<RawBufferView> views, ref NativeList<RawAccessor> accessors,
        ref NativeList<RawImage> images, ref NativeList<RawTexture> textures, ref NativeList<RawMat> materials,
        ref NativeList<RawPrim> prims, ref NativeList<RawMesh> meshes, ref NativeList<RawNode> nodes,
        ref NativeList<int> nodeChildren, ref NativeList<RawSkin> skins, ref NativeList<int> skinJoints,
        ref NativeList<RawAnim> anims, ref NativeList<RawAnimSampler> animSamplers, ref NativeList<RawAnimChannel> animChannels)
    {
        while (r.Read() && r.TokenType != JsonTokenType.StartObject) { }
        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
        {
            if (r.TokenType != JsonTokenType.PropertyName) continue;
            if (r.ValueTextEquals("buffers"u8)) ParseBuffers(ref r, baseDir, glbBinPtr, glbBinLen, ref scratch, ref buffers);
            else if (r.ValueTextEquals("bufferViews"u8)) ParseBufferViews(ref r, ref views);
            else if (r.ValueTextEquals("accessors"u8)) ParseAccessors(ref r, ref accessors);
            else if (r.ValueTextEquals("images"u8)) ParseImages(ref r, ref images);
            else if (r.ValueTextEquals("textures"u8)) ParseTextures(ref r, ref textures);
            else if (r.ValueTextEquals("materials"u8)) ParseMaterials(ref r, ref materials);
            else if (r.ValueTextEquals("meshes"u8)) ParseMeshes(ref r, ref prims, ref meshes);
            else if (r.ValueTextEquals("nodes"u8)) ParseNodes(ref r, ref nodes, ref nodeChildren);
            else if (r.ValueTextEquals("skins"u8)) ParseSkins(ref r, ref skins, ref skinJoints);
            else if (r.ValueTextEquals("animations"u8)) ParseAnimations(ref r, ref anims, ref animSamplers, ref animChannels);
            else { r.Read(); r.Skip(); }
        }
    }

    private static void ParseBuffers(ref Utf8JsonReader r, string? baseDir, byte* glbBinPtr, int glbBinLen, ref LinearArena scratch, ref NativeList<RawBuffer> buffers)
    {
        r.Read(); // StartArray
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            int byteLen = 0;
            string? uri = null;
            while (r.Read() && r.TokenType != JsonTokenType.EndObject)
            {
                if (r.TokenType != JsonTokenType.PropertyName) continue;
                if (r.ValueTextEquals("byteLength"u8)) { r.Read(); byteLen = r.GetInt32(); }
                else if (r.ValueTextEquals("uri"u8)) { r.Read(); uri = r.GetString(); }
                else { r.Read(); r.Skip(); }
            }

            if (uri == null && glbBinPtr != null)
            {
                buffers.Add(new RawBuffer { Ptr = glbBinPtr, Length = glbBinLen });
            }
            else if (uri != null && uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                int comma = uri.IndexOf(',');
                byte[] decoded = Convert.FromBase64String(uri.Substring(comma + 1));
                byte* ptr = scratch.AllocUninitialized<byte>(decoded.Length, 16);
                decoded.CopyTo(new Span<byte>(ptr, decoded.Length));
                buffers.Add(new RawBuffer { Ptr = ptr, Length = decoded.Length });
            }
            else if (uri != null && baseDir != null)
            {
                byte[] external = File.ReadAllBytes(Path.Combine(baseDir, uri));
                byte* ptr = scratch.AllocUninitialized<byte>(external.Length, 16);
                external.CopyTo(new Span<byte>(ptr, external.Length));
                buffers.Add(new RawBuffer { Ptr = ptr, Length = external.Length });
            }
            else
            {
                buffers.Add(new RawBuffer { Ptr = null, Length = byteLen });
            }
        }
    }

    private static void ParseBufferViews(ref Utf8JsonReader r, ref NativeList<RawBufferView> views)
    {
        r.Read();
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            RawBufferView bv = default;
            while (r.Read() && r.TokenType != JsonTokenType.EndObject)
            {
                if (r.TokenType != JsonTokenType.PropertyName) continue;
                if (r.ValueTextEquals("buffer"u8)) { r.Read(); bv.Buffer = r.GetInt32(); }
                else if (r.ValueTextEquals("byteOffset"u8)) { r.Read(); bv.ByteOffset = r.GetInt32(); }
                else if (r.ValueTextEquals("byteLength"u8)) { r.Read(); bv.ByteLength = r.GetInt32(); }
                else if (r.ValueTextEquals("byteStride"u8)) { r.Read(); bv.ByteStride = r.GetInt32(); }
                else { r.Read(); r.Skip(); }
            }
            views.Add(bv);
        }
    }

    private static void ParseAccessors(ref Utf8JsonReader r, ref NativeList<RawAccessor> accessors)
    {
        r.Read();
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            RawAccessor a = new RawAccessor { TypeComponents = 1 };
            while (r.Read() && r.TokenType != JsonTokenType.EndObject)
            {
                if (r.TokenType != JsonTokenType.PropertyName) continue;
                if (r.ValueTextEquals("bufferView"u8)) { r.Read(); a.BufferView = r.GetInt32(); }
                else if (r.ValueTextEquals("byteOffset"u8)) { r.Read(); a.ByteOffset = r.GetInt32(); }
                else if (r.ValueTextEquals("componentType"u8)) { r.Read(); a.ComponentType = r.GetInt32(); }
                else if (r.ValueTextEquals("count"u8)) { r.Read(); a.Count = r.GetInt32(); }
                else if (r.ValueTextEquals("normalized"u8)) { r.Read(); a.Normalized = (byte)(r.GetBoolean() ? 1 : 0); }
                else if (r.ValueTextEquals("type"u8))
                {
                    r.Read();
                    if (r.ValueTextEquals("SCALAR"u8)) a.TypeComponents = 1;
                    else if (r.ValueTextEquals("VEC2"u8)) a.TypeComponents = 2;
                    else if (r.ValueTextEquals("VEC3"u8)) a.TypeComponents = 3;
                    else if (r.ValueTextEquals("VEC4"u8)) a.TypeComponents = 4;
                    else if (r.ValueTextEquals("MAT4"u8)) a.TypeComponents = 16;
                }
                else { r.Read(); r.Skip(); }
            }
            accessors.Add(a);
        }
    }

    private static void ParseImages(ref Utf8JsonReader r, ref NativeList<RawImage> images)
    {
        r.Read();
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            RawImage img = new RawImage { BufferView = -1 };
            while (r.Read() && r.TokenType != JsonTokenType.EndObject)
            {
                if (r.TokenType != JsonTokenType.PropertyName) continue;
                if (r.ValueTextEquals("bufferView"u8)) { r.Read(); img.BufferView = r.GetInt32(); }
                else if (r.ValueTextEquals("uri"u8)) { r.Read(); img.Uri = FixedString256.FromUtf8(r.ValueSpan); }
                else { r.Read(); r.Skip(); }
            }
            images.Add(img);
        }
    }

    private static void ParseTextures(ref Utf8JsonReader r, ref NativeList<RawTexture> textures)
    {
        r.Read();
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            RawTexture tex = new RawTexture { Source = -1 };
            while (r.Read() && r.TokenType != JsonTokenType.EndObject)
            {
                if (r.TokenType != JsonTokenType.PropertyName) continue;
                if (r.ValueTextEquals("source"u8)) { r.Read(); tex.Source = r.GetInt32(); }
                else { r.Read(); r.Skip(); }
            }
            textures.Add(tex);
        }
    }

    private static void ParseMaterials(ref Utf8JsonReader r, ref NativeList<RawMat> materials)
    {
        r.Read();
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            RawMat m = new RawMat
            {
                BaseColor = ColorF.White,
                BaseColorTex = -1,
                Metallic = 0f,
                Roughness = 0.7f,
                AlphaCutoff = 0.5f,
            };
            while (r.Read() && r.TokenType != JsonTokenType.EndObject)
            {
                if (r.TokenType != JsonTokenType.PropertyName) continue;
                if (r.ValueTextEquals("name"u8)) { r.Read(); m.Name = FixedString64.FromUtf8(r.ValueSpan); }
                else if (r.ValueTextEquals("doubleSided"u8)) { r.Read(); m.DoubleSided = (byte)(r.GetBoolean() ? 1 : 0); }
                else if (r.ValueTextEquals("alphaCutoff"u8)) { r.Read(); m.AlphaCutoff = r.GetSingle(); }
                else if (r.ValueTextEquals("alphaMode"u8))
                {
                    r.Read();
                    if (r.ValueTextEquals("MASK"u8)) m.AlphaMode = MaterialAlphaMode.Mask;
                    else if (r.ValueTextEquals("BLEND"u8)) m.AlphaMode = MaterialAlphaMode.Blend;
                }
                else if (r.ValueTextEquals("emissiveFactor"u8))
                {
                    m.Emissive = ReadVec3Array(ref r);
                }
                else if (r.ValueTextEquals("pbrMetallicRoughness"u8))
                {
                    r.Read(); // StartObject
                    while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                    {
                        if (r.TokenType != JsonTokenType.PropertyName) continue;
                        if (r.ValueTextEquals("baseColorFactor"u8))
                        {
                            Vec4 c = ReadVec4Array(ref r);
                            m.BaseColor = new ColorF(c.X, c.Y, c.Z, c.W);
                        }
                        else if (r.ValueTextEquals("metallicFactor"u8)) { r.Read(); m.Metallic = r.GetSingle(); }
                        else if (r.ValueTextEquals("roughnessFactor"u8)) { r.Read(); m.Roughness = r.GetSingle(); }
                        else if (r.ValueTextEquals("baseColorTexture"u8))
                        {
                            r.Read();
                            while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                            {
                                if (r.TokenType == JsonTokenType.PropertyName && r.ValueTextEquals("index"u8))
                                { r.Read(); m.BaseColorTex = r.GetInt32(); }
                                else if (r.TokenType == JsonTokenType.PropertyName) { r.Read(); r.Skip(); }
                            }
                        }
                        else { r.Read(); r.Skip(); }
                    }
                }
                else { r.Read(); r.Skip(); }
            }
            materials.Add(m);
        }
    }

    private static void ParseMeshes(ref Utf8JsonReader r, ref NativeList<RawPrim> prims, ref NativeList<RawMesh> meshes)
    {
        r.Read();
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            RawMesh mesh = new RawMesh { PrimStart = prims.Count, PrimCount = 0 };
            while (r.Read() && r.TokenType != JsonTokenType.EndObject)
            {
                if (r.TokenType != JsonTokenType.PropertyName) continue;
                if (r.ValueTextEquals("primitives"u8))
                {
                    r.Read(); // StartArray
                    while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                    {
                        RawPrim p = new RawPrim { PosAcc = -1, NormAcc = -1, UvAcc = -1, ColorAcc = -1, JointsAcc = -1, WeightsAcc = -1, IndicesAcc = -1, MaterialIdx = -1 };
                        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                        {
                            if (r.TokenType != JsonTokenType.PropertyName) continue;
                            if (r.ValueTextEquals("indices"u8)) { r.Read(); p.IndicesAcc = r.GetInt32(); }
                            else if (r.ValueTextEquals("material"u8)) { r.Read(); p.MaterialIdx = r.GetInt32(); }
                            else if (r.ValueTextEquals("attributes"u8))
                            {
                                r.Read();
                                while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                                {
                                    if (r.TokenType != JsonTokenType.PropertyName) continue;
                                    if (r.ValueTextEquals("POSITION"u8)) { r.Read(); p.PosAcc = r.GetInt32(); }
                                    else if (r.ValueTextEquals("NORMAL"u8)) { r.Read(); p.NormAcc = r.GetInt32(); }
                                    else if (r.ValueTextEquals("TEXCOORD_0"u8)) { r.Read(); p.UvAcc = r.GetInt32(); }
                                    else if (r.ValueTextEquals("COLOR_0"u8)) { r.Read(); p.ColorAcc = r.GetInt32(); }
                                    else if (r.ValueTextEquals("JOINTS_0"u8)) { r.Read(); p.JointsAcc = r.GetInt32(); }
                                    else if (r.ValueTextEquals("WEIGHTS_0"u8)) { r.Read(); p.WeightsAcc = r.GetInt32(); }
                                    else { r.Read(); r.Skip(); }
                                }
                            }
                            else { r.Read(); r.Skip(); }
                        }
                        prims.Add(p);
                        mesh.PrimCount++;
                    }
                }
                else { r.Read(); r.Skip(); }
            }
            meshes.Add(mesh);
        }
    }

    private static void ParseNodes(ref Utf8JsonReader r, ref NativeList<RawNode> nodes, ref NativeList<int> children)
    {
        r.Read();
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            RawNode n = new RawNode
            {
                MeshIndex = -1,
                SkinIndex = -1,
                ParentIndex = -1,
                Rotation = Quat.Identity,
                Scale = Vec3.One,
                ChildStart = children.Count,
            };
            while (r.Read() && r.TokenType != JsonTokenType.EndObject)
            {
                if (r.TokenType != JsonTokenType.PropertyName) continue;
                if (r.ValueTextEquals("name"u8)) { r.Read(); n.Name = FixedString64.FromUtf8(r.ValueSpan); }
                else if (r.ValueTextEquals("mesh"u8)) { r.Read(); n.MeshIndex = r.GetInt32(); }
                else if (r.ValueTextEquals("skin"u8)) { r.Read(); n.SkinIndex = r.GetInt32(); }
                else if (r.ValueTextEquals("translation"u8)) n.Translation = ReadVec3Array(ref r);
                else if (r.ValueTextEquals("scale"u8)) n.Scale = ReadVec3Array(ref r);
                else if (r.ValueTextEquals("rotation"u8))
                {
                    Vec4 q = ReadVec4Array(ref r);
                    n.Rotation = new Quat(q.X, q.Y, q.Z, q.W).Normalized();
                }
                else if (r.ValueTextEquals("matrix"u8))
                {
                    n.HasMatrix = 1;
                    n.Matrix = ReadMat4Array(ref r);
                }
                else if (r.ValueTextEquals("children"u8))
                {
                    n.ChildStart = children.Count;
                    r.Read();
                    while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                    {
                        children.Add(r.GetInt32());
                        n.ChildCount++;
                    }
                }
                else { r.Read(); r.Skip(); }
            }
            nodes.Add(n);
        }
    }

    private static void ParseSkins(ref Utf8JsonReader r, ref NativeList<RawSkin> skins, ref NativeList<int> joints)
    {
        r.Read();
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            RawSkin s = new RawSkin { IbmAcc = -1, JointStart = joints.Count };
            while (r.Read() && r.TokenType != JsonTokenType.EndObject)
            {
                if (r.TokenType != JsonTokenType.PropertyName) continue;
                if (r.ValueTextEquals("inverseBindMatrices"u8)) { r.Read(); s.IbmAcc = r.GetInt32(); }
                else if (r.ValueTextEquals("joints"u8))
                {
                    s.JointStart = joints.Count;
                    r.Read();
                    while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                    {
                        joints.Add(r.GetInt32());
                        s.JointCount++;
                    }
                }
                else { r.Read(); r.Skip(); }
            }
            skins.Add(s);
        }
    }

    private static void ParseAnimations(ref Utf8JsonReader r, ref NativeList<RawAnim> anims, ref NativeList<RawAnimSampler> samplers, ref NativeList<RawAnimChannel> channels)
    {
        r.Read();
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            RawAnim a = new RawAnim { SamplerStart = samplers.Count, ChannelStart = channels.Count };
            while (r.Read() && r.TokenType != JsonTokenType.EndObject)
            {
                if (r.TokenType != JsonTokenType.PropertyName) continue;
                if (r.ValueTextEquals("name"u8)) { r.Read(); a.Name = FixedString64.FromUtf8(r.ValueSpan); }
                else if (r.ValueTextEquals("samplers"u8))
                {
                    a.SamplerStart = samplers.Count;
                    r.Read();
                    while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                    {
                        RawAnimSampler s = new RawAnimSampler { Interp = AnimInterpolation.Linear };
                        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                        {
                            if (r.TokenType != JsonTokenType.PropertyName) continue;
                            if (r.ValueTextEquals("input"u8)) { r.Read(); s.InputAcc = r.GetInt32(); }
                            else if (r.ValueTextEquals("output"u8)) { r.Read(); s.OutputAcc = r.GetInt32(); }
                            else if (r.ValueTextEquals("interpolation"u8))
                            {
                                r.Read();
                                if (r.ValueTextEquals("STEP"u8)) s.Interp = AnimInterpolation.Step;
                                else if (r.ValueTextEquals("CUBICSPLINE"u8)) s.Interp = AnimInterpolation.CubicSpline;
                            }
                            else { r.Read(); r.Skip(); }
                        }
                        samplers.Add(s);
                        a.SamplerCount++;
                    }
                }
                else if (r.ValueTextEquals("channels"u8))
                {
                    a.ChannelStart = channels.Count;
                    r.Read();
                    while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                    {
                        RawAnimChannel c = default;
                        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                        {
                            if (r.TokenType != JsonTokenType.PropertyName) continue;
                            if (r.ValueTextEquals("sampler"u8)) { r.Read(); c.SamplerIdx = r.GetInt32(); }
                            else if (r.ValueTextEquals("target"u8))
                            {
                                r.Read();
                                while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                                {
                                    if (r.TokenType != JsonTokenType.PropertyName) continue;
                                    if (r.ValueTextEquals("node"u8)) { r.Read(); c.TargetNode = r.GetInt32(); }
                                    else if (r.ValueTextEquals("path"u8))
                                    {
                                        r.Read();
                                        if (r.ValueTextEquals("translation"u8)) c.Path = AnimTargetPath.Translation;
                                        else if (r.ValueTextEquals("rotation"u8)) c.Path = AnimTargetPath.Rotation;
                                        else if (r.ValueTextEquals("scale"u8)) c.Path = AnimTargetPath.Scale;
                                    }
                                    else { r.Read(); r.Skip(); }
                                }
                            }
                            else { r.Read(); r.Skip(); }
                        }
                        channels.Add(c);
                        a.ChannelCount++;
                    }
                }
                else { r.Read(); r.Skip(); }
            }
            anims.Add(a);
        }
    }

    private static Vec3 ReadVec3Array(ref Utf8JsonReader r)
    {
        r.Read(); // StartArray
        r.Read(); float x = r.GetSingle();
        r.Read(); float y = r.GetSingle();
        r.Read(); float z = r.GetSingle();
        while (r.Read() && r.TokenType != JsonTokenType.EndArray) { }
        return new Vec3(x, y, z);
    }

    private static Vec4 ReadVec4Array(ref Utf8JsonReader r)
    {
        r.Read();
        r.Read(); float x = r.GetSingle();
        r.Read(); float y = r.GetSingle();
        r.Read(); float z = r.GetSingle();
        r.Read(); float w = r.GetSingle();
        while (r.Read() && r.TokenType != JsonTokenType.EndArray) { }
        return new Vec4(x, y, z, w);
    }

    private static Mat4 ReadMat4Array(ref Utf8JsonReader r)
    {
        float* f = stackalloc float[16];
        r.Read();
        for (int i = 0; i < 16; i++) { r.Read(); f[i] = r.GetSingle(); }
        while (r.Read() && r.TokenType != JsonTokenType.EndArray) { }
        return new Mat4
        {
            C0 = new Vec4(f[0], f[1], f[2], f[3]),
            C1 = new Vec4(f[4], f[5], f[6], f[7]),
            C2 = new Vec4(f[8], f[9], f[10], f[11]),
            C3 = new Vec4(f[12], f[13], f[14], f[15]),
        };
    }

    /// <summary>
    /// Instantiates a loaded glTF model into EngineWorld: spawns root + child entities,
    /// transforms, mesh renderers, and skeletal animation player (if skinned/animated).
    /// Returns the root EntityId.
    /// </summary>
    public static EntityId Instantiate(
        ref EngineWorld world,
        ref LinearArena scratchArena,
        GltfHandle gltfHandle,
        Vec3 position,
        Quat rotation,
        Vec3 scale,
        SceneId ownerScene = default,
        ColorF? tint = null,
        uint layerMask = 1u,
        bool autoPlayFirstAnimation = true)
    {
        GltfModelAssetData* model = AssetSystem.TryGetGltf(in world.Assets, gltfHandle);
        if (model == null) return EntityId.Invalid;

        EntityId root = EntitySystem.Spawn(ref world.Entities, ownerScene);
        TransformSystem.Add(ref world.Transforms, root, position, rotation, scale);

        if (model->NodeCount == 0) return root;

        ArenaMarker mark = scratchArena.SaveMarker();
        EntityId* nodeEntities = scratchArena.Alloc<EntityId>(model->NodeCount);
        int* skinAnimPlayers = model->SkinCount > 0 ? scratchArena.AllocUninitialized<int>(model->SkinCount) : null;

        for (int s = 0; s < model->SkinCount; s++)
        {
            AnimClipHandle clip = (autoPlayFirstAnimation && model->AnimationCount > 0)
                ? model->Animations[0] : AnimClipHandle.Invalid;
            skinAnimPlayers[s] = AnimationSystem.AddPlayer(ref world.Animations, root, model->Skins[s], clip, ownerScene);
        }

        // First pass: spawn entities for every node
        for (int n = 0; n < model->NodeCount; n++)
            nodeEntities[n] = EntitySystem.Spawn(ref world.Entities, ownerScene);

        // Second pass: add transforms & mesh renderers
        for (int n = 0; n < model->NodeCount; n++)
        {
            GltfNodeData nd = model->Nodes[n];
            EntityId parent = (nd.ParentIndex >= 0 && nd.ParentIndex < model->NodeCount)
                ? nodeEntities[nd.ParentIndex] : root;
            TransformSystem.Add(ref world.Transforms, nodeEntities[n], nd.LocalPosition, nd.LocalRotation, nd.LocalScale, parent);

            int animPlayerIdx = (nd.SkinIndex >= 0 && nd.SkinIndex < model->SkinCount) ? skinAnimPlayers[nd.SkinIndex] : -1;
            for (int p = 0; p < nd.PrimitiveCount; p++)
            {
                GltfPrimitiveRef pr = model->Primitives[nd.PrimitiveStart + p];
                RenderComponentSystem.AddMeshRenderer(
                    ref world.RenderComponents, nodeEntities[n], pr.Mesh, pr.Material,
                    ownerScene, tint, layerMask, animPlayerIdx);
            }
        }

        scratchArena.Rollback(mark);
        return root;
    }
}
