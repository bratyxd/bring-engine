using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using BringEngine.Assets.Compression;
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
    private struct RawImage { public int BufferView; public byte* UriOrDataPtr; public int UriOrDataLen; public byte IsDataUri; }
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
    private struct RawAnimChannel { public int SamplerIdx; public int TargetNode; public AnimTargetPath Path; public byte HasPath; }
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
        return LoadFromMemoryCore(fileBytes, baseDirectory, ref bundleArena, ref scratchArena, ref assets, ref animations, bundle, assetName, allowBglb: true);
    }

    private static GltfHandle LoadFromMemoryCore(
        ReadOnlySpan<byte> fileBytes,
        string? baseDirectory,
        ref LinearArena bundleArena,
        ref LinearArena scratchArena,
        ref AssetRegistry assets,
        ref AnimationTable animations,
        BundleId bundle,
        StringId assetName,
        bool allowBglb)
    {
        // Transparently decompress Bring Compressed GLB ("BGLB" — LZ4 or Brotli compressed .glb)
        if (fileBytes.Length >= sizeof(BglbHeader) &&
            MemoryMarshal.Read<uint>(fileBytes) == BglbHeader.MagicValue)
        {
            if (!allowBglb)
                throw new InvalidDataException("Nested BGLB compressed containers are not permitted.");

            BglbHeader hdr = MemoryMarshal.Read<BglbHeader>(fileBytes);
            if (hdr.Version != BglbHeader.CurrentVersion)
                throw new InvalidDataException($"Unsupported BGLB version {hdr.Version}.");
            if (hdr.Compression is not (PackCompressionMode.None or PackCompressionMode.Lz4 or PackCompressionMode.Brotli))
                throw new InvalidDataException($"Unsupported BGLB compression mode {hdr.Compression}.");

            int payloadOff = sizeof(BglbHeader);
            if (hdr.UncompressedBytes <= 0 || hdr.CompressedBytes < 0 ||
                (long)payloadOff + hdr.CompressedBytes > fileBytes.Length ||
                (hdr.Compression == PackCompressionMode.None && hdr.CompressedBytes != hdr.UncompressedBytes))
                throw new InvalidDataException("Corrupt BGLB compressed glTF payload.");

            ArenaMarker bglbMark = scratchArena.SaveMarker();
            try
            {
                byte* decompressedPtr = scratchArena.AllocUninitialized<byte>(hdr.UncompressedBytes, 64);
                var decompressedSpan = new Span<byte>(decompressedPtr, hdr.UncompressedBytes);
                int written = PackCompressionSystem.Decompress(fileBytes.Slice(payloadOff, hdr.CompressedBytes), hdr.Compression, decompressedSpan);
                if (written != hdr.UncompressedBytes)
                    throw new InvalidDataException($"BGLB decompressed byte count mismatch (expected {hdr.UncompressedBytes}, got {written}).");

                return LoadFromMemoryCore(decompressedSpan, baseDirectory, ref bundleArena, ref scratchArena, ref assets, ref animations, bundle, assetName, allowBglb: false);
            }
            finally
            {
                scratchArena.Rollback(bglbMark);
            }
        }

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
                if (chunkLen > (uint)(fileBytes.Length - offset))
                    throw new InvalidDataException("GLB chunk length exceeds file bounds.");

                int len = (int)chunkLen;
                if (chunkType == ChunkJson) jsonSpan = fileBytes.Slice(offset, len);
                else if (chunkType == ChunkBin) glbBinSpan = fileBytes.Slice(offset, len);
                offset += len;
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

                if (img.BufferView >= 0)
                {
                    if ((uint)img.BufferView >= (uint)views.Count)
                        throw new InvalidDataException($"Image {src} bufferView {img.BufferView} out of range.");
                    RawBufferView bv = views[img.BufferView];
                    if ((uint)bv.Buffer >= (uint)buffers.Count)
                        throw new InvalidDataException($"Image bufferView {img.BufferView} buffer {bv.Buffer} out of range.");
                    RawBuffer buf = buffers[bv.Buffer];
                    if (buf.Ptr == null || bv.ByteOffset < 0 || bv.ByteLength <= 0 || (long)bv.ByteOffset + bv.ByteLength > buf.Length)
                        throw new InvalidDataException($"Image bufferView {img.BufferView} exceeds buffer bounds.");
                    loadedTextures[t] = TextureLoader.LoadFromMemory(
                        new ReadOnlySpan<byte>(buf.Ptr + bv.ByteOffset, bv.ByteLength),
                        ref bundleArena, ref assets, bundle, texName);
                }
                else if (img.IsDataUri != 0 && img.UriOrDataPtr != null && img.UriOrDataLen > 0)
                {
                    loadedTextures[t] = TextureLoader.LoadFromMemory(
                        new ReadOnlySpan<byte>(img.UriOrDataPtr, img.UriOrDataLen),
                        ref bundleArena, ref assets, bundle, texName);
                }
                else if (img.IsDataUri == 0 && img.UriOrDataLen > 0 && baseDirectory != null)
                {
                    string uriStr = Encoding.UTF8.GetString(img.UriOrDataPtr, img.UriOrDataLen);
                    if (!PackCompressionSystem.TryResolveSafeRelativePath(baseDirectory, uriStr, out string fullPath))
                        throw new InvalidDataException($"Unsafe external glTF image URI rejected: '{uriStr}'.");
                    if (!File.Exists(fullPath))
                        throw new FileNotFoundException($"External glTF image file not found: '{fullPath}'.", fullPath);
                    loadedTextures[t] = TextureLoader.LoadFromFile(fullPath, ref bundleArena, ref assets, bundle, texName);
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

            // 2.5 Topologically sort skin joints (parent-before-child) before building skinned meshes
            int* nodeToJoint = nodes.Count > 0 ? scratchArena.AllocUninitialized<int>(nodes.Count) : null;
            for (int n = 0; n < nodes.Count; n++) nodeToJoint[n] = -1;

            int* skinOldToNew = skinJoints.Count > 0 ? scratchArena.AllocUninitialized<int>(skinJoints.Count) : null;
            int* skinNewToOld = skinJoints.Count > 0 ? scratchArena.AllocUninitialized<int>(skinJoints.Count) : null;
            int* skinParentNew = skinJoints.Count > 0 ? scratchArena.AllocUninitialized<int>(skinJoints.Count) : null;

            for (int s = 0; s < skins.Count; s++)
            {
                ref RawSkin rs = ref skins[s];
                int jc = rs.JointCount;
                if (jc < 0 || rs.JointStart < 0 || rs.JointStart + jc > skinJoints.Count)
                    throw new InvalidDataException($"Corrupt skin {s} joint range.");
                if (jc == 0) continue;

                int* oldParent = scratchArena.AllocUninitialized<int>(jc);
                byte* placed = scratchArena.Alloc<byte>(jc);
                int* oldToNew = skinOldToNew + rs.JointStart;
                int* newToOld = skinNewToOld + rs.JointStart;
                int* parentNew = skinParentNew + rs.JointStart;

                for (int j = 0; j < jc; j++)
                {
                    int nodeIdx = skinJoints[rs.JointStart + j];
                    if ((uint)nodeIdx >= (uint)nodes.Count)
                        throw new InvalidDataException($"Skin {s} joint {j} references invalid node {nodeIdx}.");

                    int parentNode = nodes[nodeIdx].ParentIndex;
                    int pjFound = -1;
                    if (parentNode >= 0)
                    {
                        for (int pj = 0; pj < jc; pj++)
                        {
                            if (skinJoints[rs.JointStart + pj] == parentNode) { pjFound = pj; break; }
                        }
                    }
                    oldParent[j] = pjFound;
                }

                int written = 0;
                while (written < jc)
                {
                    int prevWritten = written;
                    for (int j = 0; j < jc; j++)
                    {
                        if (placed[j] != 0) continue;
                        int p = oldParent[j];
                        if (p < 0 || placed[p] != 0)
                        {
                            newToOld[written] = j;
                            oldToNew[j] = written;
                            placed[j] = 1;
                            written++;
                        }
                    }
                    if (written == prevWritten)
                        throw new InvalidDataException($"Cycle detected in glTF skin {s} joint hierarchy.");
                }

                for (int newJ = 0; newJ < jc; newJ++)
                {
                    int oldJ = newToOld[newJ];
                    parentNew[newJ] = oldParent[oldJ] >= 0 ? oldToNew[oldParent[oldJ]] : -1;
                    int nodeIdx = skinJoints[rs.JointStart + oldJ];
                    nodeToJoint[nodeIdx] = newJ;
                }
            }

            int* primToSkin = prims.Count > 0 ? scratchArena.AllocUninitialized<int>(prims.Count) : null;
            for (int p = 0; p < prims.Count; p++) primToSkin[p] = -1;
            for (int n = 0; n < nodes.Count; n++)
            {
                if (nodes[n].SkinIndex >= 0)
                {
                    if (nodes[n].SkinIndex >= skins.Count)
                        throw new InvalidDataException($"Node {n} references invalid skin index {nodes[n].SkinIndex}.");
                    if (nodes[n].MeshIndex >= 0 && nodes[n].MeshIndex < meshes.Count)
                    {
                        ref RawMesh rm = ref meshes[nodes[n].MeshIndex];
                        for (int k = 0; k < rm.PrimCount; k++)
                        {
                            int pi = rm.PrimStart + k;
                            if ((uint)pi < (uint)prims.Count)
                                primToSkin[pi] = nodes[n].SkinIndex;
                        }
                    }
                }
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

                int skinIdx = primToSkin != null ? primToSkin[p] : -1;
                int* jointRemap = (skinIdx >= 0 && skins[skinIdx].JointCount > 0)
                    ? skinOldToNew + skins[skinIdx].JointStart : null;
                int jointRemapCount = skinIdx >= 0 ? skins[skinIdx].JointCount : 0;

                MeshHandle meshHandle = BuildMeshFromPrimitive(
                    in rp, in buffers, in views, in accessors,
                    jointRemap, jointRemapCount,
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

            // 5. Build Skins -> SkeletonData in bundleArena (topologically sorted parent < child)
            SkeletonHandle* outSkins = skins.Count > 0 ? bundleArena.Alloc<SkeletonHandle>(skins.Count, 16) : null;
            for (int s = 0; s < skins.Count; s++)
            {
                ref RawSkin rs = ref skins[s];
                int jc = rs.JointCount;
                if (rs.IbmAcc >= 0)
                {
                    ValidateAccessor(in buffers, in views, in accessors, rs.IbmAcc, expectedComponents: 16, requireFloat: true);
                    if (accessors[rs.IbmAcc].Count < jc)
                        throw new InvalidDataException($"Skin {s} IBM accessor count ({accessors[rs.IbmAcc].Count}) < jointCount ({jc}).");
                }

                int* parentIndices = jc > 0 ? bundleArena.AllocUninitialized<int>(jc, 16) : null;
                Mat4* ibms = jc > 0 ? bundleArena.AllocUninitialized<Mat4>(jc, 64) : null;
                Vec3* bindPos = jc > 0 ? bundleArena.AllocUninitialized<Vec3>(jc, 16) : null;
                Quat* bindRot = jc > 0 ? bundleArena.AllocUninitialized<Quat>(jc, 16) : null;
                Vec3* bindScl = jc > 0 ? bundleArena.AllocUninitialized<Vec3>(jc, 16) : null;

                int* newToOld = jc > 0 ? skinNewToOld + rs.JointStart : null;
                int* parentNew = jc > 0 ? skinParentNew + rs.JointStart : null;

                for (int newJ = 0; newJ < jc; newJ++)
                {
                    int oldJ = newToOld[newJ];
                    int nodeIdx = skinJoints[rs.JointStart + oldJ];
                    parentIndices[newJ] = parentNew[newJ];
                    bindPos[newJ] = (uint)nodeIdx < (uint)outNodeCount ? outNodes[nodeIdx].LocalPosition : Vec3.Zero;
                    bindRot[newJ] = (uint)nodeIdx < (uint)outNodeCount ? outNodes[nodeIdx].LocalRotation : Quat.Identity;
                    bindScl[newJ] = (uint)nodeIdx < (uint)outNodeCount ? outNodes[nodeIdx].LocalScale : Vec3.One;
                    ibms[newJ] = rs.IbmAcc >= 0 ? ReadAccessorMat4(in buffers, in views, in accessors, rs.IbmAcc, oldJ) : Mat4.Identity;
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
                    ValidateAccessor(in buffers, in views, in accessors, ras.InputAcc, expectedComponents: 1);
                    ValidateAccessor(in buffers, in views, in accessors, ras.OutputAcc);
                    RawAccessor inAcc = accessors[ras.InputAcc];
                    RawAccessor outAcc = accessors[ras.OutputAcc];

                    if (inAcc.Count <= 0)
                        throw new InvalidDataException($"Animation {a} sampler {si} has non-positive keyframe count {inAcc.Count}.");
                    if (outAcc.TypeComponents != 3 && outAcc.TypeComponents != 4)
                        throw new InvalidDataException($"Animation {a} sampler {si} output must be VEC3 or VEC4 (got {outAcc.TypeComponents}).");
                    long reqOut = ras.Interp == AnimInterpolation.CubicSpline ? (long)inAcc.Count * 3L : inAcc.Count;
                    if (outAcc.Count < reqOut)
                        throw new InvalidDataException($"Animation {a} sampler {si} output count ({outAcc.Count}) < required ({reqOut}).");

                    float* times = bundleArena.AllocUninitialized<float>(inAcc.Count, 16);
                    for (int k = 0; k < inAcc.Count; k++)
                    {
                        times[k] = ReadAccessorScalarFloat(in buffers, in views, in accessors, ras.InputAcc, k);
                        if (times[k] > maxDuration) maxDuration = times[k];
                    }

                    byte comps = outAcc.TypeComponents;
                    int totalFloats = checked(outAcc.Count * comps);
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
                    if ((uint)rac.SamplerIdx >= (uint)ra.SamplerCount)
                        throw new InvalidDataException($"Animation {a} channel {ci} sampler index {rac.SamplerIdx} out of range.");
                    if (rac.HasPath != 0)
                    {
                        byte reqComps = rac.Path == AnimTargetPath.Rotation ? (byte)4 : (byte)3;
                        if (sArr[rac.SamplerIdx].ComponentsPerValue != reqComps)
                            throw new InvalidDataException($"Animation {a} channel {ci} path {rac.Path} expects {reqComps} components, sampler has {sArr[rac.SamplerIdx].ComponentsPerValue}.");
                    }

                    int targetJoint = (rac.HasPath != 0 && nodeToJoint != null && (uint)rac.TargetNode < (uint)nodes.Count)
                        ? nodeToJoint[rac.TargetNode] : -1;
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
        int* jointRemap,
        int jointRemapCount,
        ref LinearArena bundleArena,
        ref AssetRegistry assets,
        BundleId bundle,
        StringId name,
        out Bounds3 bounds)
    {
        ValidateAccessor(in buffers, in views, in accessors, rp.PosAcc, expectedComponents: 3);
        int vertCount = accessors[rp.PosAcc].Count;
        if (vertCount <= 0)
            throw new InvalidDataException("glTF primitive POSITION accessor must have count > 0.");

        bool hasNormals = rp.NormAcc >= 0;
        if (hasNormals)
        {
            ValidateAccessor(in buffers, in views, in accessors, rp.NormAcc, expectedComponents: 3);
            if (accessors[rp.NormAcc].Count < vertCount)
                throw new InvalidDataException("glTF primitive NORMAL accessor count < POSITION count.");
        }

        bool hasUv = rp.UvAcc >= 0;
        if (hasUv)
        {
            ValidateAccessor(in buffers, in views, in accessors, rp.UvAcc, expectedComponents: 2);
            if (accessors[rp.UvAcc].Count < vertCount)
                throw new InvalidDataException("glTF primitive TEXCOORD_0 accessor count < POSITION count.");
        }

        bool hasColor = rp.ColorAcc >= 0;
        int colorComps = 0;
        if (hasColor)
        {
            ValidateAccessor(in buffers, in views, in accessors, rp.ColorAcc);
            colorComps = accessors[rp.ColorAcc].TypeComponents;
            if (colorComps != 3 && colorComps != 4)
                throw new InvalidDataException($"glTF primitive COLOR_0 accessor must be VEC3 or VEC4 (got {colorComps} components).");
            if (accessors[rp.ColorAcc].Count < vertCount)
                throw new InvalidDataException("glTF primitive COLOR_0 accessor count < POSITION count.");
        }

        bool hasSkin = rp.JointsAcc >= 0 && rp.WeightsAcc >= 0;
        if (hasSkin)
        {
            ValidateAccessor(in buffers, in views, in accessors, rp.JointsAcc, expectedComponents: 4);
            ValidateAccessor(in buffers, in views, in accessors, rp.WeightsAcc, expectedComponents: 4);
            if (accessors[rp.JointsAcc].Count < vertCount || accessors[rp.WeightsAcc].Count < vertCount)
                throw new InvalidDataException("glTF primitive skinning accessor count < POSITION count.");
            int jct = accessors[rp.JointsAcc].ComponentType;
            if (jct != 5121 && jct != 5123 && jct != 5125)
                throw new InvalidDataException($"glTF JOINTS_0 unsupported componentType {jct}.");
        }

        if (rp.IndicesAcc >= 0)
        {
            ValidateAccessor(in buffers, in views, in accessors, rp.IndicesAcc, expectedComponents: 1);
            int ict = accessors[rp.IndicesAcc].ComponentType;
            if (ict != 5121 && ict != 5123 && ict != 5125)
                throw new InvalidDataException($"glTF indices unsupported componentType {ict}.");
        }

        Vertex3D* verts = bundleArena.Alloc<Vertex3D>(vertCount, 64);
        bounds = Bounds3.Empty;

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
                tmp4[3] = 1f;
                ReadAccessorFloats(in buffers, in views, in accessors, rp.ColorAcc, v, tmp4, colorComps);
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
                if (jointRemap != null)
                {
                    if (j0 < (uint)jointRemapCount) j0 = (uint)jointRemap[j0];
                    if (j1 < (uint)jointRemapCount) j1 = (uint)jointRemap[j1];
                    if (j2 < (uint)jointRemapCount) j2 = (uint)jointRemap[j2];
                    if (j3 < (uint)jointRemapCount) j3 = (uint)jointRemap[j3];
                }
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
            {
                uint iv = ReadAccessorComponentUInt(in buffers, in views, in accessors, rp.IndicesAcc, i, 0);
                if (iv >= (uint)vertCount)
                    throw new InvalidDataException($"glTF primitive index {iv} out of vertex range [0..{vertCount}).");
                indices[i] = iv;
            }
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

    private static void ValidateAccessor(
        in NativeList<RawBuffer> buffers, in NativeList<RawBufferView> views, in NativeList<RawAccessor> accessors,
        int accIdx, int expectedComponents = 0, bool requireFloat = false)
    {
        if ((uint)accIdx >= (uint)accessors.Count)
            throw new InvalidDataException($"glTF accessor index {accIdx} out of range (count={accessors.Count}).");

        RawAccessor acc = accessors[accIdx];
        if (acc.Count < 0 || acc.ByteOffset < 0 || acc.TypeComponents <= 0)
            throw new InvalidDataException($"glTF accessor {accIdx} has invalid count/offset/type.");
        if (expectedComponents > 0 && acc.TypeComponents != expectedComponents)
            throw new InvalidDataException($"glTF accessor {accIdx} expected {expectedComponents} components, got {acc.TypeComponents}.");
        if (requireFloat && acc.ComponentType != 5126)
            throw new InvalidDataException($"glTF accessor {accIdx} requires FLOAT (5126), got {acc.ComponentType}.");

        int compSize = acc.ComponentType switch
        {
            5120 or 5121 => 1,
            5122 or 5123 => 2,
            5125 or 5126 => 4,
            _ => throw new InvalidDataException($"glTF accessor {accIdx} unsupported componentType {acc.ComponentType}."),
        };

        if ((uint)acc.BufferView >= (uint)views.Count)
            throw new InvalidDataException($"glTF accessor {accIdx} bufferView {acc.BufferView} out of range.");

        RawBufferView bv = views[acc.BufferView];
        if (bv.ByteOffset < 0 || bv.ByteLength < 0 || bv.ByteStride < 0)
            throw new InvalidDataException($"glTF bufferView {acc.BufferView} has negative offset/length/stride.");
        if ((uint)bv.Buffer >= (uint)buffers.Count)
            throw new InvalidDataException($"glTF bufferView {acc.BufferView} buffer {bv.Buffer} out of range.");

        RawBuffer buf = buffers[bv.Buffer];
        if (buf.Ptr == null || buf.Length < 0)
            throw new InvalidDataException($"glTF buffer {bv.Buffer} has no backing data.");
        if ((long)bv.ByteOffset + bv.ByteLength > buf.Length)
            throw new InvalidDataException($"glTF bufferView {acc.BufferView} exceeds buffer {bv.Buffer} bounds.");

        int elemSize = compSize * acc.TypeComponents;
        int stride = bv.ByteStride > 0 ? bv.ByteStride : elemSize;
        if (stride < elemSize)
            throw new InvalidDataException($"glTF bufferView {acc.BufferView} byteStride ({stride}) < element size ({elemSize}).");

        if (acc.Count > 0)
        {
            long endInView = (long)acc.ByteOffset + (long)(acc.Count - 1) * stride + elemSize;
            if (endInView > bv.ByteLength || (long)bv.ByteOffset + endInView > buf.Length)
                throw new InvalidDataException($"glTF accessor {accIdx} span exceeds bufferView/buffer bounds.");
        }
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
        return buf.Ptr + bv.ByteOffset + acc.ByteOffset + (long)elemIndex * stride;
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

        float sx = c0.Length();
        float sy = c1.Length();
        float sz = c2.Length();
        float det = Vec3.Dot(c0, Vec3.Cross(c1, c2));
        if (det < 0f) sx = -sx;
        scale = new Vec3(sx, sy, sz);

        Vec3 r = DetMath.Abs(sx) > 1e-6f ? c0 / sx : Vec3.UnitX;
        Vec3 u = DetMath.Abs(sy) > 1e-6f ? c1 / sy : Vec3.UnitY;
        Vec3 zAxis = DetMath.Abs(sz) > 1e-6f ? c2 / sz : Vec3.UnitZ;

        if (DetMath.Abs(sx) <= 1e-6f && DetMath.Abs(sy) > 1e-6f && DetMath.Abs(sz) > 1e-6f)
            r = Vec3.Cross(u, zAxis).Normalized();
        else if (DetMath.Abs(sy) <= 1e-6f && DetMath.Abs(sx) > 1e-6f && DetMath.Abs(sz) > 1e-6f)
            u = Vec3.Cross(zAxis, r).Normalized();
        else if (DetMath.Abs(sz) <= 1e-6f && DetMath.Abs(sx) > 1e-6f && DetMath.Abs(sy) > 1e-6f)
            zAxis = Vec3.Cross(r, u).Normalized();

        r = r.Normalized();
        if (r.LengthSq() < 1e-8f) r = Vec3.UnitX;
        u = (u - r * Vec3.Dot(r, u)).Normalized();
        if (u.LengthSq() < 1e-8f)
        {
            u = Vec3.Cross(zAxis, r).Normalized();
            if (u.LengthSq() < 1e-8f) u = Vec3.Cross(Vec3.UnitZ, r).Normalized();
            if (u.LengthSq() < 1e-8f) u = Vec3.Cross(Vec3.UnitX, r).Normalized();
        }
        zAxis = Vec3.Cross(r, u).Normalized();

        float m00 = r.X, m01 = u.X, m02 = zAxis.X;
        float m10 = r.Y, m11 = u.Y, m12 = zAxis.Y;
        float m20 = r.Z, m21 = u.Z, m22 = zAxis.Z;

        float trace = m00 + m11 + m22;
        if (trace > 0f)
        {
            float s = DetMath.Sqrt(trace + 1f) * 2f;
            rot = new Quat((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25f * s).Normalized();
        }
        else if (m00 > m11 && m00 > m22)
        {
            float s = DetMath.Sqrt(1f + m00 - m11 - m22) * 2f;
            rot = new Quat(0.25f * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s).Normalized();
        }
        else if (m11 > m22)
        {
            float s = DetMath.Sqrt(1f + m11 - m00 - m22) * 2f;
            rot = new Quat((m01 + m10) / s, 0.25f * s, (m12 + m21) / s, (m02 - m20) / s).Normalized();
        }
        else
        {
            float s = DetMath.Sqrt(1f + m22 - m00 - m11) * 2f;
            rot = new Quat((m02 + m20) / s, (m12 + m21) / s, 0.25f * s, (m10 - m01) / s).Normalized();
        }
    }

    // --- Streaming Utf8JsonReader Parsers (100% Zero Reflection) ---

    private static void ExpectToken(ref Utf8JsonReader r, JsonTokenType expected, string context)
    {
        if (!r.Read() || r.TokenType != expected)
            throw new InvalidDataException($"Expected {expected} in glTF {context}.");
    }

    private static int ReadInt32Checked(ref Utf8JsonReader r, string context)
    {
        if (!r.Read() || r.TokenType != JsonTokenType.Number || !r.TryGetInt32(out int v))
            throw new InvalidDataException($"Expected integer in glTF {context}.");
        return v;
    }

    private static float ReadSingleChecked(ref Utf8JsonReader r, string context)
    {
        if (!r.Read() || r.TokenType != JsonTokenType.Number || !r.TryGetSingle(out float v))
            throw new InvalidDataException($"Expected number in glTF {context}.");
        return v;
    }

    private static bool ReadBoolChecked(ref Utf8JsonReader r, string context)
    {
        if (!r.Read() || (r.TokenType != JsonTokenType.True && r.TokenType != JsonTokenType.False))
            throw new InvalidDataException($"Expected boolean in glTF {context}.");
        return r.GetBoolean();
    }

    private static string? ReadStringChecked(ref Utf8JsonReader r, string context)
    {
        if (!r.Read() || (r.TokenType != JsonTokenType.String && r.TokenType != JsonTokenType.Null))
            throw new InvalidDataException($"Expected string in glTF {context}.");
        return r.TokenType == JsonTokenType.Null ? null : r.GetString();
    }

    private static void ParseRootJson(
        ref Utf8JsonReader r, string? baseDir, byte* glbBinPtr, int glbBinLen, ref LinearArena scratch,
        ref NativeList<RawBuffer> buffers, ref NativeList<RawBufferView> views, ref NativeList<RawAccessor> accessors,
        ref NativeList<RawImage> images, ref NativeList<RawTexture> textures, ref NativeList<RawMat> materials,
        ref NativeList<RawPrim> prims, ref NativeList<RawMesh> meshes, ref NativeList<RawNode> nodes,
        ref NativeList<int> nodeChildren, ref NativeList<RawSkin> skins, ref NativeList<int> skinJoints,
        ref NativeList<RawAnim> anims, ref NativeList<RawAnimSampler> animSamplers, ref NativeList<RawAnimChannel> animChannels)
    {
        ExpectToken(ref r, JsonTokenType.StartObject, "root");
        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
        {
            if (r.TokenType != JsonTokenType.PropertyName) continue;
            if (r.ValueTextEquals("buffers"u8)) ParseBuffers(ref r, baseDir, glbBinPtr, glbBinLen, ref scratch, ref buffers);
            else if (r.ValueTextEquals("bufferViews"u8)) ParseBufferViews(ref r, ref views);
            else if (r.ValueTextEquals("accessors"u8)) ParseAccessors(ref r, ref accessors);
            else if (r.ValueTextEquals("images"u8)) ParseImages(ref r, ref scratch, ref images);
            else if (r.ValueTextEquals("textures"u8)) ParseTextures(ref r, ref textures);
            else if (r.ValueTextEquals("materials"u8)) ParseMaterials(ref r, ref materials);
            else if (r.ValueTextEquals("meshes"u8)) ParseMeshes(ref r, ref prims, ref meshes);
            else if (r.ValueTextEquals("nodes"u8)) ParseNodes(ref r, ref nodes, ref nodeChildren);
            else if (r.ValueTextEquals("skins"u8)) ParseSkins(ref r, ref skins, ref skinJoints);
            else if (r.ValueTextEquals("animations"u8)) ParseAnimations(ref r, ref anims, ref animSamplers, ref animChannels);
            else { if (!r.Read()) throw new InvalidDataException("Unexpected EOF in glTF root."); r.Skip(); }
        }
    }

    private static void ParseBuffers(ref Utf8JsonReader r, string? baseDir, byte* glbBinPtr, int glbBinLen, ref LinearArena scratch, ref NativeList<RawBuffer> buffers)
    {
        ExpectToken(ref r, JsonTokenType.StartArray, "buffers");
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            if (r.TokenType != JsonTokenType.StartObject)
                throw new InvalidDataException("Expected object in glTF buffers array.");
            int byteLen = 0;
            string? uri = null;
            while (r.Read() && r.TokenType != JsonTokenType.EndObject)
            {
                if (r.TokenType != JsonTokenType.PropertyName) continue;
                if (r.ValueTextEquals("byteLength"u8)) byteLen = ReadInt32Checked(ref r, "buffer.byteLength");
                else if (r.ValueTextEquals("uri"u8)) uri = ReadStringChecked(ref r, "buffer.uri");
                else { r.Read(); r.Skip(); }
            }

            if (byteLen < 0)
                throw new InvalidDataException($"glTF buffer byteLength cannot be negative ({byteLen}).");

            if (uri == null && glbBinPtr != null)
            {
                buffers.Add(new RawBuffer { Ptr = glbBinPtr, Length = glbBinLen });
            }
            else if (uri != null && uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                int comma = uri.IndexOf(',');
                if (comma <= 0)
                    throw new InvalidDataException("Malformed data URI in glTF buffer.");
                byte[] decoded = Convert.FromBase64String(uri.Substring(comma + 1));
                byte* ptr = scratch.AllocUninitialized<byte>(Math.Max(1, decoded.Length), 16);
                if (decoded.Length > 0) decoded.CopyTo(new Span<byte>(ptr, decoded.Length));
                buffers.Add(new RawBuffer { Ptr = ptr, Length = decoded.Length });
            }
            else if (uri != null && baseDir != null)
            {
                if (!PackCompressionSystem.TryResolveSafeRelativePath(baseDir, uri, out string fullPath))
                    throw new InvalidDataException($"Unsafe external glTF buffer URI rejected: '{uri}'.");
                if (!File.Exists(fullPath))
                    throw new FileNotFoundException($"External glTF buffer file not found: '{fullPath}'.", fullPath);
                byte[] external = File.ReadAllBytes(fullPath);
                byte* ptr = scratch.AllocUninitialized<byte>(Math.Max(1, external.Length), 16);
                if (external.Length > 0) external.CopyTo(new Span<byte>(ptr, external.Length));
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
        ExpectToken(ref r, JsonTokenType.StartArray, "bufferViews");
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            if (r.TokenType != JsonTokenType.StartObject)
                throw new InvalidDataException("Expected object in glTF bufferViews array.");
            RawBufferView bv = default;
            while (r.Read() && r.TokenType != JsonTokenType.EndObject)
            {
                if (r.TokenType != JsonTokenType.PropertyName) continue;
                if (r.ValueTextEquals("buffer"u8)) bv.Buffer = ReadInt32Checked(ref r, "bufferView.buffer");
                else if (r.ValueTextEquals("byteOffset"u8)) bv.ByteOffset = ReadInt32Checked(ref r, "bufferView.byteOffset");
                else if (r.ValueTextEquals("byteLength"u8)) bv.ByteLength = ReadInt32Checked(ref r, "bufferView.byteLength");
                else if (r.ValueTextEquals("byteStride"u8)) bv.ByteStride = ReadInt32Checked(ref r, "bufferView.byteStride");
                else { r.Read(); r.Skip(); }
            }
            views.Add(bv);
        }
    }

    private static void ParseAccessors(ref Utf8JsonReader r, ref NativeList<RawAccessor> accessors)
    {
        ExpectToken(ref r, JsonTokenType.StartArray, "accessors");
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            if (r.TokenType != JsonTokenType.StartObject)
                throw new InvalidDataException("Expected object in glTF accessors array.");
            RawAccessor a = new RawAccessor { BufferView = -1, TypeComponents = 1 };
            while (r.Read() && r.TokenType != JsonTokenType.EndObject)
            {
                if (r.TokenType != JsonTokenType.PropertyName) continue;
                if (r.ValueTextEquals("bufferView"u8)) a.BufferView = ReadInt32Checked(ref r, "accessor.bufferView");
                else if (r.ValueTextEquals("byteOffset"u8)) a.ByteOffset = ReadInt32Checked(ref r, "accessor.byteOffset");
                else if (r.ValueTextEquals("componentType"u8)) a.ComponentType = ReadInt32Checked(ref r, "accessor.componentType");
                else if (r.ValueTextEquals("count"u8)) a.Count = ReadInt32Checked(ref r, "accessor.count");
                else if (r.ValueTextEquals("normalized"u8)) a.Normalized = (byte)(ReadBoolChecked(ref r, "accessor.normalized") ? 1 : 0);
                else if (r.ValueTextEquals("type"u8))
                {
                    if (!r.Read() || r.TokenType != JsonTokenType.String)
                        throw new InvalidDataException("Expected string for glTF accessor.type.");
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

    private static void ParseImages(ref Utf8JsonReader r, ref LinearArena scratch, ref NativeList<RawImage> images)
    {
        ExpectToken(ref r, JsonTokenType.StartArray, "images");
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            if (r.TokenType != JsonTokenType.StartObject)
                throw new InvalidDataException("Expected object in glTF images array.");
            RawImage img = new RawImage { BufferView = -1 };
            while (r.Read() && r.TokenType != JsonTokenType.EndObject)
            {
                if (r.TokenType != JsonTokenType.PropertyName) continue;
                if (r.ValueTextEquals("bufferView"u8)) img.BufferView = ReadInt32Checked(ref r, "image.bufferView");
                else if (r.ValueTextEquals("uri"u8))
                {
                    string? uriStr = ReadStringChecked(ref r, "image.uri");
                    if (!string.IsNullOrEmpty(uriStr))
                    {
                        if (uriStr.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                        {
                            int comma = uriStr.IndexOf(',');
                            if (comma <= 0)
                                throw new InvalidDataException("Malformed data URI in glTF image.");
                            byte[] decoded = Convert.FromBase64String(uriStr.Substring(comma + 1));
                            if (decoded.Length > 0)
                            {
                                byte* ptr = scratch.AllocUninitialized<byte>(decoded.Length, 16);
                                decoded.CopyTo(new Span<byte>(ptr, decoded.Length));
                                img.UriOrDataPtr = ptr;
                                img.UriOrDataLen = decoded.Length;
                                img.IsDataUri = 1;
                            }
                        }
                        else
                        {
                            int byteCount = Encoding.UTF8.GetByteCount(uriStr);
                            byte* ptr = scratch.AllocUninitialized<byte>(byteCount, 4);
                            Encoding.UTF8.GetBytes(uriStr, new Span<byte>(ptr, byteCount));
                            img.UriOrDataPtr = ptr;
                            img.UriOrDataLen = byteCount;
                            img.IsDataUri = 0;
                        }
                    }
                }
                else { r.Read(); r.Skip(); }
            }
            images.Add(img);
        }
    }

    private static void ParseTextures(ref Utf8JsonReader r, ref NativeList<RawTexture> textures)
    {
        ExpectToken(ref r, JsonTokenType.StartArray, "textures");
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            if (r.TokenType != JsonTokenType.StartObject)
                throw new InvalidDataException("Expected object in glTF textures array.");
            RawTexture tex = new RawTexture { Source = -1 };
            while (r.Read() && r.TokenType != JsonTokenType.EndObject)
            {
                if (r.TokenType != JsonTokenType.PropertyName) continue;
                if (r.ValueTextEquals("source"u8)) tex.Source = ReadInt32Checked(ref r, "texture.source");
                else { r.Read(); r.Skip(); }
            }
            textures.Add(tex);
        }
    }

    private static void ParseMaterials(ref Utf8JsonReader r, ref NativeList<RawMat> materials)
    {
        ExpectToken(ref r, JsonTokenType.StartArray, "materials");
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            if (r.TokenType != JsonTokenType.StartObject)
                throw new InvalidDataException("Expected object in glTF materials array.");
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
                if (r.ValueTextEquals("name"u8))
                {
                    if (!r.Read() || r.TokenType != JsonTokenType.String)
                        throw new InvalidDataException("Expected string for glTF material.name.");
                    m.Name = FixedString64.FromUtf8(r.ValueSpan);
                }
                else if (r.ValueTextEquals("doubleSided"u8)) m.DoubleSided = (byte)(ReadBoolChecked(ref r, "material.doubleSided") ? 1 : 0);
                else if (r.ValueTextEquals("alphaCutoff"u8)) m.AlphaCutoff = ReadSingleChecked(ref r, "material.alphaCutoff");
                else if (r.ValueTextEquals("alphaMode"u8))
                {
                    if (!r.Read() || r.TokenType != JsonTokenType.String)
                        throw new InvalidDataException("Expected string for glTF material.alphaMode.");
                    if (r.ValueTextEquals("MASK"u8)) m.AlphaMode = MaterialAlphaMode.Mask;
                    else if (r.ValueTextEquals("BLEND"u8)) m.AlphaMode = MaterialAlphaMode.Blend;
                }
                else if (r.ValueTextEquals("emissiveFactor"u8))
                {
                    m.Emissive = ReadVec3Array(ref r);
                }
                else if (r.ValueTextEquals("pbrMetallicRoughness"u8))
                {
                    ExpectToken(ref r, JsonTokenType.StartObject, "material.pbrMetallicRoughness");
                    while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                    {
                        if (r.TokenType != JsonTokenType.PropertyName) continue;
                        if (r.ValueTextEquals("baseColorFactor"u8))
                        {
                            Vec4 c = ReadVec4Array(ref r);
                            m.BaseColor = new ColorF(c.X, c.Y, c.Z, c.W);
                        }
                        else if (r.ValueTextEquals("metallicFactor"u8)) m.Metallic = ReadSingleChecked(ref r, "pbrMetallicRoughness.metallicFactor");
                        else if (r.ValueTextEquals("roughnessFactor"u8)) m.Roughness = ReadSingleChecked(ref r, "pbrMetallicRoughness.roughnessFactor");
                        else if (r.ValueTextEquals("baseColorTexture"u8))
                        {
                            ExpectToken(ref r, JsonTokenType.StartObject, "pbrMetallicRoughness.baseColorTexture");
                            while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                            {
                                if (r.TokenType == JsonTokenType.PropertyName && r.ValueTextEquals("index"u8))
                                    m.BaseColorTex = ReadInt32Checked(ref r, "baseColorTexture.index");
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
        ExpectToken(ref r, JsonTokenType.StartArray, "meshes");
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            if (r.TokenType != JsonTokenType.StartObject)
                throw new InvalidDataException("Expected object in glTF meshes array.");
            RawMesh mesh = new RawMesh { PrimStart = prims.Count, PrimCount = 0 };
            while (r.Read() && r.TokenType != JsonTokenType.EndObject)
            {
                if (r.TokenType != JsonTokenType.PropertyName) continue;
                if (r.ValueTextEquals("primitives"u8))
                {
                    ExpectToken(ref r, JsonTokenType.StartArray, "mesh.primitives");
                    while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                    {
                        if (r.TokenType != JsonTokenType.StartObject)
                            throw new InvalidDataException("Expected object in glTF mesh.primitives array.");
                        RawPrim p = new RawPrim { PosAcc = -1, NormAcc = -1, UvAcc = -1, ColorAcc = -1, JointsAcc = -1, WeightsAcc = -1, IndicesAcc = -1, MaterialIdx = -1 };
                        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                        {
                            if (r.TokenType != JsonTokenType.PropertyName) continue;
                            if (r.ValueTextEquals("indices"u8)) p.IndicesAcc = ReadInt32Checked(ref r, "primitive.indices");
                            else if (r.ValueTextEquals("material"u8)) p.MaterialIdx = ReadInt32Checked(ref r, "primitive.material");
                            else if (r.ValueTextEquals("attributes"u8))
                            {
                                ExpectToken(ref r, JsonTokenType.StartObject, "primitive.attributes");
                                while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                                {
                                    if (r.TokenType != JsonTokenType.PropertyName) continue;
                                    if (r.ValueTextEquals("POSITION"u8)) p.PosAcc = ReadInt32Checked(ref r, "attributes.POSITION");
                                    else if (r.ValueTextEquals("NORMAL"u8)) p.NormAcc = ReadInt32Checked(ref r, "attributes.NORMAL");
                                    else if (r.ValueTextEquals("TEXCOORD_0"u8)) p.UvAcc = ReadInt32Checked(ref r, "attributes.TEXCOORD_0");
                                    else if (r.ValueTextEquals("COLOR_0"u8)) p.ColorAcc = ReadInt32Checked(ref r, "attributes.COLOR_0");
                                    else if (r.ValueTextEquals("JOINTS_0"u8)) p.JointsAcc = ReadInt32Checked(ref r, "attributes.JOINTS_0");
                                    else if (r.ValueTextEquals("WEIGHTS_0"u8)) p.WeightsAcc = ReadInt32Checked(ref r, "attributes.WEIGHTS_0");
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
        ExpectToken(ref r, JsonTokenType.StartArray, "nodes");
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            if (r.TokenType != JsonTokenType.StartObject)
                throw new InvalidDataException("Expected object in glTF nodes array.");
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
                if (r.ValueTextEquals("name"u8))
                {
                    if (!r.Read() || r.TokenType != JsonTokenType.String)
                        throw new InvalidDataException("Expected string for glTF node.name.");
                    n.Name = FixedString64.FromUtf8(r.ValueSpan);
                }
                else if (r.ValueTextEquals("mesh"u8)) n.MeshIndex = ReadInt32Checked(ref r, "node.mesh");
                else if (r.ValueTextEquals("skin"u8)) n.SkinIndex = ReadInt32Checked(ref r, "node.skin");
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
                    ExpectToken(ref r, JsonTokenType.StartArray, "node.children");
                    while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                    {
                        if (r.TokenType != JsonTokenType.Number || !r.TryGetInt32(out int childIdx))
                            throw new InvalidDataException("Expected integer in glTF node.children array.");
                        children.Add(childIdx);
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
        ExpectToken(ref r, JsonTokenType.StartArray, "skins");
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            if (r.TokenType != JsonTokenType.StartObject)
                throw new InvalidDataException("Expected object in glTF skins array.");
            RawSkin s = new RawSkin { IbmAcc = -1, JointStart = joints.Count };
            while (r.Read() && r.TokenType != JsonTokenType.EndObject)
            {
                if (r.TokenType != JsonTokenType.PropertyName) continue;
                if (r.ValueTextEquals("inverseBindMatrices"u8)) s.IbmAcc = ReadInt32Checked(ref r, "skin.inverseBindMatrices");
                else if (r.ValueTextEquals("joints"u8))
                {
                    s.JointStart = joints.Count;
                    ExpectToken(ref r, JsonTokenType.StartArray, "skin.joints");
                    while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                    {
                        if (r.TokenType != JsonTokenType.Number || !r.TryGetInt32(out int jIdx))
                            throw new InvalidDataException("Expected integer in glTF skin.joints array.");
                        joints.Add(jIdx);
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
        ExpectToken(ref r, JsonTokenType.StartArray, "animations");
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            if (r.TokenType != JsonTokenType.StartObject)
                throw new InvalidDataException("Expected object in glTF animations array.");
            RawAnim a = new RawAnim { SamplerStart = samplers.Count, ChannelStart = channels.Count };
            while (r.Read() && r.TokenType != JsonTokenType.EndObject)
            {
                if (r.TokenType != JsonTokenType.PropertyName) continue;
                if (r.ValueTextEquals("name"u8))
                {
                    if (!r.Read() || r.TokenType != JsonTokenType.String)
                        throw new InvalidDataException("Expected string for glTF animation.name.");
                    a.Name = FixedString64.FromUtf8(r.ValueSpan);
                }
                else if (r.ValueTextEquals("samplers"u8))
                {
                    a.SamplerStart = samplers.Count;
                    ExpectToken(ref r, JsonTokenType.StartArray, "animation.samplers");
                    while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                    {
                        if (r.TokenType != JsonTokenType.StartObject)
                            throw new InvalidDataException("Expected object in glTF animation.samplers array.");
                        RawAnimSampler s = new RawAnimSampler { InputAcc = -1, OutputAcc = -1, Interp = AnimInterpolation.Linear };
                        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                        {
                            if (r.TokenType != JsonTokenType.PropertyName) continue;
                            if (r.ValueTextEquals("input"u8)) s.InputAcc = ReadInt32Checked(ref r, "sampler.input");
                            else if (r.ValueTextEquals("output"u8)) s.OutputAcc = ReadInt32Checked(ref r, "sampler.output");
                            else if (r.ValueTextEquals("interpolation"u8))
                            {
                                if (!r.Read() || r.TokenType != JsonTokenType.String)
                                    throw new InvalidDataException("Expected string for glTF sampler.interpolation.");
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
                    ExpectToken(ref r, JsonTokenType.StartArray, "animation.channels");
                    while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                    {
                        if (r.TokenType != JsonTokenType.StartObject)
                            throw new InvalidDataException("Expected object in glTF animation.channels array.");
                        RawAnimChannel c = new RawAnimChannel { SamplerIdx = -1, TargetNode = -1 };
                        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                        {
                            if (r.TokenType != JsonTokenType.PropertyName) continue;
                            if (r.ValueTextEquals("sampler"u8)) c.SamplerIdx = ReadInt32Checked(ref r, "channel.sampler");
                            else if (r.ValueTextEquals("target"u8))
                            {
                                ExpectToken(ref r, JsonTokenType.StartObject, "channel.target");
                                while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                                {
                                    if (r.TokenType != JsonTokenType.PropertyName) continue;
                                    if (r.ValueTextEquals("node"u8)) c.TargetNode = ReadInt32Checked(ref r, "target.node");
                                    else if (r.ValueTextEquals("path"u8))
                                    {
                                        if (!r.Read() || r.TokenType != JsonTokenType.String)
                                            throw new InvalidDataException("Expected string for glTF target.path.");
                                        if (r.ValueTextEquals("translation"u8)) { c.Path = AnimTargetPath.Translation; c.HasPath = 1; }
                                        else if (r.ValueTextEquals("rotation"u8)) { c.Path = AnimTargetPath.Rotation; c.HasPath = 1; }
                                        else if (r.ValueTextEquals("scale"u8)) { c.Path = AnimTargetPath.Scale; c.HasPath = 1; }
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
        ExpectToken(ref r, JsonTokenType.StartArray, "vec3");
        float x = ReadSingleChecked(ref r, "vec3[0]");
        float y = ReadSingleChecked(ref r, "vec3[1]");
        float z = ReadSingleChecked(ref r, "vec3[2]");
        ExpectToken(ref r, JsonTokenType.EndArray, "vec3 end");
        return new Vec3(x, y, z);
    }

    private static Vec4 ReadVec4Array(ref Utf8JsonReader r)
    {
        ExpectToken(ref r, JsonTokenType.StartArray, "vec4");
        float x = ReadSingleChecked(ref r, "vec4[0]");
        float y = ReadSingleChecked(ref r, "vec4[1]");
        float z = ReadSingleChecked(ref r, "vec4[2]");
        float w = ReadSingleChecked(ref r, "vec4[3]");
        ExpectToken(ref r, JsonTokenType.EndArray, "vec4 end");
        return new Vec4(x, y, z, w);
    }

    private static Mat4 ReadMat4Array(ref Utf8JsonReader r)
    {
        float* f = stackalloc float[16];
        ExpectToken(ref r, JsonTokenType.StartArray, "mat4");
        for (int i = 0; i < 16; i++)
            f[i] = ReadSingleChecked(ref r, "mat4 element");
        ExpectToken(ref r, JsonTokenType.EndArray, "mat4 end");
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
        ArenaMarker mark = scratchArena.SaveMarker();
        EntityId* nodeEntities = null;
        int spawnedNodeCount = 0;

        try
        {
            TransformSystem.Add(ref world.Transforms, root, position, rotation, scale);

            if (model->NodeCount == 0) return root;

            nodeEntities = scratchArena.Alloc<EntityId>(model->NodeCount);
            AnimPlayerHandle* skinAnimPlayers = model->SkinCount > 0 ? scratchArena.AllocUninitialized<AnimPlayerHandle>(model->SkinCount) : null;

            for (int s = 0; s < model->SkinCount; s++)
            {
                AnimClipHandle clip = (autoPlayFirstAnimation && model->AnimationCount > 0)
                    ? model->Animations[0] : AnimClipHandle.Invalid;
                skinAnimPlayers[s] = AnimationSystem.AddPlayer(ref world.Animations, root, model->Skins[s], clip, ownerScene);
            }

            // First pass: spawn entities for every node
            for (int n = 0; n < model->NodeCount; n++)
            {
                nodeEntities[n] = EntitySystem.Spawn(ref world.Entities, ownerScene);
                spawnedNodeCount++;
            }

            // Second pass: add transforms & mesh renderers
            for (int n = 0; n < model->NodeCount; n++)
            {
                GltfNodeData nd = model->Nodes[n];
                EntityId parent = (nd.ParentIndex >= 0 && nd.ParentIndex < model->NodeCount)
                    ? nodeEntities[nd.ParentIndex] : root;
                TransformSystem.Add(ref world.Transforms, nodeEntities[n], nd.LocalPosition, nd.LocalRotation, nd.LocalScale, parent);

                AnimPlayerHandle animPlayer = (nd.SkinIndex >= 0 && nd.SkinIndex < model->SkinCount) ? skinAnimPlayers[nd.SkinIndex] : AnimPlayerHandle.Invalid;
                for (int p = 0; p < nd.PrimitiveCount; p++)
                {
                    GltfPrimitiveRef pr = model->Primitives[nd.PrimitiveStart + p];
                    RenderComponentSystem.AddMeshRenderer(
                        ref world.RenderComponents, nodeEntities[n], pr.Mesh, pr.Material,
                        ownerScene, tint, layerMask, animPlayer);
                }
            }

            return root;
        }
        catch
        {
            if (nodeEntities != null)
            {
                for (int n = 0; n < spawnedNodeCount; n++)
                {
                    if (EntitySystem.IsAlive(in world.Entities, nodeEntities[n]))
                        EngineWorldSystem.DestroyEntity(ref world, nodeEntities[n]);
                }
            }
            if (EntitySystem.IsAlive(in world.Entities, root))
                EngineWorldSystem.DestroyEntity(ref world, root);
            throw;
        }
        finally
        {
            scratchArena.Rollback(mark);
        }
    }
}
