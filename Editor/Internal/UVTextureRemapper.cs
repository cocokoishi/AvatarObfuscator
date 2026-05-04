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
    /// Per-UV-island texture rearrangement obfuscator.
    ///
    /// <para>This is the same principle as TTT's <c>AtlasTexture</c> — even a
    /// "single-texture atlas group" rearranges UV islands at pack time, which
    /// produces a byte-different texture without altering the visible result.
    /// We adopt that mental model here: every texture on the avatar is treated
    /// as its own one-texture atlas group, its UV islands are detected via the
    /// same union-find approach TTT uses (<c>IslandUtility</c>), and each
    /// island gets a deterministic within-bbox transform (FlipH / FlipV /
    /// Rot180). Mesh UVs are rewritten in lockstep with the texture-pixel
    /// transform so visuals remain identical.</para>
    ///
    /// <para>Why per-island, not per-tile: a uniform N×N tile permutation tears
    /// any triangle whose three UV vertices fall in different tiles — the GPU
    /// linearly interpolates UV across the triangle, which after a per-vertex
    /// tile remap samples disjoint regions of the shuffled texture. Per-island
    /// transforms avoid this because every triangle's three vertices belong to
    /// the same island by construction (they share vertex indices, hence the
    /// same union-find component), so all three vertices receive the same
    /// transform and the triangle moves as a unit.</para>
    ///
    /// <para>Why FlipH / FlipV / Rot180 (and not arbitrary translations): each
    /// of these is a within-bbox involution, so the island stays inside its
    /// own UV bbox and the texture-pixel transform stays inside the same
    /// bbox of pixels. Two different islands that happen to share overlapping
    /// bboxes therefore can't both apply non-identity transforms without
    /// corrupting each other; we resolve such conflicts by giving the smaller
    /// island the identity transform (largest-island-first ordering).</para>
    ///
    /// <para>Material reference rewrites are recorded in
    /// <see cref="ObfuscationContext.MaterialReplacements"/> and mesh
    /// replacements in <see cref="ObfuscationContext.MeshReplacements"/> so the
    /// animation-clip pass redirects ObjectReference curves accordingly.</para>
    /// </summary>
    internal static class UVTextureRemapper
    {
        // ====================================================================
        // Public entry
        // ====================================================================

        public static void Run(BuildContext context, ObfuscationContext state)
        {
            var renderers = context.AvatarRootObject.GetComponentsInChildren<Renderer>(true);

            // ---------------------------------------------------------------
            // 1. Collect every material and every Texture2D in use.
            //    Also collect the unique meshes we'll need to scan for islands.
            // ---------------------------------------------------------------
            var allMaterials = new HashSet<Material>();
            var allTextures = new HashSet<Texture2D>();
            var allMeshes = new HashSet<Mesh>();
            foreach (var r in renderers)
            {
                if (r == null) continue;
                var srcMesh = GetSharedMesh(r);
                if (srcMesh != null) allMeshes.Add(srcMesh);
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || m.shader == null) continue;
                    allMaterials.Add(m);
                    EnumerateMaterialTextures(m, allTextures);
                }
            }
            if (allMaterials.Count == 0 || allMeshes.Count == 0) return;

            int avatarSeed = context.AvatarRootObject != null
                ? context.AvatarRootObject.GetInstanceID()
                : 42;

            // ---------------------------------------------------------------
            // 2. Per-mesh, detect UV0 islands via union-find on shared UVs.
            //    Cache by Mesh asset (multiple renderers may share a mesh).
            // ---------------------------------------------------------------
            var meshIslands = new Dictionary<Mesh, List<UvIsland>>();
            foreach (var mesh in allMeshes)
            {
                if (mesh == null) continue;
                var islands = DetectIslands(mesh);
                if (islands.Count > 0) meshIslands[mesh] = islands;
            }
            if (meshIslands.Count == 0) return;

            // ---------------------------------------------------------------
            // 3. Build texture → list of (mesh, island) entries.
            //    For each renderer × submesh, the assigned material's textures
            //    are sampled by the islands that own at least one triangle in
            //    that submesh. We avoid double-counting via a HashSet, and
            //    cache the vertex → island map per mesh so renderers sharing
            //    the same mesh / multiple submeshes don't pay the rebuild cost.
            // ---------------------------------------------------------------
            var textureIslands = new Dictionary<Texture2D, List<MeshIsland>>();
            var seen = new HashSet<(Texture2D, Mesh, UvIsland)>();
            var vertToIslandCache = new Dictionary<Mesh, Dictionary<int, UvIsland>>();
            foreach (var r in renderers)
            {
                if (r == null) continue;
                var mesh = GetSharedMesh(r);
                if (mesh == null || !meshIslands.TryGetValue(mesh, out var islands)) continue;
                if (!vertToIslandCache.TryGetValue(mesh, out var vertToIsland))
                    vertToIsland = vertToIslandCache[mesh] = BuildVertToIsland(islands);

                var mats = r.sharedMaterials;
                int submeshCount = mesh.subMeshCount;
                int slotCount = Math.Min(mats != null ? mats.Length : 0, submeshCount);

                for (int s = 0; s < slotCount; s++)
                {
                    var mat = mats[s];
                    if (mat == null || mat.shader == null) continue;

                    var islandsInSubmesh = IslandsTouchingSubmesh(mesh, s, vertToIsland);
                    if (islandsInSubmesh.Count == 0) continue;

                    var matTextures = new HashSet<Texture2D>();
                    EnumerateMaterialTextures(mat, matTextures);

                    foreach (var tex in matTextures)
                    {
                        if (tex == null) continue;
                        if (!textureIslands.TryGetValue(tex, out var list))
                            list = textureIslands[tex] = new List<MeshIsland>();
                        foreach (var island in islandsInSubmesh)
                        {
                            var key = (tex, mesh, island);
                            if (seen.Add(key)) list.Add(new MeshIsland(mesh, island));
                        }
                    }
                }
            }
            if (textureIslands.Count == 0) return;

            // ---------------------------------------------------------------
            // 4. Pick a deterministic transform per island, with bbox conflict
            //    resolution per texture. Process islands largest-bbox-first so
            //    big regions win over small overlapping ones.
            // ---------------------------------------------------------------
            // First, determine for every island the textures it touches, by
            // inverting textureIslands.
            var islandTextures = new Dictionary<UvIsland, List<Texture2D>>();
            foreach (var kv in textureIslands)
            {
                foreach (var mi in kv.Value)
                {
                    if (!islandTextures.TryGetValue(mi.Island, out var list))
                        list = islandTextures[mi.Island] = new List<Texture2D>();
                    list.Add(kv.Key);
                }
            }

            // Flatten and sort all (mesh, island) pairs by area desc, with
            // explicit tiebreakers on bbox xMin / yMin / mesh id so the order
            // is deterministic for ties (List.Sort is not stable).
            var allPairs = new List<MeshIsland>();
            foreach (var kv in meshIslands)
                foreach (var island in kv.Value)
                    if (islandTextures.ContainsKey(island))
                        allPairs.Add(new MeshIsland(kv.Key, island));
            allPairs.Sort((a, b) =>
            {
                int areaCmp = b.Island.Bbox.Area().CompareTo(a.Island.Bbox.Area());
                if (areaCmp != 0) return areaCmp;
                int xCmp = a.Island.Bbox.xMin.CompareTo(b.Island.Bbox.xMin);
                if (xCmp != 0) return xCmp;
                int yCmp = a.Island.Bbox.yMin.CompareTo(b.Island.Bbox.yMin);
                if (yCmp != 0) return yCmp;
                int meshCmp = a.Mesh.GetInstanceID().CompareTo(b.Mesh.GetInstanceID());
                if (meshCmp != 0) return meshCmp;
                return a.Island.RootId.CompareTo(b.Island.RootId);
            });

            // Per-texture occupancy lists (to detect overlapping non-identity
            // bboxes). An island only gets a transform when its bbox doesn't
            // overlap any earlier-applied island's bbox in any of its textures.
            var occupancy = new Dictionary<Texture2D, List<Rect>>();
            foreach (var pair in allPairs)
            {
                var island = pair.Island;
                if (island.Bbox.Area() < 1e-6f) { island.Transform = IslandTransform.Identity; continue; }
                if (!islandTextures.TryGetValue(island, out var textures)) continue;

                bool conflict = false;
                foreach (var tex in textures)
                {
                    if (occupancy.TryGetValue(tex, out var rects) && AnyOverlap(island.Bbox, rects))
                    { conflict = true; break; }
                }

                if (conflict)
                {
                    island.Transform = IslandTransform.Identity;
                    continue;
                }

                island.Transform = PickTransform(island.Bbox, pair.Mesh.GetInstanceID(), avatarSeed);
                if (island.Transform == IslandTransform.Identity) continue;
                foreach (var tex in textures)
                {
                    if (!occupancy.TryGetValue(tex, out var rects))
                        rects = occupancy[tex] = new List<Rect>();
                    rects.Add(island.Bbox);
                }
            }

            // ---------------------------------------------------------------
            // 5. Build the rearranged texture per source Texture2D. A texture
            //    referenced by N materials produces exactly 1 obfuscated copy.
            // ---------------------------------------------------------------
            var rearrangedCache = new Dictionary<Texture2D, Texture2D>();
            foreach (var kv in textureIslands)
            {
                var tex = kv.Key;
                if (tex == null || rearrangedCache.ContainsKey(tex)) continue;
                if (IsHdrFormat(tex.format)) continue; // HDR: skip safely

                // Skip if no island for this texture got a non-identity transform.
                bool anyTransform = false;
                foreach (var mi in kv.Value)
                    if (mi.Island.Transform != IslandTransform.Identity) { anyTransform = true; break; }
                if (!anyTransform) continue;

                var built = BuildRearrangedTexture(context, state, tex, kv.Value);
                if (built == null) continue;

                built.name = state.NameGen != null ? state.NameGen.Next() : tex.name;
                context.AssetSaver.SaveAsset(built);
                ObjectRegistry.RegisterReplacedObject(tex, built);
                rearrangedCache[tex] = built;
            }
            if (rearrangedCache.Count == 0) return;

            // ---------------------------------------------------------------
            // 6. Clone every material that references at least one
            //    rearranged texture, swapping its texture references.
            //    Material UV scale/offset is NOT changed — the rearranged
            //    texture occupies the same [0,1]² UV space.
            // ---------------------------------------------------------------
            var matRemap = new Dictionary<Material, Material>();
            foreach (var orig in allMaterials)
            {
                var newMat = BuildRemappedMaterial(context, orig, rearrangedCache);
                if (newMat == null || newMat == orig) continue;
                matRemap[orig] = newMat;
                state.MaterialReplacements[orig] = newMat;
                ObjectRegistry.RegisterReplacedObject(orig, newMat);
            }

            // ---------------------------------------------------------------
            // 7. Clone every mesh whose island transforms produced a non-
            //    identity edit, remap UVs (all 4 channels) per island, and
            //    swap renderer mesh + material slots in lockstep.
            // ---------------------------------------------------------------
            var meshCloneCache = new Dictionary<Mesh, Mesh>();
            foreach (var r in renderers)
            {
                if (r == null) continue;
                var srcMesh = GetSharedMesh(r);
                if (srcMesh == null || !meshIslands.TryGetValue(srcMesh, out var islands)) continue;

                bool anyEdit = false;
                foreach (var island in islands)
                    if (island.Transform != IslandTransform.Identity) { anyEdit = true; break; }

                bool anyMatRemap = false;
                var mats = r.sharedMaterials;
                if (mats != null)
                    for (int s = 0; s < mats.Length; s++)
                        if (mats[s] != null && matRemap.ContainsKey(mats[s])) { anyMatRemap = true; break; }

                if (!anyEdit && !anyMatRemap) continue;

                // Swap material slots first (cheap, mesh-independent).
                if (anyMatRemap && mats != null)
                {
                    var newMats = mats;
                    for (int s = 0; s < newMats.Length; s++)
                        if (newMats[s] != null && matRemap.TryGetValue(newMats[s], out var rep))
                            newMats[s] = rep;
                    r.sharedMaterials = newMats;
                }

                if (!anyEdit) continue;

                if (!meshCloneCache.TryGetValue(srcMesh, out var clonedMesh))
                {
                    clonedMesh = Object.Instantiate(srcMesh);
                    clonedMesh.name = srcMesh.name; // FinalizeAssetsPass renames later
                    RemapAllUvChannels(clonedMesh, islands);
                    clonedMesh.UploadMeshData(false);
                    context.AssetSaver.SaveAsset(clonedMesh);
                    ObjectRegistry.RegisterReplacedObject(srcMesh, clonedMesh);
                    meshCloneCache[srcMesh] = clonedMesh;
                    state.MeshReplacements[srcMesh] = clonedMesh;
                }

                SetSharedMesh(r, clonedMesh);
            }
        }

        // ====================================================================
        // Per-island transform picking
        // ====================================================================

        /// <summary>
        /// Pick a deterministic non-identity transform for a UV island. Identity
        /// is reserved for explicit conflict-fallback by the caller.
        /// </summary>
        private static IslandTransform PickTransform(Rect bbox, int meshId, int avatarSeed)
        {
            uint h = (uint)(avatarSeed ^ meshId);
            int cx = (int)(bbox.center.x * 65536f);
            int cy = (int)(bbox.center.y * 65536f);
            h ^= (uint)cx;
            h ^= ((uint)cy) << 16;
            h ^= h << 13; h ^= h >> 17; h ^= h << 5;
            // 3 non-identity transforms; identity is reserved for fallback.
            switch (h % 3u)
            {
                case 0: return IslandTransform.FlipH;
                case 1: return IslandTransform.FlipV;
                default: return IslandTransform.Rot180;
            }
        }

        // ====================================================================
        // Island detection (TTT-style union-find on shared UV positions)
        // ====================================================================

        /// <summary>
        /// Build UV islands for a mesh's UV0 channel. Two triangles are placed
        /// in the same island iff they share at least one UV position (matching
        /// the TTT IslandUtility approach: vertex positions identical in UV
        /// space are merged first, then triangles unite their merged-vertex
        /// classes).
        /// </summary>
        private static List<UvIsland> DetectIslands(Mesh mesh)
        {
            var result = new List<UvIsland>();
            if (mesh == null) return result;

            var uvList = new List<Vector2>();
            mesh.GetUVs(0, uvList);
            int vertCount = uvList.Count;
            if (vertCount == 0) return result;

            // Map identical UV positions to a unique key, so two vertices that
            // share a UV (but have separate vertex indices because of split
            // normals/tangents) end up in the same union-find group.
            var uvToUnique = new Dictionary<Vector2, int>(vertCount);
            var vertToUnique = new int[vertCount];
            int uniqueCount = 0;
            for (int i = 0; i < vertCount; i++)
            {
                var uv = uvList[i];
                if (!uvToUnique.TryGetValue(uv, out var u))
                { u = uniqueCount++; uvToUnique[uv] = u; }
                vertToUnique[i] = u;
            }

            // Union-find on unique UV positions.
            var parent = new int[uniqueCount];
            var rank = new int[uniqueCount];
            for (int i = 0; i < uniqueCount; i++) parent[i] = i;

            int Find(int x)
            {
                while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
                return x;
            }
            void Union(int a, int b)
            {
                a = Find(a); b = Find(b);
                if (a == b) return;
                if (rank[a] < rank[b]) (a, b) = (b, a);
                parent[b] = a;
                if (rank[a] == rank[b]) rank[a]++;
            }

            // Walk every submesh's triangles to merge connected components.
            int subCount = mesh.subMeshCount;
            for (int s = 0; s < subCount; s++)
            {
                var tris = mesh.GetTriangles(s);
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    int v0 = tris[t], v1 = tris[t + 1], v2 = tris[t + 2];
                    if (v0 < 0 || v1 < 0 || v2 < 0) continue;
                    if (v0 >= vertCount || v1 >= vertCount || v2 >= vertCount) continue;
                    int u0 = vertToUnique[v0], u1 = vertToUnique[v1], u2 = vertToUnique[v2];
                    Union(u0, u1);
                    Union(u1, u2);
                }
            }

            // Collect islands — one per representative of the union-find.
            var rootToIsland = new Dictionary<int, UvIsland>();
            for (int i = 0; i < vertCount; i++)
            {
                int root = Find(vertToUnique[i]);
                if (!rootToIsland.TryGetValue(root, out var island))
                {
                    island = new UvIsland(root, uvList[i]);
                    rootToIsland[root] = island;
                    result.Add(island);
                }
                else
                {
                    island.Expand(uvList[i]);
                }
                island.AddVertex(i);
            }

            return result;
        }

        /// <summary>
        /// Find islands that own at least one triangle in <paramref name="submeshIndex"/>
        /// of <paramref name="mesh"/>. Used to associate a submesh's material
        /// textures with the correct islands. The vertex → island lookup is
        /// supplied externally so it can be reused across calls on the same
        /// mesh (multiple submeshes / multiple renderers sharing the mesh).
        /// </summary>
        private static HashSet<UvIsland> IslandsTouchingSubmesh(Mesh mesh, int submeshIndex,
            Dictionary<int, UvIsland> vertToIsland)
        {
            var result = new HashSet<UvIsland>();
            var tris = mesh.GetTriangles(submeshIndex);
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                if (vertToIsland.TryGetValue(tris[t], out var island))
                    result.Add(island);
            }
            return result;
        }

        /// <summary>
        /// Build the vertex → island lookup for a mesh. O(vertCount) — cache
        /// per mesh so multiple submeshes / multiple renderers reuse it.
        /// </summary>
        private static Dictionary<int, UvIsland> BuildVertToIsland(List<UvIsland> islands)
        {
            var map = new Dictionary<int, UvIsland>();
            foreach (var island in islands)
                foreach (var v in island.VertIndices)
                    map[v] = island;
            return map;
        }

        // ====================================================================
        // Rect helpers
        // ====================================================================

        private static bool AnyOverlap(Rect bbox, List<Rect> others)
        {
            foreach (var o in others)
                if (RectOverlaps(bbox, o)) return true;
            return false;
        }

        private static bool RectOverlaps(Rect a, Rect b)
        {
            // Half-open overlap: rects sharing only an edge are NOT considered
            // overlapping, so adjacent islands packed tightly can both be
            // transformed.
            if (a.xMax <= b.xMin || b.xMax <= a.xMin) return false;
            if (a.yMax <= b.yMin || b.yMax <= a.yMin) return false;
            return true;
        }

        // ====================================================================
        // Mesh UV remap
        // ====================================================================

        /// <summary>
        /// Apply each island's transform to every UV channel (0–3) on
        /// <paramref name="mesh"/>. UV2 / UV3 are stored as Vector4 by Unity;
        /// only the xy components are remapped. A vertex not in any island is
        /// left untouched.
        /// </summary>
        private static void RemapAllUvChannels(Mesh mesh, List<UvIsland> islands)
        {
            // Build vertex → (island, transform, bbox) lookup once.
            int vertCount = mesh.vertexCount;
            var perVertIsland = new UvIsland[vertCount];
            foreach (var island in islands)
                foreach (var v in island.VertIndices)
                    if (v >= 0 && v < vertCount) perVertIsland[v] = island;

            for (int ch = 0; ch < 4; ch++)
            {
                var uvs = new List<Vector4>();
                mesh.GetUVs(ch, uvs);
                if (uvs.Count == 0) continue;

                int n = Math.Min(uvs.Count, vertCount);
                bool changed = false;
                for (int i = 0; i < n; i++)
                {
                    var island = perVertIsland[i];
                    if (island == null || island.Transform == IslandTransform.Identity) continue;

                    var uv = uvs[i];
                    var p = ApplyUvTransform(new Vector2(uv.x, uv.y), island.Bbox, island.Transform);
                    uvs[i] = new Vector4(p.x, p.y, uv.z, uv.w);
                    changed = true;
                }
                if (changed) mesh.SetUVs(ch, uvs);
            }
        }

        private static Vector2 ApplyUvTransform(Vector2 uv, Rect bbox, IslandTransform t)
        {
            switch (t)
            {
                case IslandTransform.FlipH:
                    return new Vector2(bbox.xMin + bbox.xMax - uv.x, uv.y);
                case IslandTransform.FlipV:
                    return new Vector2(uv.x, bbox.yMin + bbox.yMax - uv.y);
                case IslandTransform.Rot180:
                    return new Vector2(bbox.xMin + bbox.xMax - uv.x,
                                       bbox.yMin + bbox.yMax - uv.y);
                default: return uv;
            }
        }

        // ====================================================================
        // Texture rearrangement
        // ====================================================================

        /// <summary>
        /// Build a texture whose pixels have been rearranged in lockstep with
        /// the UV transforms decided per island. The pipeline mirrors the
        /// previous v0.2.3 path (Blit → ReadPixels → CPU mutation → SetPixels
        /// → recompress) so it works for every readable / non-readable input.
        /// </summary>
        private static Texture2D BuildRearrangedTexture(BuildContext context,
            ObfuscationContext state, Texture2D src, List<MeshIsland> islandPairs)
        {
            if (src == null) return null;
            int w = src.width;
            int h = src.height;
            if (w <= 0 || h <= 0) return null;

            bool linear = ResolveLinearFromTexture(src);
            var rwMode = linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB;
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, rwMode);
            var prevActive = RenderTexture.active;
            Texture2D output = null;

            try
            {
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;

                var cpuSrc = new Texture2D(w, h, TextureFormat.RGBA32, false, linear);
                cpuSrc.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                cpuSrc.Apply(false);

                var srcPixels = cpuSrc.GetPixels();
                // Initial: identity copy, so untouched regions retain source.
                var dstPixels = (Color[])srcPixels.Clone();
                Object.DestroyImmediate(cpuSrc);

                // Apply each island's transform to the corresponding pixel
                // bbox. Distinct islands may map to the same source bbox in
                // pixel space (rare, and resolved by AnyOverlap conflict logic
                // upstream — at most one of them is non-identity), so we just
                // apply them in order.
                foreach (var mi in islandPairs)
                {
                    var island = mi.Island;
                    if (island.Transform == IslandTransform.Identity) continue;

                    ApplyTexturePixelTransform(srcPixels, dstPixels, w, h,
                        island.Bbox, island.Transform);
                }

                output = new Texture2D(w, h, TextureFormat.RGBA32, src.mipmapCount > 1, linear)
                {
                    wrapMode   = src.wrapMode,
                    filterMode = src.filterMode,
                    anisoLevel = src.anisoLevel,
                };
                output.SetPixels(dstPixels);
                output.Apply(updateMipmaps: src.mipmapCount > 1);

                TryRecompress(output, src.format, src.name);
                output.Apply(false, makeNoLongerReadable: true);
            }
            catch (Exception e)
            {
                if (output != null) Object.DestroyImmediate(output);
                Debug.LogWarning($"[AvatarObfuscator] Per-island rearrangement failed for '{src.name}': {e.Message}");
                return null;
            }
            finally
            {
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
            }

            return output;
        }

        /// <summary>
        /// Within the texel rectangle that corresponds to <paramref name="bbox"/>,
        /// rewrite <paramref name="dst"/> from <paramref name="src"/> through
        /// the chosen transform. The bbox is in [0,1] UV space; we convert
        /// to pixel coordinates with explicit clamping so off-by-one rounding
        /// at the bbox edge can't read or write outside the buffer.
        /// </summary>
        private static void ApplyTexturePixelTransform(Color[] src, Color[] dst,
            int w, int h, Rect bbox, IslandTransform t)
        {
            int x0 = Mathf.Clamp(Mathf.FloorToInt(bbox.xMin * w), 0, w);
            int x1 = Mathf.Clamp(Mathf.CeilToInt (bbox.xMax * w), 0, w);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(bbox.yMin * h), 0, h);
            int y1 = Mathf.Clamp(Mathf.CeilToInt (bbox.yMax * h), 0, h);
            if (x1 <= x0 || y1 <= y0) return;

            // For each transform, map dst[x, y] ← src[mirrored_x, mirrored_y]
            // within the rectangle. Only one of the two coords flips per axis
            // for FlipH / FlipV; both flip for Rot180.
            int xFlipBase = x0 + x1 - 1;
            int yFlipBase = y0 + y1 - 1;
            for (int y = y0; y < y1; y++)
            {
                int dstRow = y * w;
                for (int x = x0; x < x1; x++)
                {
                    int sx, sy;
                    switch (t)
                    {
                        case IslandTransform.FlipH: sx = xFlipBase - x; sy = y; break;
                        case IslandTransform.FlipV: sx = x; sy = yFlipBase - y; break;
                        case IslandTransform.Rot180: sx = xFlipBase - x; sy = yFlipBase - y; break;
                        default: continue;
                    }
                    int srcRow = sy * w;
                    dst[dstRow + x] = src[srcRow + sx];
                }
            }
        }

        // ====================================================================
        // Material build (unchanged from v0.2.3)
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
            Material src, Dictionary<Texture2D, Texture2D> rearrangedCache)
        {
            if (src == null || src.shader == null) return null;

            // Skip cloning entirely if the source has no rearranged texture
            // referenced — saves an asset and avoids needless duplication.
            int propCount = src.shader.GetPropertyCount();
            bool anyChange = false;
            for (int p = 0; p < propCount; p++)
            {
                if (src.shader.GetPropertyType(p) != ShaderPropertyType.Texture) continue;
                var t = src.GetTexture(src.shader.GetPropertyName(p));
                if (t is Texture2D t2d && rearrangedCache.ContainsKey(t2d)) { anyChange = true; break; }
            }
            if (!anyChange) return null;

            Material copy;
            using (MaterialEditorReflection.BeginNoApplyMaterialPropertyDrawers())
                copy = new Material(src);
#if UNITY_2022_1_OR_NEWER
            copy.parent = null;
#endif
            copy.name = src.name;
            context.AssetSaver.SaveAsset(copy);

            for (int p = 0; p < propCount; p++)
            {
                if (src.shader.GetPropertyType(p) != ShaderPropertyType.Texture) continue;
                var propName = src.shader.GetPropertyName(p);
                if (!copy.HasProperty(propName)) continue;

                var t = src.GetTexture(propName);
                if (t is Texture2D t2d && rearrangedCache.TryGetValue(t2d, out var rep))
                    copy.SetTexture(propName, rep);
            }

            return copy;
        }

        // ====================================================================
        // Mesh helpers (unchanged from v0.2.3)
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
        // Texture format helpers (carried over from v0.2.3 unchanged)
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

        // ====================================================================
        // Per-island bookkeeping types
        // ====================================================================

        /// <summary>
        /// One UV island: a connected component of triangles whose UV0 vertices
        /// are linked through shared positions. Carries a UV bounding rect, a
        /// sorted list of vertex indices and the chosen within-bbox transform.
        /// </summary>
        internal sealed class UvIsland
        {
            public readonly int RootId;
            public Rect Bbox;
            public readonly List<int> VertIndices = new List<int>();
            public IslandTransform Transform = IslandTransform.Identity;

            public UvIsland(int root, Vector2 firstUv)
            {
                RootId = root;
                Bbox = new Rect(firstUv.x, firstUv.y, 0, 0);
            }

            public void Expand(Vector2 uv)
            {
                float xMin = Mathf.Min(Bbox.xMin, uv.x);
                float yMin = Mathf.Min(Bbox.yMin, uv.y);
                float xMax = Mathf.Max(Bbox.xMax, uv.x);
                float yMax = Mathf.Max(Bbox.yMax, uv.y);
                Bbox = new Rect(xMin, yMin, xMax - xMin, yMax - yMin);
            }

            public void AddVertex(int v) { VertIndices.Add(v); }
        }

        /// <summary>Pair of (mesh, island) — used to associate a texture with the islands that sample it.</summary>
        internal readonly struct MeshIsland
        {
            public readonly Mesh Mesh;
            public readonly UvIsland Island;
            public MeshIsland(Mesh mesh, UvIsland island) { Mesh = mesh; Island = island; }
        }

        /// <summary>One of four within-bbox involutions an island can take.</summary>
        internal enum IslandTransform
        {
            Identity = 0,
            FlipH    = 1,
            FlipV    = 2,
            Rot180   = 3,
        }
    }

    internal static class RectExtensionsForRemap
    {
        public static float Area(this Rect r) => Mathf.Max(0, r.width) * Mathf.Max(0, r.height);
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
