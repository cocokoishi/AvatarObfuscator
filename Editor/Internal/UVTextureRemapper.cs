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
    /// (none / flipX / flipY / flipBoth), regenerates every Texture2D bound to
    /// that material with the inverse flip applied (so the asset bytes are
    /// different from the original), and rewrites mesh UV0 of the renderers
    /// that use the material so the visual result is unchanged.</para>
    ///
    /// <para>This is intentionally a minimal implementation — it does not
    /// detect UV islands, does not relocate them, does not pack them. It
    /// produces a byte-different texture (which defeats content-addressable
    /// asset matching by rippers) at near-zero risk to visual fidelity.</para>
    ///
    /// <para>Vertices shared between two submeshes that map to differently-
    /// flipped materials are detected and their renderer is skipped (with a
    /// console warning) — a single vertex cannot carry two conflicting flips.</para>
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
            // 2. Detect unsafe renderers (vertex sharing across submeshes that
            //    would receive conflicting flips). Mark every material that
            //    participates in any such conflict as "do not flip" so the
            //    renderer's mesh stays untouched.
            // ---------------------------------------------------------------
            var unsafeMaterials = new HashSet<Material>();
            foreach (var r in renderers)
            {
                if (r == null) continue;
                var mesh = GetSharedMesh(r);
                if (mesh == null) continue;
                if (!mesh.isReadable) continue; // we'll skip silently when we get to it

                var mats = r.sharedMaterials;
                int slotCount = Mathf.Min(mats.Length, mesh.subMeshCount);

                // Per-vertex flip claim. If a vertex is hit by two submeshes
                // whose materials want different flips, mark all those materials
                // as unsafe.
                var claim = new Dictionary<int, FlipMode>();
                for (int s = 0; s < slotCount; s++)
                {
                    var mat = mats[s];
                    if (mat == null) continue;
                    if (!flipFor.TryGetValue(mat, out var f)) continue;

                    foreach (var v in EnumerateSubmeshVertices(mesh, s))
                    {
                        if (claim.TryGetValue(v, out var existing))
                        {
                            if (existing != f)
                            {
                                // Conflict — both materials become unsafe.
                                unsafeMaterials.Add(mat);
                                // Walk back and find the materials that placed the existing claim.
                                for (int s2 = 0; s2 < slotCount; s2++)
                                {
                                    var m2 = mats[s2];
                                    if (m2 == null) continue;
                                    if (m2 == mat) continue;
                                    if (flipFor.TryGetValue(m2, out var f2) && f2 == existing)
                                    {
                                        // Rough heuristic; if multiple materials hold the same flip mode
                                        // on this renderer, all candidate ones get marked unsafe.
                                        if (DoesSubmeshUseVertex(mesh, s2, v))
                                            unsafeMaterials.Add(m2);
                                    }
                                }
                            }
                        }
                        else
                        {
                            claim[v] = f;
                        }
                    }
                }
            }

            if (unsafeMaterials.Count > 0)
            {
                Debug.LogWarning(
                    $"[AvatarObfuscator] UV remap: {unsafeMaterials.Count} material(s) skipped because " +
                    $"they share vertices with another submesh on the same renderer that would receive " +
                    $"a conflicting UV flip. Affected materials: " +
                    string.Join(", ", unsafeMaterials.Select(m => m.name).Take(8)) +
                    (unsafeMaterials.Count > 8 ? ", ..." : ""));
                foreach (var m in unsafeMaterials) flipFor.Remove(m);
            }

            if (flipFor.Count == 0) return;

            // ---------------------------------------------------------------
            // 3. Build the per-material remapped material (with flipped textures).
            // ---------------------------------------------------------------
            var remappedMaterial = new Dictionary<Material, Material>(flipFor.Count);
            foreach (var (orig, flip) in flipFor.Select(kv => (kv.Key, kv.Value)))
            {
                var newMat = BuildRemappedMaterial(context, orig, flip);
                if (newMat == null) continue;
                remappedMaterial[orig] = newMat;
                state.MaterialReplacements[orig] = newMat;
                ObjectRegistry.RegisterReplacedObject(orig, newMat);
            }
            if (remappedMaterial.Count == 0) return;

            // ---------------------------------------------------------------
            // 4. Rewrite mesh UV0 + renderer.sharedMaterials for every renderer.
            // ---------------------------------------------------------------
            var clonedMeshByRenderer = new Dictionary<Renderer, Mesh>();
            foreach (var r in renderers)
            {
                if (r == null) continue;
                var mesh = GetSharedMesh(r);
                if (mesh == null) continue;
                var mats = r.sharedMaterials;
                int slotCount = Mathf.Min(mats.Length, mesh.subMeshCount);

                // Decide which slots have a remap. If none, no work for this renderer.
                var slotFlip = new Dictionary<int, FlipMode>();
                for (int s = 0; s < slotCount; s++)
                {
                    var mat = mats[s];
                    if (mat != null && flipFor.TryGetValue(mat, out var f) && f != FlipMode.None)
                        slotFlip[s] = f;
                }
                if (slotFlip.Count == 0)
                {
                    // Still rewrite material slot if any are remapped to a NEW mat (none here).
                    bool anySwap = false;
                    for (int s = 0; s < mats.Length; s++)
                        if (mats[s] != null && remappedMaterial.TryGetValue(mats[s], out var rep) && rep != mats[s])
                        { mats[s] = rep; anySwap = true; }
                    if (anySwap) r.sharedMaterials = mats;
                    continue;
                }

                if (!mesh.isReadable)
                {
                    Debug.LogWarning(
                        $"[AvatarObfuscator] UV remap skipped for renderer '{r.name}': mesh '{mesh.name}' " +
                        $"is not Read/Write enabled on its importer. Enable Read/Write or accept the " +
                        $"original textures for this renderer.");
                    // Without mesh access we cannot remap UV — skip mat swap too, otherwise
                    // we'd have a flipped texture being sampled with the original UV.
                    continue;
                }

                // Get-or-clone the mesh.
                if (!clonedMeshByRenderer.TryGetValue(r, out var workMesh))
                {
                    workMesh = Object.Instantiate(mesh);
                    workMesh.name = mesh.name;
                    context.AssetSaver.SaveAsset(workMesh);
                    ObjectRegistry.RegisterReplacedObject(mesh, workMesh);
                    SetSharedMesh(r, workMesh);
                    clonedMeshByRenderer[r] = workMesh;
                }

                // Per-vertex flip mode. Pre-validated to be conflict-free above.
                var vertexToFlip = new Dictionary<int, FlipMode>();
                foreach (var (slot, flip) in slotFlip.Select(kv => (kv.Key, kv.Value)))
                {
                    foreach (var v in EnumerateSubmeshVertices(workMesh, slot))
                        vertexToFlip[v] = flip;
                }

                ApplyUVFlip(workMesh, vertexToFlip);

                // Swap materials.
                bool anySwap2 = false;
                for (int s = 0; s < mats.Length; s++)
                {
                    if (mats[s] != null && remappedMaterial.TryGetValue(mats[s], out var rep) && rep != mats[s])
                    { mats[s] = rep; anySwap2 = true; }
                }
                if (anySwap2) r.sharedMaterials = mats;
            }
        }

        // ====================================================================
        // Material build
        // ====================================================================

        private static Material BuildRemappedMaterial(BuildContext context, Material src, FlipMode flip)
        {
            if (src == null || src.shader == null) return null;

            var copy = Object.Instantiate(src);
            copy.name = src.name;
            context.AssetSaver.SaveAsset(copy);

            // For every Texture2D slot, create a flipped copy and rebind.
            int propCount = ShaderUtil.GetPropertyCount(src.shader);
            for (int p = 0; p < propCount; p++)
            {
                if (ShaderUtil.GetPropertyType(src.shader, p) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                var propName = ShaderUtil.GetPropertyName(src.shader, p);
                var t = src.GetTexture(propName);
                if (t == null) continue;
                if (!(t is Texture2D t2d))
                {
                    // Cubemaps, RTs, 3D textures — keep as-is. We only flip 2D.
                    continue;
                }

                var flipped = BuildFlippedTexture(t2d, flip);
                if (flipped == null) continue;
                flipped.name = src.name + "_" + SanitizePropertyName(propName);
                context.AssetSaver.SaveAsset(flipped);
                ObjectRegistry.RegisterReplacedObject(t2d, flipped);

                copy.SetTexture(propName, flipped);
                // Tile/offset are kept as-is on the new material; the flip is fully
                // baked into the texture pixels and the mesh UVs.
            }

            return copy;
        }

        private static Texture2D BuildFlippedTexture(Texture2D src, FlipMode flip)
        {
            if (src == null) return null;
            if (flip == FlipMode.None) return null;

            int w = src.width;
            int h = src.height;
            if (w <= 0 || h <= 0) return null;

            // sRGB / linear is read from the asset import settings. Default to
            // sRGB unless the importer explicitly says linear — same heuristic
            // as the previous TextureAtlasBuilder.
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
        // Mesh helpers
        // ====================================================================

        private static Mesh GetSharedMesh(Renderer r)
        {
            if (r is SkinnedMeshRenderer smr) return smr.sharedMesh;
            if (r.TryGetComponent<MeshFilter>(out var mf)) return mf.sharedMesh;
            return null;
        }

        private static void SetSharedMesh(Renderer r, Mesh m)
        {
            if (r is SkinnedMeshRenderer smr) { smr.sharedMesh = m; return; }
            if (r.TryGetComponent<MeshFilter>(out var mf)) mf.sharedMesh = m;
        }

        private static IEnumerable<int> EnumerateSubmeshVertices(Mesh mesh, int submeshIndex)
        {
            if (submeshIndex < 0 || submeshIndex >= mesh.subMeshCount) yield break;
            var indices = mesh.GetIndices(submeshIndex);
            for (int i = 0; i < indices.Length; i++) yield return indices[i];
        }

        private static bool DoesSubmeshUseVertex(Mesh mesh, int submeshIndex, int vertex)
        {
            if (submeshIndex < 0 || submeshIndex >= mesh.subMeshCount) return false;
            var indices = mesh.GetIndices(submeshIndex);
            for (int i = 0; i < indices.Length; i++)
                if (indices[i] == vertex) return true;
            return false;
        }

        private static void ApplyUVFlip(Mesh mesh, Dictionary<int, FlipMode> vertexToFlip)
        {
            // Preserve the channel's storage dimensionality — some VRChat shaders
            // pack data in UV0.zw (matcap masks, audio link UVs, dissolve coords).
            // Using SetUVs(0, List<Vector2>) on a Vector3/Vector4 channel would
            // silently downgrade the channel and drop the packed data.
            var dim = mesh.GetVertexAttributeDimension(UnityEngine.Rendering.VertexAttribute.TexCoord0);
            if (dim <= 0)
            {
                // No UV0 — without UVs the texture sample position is undefined.
                // Nothing to flip; leave the mesh alone.
                return;
            }

            switch (dim)
            {
                case 2:
                {
                    var uvs = new List<Vector2>(mesh.vertexCount);
                    mesh.GetUVs(0, uvs);
                    if (uvs.Count == 0) return;
                    foreach (var (v, flip) in vertexToFlip.Select(kv => (kv.Key, kv.Value)))
                    {
                        if (v < 0 || v >= uvs.Count) continue;
                        var u = uvs[v];
                        uvs[v] = ApplyFlip2(u, flip);
                    }
                    mesh.SetUVs(0, uvs);
                    break;
                }
                case 3:
                {
                    var uvs = new List<Vector3>(mesh.vertexCount);
                    mesh.GetUVs(0, uvs);
                    if (uvs.Count == 0) return;
                    foreach (var (v, flip) in vertexToFlip.Select(kv => (kv.Key, kv.Value)))
                    {
                        if (v < 0 || v >= uvs.Count) continue;
                        var u = uvs[v];
                        var f = ApplyFlip2(new Vector2(u.x, u.y), flip);
                        uvs[v] = new Vector3(f.x, f.y, u.z);
                    }
                    mesh.SetUVs(0, uvs);
                    break;
                }
                default: // 4 or unexpected
                {
                    var uvs = new List<Vector4>(mesh.vertexCount);
                    mesh.GetUVs(0, uvs);
                    if (uvs.Count == 0) return;
                    foreach (var (v, flip) in vertexToFlip.Select(kv => (kv.Key, kv.Value)))
                    {
                        if (v < 0 || v >= uvs.Count) continue;
                        var u = uvs[v];
                        var f = ApplyFlip2(new Vector2(u.x, u.y), flip);
                        uvs[v] = new Vector4(f.x, f.y, u.z, u.w);
                    }
                    mesh.SetUVs(0, uvs);
                    break;
                }
            }
        }

        private static Vector2 ApplyFlip2(Vector2 u, FlipMode flip)
        {
            switch (flip)
            {
                case FlipMode.FlipX:    return new Vector2(1f - u.x, u.y);
                case FlipMode.FlipY:    return new Vector2(u.x, 1f - u.y);
                case FlipMode.FlipBoth: return new Vector2(1f - u.x, 1f - u.y);
                default:                return u;
            }
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
