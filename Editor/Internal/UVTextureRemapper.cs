using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FuckRipper.AvatarObfuscator.Internal
{
    /// <summary>
    /// Per-material UV-flip remapper.
    ///
    /// <para>For each material on the avatar, picks a deterministic flip mode
    /// (FlipX / FlipY / FlipBoth) and bakes the inverse flip into <em>both</em>
    /// the texture pixels <em>and</em> the material's per-texture
    /// <c>scale / offset</c> (the <c>_TextureName_ST</c> properties), so that
    /// the final sampling coordinate <c>mesh_uv * scale + offset</c> hits the
    /// same pixel as the original. Mesh UV0 is NOT modified — this way any
    /// number of texture slots with arbitrary <c>scale / offset</c> values, on
    /// any UV channel, all stay correct simultaneously, and screen-space
    /// derivatives (<c>ddx</c>/<c>ddy</c>) keep their original sign so
    /// tangent-space normal maps and parallax effects are unaffected.</para>
    ///
    /// <para>Material reference rewrites are recorded in
    /// <see cref="ObfuscationContext.MaterialReplacements"/> so the
    /// animation-clip pass redirects ObjectReference curves accordingly.</para>
    /// </summary>
    internal static class UVTextureRemapper
    {
        internal enum FlipMode
        {
            None     = 0,
            FlipX    = 1,
            FlipY    = 2,
            FlipBoth = 3,
        }

        // We deliberately exclude FlipMode.None from the random pick — every
        // material must produce a different texture; "none" would re-emit the
        // same pixels and (after Unity's deterministic RGBA32 round-trip)
        // produce identical bytes.
        private static readonly FlipMode[] s_NonIdentityFlips = {
            FlipMode.FlipX, FlipMode.FlipY, FlipMode.FlipBoth,
        };

        public static void Run(BuildContext context, ObfuscationContext state)
        {
            var renderers = context.AvatarRootObject.GetComponentsInChildren<Renderer>(true);

            // ---------------------------------------------------------------
            // 1. Collect every material in use, and assign each one a flip mode.
            //    Materials are obfuscated individually — there is no per-vertex
            //    flip conflict possible because we do NOT touch mesh UVs.
            // ---------------------------------------------------------------
            var allMaterials = new HashSet<Material>();
            foreach (var r in renderers)
            {
                if (r == null) continue;
                foreach (var m in r.sharedMaterials)
                    if (m != null) allMaterials.Add(m);
            }
            if (allMaterials.Count == 0) return;

            var rng = new System.Random(MakeSeed(state.Options.seed));

            var flipFor = new Dictionary<Material, FlipMode>(allMaterials.Count);
            foreach (var mat in allMaterials)
            {
                flipFor[mat] = s_NonIdentityFlips[rng.Next(s_NonIdentityFlips.Length)];
            }

            // ---------------------------------------------------------------
            // 2. Build the per-material remapped material with flipped textures
            //    and matching scale/offset on every texture slot.
            //
            //    A texture that is referenced by N materials with N different
            //    flip modes naturally produces N flipped copies — that is the
            //    desired behaviour: two visually-identical materials end up
            //    with byte-different texture assets.
            // ---------------------------------------------------------------
            var remappedMaterial = new Dictionary<Material, Material>(flipFor.Count);
            foreach (var kv in flipFor)
            {
                var orig = kv.Key;
                var flip = kv.Value;
                var newMat = BuildRemappedMaterial(context, orig, flip);
                if (newMat == null) continue;
                remappedMaterial[orig] = newMat;
                state.MaterialReplacements[orig] = newMat;
                ObjectRegistry.RegisterReplacedObject(orig, newMat);
            }
            if (remappedMaterial.Count == 0) return;

            // ---------------------------------------------------------------
            // 3. Swap each renderer's material slots to the remapped versions.
            //    No mesh UVs are touched — the flip is fully baked into the
            //    texture pixels and the per-texture scale/offset on the new
            //    material.
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

        private static Material BuildRemappedMaterial(BuildContext context, Material src, FlipMode flip)
        {
            if (src == null || src.shader == null) return null;

            // Object.Instantiate carries over every serialized field on the
            // material — shader, render queue override, shader keywords, every
            // float/color/vector/int property, every texture binding, every
            // texture's scale & offset. We then mutate the copy in place: flip
            // textures and bake the inverse flip into the corresponding ST
            // values.
            var copy = Object.Instantiate(src);
            copy.name = src.name;
            context.AssetSaver.SaveAsset(copy);

            // Cache: the same source Texture2D might be bound to multiple slots
            // on the same material (e.g. _MainTex == _DetailMask). We only flip
            // it once and reuse the result so we don't duplicate work or assets.
            var flippedCache = new Dictionary<Texture2D, Texture2D>();

            int propCount = ShaderUtil.GetPropertyCount(src.shader);
            for (int p = 0; p < propCount; p++)
            {
                if (ShaderUtil.GetPropertyType(src.shader, p) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                var propName = ShaderUtil.GetPropertyName(src.shader, p);
                var t = src.GetTexture(propName);
                if (t == null) continue;

                // Cubemaps, 3D textures, render textures, 2D arrays — these are
                // not sampled with the mesh UV / material ST in the same way;
                // we keep them untouched and DO NOT touch their ST either.
                if (!(t is Texture2D t2d)) continue;

                // Flip the texture (cached so we don't re-blit shared textures).
                if (!flippedCache.TryGetValue(t2d, out var flipped))
                {
                    flipped = BuildFlippedTexture(t2d, flip);
                    if (flipped != null)
                    {
                        flipped.name = src.name + "_" + SanitizePropertyName(propName);
                        context.AssetSaver.SaveAsset(flipped);
                        ObjectRegistry.RegisterReplacedObject(t2d, flipped);
                    }
                    flippedCache[t2d] = flipped;
                }

                if (flipped == null) continue;

                copy.SetTexture(propName, flipped);

                // Bake the inverse flip into the per-texture scale/offset so
                // that the final sampling coordinate
                //   final = mesh_uv * scale_new + offset_new
                // satisfies
                //   flipped_tex(final) == original_tex(mesh_uv * scale_old + offset_old)
                // for every mesh_uv simultaneously.
                //
                // Since flipped_tex(p) = original_tex(1 - p) along each flipped
                // axis, we need:
                //   1 - (mesh_uv * scale_new + offset_new) == mesh_uv * scale_old + offset_old
                // which solves to:
                //   scale_new = -scale_old
                //   offset_new = 1 - offset_old - scale_old
                // along each flipped axis. Unflipped axes are left as-is.
                var oldScale  = src.GetTextureScale(propName);
                var oldOffset = src.GetTextureOffset(propName);
                var newScale  = oldScale;
                var newOffset = oldOffset;

                if (flip == FlipMode.FlipX || flip == FlipMode.FlipBoth)
                {
                    newScale.x  = -oldScale.x;
                    newOffset.x = 1f - oldOffset.x - oldScale.x;
                }
                if (flip == FlipMode.FlipY || flip == FlipMode.FlipBoth)
                {
                    newScale.y  = -oldScale.y;
                    newOffset.y = 1f - oldOffset.y - oldScale.y;
                }

                copy.SetTextureScale(propName, newScale);
                copy.SetTextureOffset(propName, newOffset);
            }

            return copy;
        }

        // ====================================================================
        // Texture flip
        // ====================================================================

        /// <summary>
        /// Produce a Texture2D that is the input texture mirrored along the
        /// requested axes. Internally uses Graphics.Blit on a render texture
        /// so the source asset does not need Read/Write enabled. Color space
        /// is preserved by reading the importer's sRGB flag when available.
        ///
        /// <para>The original texture format is replaced with RGBA32 — the
        /// alternative is to deal with every BC/ETC variant and the build-time
        /// platform recompression handles compression for us downstream.</para>
        /// </summary>
        private static Texture2D BuildFlippedTexture(Texture2D src, FlipMode flip)
        {
            if (src == null) return null;
            if (flip == FlipMode.None) return null;

            int w = src.width;
            int h = src.height;
            if (w <= 0 || h <= 0) return null;

            // Determine the source's color space. We default to sRGB and only
            // mark a texture linear when the importer explicitly says so.
            // Built-in textures and runtime-generated textures (no asset path)
            // fall back to sRGB which is the conservative choice for albedo /
            // emission / etc.; users with linear-space mask textures can mark
            // them as such on the importer.
            bool linear = false;
            var path = AssetDatabase.GetAssetPath(src);
            if (!string.IsNullOrEmpty(path))
            {
                var imp = AssetImporter.GetAtPath(path) as TextureImporter;
                if (imp != null) linear = !imp.sRGBTexture;
            }

            var rwMode = linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB;
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, rwMode);
            var prevActive = RenderTexture.active;
            try
            {
                // Graphics.Blit with scale/offset performs the flip on the GPU,
                // which avoids needing Read/Write enabled on the source asset.
                Vector2 scale, offset;
                switch (flip)
                {
                    case FlipMode.FlipX:    scale = new Vector2(-1,  1); offset = new Vector2(1, 0); break;
                    case FlipMode.FlipY:    scale = new Vector2( 1, -1); offset = new Vector2(0, 1); break;
                    case FlipMode.FlipBoth: scale = new Vector2(-1, -1); offset = new Vector2(1, 1); break;
                    default:                scale = Vector2.one;          offset = Vector2.zero;     break;
                }
                Graphics.Blit(src, rt, scale, offset);

                RenderTexture.active = rt;
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: true, linear: linear)
                {
                    name = src.name,
                    wrapMode = src.wrapMode,
                    filterMode = src.filterMode,
                    anisoLevel = src.anisoLevel,
                };
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply(updateMipmaps: true, makeNoLongerReadable: false);
                return tex;
            }
            finally
            {
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        private static string SanitizePropertyName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "tex";
            var chars = raw.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                var c = chars[i];
                if (System.IO.Path.GetInvalidFileNameChars().Contains(c)) chars[i] = '_';
            }
            return new string(chars);
        }

        // ====================================================================
        // Misc
        // ====================================================================

        private static int MakeSeed(int userSeed)
        {
            if (userSeed != 0) return userSeed;
            unchecked
            {
                return (int)(System.DateTime.Now.Ticks ^ System.Environment.TickCount);
            }
        }
    }
}
