using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace FuckRipper.AvatarObfuscator.Internal
{
    /// <summary>
    /// Per-material texture atlas rearrangement obfuscator.
    ///
    /// <para>Follows the same principle as TTT's atlas builder: repack texture
    /// content into a different spatial layout so every byte of the output
    /// differs from the source — no ripper reverse-image-search (SHA-256, pHash)
    /// can match the original. Because mesh UVs are remapped in lockstep, the
    /// visual result is unchanged.</para>
    ///
    /// <para>The rearranging uses a uniform N×N tile-grid shuffle across all
    /// textures on the avatar. A single deterministic permutation (seeded from
    /// the avatar root instance ID) is applied to every texture, and every mesh
    /// UV channel is remapped through the same grid transform. This keeps the
    /// code minimal (no per-island packing optimizer) while still producing a
    /// complete byte-level rewrite of every obfuscated texture.</para>
    ///
    /// <para>Material reference rewrites are recorded in
    /// <see cref="ObfuscationContext.MaterialReplacements"/> and mesh replacements
    /// in <see cref="ObfuscationContext.MeshReplacements"/> so the animation-clip
    /// pass redirects ObjectReference curves accordingly.</para>
    /// </summary>
    internal static class UVTextureRemapper
    {
        public static void Run(BuildContext context, ObfuscationContext state)
        {
            var renderers = context.AvatarRootObject.GetComponentsInChildren<Renderer>(true);

            // ---------------------------------------------------------------
            // 1. Collect every material and every Texture2D in use.
            // ---------------------------------------------------------------
            var allMaterials = new HashSet<Material>();
            var allTextures = new HashSet<Texture2D>();
            foreach (var r in renderers)
            {
                if (r == null) continue;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || m.shader == null) continue;
                    allMaterials.Add(m);
                    EnumerateMaterialTextures(m, allTextures);
                }
            }
            if (allMaterials.Count == 0) return;

            // ---------------------------------------------------------------
            // 2. Determine uniform grid and permutation.
            //    Grid size is derived from the largest texture so tiles are
            //    never microscopic; min 2×2, max 6×6, each tile ≥ 64 px.
            // ---------------------------------------------------------------
            int maxTexSize = 64;
            foreach (var t in allTextures)
            {
                if (t == null) continue;
                maxTexSize = Mathf.Max(maxTexSize, Mathf.Max(t.width, t.height));
            }
            int gridSize = Mathf.Clamp(maxTexSize / 64, 2, 6);
            int cellCount = gridSize * gridSize;

            // Deterministic permutation seeded from avatar root + texture count
            // so the same avatar always produces the same shuffle across builds.
            int seed = (context.AvatarRootObject != null
                ? context.AvatarRootObject.GetInstanceID()
                : 42) ^ (allTextures.Count << 8);
            var perm = BuildPermutation(cellCount, seed);

            // ---------------------------------------------------------------
            // 3. Build shuffled texture cache (global — a texture shared
            //    across N materials is only shuffled once, like TTT's cache).
            // ---------------------------------------------------------------
            var shuffledCache = new Dictionary<Texture2D, Texture2D>();
            foreach (var tex in allTextures)
            {
                if (tex == null || shuffledCache.ContainsKey(tex)) continue;
                if (IsHdrFormat(tex.format)) continue; // HDR: skip, can't safely reprocess

                var shuffled = BuildShuffledTexture(context, state, tex, gridSize, perm);
                if (shuffled == null) continue;

                shuffled.name = state.NameGen != null ? state.NameGen.Next() : tex.name;
                context.AssetSaver.SaveAsset(shuffled);
                ObjectRegistry.RegisterReplacedObject(tex, shuffled);
                shuffledCache[tex] = shuffled;
            }
            if (shuffledCache.Count == 0) return;

            // ---------------------------------------------------------------
            // 4. Clone every material, swap textures to shuffled versions.
            //    Material UV scale/offset is NOT changed — the shuffled
            //    texture occupies the same [0,1]×[0,1] space.
            // ---------------------------------------------------------------
            var matRemap = new Dictionary<Material, Material>();
            foreach (var orig in allMaterials)
            {
                var newMat = BuildRemappedMaterial(context, orig, shuffledCache);
                if (newMat == null || newMat == orig) continue;
                matRemap[orig] = newMat;
                state.MaterialReplacements[orig] = newMat;
                ObjectRegistry.RegisterReplacedObject(orig, newMat);
            }

            // ---------------------------------------------------------------
            // 5. Clone every mesh that uses a remapped material, remap ALL
            //    UV channels through the grid transform, and swap the
            //    renderer's mesh + material slots.
            // ---------------------------------------------------------------
            var meshCloneCache = new Dictionary<Mesh, Mesh>(); // mesh → cloned+remapped
            var rendererMeshRemap = new Dictionary<Renderer, Mesh>(); // for anim curves

            foreach (var r in renderers)
            {
                if (r == null) continue;
                var mats = r.sharedMaterials;
                bool needsRemap = false;
                for (int s = 0; s < mats.Length; s++)
                    if (mats[s] != null && matRemap.ContainsKey(mats[s]))
                        needsRemap = true;
                if (!needsRemap) continue;

                // Swap material slots.
                for (int s = 0; s < mats.Length; s++)
                {
                    if (mats[s] != null && matRemap.TryGetValue(mats[s], out var rep))
                        mats[s] = rep;
                }
                r.sharedMaterials = mats;

                // Clone + UV-remap the mesh.
                Mesh srcMesh = GetSharedMesh(r);
                if (srcMesh == null) continue;

                if (!meshCloneCache.TryGetValue(srcMesh, out var clonedMesh))
                {
                    clonedMesh = Object.Instantiate(srcMesh);
                    clonedMesh.name = srcMesh.name; // keep original name; FinalizeAssetsPass renames later
                    RemapAllUvChannels(clonedMesh, gridSize, perm);
                    clonedMesh.UploadMeshData(false);
                    context.AssetSaver.SaveAsset(clonedMesh);
                    ObjectRegistry.RegisterReplacedObject(srcMesh, clonedMesh);
                    meshCloneCache[srcMesh] = clonedMesh;
                }

                SetSharedMesh(r, clonedMesh);
                rendererMeshRemap[r] = clonedMesh;
            }

            // Record mesh replacements for downstream passes.
            foreach (var kv in meshCloneCache)
                state.MeshReplacements[kv.Key] = kv.Value;
        }

        // ====================================================================
        // Grid & permutation
        // ====================================================================

        /// <summary>
        /// Build a Fisher-Yates shuffle of [0..count-1] seeded from the given int.
        /// </summary>
        private static int[] BuildPermutation(int count, int seed)
        {
            var p = new int[count];
            for (int i = 0; i < count; i++) p[i] = i;

            // Deterministic PRNG — no System.Random needed.
            uint state = (uint)seed;
            for (int i = count - 1; i > 0; i--)
            {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                int j = (int)(state % (uint)(i + 1));
                (p[i], p[j]) = (p[j], p[i]);
            }
            return p;
        }

        // ====================================================================
        // Material build
        // ====================================================================

        private static void EnumerateMaterialTextures(Material mat, HashSet<Texture2D> sink)
        {
            if (mat == null || mat.shader == null) return;
            int propCount = mat.shader.GetPropertyCount();
            for (int p = 0; p < propCount; p++)
            {
                if (mat.shader.GetPropertyType(p) != ShaderPropertyType.Texture) continue;
                var t = mat.GetTexture(mat.shader.GetPropertyName(p));
                if (t is Texture2D t2d && !IsHdrFormat(t2d.format))
                    sink.Add(t2d);
            }
        }

        private static Material BuildRemappedMaterial(BuildContext context,
            Material src, Dictionary<Texture2D, Texture2D> shuffledCache)
        {
            if (src == null || src.shader == null) return null;

            Material copy;
            using (MaterialEditorReflection.BeginNoApplyMaterialPropertyDrawers())
                copy = new Material(src);
#if UNITY_2022_1_OR_NEWER
            copy.parent = null;
#endif
            copy.name = src.name;
            context.AssetSaver.SaveAsset(copy);

            int propCount = src.shader.GetPropertyCount();
            for (int p = 0; p < propCount; p++)
            {
                if (src.shader.GetPropertyType(p) != ShaderPropertyType.Texture) continue;
                var propName = src.shader.GetPropertyName(p);
                if (!copy.HasProperty(propName)) continue;

                var t = src.GetTexture(propName);
                if (t is Texture2D t2d && shuffledCache.TryGetValue(t2d, out var shuffled))
                    copy.SetTexture(propName, shuffled);
            }

            return copy;
        }

        // ====================================================================
        // Texture shuffle (GPU pipeline — works for all readable+non-readable)
        // ====================================================================

        /// <summary>
        /// Create a shuffled Texture2D by reading source pixels into an
        /// intermediate RGBA32 buffer, rearranging tiles in CPU memory, then
        /// recompressing to match the source format (BC7, DXT5, ASTC, etc.).
        /// The output has the same dimensions, mip count, and color space as
        /// the source.
        /// </summary>
        private static Texture2D BuildShuffledTexture(BuildContext context,
            ObfuscationContext state, Texture2D src, int gridSize, int[] perm)
        {
            if (src == null) return null;
            int w = src.width;
            int h = src.height;
            if (w <= 0 || h <= 0) return null;

            bool linear = ResolveLinearFromTexture(src);
            var rwMode = linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB;

            // Step 1: Read the whole source into a CPU-side Color[].
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, rwMode);
            var prevActive = RenderTexture.active;
            Texture2D shuffled = null;

            try
            {
                // Blit source → temp RT respecting color space.
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;

                // Read back as RGBA32 CPU pixels.
                var srcTex = new Texture2D(w, h, TextureFormat.RGBA32, false, linear);
                srcTex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                srcTex.Apply(false);

                // Step 2: Rearrange tiles.
                var srcPixels = srcTex.GetPixels();
                var dstPixels = new Color[w * h];
                int tw = w / gridSize;
                int th = h / gridSize;

                for (int ti = 0; ti < perm.Length; ti++)
                {
                    int srcIdx = ti;        // source tile linear index
                    int dstIdx = perm[ti];  // destination tile linear index

                    int srcCol = srcIdx % gridSize;
                    int srcRow = srcIdx / gridSize;
                    int dstCol = dstIdx % gridSize;
                    int dstRow = dstIdx / gridSize;

                    int srcX = srcCol * tw;
                    int srcY = srcRow * th;
                    int dstX = dstCol * tw;
                    int dstY = dstRow * th;

                    for (int y = 0; y < th; y++)
                    {
                        int srcRowStart = (srcY + y) * w + srcX;
                        int dstRowStart = (dstY + y) * w + dstX;
                        Array.Copy(srcPixels, srcRowStart, dstPixels, dstRowStart, tw);
                    }
                }

                Object.DestroyImmediate(srcTex);

                // Step 3: Write rearranged pixels to a new RGBA32 texture.
                shuffled = new Texture2D(w, h, TextureFormat.RGBA32, src.mipmapCount > 1, linear)
                {
                    wrapMode   = src.wrapMode,
                    filterMode = src.filterMode,
                    anisoLevel = src.anisoLevel,
                };
                shuffled.SetPixels(dstPixels);
                shuffled.Apply(updateMipmaps: src.mipmapCount > 1);

                // Step 4: Recompress to source-matching format.
                TryRecompress(shuffled, src.format, src.name);

                // Drop CPU copy.
                shuffled.Apply(false, makeNoLongerReadable: true);
            }
            catch (Exception e)
            {
                if (shuffled != null) Object.DestroyImmediate(shuffled);
                Debug.LogWarning($"[AvatarObfuscator] Grid shuffle failed for '{src.name}': {e.Message}");
                return null;
            }
            finally
            {
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
            }

            return shuffled;
        }

        // ====================================================================
        // Mesh UV remapping
        // ====================================================================

        /// <summary>
        /// Remap every UV channel (0–3) on <paramref name="mesh"/> through
        /// the grid permutation transform. UV2 and UV3 are stored as Vector4
        /// by Unity's GetUVs/SetUVs; we only touch the xy components.
        /// </summary>
        private static void RemapAllUvChannels(Mesh mesh, int gridSize, int[] perm)
        {
            float invGrid = 1f / gridSize;

            // Precompute destination col/row for each source tile.
            var dstCol = new int[perm.Length];
            var dstRow = new int[perm.Length];
            for (int i = 0; i < perm.Length; i++)
            {
                dstCol[i] = perm[i] % gridSize;
                dstRow[i] = perm[i] / gridSize;
            }

            for (int ch = 0; ch < 4; ch++)
            {
                var uvs = new List<Vector4>();
                mesh.GetUVs(ch, uvs);
                if (uvs.Count == 0) continue;

                for (int i = 0; i < uvs.Count; i++)
                {
                    var uv = uvs[i];
                    float u = uv.x;
                    float v = uv.y;

                    // Clamp to [0,1] so out-of-range UVs don't index out of bounds.
                    int sc = Mathf.Clamp((int)(u * gridSize), 0, gridSize - 1);
                    int sr = Mathf.Clamp((int)(v * gridSize), 0, gridSize - 1);
                    int srcTile = sr * gridSize + sc;

                    float localU = (u * gridSize) - sc;
                    float localV = (v * gridSize) - sr;

                    uvs[i] = new Vector4(
                        (dstCol[srcTile] + localU) * invGrid,
                        (dstRow[srcTile] + localV) * invGrid,
                        uv.z, uv.w);
                }

                mesh.SetUVs(ch, uvs);
            }
        }

        // ====================================================================
        // Mesh get/set helpers (handle SkinnedMeshRenderer + MeshRenderer)
        // ====================================================================

        private static Mesh GetSharedMesh(Renderer r)
        {
            if (r is SkinnedMeshRenderer smr) return smr.sharedMesh;
            var mf = r.GetComponent<MeshFilter>();
            return mf != null ? mf.sharedMesh : null;
        }

        private static void SetSharedMesh(Renderer r, Mesh mesh)
        {
            if (r is SkinnedMeshRenderer smr) smr.sharedMesh = mesh;
            else { var mf = r.GetComponent<MeshFilter>(); if (mf != null) mf.sharedMesh = mesh; }
        }

        // ====================================================================
        // Texture format helpers (retained from previous version)
        // ====================================================================

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

        private static void TryRecompress(Texture2D tex, TextureFormat sourceFormat, string srcName)
        {
            var preferred = ChooseTargetFormat(sourceFormat);
            if (preferred == tex.format) return;

            try { EditorUtility.CompressTexture(tex, preferred, TextureCompressionQuality.Normal); return; }
            catch (Exception e1)
            {
                if (preferred != TextureFormat.BC7)
                {
                    try { EditorUtility.CompressTexture(tex, TextureFormat.BC7, TextureCompressionQuality.Normal); return; }
                    catch (Exception e2)
                    {
                        try { tex.Compress(true); return; }
                        catch (Exception e3)
                        {
                            Debug.LogWarning(
                                $"[AvatarObfuscator] Recompression failed for '{srcName}': " +
                                $"{preferred}/{e1.Message}, BC7/{e2.Message}, Compress/{e3.Message}");
                        }
                    }
                }
                else
                {
                    try { tex.Compress(true); return; }
                    catch (Exception e2)
                    {
                        Debug.LogWarning(
                            $"[AvatarObfuscator] Recompression failed for '{srcName}': " +
                            $"BC7/{e1.Message}, Compress/{e2.Message}");
                    }
                }
            }
        }

        private static TextureFormat ChooseTargetFormat(TextureFormat sourceFormat)
        {
            switch (sourceFormat)
            {
                case TextureFormat.DXT1:
                case TextureFormat.DXT1Crunched:
                case TextureFormat.DXT5:
                case TextureFormat.DXT5Crunched:
                case TextureFormat.BC4:
                case TextureFormat.BC5:
                case TextureFormat.BC7:
                    return sourceFormat;
                case TextureFormat.ASTC_4x4:
                case TextureFormat.ASTC_5x5:
                case TextureFormat.ASTC_6x6:
                case TextureFormat.ASTC_8x8:
                case TextureFormat.ASTC_10x10:
                case TextureFormat.ASTC_12x12:
                    return sourceFormat;
                case TextureFormat.ETC_RGB4:
                case TextureFormat.ETC2_RGB:
                case TextureFormat.ETC2_RGBA1:
                case TextureFormat.ETC2_RGBA8:
                case TextureFormat.ETC2_RGBA8Crunched:
                    return sourceFormat;
                default:
                    return TextureFormat.BC7;
            }
        }

        private static bool ResolveLinearFromTexture(Texture2D src)
        {
            if (src == null) return false;
#if UNITY_2022_1_OR_NEWER
            return !src.isDataSRGB;
#else
            var path = AssetDatabase.GetAssetPath(src);
            if (string.IsNullOrEmpty(path)) return false;
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            return imp != null && !imp.sRGBTexture;
#endif
        }
    }

    /// <summary>
    /// Reflection helper for <c>EditorMaterialUtility.disableApplyMaterialPropertyDrawers</c>.
    /// Mirrors AAO's DupliacteAssets pass — prevents lilToon / Poiyomi custom-drawer
    /// side effects from firing during <c>new Material(src)</c>.
    /// </summary>
    internal static class MaterialEditorReflection
    {
        private static readonly PropertyInfo s_Property;

        static MaterialEditorReflection()
        {
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
