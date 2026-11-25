using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BringEngine.Assets;
using BringEngine.Assets.Fonts;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Simulation.Camera;
using BringEngine.Simulation.RenderData;
using BringEngine.Simulation.World;

namespace BringEngine.Rendering;

/// <summary>
/// GPU-ready 128-byte SDF glyph instance matching sdf_text.hlsl exactly.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct GlyphGpuInstance
{
    public Vec4 OriginAndPad;   // xyz = bottom-left world corner
    public Vec4 RightVec;       // xyz = scaled right axis, w = shinePhase
    public Vec4 UpVec;          // xyz = scaled up axis, w = charNormT
    public Vec4 UvRect;         // (u0, v0, uW, vH)
    public ColorF FillColor;
    public ColorF OutlineColor;
    public ColorF GlowColor;
    public Vec4 StyleParams;    // x = onEdge (0.706), y = outlineWidth, z = softness, w = glowIntensity
}

/// <summary>
/// Pure CPU, zero-GC SDF text layout and per-character animation engine.
/// Works headlessly (for tests) and writes directly into a FrameArena NativeList<GlyphGpuInstance>.
/// </summary>
public static unsafe class TextLayoutSystem
{
    private struct LaidChar
    {
        public int GlyphIndex;
        public float X;
        public float Y;
        public int CharIndex;
    }

    public static int LayoutTextIntoGlyphs(
        in TextData td,
        in FontAssetData font,
        in TransformTable transforms,
        in CameraData cam,
        ref LinearArena scratchArena,
        ref NativeList<GlyphGpuInstance> outGlyphs)
    {
        if (td.Visible == 0 || td.Content.Length <= 0 || td.TypewriterProgress <= 0f)
            return 0;

        ReadOnlySpan<byte> utf8 = td.Content.AsUtf8Span();
        int totalChars = utf8.Length;
        float visibleCharsF = totalChars * DetMath.Clamp01(td.TypewriterProgress);
        int visibleCount = (int)MathF.Ceiling(visibleCharsF);
        if (visibleCount <= 0) return 0;
        if (visibleCount > totalChars) visibleCount = totalChars;

        ArenaMarker mark = scratchArena.SaveMarker();
        try
        {
            LaidChar* laid = scratchArena.AllocUninitialized<LaidChar>(totalChars, 16);
            int* lineStarts = scratchArena.AllocUninitialized<int>(64, 16);
            int* lineEnds = scratchArena.AllocUninitialized<int>(64, 16);
            float* lineWidths = scratchArena.AllocUninitialized<float>(64, 16);

            int laidCount = 0;
            int lineCount = 0;
            float penX = 0f;
            float penY = 0f;
            float lineHeight = font.LineHeight * td.LineSpacing;
            float maxLocalWidth = td.MaxWidth > 0f && td.FontScale > 1e-5f ? (td.MaxWidth / td.FontScale) : float.MaxValue;

            lineStarts[0] = 0;
            int prevGlyph = -1;
            int lastSpaceLaidIdx = -1;
            float lastSpacePenX = 0f;

            for (int c = 0; c < totalChars; c++)
            {
                byte ch = utf8[c];
                if (ch == (byte)'\n')
                {
                    if (lineCount < 63)
                    {
                        lineEnds[lineCount] = laidCount;
                        lineWidths[lineCount] = penX;
                        lineCount++;
                        lineStarts[lineCount] = laidCount;
                    }
                    penX = 0f;
                    penY -= lineHeight;
                    prevGlyph = -1;
                    lastSpaceLaidIdx = -1;
                    continue;
                }

                int gi = ch - SdfFontLoader.FirstAscii;
                if ((uint)gi >= SdfFontLoader.AsciiCount) gi = 0; // fallback space

                ref GlyphMetrics gm = ref font.AsciiGlyphs[gi];
                if (prevGlyph >= 0 && font.AsciiKerning != null)
                    penX += font.AsciiKerning[prevGlyph * SdfFontLoader.AsciiCount + gi];

                // Word wrap check
                if (penX + gm.Advance > maxLocalWidth && penX > 0f && lineCount < 63)
                {
                    if (lastSpaceLaidIdx >= lineStarts[lineCount] && lastSpaceLaidIdx < laidCount)
                    {
                        float shiftX = laid[lastSpaceLaidIdx].X;
                        lineEnds[lineCount] = lastSpaceLaidIdx;
                        lineWidths[lineCount] = lastSpacePenX;
                        lineCount++;
                        lineStarts[lineCount] = lastSpaceLaidIdx;
                        penY -= lineHeight;
                        for (int k = lastSpaceLaidIdx; k < laidCount; k++)
                        {
                            laid[k].X -= shiftX;
                            laid[k].Y -= lineHeight;
                        }
                        penX -= shiftX;
                        lastSpaceLaidIdx = -1;
                    }
                    else
                    {
                        lineEnds[lineCount] = laidCount;
                        lineWidths[lineCount] = penX;
                        lineCount++;
                        lineStarts[lineCount] = laidCount;
                        penX = 0f;
                        penY -= lineHeight;
                    }
                }

                if (ch == (byte)' ')
                {
                    lastSpaceLaidIdx = laidCount;
                    lastSpacePenX = penX;
                }

                laid[laidCount++] = new LaidChar { GlyphIndex = gi, X = penX, Y = penY, CharIndex = c };
                penX += gm.Advance + td.LetterSpacing;
                prevGlyph = gi;
            }

            if (lineCount < 64)
            {
                lineEnds[lineCount] = laidCount;
                lineWidths[lineCount] = penX;
                lineCount++;
            }

            // Alignment offsets
            float totalHeight = lineCount * lineHeight;
            float offsetY = td.AlignV switch
            {
                TextAlignmentV.Top => -font.Ascent,
                TextAlignmentV.Middle => totalHeight * 0.5f - font.Ascent,
                TextAlignmentV.Bottom => totalHeight - font.Ascent,
                _ => 0f,
            };

            for (int l = 0; l < lineCount; l++)
            {
                float offX = td.AlignH switch
                {
                    TextAlignmentH.Center => -lineWidths[l] * 0.5f,
                    TextAlignmentH.Right => -lineWidths[l],
                    _ => 0f,
                };
                for (int k = lineStarts[l]; k < lineEnds[l]; k++)
                {
                    laid[k].X += offX;
                    laid[k].Y += offsetY;
                }
            }

            // Compute world basis (Origin, Right, Up) supporting None / Spherical / CylindricalY billboards
            Vec3 origin = Vec3.Zero;
            Vec3 basisRight = Vec3.Right;
            Vec3 basisUp = Vec3.Up;
            Vec3 basisFwd = new Vec3(0, 0, 1);

            int tdIdx = TransformSystem.GetDenseIndex(in transforms, td.Entity);
            if (tdIdx >= 0)
            {
                Mat4 wm = transforms.WorldMatrix[tdIdx];
                origin = transforms.WorldPosition[tdIdx];
                if (td.Billboard == SpriteBillboardMode.Spherical)
                {
                    Vec3 scl = transforms.LocalScale[tdIdx];
                    basisRight = cam.Right * scl.X;
                    basisUp = cam.CameraUp * scl.Y;
                    basisFwd = -cam.Forward;
                }
                else if (td.Billboard == SpriteBillboardMode.CylindricalY)
                {
                    Vec3 scl = transforms.LocalScale[tdIdx];
                    Vec3 toCam = cam.ShakenPosition - origin;
                    toCam.Y = 0f;
                    Vec3 f = toCam.LengthSq() > 1e-6f ? toCam.Normalized() : Vec3.UnitZ;
                    Vec3 r = Vec3.Cross(Vec3.Up, f).Normalized();
                    basisRight = r * scl.X;
                    basisUp = Vec3.Up * scl.Y;
                    basisFwd = f;
                }
                else
                {
                    basisRight = new Vec3(wm.C0.X, wm.C0.Y, wm.C0.Z);
                    basisUp = new Vec3(wm.C1.X, wm.C1.Y, wm.C1.Z);
                    basisFwd = new Vec3(wm.C2.X, wm.C2.Y, wm.C2.Z).Normalized();
                }
            }

            float onEdge = font.SdfOnEdgeValue / 255f;
            bool hasShadow = td.ShadowColor.A > 0.01f && (DetMath.Abs(td.ShadowOffset.X) > 1e-4f || DetMath.Abs(td.ShadowOffset.Y) > 1e-4f);
            int emitted = 0;

            for (int k = 0; k < laidCount; k++)
            {
                LaidChar lc = laid[k];
                if (lc.CharIndex >= visibleCount) continue;
                ref GlyphMetrics gm = ref font.AsciiGlyphs[lc.GlyphIndex];
                if (gm.HasBitmap == 0) continue;

                float charNorm = totalChars > 1 ? (float)lc.CharIndex / (totalChars - 1) : 0f;
                float charReveal = DetMath.Clamp01(visibleCharsF - lc.CharIndex);

                // Per-character procedural animation offsets
                float dx = 0f, dy = 0f, scaleMul = 1f;
                ColorF fillCol = ColorF.Lerp(td.Color, td.SecondaryColor, charNorm);
                float glowInt = td.GlowIntensity;
                float shinePhase = 0f;

                float phase = td.AnimTime * td.AnimSpeed + lc.CharIndex * td.AnimFrequency;

                if ((td.AnimFlags & TextAnimFlags.Wave) != 0)
                    dy += DetMath.Sin(phase) * td.AnimAmplitude;

                if ((td.AnimFlags & TextAnimFlags.Bounce) != 0)
                    dy += DetMath.Abs(DetMath.Sin(phase)) * td.AnimAmplitude * 1.4f;

                if ((td.AnimFlags & TextAnimFlags.Shake) != 0)
                {
                    dx += DetMath.Sin(td.AnimTime * 47f + lc.CharIndex * 3.1f) * td.AnimAmplitude * 0.45f;
                    dy += DetMath.Cos(td.AnimTime * 53f + lc.CharIndex * 5.7f) * td.AnimAmplitude * 0.45f;
                }

                if ((td.AnimFlags & TextAnimFlags.Rainbow) != 0)
                    fillCol = HsvToColorF((charNorm + td.AnimTime * 0.35f) % 1f, 0.85f, 1.0f, td.Color.A);

                if ((td.AnimFlags & TextAnimFlags.ScalePop) != 0)
                    scaleMul *= 0.85f + 0.25f * DetMath.Abs(DetMath.Sin(phase * 0.8f));

                if ((td.AnimFlags & TextAnimFlags.Typewriter) != 0)
                {
                    scaleMul *= 0.4f + 0.6f * charReveal;
                    fillCol.A *= charReveal;
                }

                if ((td.AnimFlags & TextAnimFlags.GlowPulse) != 0)
                    glowInt *= 0.55f + 0.65f * (0.5f + 0.5f * DetMath.Sin(td.AnimTime * td.AnimSpeed));

                if ((td.AnimFlags & TextAnimFlags.ShineSweep) != 0)
                    shinePhase = ((td.AnimTime * 0.7f - charNorm * 0.6f) % 1.4f + 1.4f) % 1.4f;

                float gx = (lc.X + gm.BearingX) * td.FontScale + dx;
                float gy = (lc.Y + gm.BearingY - gm.Height) * td.FontScale + dy;
                float gw = gm.Width * td.FontScale * scaleMul;
                float gh = gm.Height * td.FontScale * scaleMul;

                // Center scale pop around glyph center
                gx -= (gw - gm.Width * td.FontScale) * 0.5f;
                gy -= (gh - gm.Height * td.FontScale) * 0.5f;

                Vec3 rightScaled = basisRight * gw;
                Vec3 upScaled = basisUp * gh;

                // Optional drop shadow glyph behind main glyph
                if (hasShadow && outGlyphs.Count < outGlyphs.Capacity)
                {
                    Vec3 shadowOrigin = origin
                        + basisRight * (gx + td.ShadowOffset.X)
                        + basisUp * (gy + td.ShadowOffset.Y)
                        - basisFwd * 0.002f;
                    ColorF shCol = td.ShadowColor;
                    shCol.A *= fillCol.A;

                    outGlyphs.Add(new GlyphGpuInstance
                    {
                        OriginAndPad = new Vec4(shadowOrigin, 0f),
                        RightVec = new Vec4(rightScaled, 0f),
                        UpVec = new Vec4(upScaled, charNorm),
                        UvRect = gm.UvRect,
                        FillColor = shCol,
                        OutlineColor = shCol,
                        GlowColor = ColorF.Transparent,
                        StyleParams = new Vec4(onEdge, td.OutlineWidth, td.Softness * 2.2f, 0f),
                    });
                    emitted++;
                }

                if (outGlyphs.Count < outGlyphs.Capacity)
                {
                    Vec3 glyphOrigin = origin + basisRight * gx + basisUp * gy;
                    ColorF outCol = td.OutlineColor;
                    outCol.A *= fillCol.A;

                    outGlyphs.Add(new GlyphGpuInstance
                    {
                        OriginAndPad = new Vec4(glyphOrigin, 0f),
                        RightVec = new Vec4(rightScaled, shinePhase),
                        UpVec = new Vec4(upScaled, charNorm),
                        UvRect = gm.UvRect,
                        FillColor = fillCol,
                        OutlineColor = outCol,
                        GlowColor = td.GlowColor,
                        StyleParams = new Vec4(onEdge, td.OutlineWidth, td.Softness, glowInt),
                    });
                    emitted++;
                }
            }

            return emitted;
        }
        finally
        {
            scratchArena.Rollback(mark);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ColorF HsvToColorF(float h, float s, float v, float a)
    {
        if (h < 0f) h += 1f;
        float h6 = h * 6f;
        int sector = (int)h6 % 6;
        float f = h6 - (int)h6;
        float p = v * (1f - s);
        float q = v * (1f - f * s);
        float t = v * (1f - (1f - f) * s);
        return sector switch
        {
            0 => new ColorF(v, t, p, a),
            1 => new ColorF(q, v, p, a),
            2 => new ColorF(p, v, t, a),
            3 => new ColorF(p, q, v, a),
            4 => new ColorF(t, p, v, a),
            _ => new ColorF(v, p, q, a),
        };
    }
}
