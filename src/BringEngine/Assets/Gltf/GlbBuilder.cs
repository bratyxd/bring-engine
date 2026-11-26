using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using BringEngine.Core.Math;

namespace BringEngine.Assets.Gltf;

/// <summary>
/// Spec-compliant glTF 2.0 (.glb) binary exporter/generator (100% NativeAOT, zero reflection).
/// Allows tests and games to author genuine .glb files (static PBR meshes AND skinned/animated rigs)
/// loaded through Bring Engine's first-class GltfLoader pipeline.
/// </summary>
public static unsafe class GlbBuilder
{
    public struct MeshSpec
    {
        public Vec3[] Positions;
        public Vec3[] Normals;
        public Vec2[] Uvs;
        public ColorF[]? Colors;
        public byte[]? Joints4;    // 4 bytes per vertex
        public Vec4[]? Weights4;   // 4 floats per vertex
        public uint[] Indices;
        public ColorF BaseColor;
        public Vec3 Emissive;
        public float Metallic;
        public float Roughness;
    }

    /// <summary>
    /// Writes a single-mesh PBR .glb file (optionally with 2-joint skinning & bobbing/swaying animation!).
    /// </summary>
    public static byte[] BuildGlb(in MeshSpec mesh, bool includeDemoAnimation = false)
    {
        if (mesh.Positions == null || mesh.Positions.Length == 0)
            throw new ArgumentException("GlbBuilder MeshSpec.Positions must be non-null and non-empty.", nameof(mesh));
        if (mesh.Normals == null || mesh.Normals.Length != mesh.Positions.Length)
            throw new ArgumentException("GlbBuilder MeshSpec.Normals must be non-null and match Positions length.", nameof(mesh));
        if (mesh.Uvs == null || mesh.Uvs.Length != mesh.Positions.Length)
            throw new ArgumentException("GlbBuilder MeshSpec.Uvs must be non-null and match Positions length.", nameof(mesh));
        if (mesh.Indices == null || mesh.Indices.Length == 0)
            throw new ArgumentException("GlbBuilder MeshSpec.Indices must be non-null and non-empty.", nameof(mesh));
        if (mesh.Colors != null && mesh.Colors.Length != mesh.Positions.Length)
            throw new ArgumentException("GlbBuilder MeshSpec.Colors length must match Positions length.", nameof(mesh));
        if ((mesh.Joints4 != null) != (mesh.Weights4 != null))
            throw new ArgumentException("GlbBuilder MeshSpec.Joints4 and Weights4 must both be set or both null.", nameof(mesh));
        if (mesh.Joints4 != null)
        {
            if (mesh.Joints4.Length != mesh.Positions.Length * 4)
                throw new ArgumentException("GlbBuilder MeshSpec.Joints4 length must be Positions.Length * 4.", nameof(mesh));
            if (mesh.Weights4!.Length != mesh.Positions.Length)
                throw new ArgumentException("GlbBuilder MeshSpec.Weights4 length must match Positions length.", nameof(mesh));
        }
        for (int i = 0; i < mesh.Indices.Length; i++)
        {
            if (mesh.Indices[i] >= (uint)mesh.Positions.Length)
                throw new ArgumentOutOfRangeException(nameof(mesh), $"GlbBuilder index {mesh.Indices[i]} out of range [0..{mesh.Positions.Length}).");
        }

        using var bin = new MemoryStream();
        using var bw = new BinaryWriter(bin);

        // Helper to write aligned chunk to BIN and record (offset, length)
        (int off, int len) WriteFloats(float[] data)
        {
            while ((bin.Position & 3) != 0) bw.Write((byte)0);
            int o = (int)bin.Position;
            for (int i = 0; i < data.Length; i++) bw.Write(data[i]);
            return (o, data.Length * 4);
        }

        (int off, int len) WriteVec3s(Vec3[] data)
        {
            while ((bin.Position & 3) != 0) bw.Write((byte)0);
            int o = (int)bin.Position;
            for (int i = 0; i < data.Length; i++) { bw.Write(data[i].X); bw.Write(data[i].Y); bw.Write(data[i].Z); }
            return (o, data.Length * 12);
        }

        (int off, int len) WriteVec2s(Vec2[] data)
        {
            while ((bin.Position & 3) != 0) bw.Write((byte)0);
            int o = (int)bin.Position;
            for (int i = 0; i < data.Length; i++) { bw.Write(data[i].X); bw.Write(data[i].Y); }
            return (o, data.Length * 8);
        }

        (int off, int len) WriteVec4s(Vec4[] data)
        {
            while ((bin.Position & 3) != 0) bw.Write((byte)0);
            int o = (int)bin.Position;
            for (int i = 0; i < data.Length; i++) { bw.Write(data[i].X); bw.Write(data[i].Y); bw.Write(data[i].Z); bw.Write(data[i].W); }
            return (o, data.Length * 16);
        }

        (int off, int len) WriteBytes(byte[] data)
        {
            while ((bin.Position & 3) != 0) bw.Write((byte)0);
            int o = (int)bin.Position;
            bw.Write(data);
            return (o, data.Length);
        }

        (int off, int len) WriteUInts(uint[] data)
        {
            while ((bin.Position & 3) != 0) bw.Write((byte)0);
            int o = (int)bin.Position;
            for (int i = 0; i < data.Length; i++) bw.Write(data[i]);
            return (o, data.Length * 4);
        }

        // Compute min/max for POSITION accessor
        Vec3 minP = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
        Vec3 maxP = new Vec3(float.MinValue, float.MinValue, float.MinValue);
        for (int i = 0; i < mesh.Positions.Length; i++)
        {
            minP = Vec3.Min(minP, mesh.Positions[i]);
            maxP = Vec3.Max(maxP, mesh.Positions[i]);
        }

        var bvPos = WriteVec3s(mesh.Positions); // bv 0, acc 0
        var bvNorm = WriteVec3s(mesh.Normals);  // bv 1, acc 1
        var bvUv = WriteVec2s(mesh.Uvs);        // bv 2, acc 2
        var bvIdx = WriteUInts(mesh.Indices);   // bv 3, acc 3

        bool hasColors = mesh.Colors != null && mesh.Colors.Length == mesh.Positions.Length;
        bool hasSkin = mesh.Joints4 != null && mesh.Weights4 != null;

        var bvList = new StringBuilder();
        var accList = new StringBuilder();
        int nextBv = 0, nextAcc = 0;

        void AddBvAcc(int off, int len, int compType, int count, string type, string? extraAcc = null)
        {
            if (nextBv > 0) bvList.Append(',');
            bvList.Append($"{{\"buffer\":0,\"byteOffset\":{off},\"byteLength\":{len}}}");
            if (nextAcc > 0) accList.Append(',');
            accList.Append($"{{\"bufferView\":{nextBv},\"componentType\":{compType},\"count\":{count},\"type\":\"{type}\"{extraAcc ?? ""}}}");
            nextBv++;
            nextAcc++;
        }

        string minMaxJson = $",\"min\":[{Fmt(minP.X)},{Fmt(minP.Y)},{Fmt(minP.Z)}],\"max\":[{Fmt(maxP.X)},{Fmt(maxP.Y)},{Fmt(maxP.Z)}]";
        AddBvAcc(bvPos.off, bvPos.len, 5126, mesh.Positions.Length, "VEC3", minMaxJson);
        AddBvAcc(bvNorm.off, bvNorm.len, 5126, mesh.Normals.Length, "VEC3");
        AddBvAcc(bvUv.off, bvUv.len, 5126, mesh.Uvs.Length, "VEC2");
        AddBvAcc(bvIdx.off, bvIdx.len, 5125, mesh.Indices.Length, "SCALAR");

        int colorAcc = -1, jointsAcc = -1, weightsAcc = -1;
        if (hasColors)
        {
            Vec4[] cols = new Vec4[mesh.Colors!.Length];
            for (int i = 0; i < cols.Length; i++) cols[i] = mesh.Colors[i].ToVec4();
            var bvCol = WriteVec4s(cols);
            colorAcc = nextAcc;
            AddBvAcc(bvCol.off, bvCol.len, 5126, cols.Length, "VEC4");
        }

        int ibmAcc = -1, animTimeAcc = -1, animPosAcc = -1, animRotAcc = -1;
        if (hasSkin)
        {
            var bvJ = WriteBytes(mesh.Joints4!);
            jointsAcc = nextAcc;
            AddBvAcc(bvJ.off, bvJ.len, 5121, mesh.Positions.Length, "VEC4");

            var bvW = WriteVec4s(mesh.Weights4!);
            weightsAcc = nextAcc;
            AddBvAcc(bvW.off, bvW.len, 5126, mesh.Weights4!.Length, "VEC4");

            // 2 identity inverse bind matrices
            float[] ibms = new float[32];
            ibms[0] = 1; ibms[5] = 1; ibms[10] = 1; ibms[15] = 1;
            ibms[16] = 1; ibms[21] = 1; ibms[26] = 1; ibms[31] = 1;
            var bvIbm = WriteFloats(ibms);
            ibmAcc = nextAcc;
            AddBvAcc(bvIbm.off, bvIbm.len, 5126, 2, "MAT4");

            if (includeDemoAnimation)
            {
                var bvT = WriteFloats(new[] { 0f, 0.5f, 1.0f });
                animTimeAcc = nextAcc;
                AddBvAcc(bvT.off, bvT.len, 5126, 3, "SCALAR", ",\"min\":[0],\"max\":[1]");

                var bvP = WriteVec3s(new[] { new Vec3(0, 0f, 0), new Vec3(0, 0.22f, 0), new Vec3(0, 0f, 0) });
                animPosAcc = nextAcc;
                AddBvAcc(bvP.off, bvP.len, 5126, 3, "VEC3");

                Quat q0 = Quat.FromAxisAngle(Vec3.UnitZ, -0.18f);
                Quat q1 = Quat.FromAxisAngle(Vec3.UnitZ, 0.18f);
                var bvR = WriteVec4s(new[]
                {
                    new Vec4(q0.X, q0.Y, q0.Z, q0.W),
                    new Vec4(q1.X, q1.Y, q1.Z, q1.W),
                    new Vec4(q0.X, q0.Y, q0.Z, q0.W),
                });
                animRotAcc = nextAcc;
                AddBvAcc(bvR.off, bvR.len, 5126, 3, "VEC4");
            }
        }

        while ((bin.Position & 3) != 0) bw.Write((byte)0);
        byte[] binBytes = bin.ToArray();

        var attrs = new StringBuilder("\"POSITION\":0,\"NORMAL\":1,\"TEXCOORD_0\":2");
        if (colorAcc >= 0) attrs.Append($",\"COLOR_0\":{colorAcc}");
        if (jointsAcc >= 0) attrs.Append($",\"JOINTS_0\":{jointsAcc},\"WEIGHTS_0\":{weightsAcc}");

        string nodesJson = hasSkin
            ? "[{\"name\":\"MeshRoot\",\"mesh\":0,\"skin\":0,\"children\":[1]},{\"name\":\"JointRoot\",\"children\":[2]},{\"name\":\"JointTop\"}]"
            : "[{\"name\":\"MeshRoot\",\"mesh\":0}]";

        string skinsJson = hasSkin
            ? $",\"skins\":[{{\"inverseBindMatrices\":{ibmAcc},\"joints\":[1,2]}}]"
            : "";

        string animsJson = (hasSkin && includeDemoAnimation)
            ? $",\"animations\":[{{\"name\":\"IdleBob\",\"samplers\":[{{\"input\":{animTimeAcc},\"output\":{animPosAcc},\"interpolation\":\"LINEAR\"}},{{\"input\":{animTimeAcc},\"output\":{animRotAcc},\"interpolation\":\"LINEAR\"}}],\"channels\":[{{\"sampler\":0,\"target\":{{\"node\":1,\"path\":\"translation\"}}}},{{\"sampler\":1,\"target\":{{\"node\":2,\"path\":\"rotation\"}}}}]}}]"
            : "";

        string json = "{" +
            "\"asset\":{\"version\":\"2.0\",\"generator\":\"BringEngine.GlbBuilder\"}," +
            "\"scene\":0,\"scenes\":[{\"nodes\":[0]}]," +
            $"\"nodes\":{nodesJson}," +
            $"\"materials\":[{{\"name\":\"PBRMat\",\"pbrMetallicRoughness\":{{\"baseColorFactor\":[{Fmt(mesh.BaseColor.R)},{Fmt(mesh.BaseColor.G)},{Fmt(mesh.BaseColor.B)},{Fmt(mesh.BaseColor.A)}],\"metallicFactor\":{Fmt(mesh.Metallic)},\"roughnessFactor\":{Fmt(mesh.Roughness)}}},\"emissiveFactor\":[{Fmt(mesh.Emissive.X)},{Fmt(mesh.Emissive.Y)},{Fmt(mesh.Emissive.Z)}]}}]," +
            $"\"meshes\":[{{\"primitives\":[{{\"attributes\":{{{attrs}}},\"indices\":3,\"material\":0}}]}}]," +
            $"\"buffers\":[{{\"byteLength\":{binBytes.Length}}}]," +
            $"\"bufferViews\":[{bvList}]," +
            $"\"accessors\":[{accList}]" +
            skinsJson + animsJson + "}";

        byte[] jsonBytesRaw = Encoding.UTF8.GetBytes(json);
        int jsonPad = (4 - (jsonBytesRaw.Length & 3)) & 3;
        int jsonChunkLen = jsonBytesRaw.Length + jsonPad;
        int totalLen = 12 + 8 + jsonChunkLen + 8 + binBytes.Length;

        using var outMs = new MemoryStream(totalLen);
        using var ow = new BinaryWriter(outMs);
        ow.Write(0x46546C67u); // magic "glTF"
        ow.Write(2u);          // version 2
        ow.Write((uint)totalLen);

        ow.Write((uint)jsonChunkLen);
        ow.Write(0x4E4F534Au); // "JSON"
        ow.Write(jsonBytesRaw);
        for (int i = 0; i < jsonPad; i++) ow.Write((byte)0x20); // JSON space padding

        ow.Write((uint)binBytes.Length);
        ow.Write(0x004E4942u); // "BIN\0"
        ow.Write(binBytes);

        return outMs.ToArray();
    }

    private static string Fmt(float v)
    {
        if (!float.IsFinite(v))
            throw new ArgumentOutOfRangeException(nameof(v), v, "GlbBuilder cannot serialize non-finite float to glTF JSON.");
        return v.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Generates a beveled glowing crystal/octahedron MeshSpec (optional 2-joint skinning for animation!).
    /// </summary>
    public static MeshSpec CreateCrystalSpec(float radius, float height, ColorF baseColor, Vec3 emissive, bool skinned = true)
    {
        Vec3 top = new Vec3(0, height, 0);
        Vec3 bot = new Vec3(0, 0, 0);
        Vec3 mid0 = new Vec3(radius, height * 0.5f, 0);
        Vec3 mid1 = new Vec3(0, height * 0.5f, radius);
        Vec3 mid2 = new Vec3(-radius, height * 0.5f, 0);
        Vec3 mid3 = new Vec3(0, height * 0.5f, -radius);

        Vec3[] ring = new[] { mid0, mid1, mid2, mid3 };
        Vec3[] pos = new Vec3[24];
        Vec3[] nrm = new Vec3[24];
        Vec2[] uv = new Vec2[24];
        uint[] idx = new uint[24];
        byte[]? joints = skinned ? new byte[24 * 4] : null;
        Vec4[]? weights = skinned ? new Vec4[24] : null;

        int v = 0;
        for (int f = 0; f < 4; f++)
        {
            Vec3 a = ring[f];
            Vec3 b = ring[(f + 1) & 3];

            // Upper face (b, a, top) -> right-handed CCW outward normal: Cross(a-b, top-b)
            Vec3 nUp = Vec3.Cross(a - b, top - b).Normalized();
            AddTri(b, a, top, nUp, 0.5f, 0.5f, 1f);

            // Lower face (a, b, bot) -> right-handed CCW outward normal: Cross(b-a, bot-a)
            Vec3 nDn = Vec3.Cross(b - a, bot - a).Normalized();
            AddTri(a, b, bot, nDn, 0.5f, 0.5f, 0f);
        }

        void AddTri(Vec3 p0, Vec3 p1, Vec3 p2, Vec3 n, float wTop0, float wTop1, float wTop2)
        {
            Vec3* ps = stackalloc Vec3[3] { p0, p1, p2 };
            float* wt = stackalloc float[3] { wTop0, wTop1, wTop2 };
            for (int k = 0; k < 3; k++)
            {
                pos[v] = ps[k];
                nrm[v] = n;
                uv[v] = new Vec2(k == 1 ? 1f : 0f, k == 2 ? 1f : 0f);
                idx[v] = (uint)v;
                if (skinned)
                {
                    joints![v * 4 + 0] = 0;
                    joints[v * 4 + 1] = 1;
                    weights![v] = new Vec4(1f - wt[k], wt[k], 0f, 0f);
                }
                v++;
            }
        }

        return new MeshSpec
        {
            Positions = pos,
            Normals = nrm,
            Uvs = uv,
            Joints4 = joints,
            Weights4 = weights,
            Indices = idx,
            BaseColor = baseColor,
            Emissive = emissive,
            Metallic = 0.35f,
            Roughness = 0.25f,
        };
    }

    /// <summary>
    /// Generates a unit/sized PBR Box MeshSpec with proper per-face normals and UVs.
    /// </summary>
    public static MeshSpec CreateBoxSpec(Vec3 size, ColorF baseColor, Vec3 emissive = default, float metallic = 0.1f, float roughness = 0.6f, bool skinned = false)
    {
        float hx = size.X * 0.5f, hy = size.Y * 0.5f, hz = size.Z * 0.5f;
        Vec3[] pos = new Vec3[24];
        Vec3[] nrm = new Vec3[24];
        Vec2[] uv = new Vec2[24];
        uint[] idx = new uint[36];
        byte[]? joints = skinned ? new byte[24 * 4] : null;
        Vec4[]? weights = skinned ? new Vec4[24] : null;

        int vc = 0, ic = 0;
        void Face(Vec3 p0, Vec3 p1, Vec3 p2, Vec3 p3, Vec3 n)
        {
            int b = vc;
            Vec3* pts = stackalloc Vec3[4] { p0, p1, p2, p3 };
            Vec2* uvs = stackalloc Vec2[4] { new Vec2(0, 1), new Vec2(1, 1), new Vec2(1, 0), new Vec2(0, 0) };
            for (int i = 0; i < 4; i++)
            {
                pos[vc] = pts[i];
                nrm[vc] = n;
                uv[vc] = uvs[i];
                if (skinned)
                {
                    float topW = pts[i].Y > 0f ? 1f : 0f;
                    joints![vc * 4 + 0] = 0;
                    joints[vc * 4 + 1] = 1;
                    weights![vc] = new Vec4(1f - topW, topW, 0f, 0f);
                }
                vc++;
            }
            idx[ic++] = (uint)(b + 0); idx[ic++] = (uint)(b + 1); idx[ic++] = (uint)(b + 2);
            idx[ic++] = (uint)(b + 0); idx[ic++] = (uint)(b + 2); idx[ic++] = (uint)(b + 3);
        }

        // +Z, -Z, +X, -X, +Y, -Y (CCW outward)
        Face(new Vec3(-hx, -hy, hz), new Vec3(hx, -hy, hz), new Vec3(hx, hy, hz), new Vec3(-hx, hy, hz), new Vec3(0, 0, 1));
        Face(new Vec3(hx, -hy, -hz), new Vec3(-hx, -hy, -hz), new Vec3(-hx, hy, -hz), new Vec3(hx, hy, -hz), new Vec3(0, 0, -1));
        Face(new Vec3(hx, -hy, hz), new Vec3(hx, -hy, -hz), new Vec3(hx, hy, -hz), new Vec3(hx, hy, hz), new Vec3(1, 0, 0));
        Face(new Vec3(-hx, -hy, -hz), new Vec3(-hx, -hy, hz), new Vec3(-hx, hy, hz), new Vec3(-hx, hy, -hz), new Vec3(-1, 0, 0));
        Face(new Vec3(-hx, hy, hz), new Vec3(hx, hy, hz), new Vec3(hx, hy, -hz), new Vec3(-hx, hy, -hz), new Vec3(0, 1, 0));
        Face(new Vec3(-hx, -hy, -hz), new Vec3(hx, -hy, -hz), new Vec3(hx, -hy, hz), new Vec3(-hx, -hy, hz), new Vec3(0, -1, 0));

        return new MeshSpec
        {
            Positions = pos,
            Normals = nrm,
            Uvs = uv,
            Joints4 = joints,
            Weights4 = weights,
            Indices = idx,
            BaseColor = baseColor,
            Emissive = emissive,
            Metallic = metallic,
            Roughness = roughness,
        };
    }

    /// <summary>
    /// Generates a stylized multi-part hero mech / character .glb (torso + visor + hovering crown, rigged for animation!).
    /// </summary>
    public static MeshSpec CreateHeroMechSpec(ColorF bodyColor, ColorF accentColor)
    {
        // Combine 3 boxes into one vertex-colored skinned MeshSpec!
        var torso = CreateBoxSpec(new Vec3(0.95f, 1.05f, 0.75f), ColorF.White, skinned: true);
        var head = CreateBoxSpec(new Vec3(0.68f, 0.56f, 0.62f), ColorF.White, skinned: true);
        var visor = CreateBoxSpec(new Vec3(0.58f, 0.18f, 0.14f), ColorF.White, skinned: true);

        int vTotal = torso.Positions.Length + head.Positions.Length + visor.Positions.Length;
        int iTotal = torso.Indices.Length + head.Indices.Length + visor.Indices.Length;

        Vec3[] pos = new Vec3[vTotal];
        Vec3[] nrm = new Vec3[vTotal];
        Vec2[] uv = new Vec2[vTotal];
        ColorF[] col = new ColorF[vTotal];
        byte[] joints = new byte[vTotal * 4];
        Vec4[] weights = new Vec4[vTotal];
        uint[] idx = new uint[iTotal];

        int vOff = 0, iOff = 0;
        void Append(in MeshSpec part, Vec3 offset, ColorF tint, float joint1Weight)
        {
            for (int i = 0; i < part.Positions.Length; i++)
            {
                pos[vOff + i] = part.Positions[i] + offset;
                nrm[vOff + i] = part.Normals[i];
                uv[vOff + i] = part.Uvs[i];
                col[vOff + i] = tint;
                joints[(vOff + i) * 4 + 0] = 0;
                joints[(vOff + i) * 4 + 1] = 1;
                weights[vOff + i] = new Vec4(1f - joint1Weight, joint1Weight, 0f, 0f);
            }
            for (int i = 0; i < part.Indices.Length; i++)
                idx[iOff + i] = part.Indices[i] + (uint)vOff;
            vOff += part.Positions.Length;
            iOff += part.Indices.Length;
        }

        Append(in torso, new Vec3(0, 0.65f, 0), bodyColor, 0.15f);
        Append(in head, new Vec3(0, 1.48f, 0), ColorF.Lerp(bodyColor, ColorF.White, 0.25f), 0.9f);
        Append(in visor, new Vec3(0, 1.50f, -0.32f), accentColor, 1.0f);

        return new MeshSpec
        {
            Positions = pos,
            Normals = nrm,
            Uvs = uv,
            Colors = col,
            Joints4 = joints,
            Weights4 = weights,
            Indices = idx,
            BaseColor = ColorF.White,
            Emissive = new Vec3(accentColor.R * 0.12f, accentColor.G * 0.12f, accentColor.B * 0.12f),
            Metallic = 0.45f,
            Roughness = 0.3f,
        };
    }
}
