using System;
using System.Collections.Generic;
using System.Reflection;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace FuckRipper.AvatarObfuscator.Internal
{
    /// <summary>
    /// Per-texture bit-jitter material obfuscator.
    ///
    /// <para>For every <see cref="Texture2D"/> referenced by every material on the
    /// avatar, build a byte-different copy by reading the source pixels through a
    /// GPU blit, perturbing one sub-pixel low-significance bit, then recompressing
    /// to a format that matches the source (BC7 / DXT5 / ASTC / ETC2 / etc.) so
    /// runtime VRAM and bundle size stay the same as the original. The jittered
    /// texture renders identically — the perturbation is well below human
    /// discrimination threshold — but every byte-level content hash (SHA-256 used
    /// by ripper reverse-image-search workflows, perceptual-hash variants, etc.)
    /// changes.</para>
    ///
    /// <para>Because the source's compressed format is preserved, there is no VRAM
    /// blow-up. Because mesh UVs are never modified and material per-texture
    /// scale/offset (<c>_TextureName_ST</c>) values are kept identical to the
    /// source, the visual result is unchanged regardless of how many UV channels
    /// are bound, what tiling/offset is in use, and whether normal maps / detail
    /// masks / matcaps / parallax effects are present.</para>
    ///
    /// <para>Material reference rewrites are recorded in
    /// <see cref="ObfuscationContext.MaterialReplacements"/> so that the
    /// animation-clip pass redirects ObjectReference curves accordingly.</para>
    /// </summary>
    internal static class UVTextureRemapper
    {
        public static void Run(BuildContext context, ObfuscationContext state)
        {
            var renderers = context.AvatarRootObject.GetComponentsInChildren<Renderer>(true);

            // ---------------------------------------------------------------
            // 1. Collect every material in use.
            // ---------------------------------------------------------------
            var allMaterials = new HashSet<Material>();
            foreach (var r in renderers)
            {
                if (r == null) continue;
                foreach (var m in r.sharedMaterials)
                    if (m != null) allMaterials.Add(m);
            }
            if (allMaterials.Count == 0) return;

            // ---------------------------------------------------------------
            // 2. Build per-source-texture jitter cache (GLOBAL across the pass)
            //    and per-source-material remap. A texture shared across N
            //    materials produces exactly 1 obfuscated copy in VRAM, not N.
            // ---------------------------------------------------------------
            var jitterCache = new Dictionary<Texture2D, Texture2D>();
            var remappedMaterial = new Dictionary<Material, Material>(allMaterials.Count);

            foreach (var orig in allMaterials)
            {
                var newMat = BuildRemappedMaterial(context, state, orig, jitterCache);
                if (newMat == null) continue;
                remappedMaterial[orig] = newMat;
                state.MaterialReplacements[orig] = newMat;
                ObjectRegistry.RegisterReplacedObject(orig, newMat);
            }
            if (remappedMaterial.Count == 0) return;

            // ---------------------------------------------------------------
            // 3. Swap each renderer's material slots to the obfuscated versions.
            //    Mesh UVs are NOT modified.
            // ---------------------------------------------------------------
            foreach (var r in renderers)
            {
                if (r == null) continue;
                var mats = r.sharedMaterials;
                bool anySwap = false;
                for (int s = 0; s < mats.Length; s++)
                {
                    if (mats[s] != null
                        && remappedMaterial.TryGetValue(mats[s], out var rep)
                        && rep != mats[s])
                    {
                        mats[s] = rep;
                        anySwap = true;
                    }
                }
                if (anySwap) r.sharedMaterials = mats;
            }
        }

        // ====================================================================
        // Material build
        // ====================================================================

        private static Material BuildRemappedMaterial(BuildContext context,
            ObfuscationContext state, Material src, Dictionary<Texture2D, Texture2D> jitterCache)
        {
            if (src == null || src.shader == null) return null;

            // Material clone strategy mirrors Avatar Optimizer's DupliacteAssets
            // pass:
            //
            //   1. Use the `new Material(src)` copy constructor — this is the
            //      canonical Material-cloning path documented by Unity. It
            //      preserves every serialized field: shader, render queue,
            //      every float/color/vector/int property, every texture binding
            //      with its scale/offset, every shader keyword toggle, GI flags,
            //      enableInstancing, doubleSidedGI, etc.
            //
            //   2. Wrap the construction in `BeginNoApplyMaterialPropertyDrawers`
            //      so that material property drawer side effects (e.g. lilToon /
            //      Poiyomi auto-recomputing render queue or related properties
            //      when a property changes) do NOT fire during the clone. AAO
            //      learned this the hard way; we follow their lead.
            //
            //   3. Set `parent = null` afterwards to flatten Material Variants
            //      (Unity 2022.1+). Without this, the clone retains a parent
            //      reference and any subsequent SetTexture write would not
            //      override an inherited value.
            Material copy;
            using (MaterialEditorReflection.BeginNoApplyMaterialPropertyDrawers())
            {
                copy = new Material(src);
            }
#if UNITY_2022_1_OR_NEWER
            copy.parent = null; // force flatten material variants
#endif
            copy.name = src.name;
            context.AssetSaver.SaveAsset(copy);

            int propCount = src.shader.GetPropertyCount();
            for (int p = 0; p < propCount; p++)
            {
                if (src.shader.GetPropertyType(p) != ShaderPropertyType.Texture) continue;
                var propName = src.shader.GetPropertyName(p);

                // Defensive: skip if the (cloned) material doesn't actually have
                // this property — covers shader-property-not-found edge cases.
                if (!copy.HasProperty(propName)) continue;

                var t = src.GetTexture(propName);
                if (t == null) continue;

                // Cubemaps, 3D textures, render textures, 2D arrays — leave them
                // untouched. They are not the typical content-hash matching surface
                // for ripper workflows, and re-encoding them safely is non-trivial.
                if (!(t is Texture2D t2d)) continue;

                if (!jitterCache.TryGetValue(t2d, out var jittered))
                {
                    jittered = BuildJitteredTexture(t2d);
                    if (jittered != null)
                    {
                        // Use a homoglyph name so the texture's metadata in the
                        // exported bundle blends in with the rest of the
                        // obfuscated identifiers.
                        jittered.name = state.NameGen != null ? state.NameGen.Next() : t2d.name;
                        context.AssetSaver.SaveAsset(jittered);
                        ObjectRegistry.RegisterReplacedObject(t2d, jittered);
                    }
                    jitterCache[t2d] = jittered;
                }

                if (jittered == null) continue;

                copy.SetTexture(propName, jittered);
                // We deliberately DO NOT touch GetTextureScale / GetTextureOffset —
                // the new texture has the same pixel layout as the source, so
                // the original ST sampling is correct.
            }

            return copy;
        }

        // ====================================================================
        // Texture jitter
        // ====================================================================

        /// <summary>
        /// Produce a Texture2D that is byte-different from the source while
        /// remaining visually indistinguishable.
        ///
        /// <para><b>Primary path (lossless, AAO-style)</b>: allocate a new
        /// Texture2D with EXACTLY the source's format / dimensions / mip count
        /// / color space, GPU-copy the source into it via
        /// <see cref="Graphics.CopyTexture(Texture, Texture)"/>, then flip a
        /// single bit in the raw byte buffer of the smallest mip via
        /// <see cref="Texture2D.GetRawTextureData{T}"/> +
        /// <see cref="Texture2D.SetPixelData{T}(byte[], int)"/>. The output is
        /// byte-for-byte identical to the source EXCEPT one bit, in the
        /// source's original compressed format — zero VRAM blow-up, zero
        /// quality loss from re-encoding.</para>
        ///
        /// <para><b>Fallback (lossy)</b>: if the primary path is not available
        /// for the source format (Crunch, exotic), we recompress through an
        /// LDR ARGB32 blit pipeline — same path as 0.2.0 but now with
        /// CompressTexture back to the source's compressed format so VRAM
        /// still matches.</para>
        ///
        /// <para><b>Final fallback</b>: if both paths fail, return null. The
        /// caller leaves the original texture in use — no obfuscation but no
        /// breakage. Better than shipping a 4× bloated RGBA32 (the original
        /// 0.2.0/0.2.1 regression).</para>
        /// </summary>
        private static Texture2D BuildJitteredTexture(Texture2D src)
        {
            if (src == null) return null;
            int w = src.width;
            int h = src.height;
            if (w <= 0 || h <= 0) return null;

            // HDR: skip. A 1-bit XOR on raw HDR bytes can flip exponent bits
            // and produce a visible artifact (e.g., a single pixel becoming
            // very bright). Lossy LDR re-encode would also discard HDR range.
            // Pass HDR textures through unmodified — no obfuscation but no
            // breakage.
            if (IsHdrFormat(src.format)) return null;

            // Primary path: byte-level lossless copy + 1-bit XOR.
            var lossless = TryBuildLosslessJittered(src);
            if (lossless != null) return lossless;

            // Fallback path: blit + ReadPixels + recompress.
            return TryBuildLossyJittered(src);
        }

        // --------------------------------------------------------------------
        // Primary path: lossless byte-level XOR
        // --------------------------------------------------------------------

        private static Texture2D TryBuildLosslessJittered(Texture2D src)
        {
            // Crunch-compressed textures return an empty array from
            // GetRawTextureData (the byte buffer holds Crunch headers, not
            // pixel data we can XOR meaningfully). Skip them in the primary
            // path; the fallback handles them.
            if (IsCrunchFormat(src.format)) return null;

            Texture2D dst = null;
            try
            {
                bool linear = ResolveLinearFromTexture(src);

                // Same format / dimensions / mips / color space as src. The
                // default `new Texture2D` is CPU-readable, which is required
                // for GetRawTextureData below.
                dst = new Texture2D(src.width, src.height, src.format,
                    src.mipmapCount, linear)
                {
                    wrapMode    = src.wrapMode,
                    filterMode  = src.filterMode,
                    anisoLevel  = src.anisoLevel,
                };

                // GPU-side byte-perfect copy. Works without src.isReadable
                // because CopyTexture operates on the GPU representation.
                Graphics.CopyTexture(src, dst);
                // Sync the CPU-side buffer (some Unity versions populate it
                // lazily on the first GetRawTextureData call; explicit Apply
                // is the safe documented path that AAO also uses).
                dst.Apply(updateMipmaps: false);

                var raw = dst.GetRawTextureData<byte>();
                if (raw.Length == 0)
                {
                    // CopyTexture didn't expose the bytes (rare — usually only
                    // for surface-format-only textures). Fall back.
                    Object.DestroyImmediate(dst);
                    return null;
                }

                // Pull out the bytes, flip one LSB, write back. We allocate a
                // managed byte[] for SetPixelData<byte>(byte[]); the NativeArray
                // returned by GetRawTextureData is a view, but SetPixelData on
                // the same view doesn't trigger a re-upload reliably. The
                // managed-array round-trip is what AAO uses too.
                var bytes = new byte[raw.Length];
                raw.CopyTo(bytes);

                // Flip the LSB of the very last byte. For BC/DXT/ASTC block-
                // compressed formats this lands in the smallest mip's last
                // block — a 4×4 patch at the lowest mip level, sampled only at
                // extreme distances. For uncompressed formats it's a single
                // sub-channel LSB on the corner pixel of the smallest mip.
                // Both are visually invisible; the byte-level (and SHA-256)
                // change is total.
                bytes[bytes.Length - 1] = (byte)(bytes[bytes.Length - 1] ^ 1);
                dst.SetPixelData(bytes, 0);

                // Final upload + drop CPU copy. From here the texture lives
                // only on the GPU at the source's native size and format.
                dst.Apply(updateMipmaps: false, makeNoLongerReadable: true);
                return dst;
            }
            catch (System.Exception e)
            {
                if (dst != null) Object.DestroyImmediate(dst);
                Debug.Log(
                    $"[AvatarObfuscator] Lossless bit-jitter unavailable for " +
                    $"'{src.name}' (format={src.format}): {e.Message}. " +
                    $"Falling back to lossy path.");
                return null;
            }
        }

        // --------------------------------------------------------------------
        // Fallback path: blit + ReadPixels + recompress (lossy)
        // --------------------------------------------------------------------

        private static Texture2D TryBuildLossyJittered(Texture2D src)
        {
            int w = src.width;
            int h = src.height;
            bool linear = ResolveLinearFromTexture(src);
            var rwMode = linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB;

            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, rwMode);
            var prevActive = RenderTexture.active;

            Texture2D tex = null;
            try
            {
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;

                tex = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: true, linear: linear)
                {
                    wrapMode = src.wrapMode,
                    filterMode = src.filterMode,
                    anisoLevel = src.anisoLevel,
                };
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);

                var c0 = tex.GetPixel(0, 0);
                int r8 = Mathf.Clamp(Mathf.RoundToInt(c0.r * 255f), 0, 255) ^ 1;
                c0.r = r8 / 255f;
                tex.SetPixel(0, 0, c0);

                tex.Apply(updateMipmaps: true);

                if (!TryRecompress(tex, src.format, src.name))
                {
                    Object.DestroyImmediate(tex);
                    return null;
                }

                tex.Apply(updateMipmaps: false, makeNoLongerReadable: true);
                return tex;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning(
                    $"[AvatarObfuscator] Lossy bit-jitter fallback failed for " +
                    $"'{src.name}' (format={src.format}, {w}x{h}): {e.Message}");
                if (tex != null) Object.DestroyImmediate(tex);
                return null;
            }
            finally
            {
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        /// <summary>
        /// Crunch is a meta-compression on top of DXT — the raw byte buffer
        /// of a Crunch texture holds Crunch headers, not pixel data we can
        /// XOR meaningfully. Detected separately so the lossless path can
        /// skip it.
        /// </summary>
        private static bool IsCrunchFormat(TextureFormat fmt)
        {
            switch (fmt)
            {
                case TextureFormat.DXT1Crunched:
                case TextureFormat.DXT5Crunched:
                case TextureFormat.ETC_RGB4Crunched:
                case TextureFormat.ETC2_RGBA8Crunched:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Recompress an RGBA32 readable Texture2D to a sensible compressed format.
        /// Returns true on success. On failure, leaves the texture in whatever
        /// state the partial compression left it (the caller decides what to do).
        /// </summary>
        private static bool TryRecompress(Texture2D tex, TextureFormat sourceFormat, string srcName)
        {
            var preferred = ChooseTargetFormat(sourceFormat);
            if (preferred == tex.format) return true; // already in target format

            // Try preferred (source-matching) format.
            try
            {
                EditorUtility.CompressTexture(tex, preferred, TextureCompressionQuality.Normal);
                return true;
            }
            catch (System.Exception e1)
            {
                // Preferred failed. Try BC7 (most universal modern desktop format).
                if (preferred != TextureFormat.BC7)
                {
                    try
                    {
                        EditorUtility.CompressTexture(tex, TextureFormat.BC7, TextureCompressionQuality.Normal);
                        return true;
                    }
                    catch (System.Exception e2)
                    {
                        // BC7 also failed. Try built-in Compress (DXT1/DXT5).
                        try
                        {
                            tex.Compress(highQuality: true);
                            return true;
                        }
                        catch (System.Exception e3)
                        {
                            Debug.LogWarning(
                                $"[AvatarObfuscator] All recompression paths failed for '{srcName}' " +
                                $"(source={sourceFormat}): preferred={preferred}/{e1.Message}, " +
                                $"BC7/{e2.Message}, Compress/{e3.Message}.");
                            return false;
                        }
                    }
                }
                else
                {
                    // Preferred was already BC7. Try built-in Compress as final fallback.
                    try
                    {
                        tex.Compress(highQuality: true);
                        return true;
                    }
                    catch (System.Exception e2)
                    {
                        Debug.LogWarning(
                            $"[AvatarObfuscator] Recompression failed for '{srcName}' " +
                            $"(source={sourceFormat}): BC7/{e1.Message}, Compress/{e2.Message}.");
                        return false;
                    }
                }
            }
        }

        /// <summary>
        /// Pick a sensible compressed target format given the source's format.
        /// For already-compressed sources we preserve the format exactly so that
        /// VRAM matches; for uncompressed sources we default to BC7 (the
        /// universal modern desktop colour format).
        /// </summary>
        private static TextureFormat ChooseTargetFormat(TextureFormat sourceFormat)
        {
            switch (sourceFormat)
            {
                // BC family — preserve exactly (PC builds).
                case TextureFormat.DXT1:
                case TextureFormat.DXT1Crunched:
                case TextureFormat.DXT5:
                case TextureFormat.DXT5Crunched:
                case TextureFormat.BC4:
                case TextureFormat.BC5:
                case TextureFormat.BC7:
                    return sourceFormat;

                // ASTC family — Quest / Android.
                case TextureFormat.ASTC_4x4:
                case TextureFormat.ASTC_5x5:
                case TextureFormat.ASTC_6x6:
                case TextureFormat.ASTC_8x8:
                case TextureFormat.ASTC_10x10:
                case TextureFormat.ASTC_12x12:
                    return sourceFormat;

                // ETC family — fallback Android, also some legacy targets.
                case TextureFormat.ETC_RGB4:
                case TextureFormat.ETC2_RGB:
                case TextureFormat.ETC2_RGBA1:
                case TextureFormat.ETC2_RGBA8:
                case TextureFormat.ETC2_RGBA8Crunched:
                    return sourceFormat;

                // Uncompressed colour → BC7 (high quality, universal modern desktop).
                default:
                    return TextureFormat.BC7;
            }
        }

        /// <summary>
        /// Detect HDR / floating-point texture formats. The blit-and-jitter
        /// pipeline is LDR (ARGB32) and would clip / lose precision on these.
        /// We skip them rather than corrupt them.
        /// </summary>
        private static bool IsHdrFormat(TextureFormat fmt)
        {
            switch (fmt)
            {
                case TextureFormat.RHalf:
                case TextureFormat.RGHalf:
                case TextureFormat.RGBAHalf:
                case TextureFormat.RFloat:
                case TextureFormat.RGFloat:
                case TextureFormat.RGBAFloat:
                case TextureFormat.RGB9e5Float:
                case TextureFormat.BC6H:
                case TextureFormat.ASTC_HDR_4x4:
                case TextureFormat.ASTC_HDR_5x5:
                case TextureFormat.ASTC_HDR_6x6:
                case TextureFormat.ASTC_HDR_8x8:
                case TextureFormat.ASTC_HDR_10x10:
                case TextureFormat.ASTC_HDR_12x12:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Determine whether the source texture's data is in linear color space.
        ///
        /// <para>Prefers the texture's own <see cref="Texture2D.isDataSRGB"/>
        /// flag (Unity 2022.1+) — it works for runtime / sub-asset / imported
        /// textures uniformly. Falls back to the importer's <c>sRGBTexture</c>
        /// flag when the texture has an asset path; finally defaults to sRGB
        /// (the conservative choice for albedo / emission).</para>
        /// </summary>
        private static bool ResolveLinearFromTexture(Texture2D src)
        {
            if (src == null) return false;

            // Texture2D.isDataSRGB: true if the data is sRGB-encoded. We want
            // the inverse: true if data is linear.
#if UNITY_2022_1_OR_NEWER
            // isDataSRGB is reliable for both imported and runtime textures.
            return !src.isDataSRGB;
#else
            return ResolveLinear(src);
#endif
        }

        /// <summary>
        /// Determine whether the source texture is in linear color space. Reads the
        /// importer's sRGB flag when available; runtime / sub-asset textures with no
        /// importer fall back to sRGB (the conservative default for albedo /
        /// emission). Users with linear-space mask textures should mark them as
        /// such on the importer.
        /// </summary>
        private static bool ResolveLinear(Texture2D src)
        {
            var path = AssetDatabase.GetAssetPath(src);
            if (string.IsNullOrEmpty(path)) return false;
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            return imp != null && !imp.sRGBTexture;
        }
    }

    /// <summary>
    /// Reflection helper for the editor-internal flag
    /// <c>EditorMaterialUtility.disableApplyMaterialPropertyDrawers</c>.
    ///
    /// <para>When a Material is constructed via <c>new Material(other)</c>, Unity's
    /// editor pipeline runs every shader's <c>MaterialPropertyDrawer.OnGUI</c>
    /// hook to allow the drawer to fix up dependent properties. For shaders with
    /// elaborate custom drawers (lilToon, Poiyomi, …), this side effect can
    /// silently change render queue / shader keywords / dependent properties
    /// during the clone — which we do NOT want during a build pipeline that
    /// promises to be transparent to the user's source assets.</para>
    ///
    /// <para>Setting <c>disableApplyMaterialPropertyDrawers</c> to true for the
    /// duration of the clone suppresses every drawer, so the cloned material is
    /// a faithful copy of the source. This is the same trick used by
    /// <c>com.anatawa12.avatar-optimizer</c>'s <c>DupliacteAssets</c> pass.</para>
    /// </summary>
    internal static class MaterialEditorReflection
    {
        private static readonly PropertyInfo s_Property;

        static MaterialEditorReflection()
        {
            // The flag lives on UnityEditor.EditorMaterialUtility as a non-public
            // static property. It has been there since Unity 2018; we treat it
            // as a hard dependency.
            s_Property = typeof(EditorMaterialUtility).GetProperty(
                "disableApplyMaterialPropertyDrawers",
                BindingFlags.Static | BindingFlags.NonPublic);
        }

        public static DisableApplyMaterialPropertyDisposable BeginNoApplyMaterialPropertyDrawers()
        {
            return new DisableApplyMaterialPropertyDisposable(true);
        }

        private static bool DisableApplyMaterialPropertyDrawers
        {
            get => s_Property != null && (bool)s_Property.GetValue(null);
            set { if (s_Property != null) s_Property.SetValue(null, value); }
        }

        public struct DisableApplyMaterialPropertyDisposable : IDisposable
        {
            private readonly bool _originalValue;

            public DisableApplyMaterialPropertyDisposable(bool value)
            {
                _originalValue = DisableApplyMaterialPropertyDrawers;
                DisableApplyMaterialPropertyDrawers = value;
            }

            public void Dispose()
            {
                DisableApplyMaterialPropertyDrawers = _originalValue;
            }
        }
    }
}
