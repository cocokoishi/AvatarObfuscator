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
    /// Per-UV-island texture rearrangement obfuscator (atlas-style relocation).
    ///
    /// <para>Mirrors TexTransTool's <c>AtlasTexture</c>: every texture is
    /// treated as a one-texture atlas group, its UV islands are detected via
    /// union-find on shared UV positions (<c>IslandUtility.UVtoIsland</c>),
    /// and the islands are repacked into the [0,1]² UV square via Next-Fit
    /// Decreasing Height (the same NFDH variant TTT uses, just without 90°
    /// rotation for code simplicity). Islands move to genuinely new positions
    /// — not in-place flips — so the rearranged texture visibly differs from
    /// the source while the rendered avatar stays pixel-identical (mesh UVs
    /// are translated in lockstep).</para>
    ///
    /// <para>Each island is allocated a small padding band (≈ 0.005 UV, 5 px
    /// at 1K) around its placed bbox; that band is filled by edge-replicating
    /// the island's own border colour, so bilinear / mip filtering at island
    /// boundaries doesn't bleed in pixels from a neighbouring island that the
    /// packer happened to place adjacent. This kills the v0.2.4 boundary-seam
    /// artefact.</para>
    ///
    /// <para>Algorithm overview:</para>
    /// <list type="number">
    /// <item>Collect every renderer / material / Texture2D / unique mesh.</item>
    /// <item>Per mesh, detect UV0 islands via union-find.</item>
    /// <item>Per mesh, run NFDH packing on the island bboxes (with padding).
    /// Each island either gets a per-vertex translation or is left untouched
    /// if NFDH ran out of room.</item>
    /// <item>For every texture, re-paint a clone: copy each packed island's
    /// source bbox (plus a padding skirt of edge-replicated colour) to the
    /// island's translated target position. Untouched regions retain the
    /// source pixels (identity backing) so any UVs that didn't belong to any
    /// island still sample correctly.</item>
    /// <item>Per mesh, clone and apply per-island UV translations to all four
    /// UV channels.</item>
    /// </list>
    ///
    /// <para>Cross-mesh shared textures: if a texture is sampled by islands
    /// from multiple meshes, the rearranged texture can only be painted
    /// when every contributing mesh's NFDH succeeded. Otherwise the texture
    /// (and every island that touches it) reverts to identity — a fixed-
    /// point loop propagates that revert until stable. This trades a little
    /// obfuscation aggressiveness for predictable correctness.</para>
    ///
    /// <para>Material reference rewrites are recorded in
    /// <see cref="ObfuscationContext.MaterialReplacements"/>; mesh
    /// replacements in <see cref="ObfuscationContext.MeshReplacements"/>; both
    /// are consumed by <c>ObfuscateAnimationClipsPass</c> to redirect
    /// ObjectReference curves.</para>
    /// </summary>
    internal static class UVTextureRemapper
    {
        /// <summary>UV-space padding around each island in the packed layout.</summary>
        private const float PaddingUv = 0.005f;
        /// <summary>Hard floor on the pixel-space padding (so very small textures still get a usable skirt).</summary>
        private const int MinPaddingPixels = 2;

        // ====================================================================
        // Public entry
        // ====================================================================

        public static void Run(BuildContext context, ObfuscationContext state)
        {
            var renderers = context.AvatarRootObject.GetComponentsInChildren<Renderer>(true);

            // ---------------------------------------------------------------
            // 1. Collect every material / Texture2D / unique mesh.
            // ---------------------------------------------------------------
            var allMaterials = new HashSet<Material>();
            var allMeshes = new HashSet<Mesh>();
            foreach (var r in renderers)
            {
                if (r == null) continue;
                var srcMesh = GetSharedMesh(r);
                if (srcMesh != null) allMeshes.Add(srcMesh);
                if (r.sharedMaterials == null) continue;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || m.shader == null) continue;
                    allMaterials.Add(m);
                }
            }
            if (allMaterials.Count == 0 || allMeshes.Count == 0) return;

            // ---------------------------------------------------------------
            // 2. Per-mesh UV0 island detection (union-find on shared UVs).
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
            //    that submesh. Cache the vertex → island map per mesh so
            //    multiple submeshes / multiple renderers don't pay the rebuild.
            // ---------------------------------------------------------------
            var textureIslands = new Dictionary<Texture2D, List<MeshIsland>>();
            var seen = new HashSet<(Texture2D, Mesh, UvIsland)>();
            var vertToIslandCache = new Dictionary<Mesh, Dictionary<int, UvIsland>>();
            // Textures observed with a non-identity per-material _ST anywhere
            // on the avatar — these get skipped because our UV-translation
            // shift (Translation) and the GPU's sample shift (Translation *
            // Scale) disagree whenever Scale != 1 / Offset != 0. See
            // EnumerateMaterialTextures for the full reasoning.
            var nonIdentityStTextures = new HashSet<Texture2D>();
            foreach (var r in renderers)
            {
                if (r == null) continue;
                var mesh = GetSharedMesh(r);
                if (mesh == null || !meshIslands.TryGetValue(mesh, out var islands)) continue;
                if (!vertToIslandCache.TryGetValue(mesh, out var vertToIsland))
                    vertToIsland = vertToIslandCache[mesh] = BuildVertToIsland(islands);

                var mats = r.sharedMaterials;
                if (mats == null) continue;
                int submeshCount = mesh.subMeshCount;
                int slotCount = Math.Min(mats.Length, submeshCount);

                for (int s = 0; s < slotCount; s++)
                {
                    var mat = mats[s];
                    if (mat == null || mat.shader == null) continue;

                    var islandsInSubmesh = IslandsTouchingSubmesh(mesh, s, vertToIsland);
                    if (islandsInSubmesh.Count == 0) continue;

                    var matTextures = new HashSet<Texture2D>();
                    EnumerateMaterialTextures(mat, matTextures, nonIdentityStTextures);

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
            // Remove any texture that had a non-identity _ST in ANY material
            // — we can't obfuscate it safely. A single-material avatar with
            // non-default tiling on _MainTex would otherwise produce the
            // exact scrambled-texture symptom we're fixing.
            if (nonIdentityStTextures.Count > 0)
                foreach (var tex in nonIdentityStTextures) textureIslands.Remove(tex);
            if (textureIslands.Count == 0) return;

            // ---------------------------------------------------------------
            // 4. Per-mesh, run NFDH packing. Each island either gets a
            //    Translation + IsPacked, or stays at identity.
            // ---------------------------------------------------------------
            foreach (var kv in meshIslands)
                NFDHPack(kv.Value, PaddingUv);

            // ---------------------------------------------------------------
            // 4.5. Cross-mesh consistency: a texture can only be painted if
            //      EVERY island it touches is packed. If even one is
            //      un-packed, we revert the rest (an island's revert might
            //      cascade — a fixed-point loop converges).
            // ---------------------------------------------------------------
            EnforceCrossMeshConsistency(textureIslands);

            // ---------------------------------------------------------------
            // 5. Build the rearranged texture per source Texture2D.
            //    A texture referenced by N materials produces exactly 1 copy.
            // ---------------------------------------------------------------
            var rearrangedCache = new Dictionary<Texture2D, Texture2D>();
            foreach (var kv in textureIslands)
            {
                var tex = kv.Key;
                if (tex == null || rearrangedCache.ContainsKey(tex)) continue;
                if (IsHdrFormat(tex.format)) continue;

                bool anyPacked = false;
                bool anyUnpacked = false;
                foreach (var mi in kv.Value)
                {
                    if (mi.Island.IsPacked) anyPacked = true;
                    else anyUnpacked = true;
                }
                // Cross-mesh consistency above means it's all-or-nothing per
                // texture, but defend anyway.
                if (!anyPacked || anyUnpacked) continue;

                var built = BuildRearrangedTexture(context, tex, kv.Value);
                if (built == null) continue;

                built.name = state.NameGen != null ? state.NameGen.Next() : tex.name;
                context.AssetSaver.SaveAsset(built);
                ObjectRegistry.RegisterReplacedObject(tex, built);
                rearrangedCache[tex] = built;
            }
            if (rearrangedCache.Count == 0) return;

            // ---------------------------------------------------------------
            // 6. Clone every material that references at least one rearranged
            //    texture. UV scale/offset is NOT changed.
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
            // 7. Clone every mesh whose island translations produced a non-
            //    identity edit, remap UV channels 0..3, and swap renderer
            //    mesh + material slots in lockstep.
            // ---------------------------------------------------------------
            var meshCloneCache = new Dictionary<Mesh, Mesh>();
            foreach (var r in renderers)
            {
                if (r == null) continue;
                var srcMesh = GetSharedMesh(r);
                if (srcMesh == null || !meshIslands.TryGetValue(srcMesh, out var islands)) continue;

                bool anyEdit = false;
                foreach (var island in islands)
                    if (island.IsPacked) { anyEdit = true; break; }

                bool anyMatRemap = false;
                var mats = r.sharedMaterials;
                if (mats != null)
                    for (int s = 0; s < mats.Length; s++)
                        if (mats[s] != null && matRemap.ContainsKey(mats[s])) { anyMatRemap = true; break; }

                if (!anyEdit && !anyMatRemap) continue;

                if (anyMatRemap && mats != null)
                {
                    for (int s = 0; s < mats.Length; s++)
                        if (mats[s] != null && matRemap.TryGetValue(mats[s], out var rep))
                            mats[s] = rep;
                    r.sharedMaterials = mats;
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
        // NFDH packer (Next-Fit Decreasing Height, no rotation)
        // ====================================================================

        /// <summary>
        /// Pack the islands' bboxes (each expanded by <paramref name="padding"/>
        /// on every side) into [0,1]² using Next-Fit Decreasing Height.
        /// Either every non-degenerate island ends up with
        /// <see cref="UvIsland.IsPacked"/>=true and a non-zero
        /// <see cref="UvIsland.Translation"/>, or — if the islands don't fit —
        /// every island in the list is left at identity.
        /// </summary>
        /// <returns>true iff the packing succeeded for every non-degenerate
        /// island in the list.</returns>
        private static bool NFDHPack(List<UvIsland> islands, float padding)
        {
            int n = islands.Count;
            if (n == 0) return true;

            // Reset state from any previous run and skip degenerate islands —
            // a 0-area island has nothing to translate and should never
            // affect packing decisions.
            foreach (var island in islands)
            {
                island.IsPacked = false;
                island.Translation = Vector2.zero;
            }

            // Filter to non-degenerate. Sort tall first; deterministic
            // tiebreakers so the packed layout is identical run-to-run.
            var order = new List<int>(n);
            for (int i = 0; i < n; i++)
            {
                var b = islands[i].Bbox;
                if (b.width > 1e-5f && b.height > 1e-5f) order.Add(i);
            }
            if (order.Count == 0) return true;

            order.Sort((a, b) =>
            {
                var ba = islands[a].Bbox;
                var bb = islands[b].Bbox;
                int hCmp = bb.height.CompareTo(ba.height);
                if (hCmp != 0) return hCmp;
                int wCmp = bb.width.CompareTo(ba.width);
                if (wCmp != 0) return wCmp;
                int xCmp = ba.xMin.CompareTo(bb.xMin);
                if (xCmp != 0) return xCmp;
                int yCmp = ba.yMin.CompareTo(bb.yMin);
                if (yCmp != 0) return yCmp;
                return islands[a].RootId.CompareTo(islands[b].RootId);
            });

            float curX = 0f, curY = 0f, rowH = 0f;
            const float container = 1f;

            for (int k = 0; k < order.Count; k++)
            {
                int idx = order[k];
                var b = islands[idx].Bbox;
                float pw = b.width  + 2f * padding;
                float ph = b.height + 2f * padding;

                // Single padded island taller than the container = unpackable.
                if (pw > container || ph > container)
                {
                    // Roll back any placements done so far.
                    foreach (var island in islands) { island.IsPacked = false; island.Translation = Vector2.zero; }
                    return false;
                }

                // New row when the current row can't fit the next island.
                if (curX + pw > container && curX > 0f)
                {
                    curY += rowH;
                    curX = 0f;
                    rowH = 0f;
                }

                if (curY + ph > container)
                {
                    foreach (var island in islands) { island.IsPacked = false; island.Translation = Vector2.zero; }
                    return false;
                }

                // Place padded box at (curX, curY); content top-left lives at
                // (curX + padding, curY + padding) so the padding band stays
                // inside the padded box but outside the content rect.
                var newSrcMin = new Vector2(curX + padding, curY + padding);
                var oldSrcMin = new Vector2(b.xMin, b.yMin);
                islands[idx].Translation = newSrcMin - oldSrcMin;
                islands[idx].IsPacked = true;

                curX += pw;
                if (ph > rowH) rowH = ph;
            }

            return true;
        }

        // ====================================================================
        // Cross-mesh consistency
        // ====================================================================

        /// <summary>
        /// Cross-mesh consistency enforcement. There are two failure modes:
        ///
        /// <para>1. Mixed pack state: one mesh's islands packed, another's
        /// unpacked. The rearranged texture gets painted from the packed
        /// mesh's layout, but the unpacked mesh still samples at original
        /// UVs → wrong content.</para>
        ///
        /// <para>2. Independent-NFDH target collision: both meshes packed,
        /// but each mesh's NFDH ran independently, so two different islands
        /// (one per mesh) can be assigned to overlapping target rects in
        /// [0,1]². Painting the single shared texture then overwrites one
        /// mesh's content with the other's; that mesh samples garbage.</para>
        ///
        /// <para>Both modes are a correctness bug for the texture — the
        /// cheapest safe fix is to revert EVERY island of any shared
        /// texture (touched by &gt; 1 distinct Mesh) to identity, then
        /// propagate the revert via the original mixed-state loop until a
        /// fixed point.</para>
        /// </summary>
        private static void EnforceCrossMeshConsistency(
            Dictionary<Texture2D, List<MeshIsland>> textureIslands)
        {
            // Pass 1: any texture referenced by islands from ≥ 2 distinct
            // meshes → revert every island of that texture to identity.
            // NFDH is per-mesh, so independent layouts in shared textures
            // can collide in target space and overwrite each other.
            foreach (var kv in textureIslands)
            {
                Mesh firstMesh = null;
                bool multiMesh = false;
                foreach (var mi in kv.Value)
                {
                    if (firstMesh == null) firstMesh = mi.Mesh;
                    else if (mi.Mesh != firstMesh) { multiMesh = true; break; }
                }
                if (!multiMesh) continue;
                foreach (var mi in kv.Value)
                {
                    mi.Island.IsPacked = false;
                    mi.Island.Translation = Vector2.zero;
                }
            }

            // Pass 2: fixed-point propagation of the mixed-pack-state
            // invariant (any unpacked island in a texture's set ⇒ revert
            // every island in that set). Pass 1 may have created new
            // unpacked islands that cascade through other textures of the
            // same mesh.
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var kv in textureIslands)
                {
                    bool anyUnpacked = false;
                    foreach (var mi in kv.Value)
                        if (!mi.Island.IsPacked) { anyUnpacked = true; break; }
                    if (!anyUnpacked) continue;

                    foreach (var mi in kv.Value)
                    {
                        if (mi.Island.IsPacked)
                        {
                            mi.Island.IsPacked = false;
                            mi.Island.Translation = Vector2.zero;
                            changed = true;
                        }
                    }
                }
            }
        }

        // ====================================================================
        // Island detection (TTT-style union-find on shared UV positions)
        // ====================================================================

        private static List<UvIsland> DetectIslands(Mesh mesh)
        {
            var result = new List<UvIsland>();
            if (mesh == null) return result;

            var uvList = new List<Vector2>();
            mesh.GetUVs(0, uvList);
            int vertCount = uvList.Count;
            if (vertCount == 0) return result;

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

            int subCount = mesh.subMeshCount;
            for (int s = 0; s < subCount; s++)
            {
                var tris = mesh.GetTriangles(s);
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    int v0 = tris[t], v1 = tris[t + 1], v2 = tris[t + 2];
                    if (v0 < 0 || v1 < 0 || v2 < 0) continue;
                    if (v0 >= vertCount || v1 >= vertCount || v2 >= vertCount) continue;
                    Union(vertToUnique[v0], vertToUnique[v1]);
                    Union(vertToUnique[v1], vertToUnique[v2]);
                }
            }

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

        private static HashSet<UvIsland> IslandsTouchingSubmesh(Mesh mesh, int submeshIndex,
            Dictionary<int, UvIsland> vertToIsland)
        {
            var result = new HashSet<UvIsland>();
            var tris = mesh.GetTriangles(submeshIndex);
            for (int t = 0; t + 2 < tris.Length; t += 3)
                if (vertToIsland.TryGetValue(tris[t], out var island))
                    result.Add(island);
            return result;
        }

        private static Dictionary<int, UvIsland> BuildVertToIsland(List<UvIsland> islands)
        {
            var map = new Dictionary<int, UvIsland>();
            foreach (var island in islands)
                foreach (var v in island.VertIndices)
                    map[v] = island;
            return map;
        }

        // ====================================================================
        // Mesh UV remap — translate each packed island's vertices.
        // ====================================================================

        private static void RemapAllUvChannels(Mesh mesh, List<UvIsland> islands)
        {
            int vertCount = mesh.vertexCount;
            var perVertIsland = new UvIsland[vertCount];
            foreach (var island in islands)
                foreach (var v in island.VertIndices)
                    if (v >= 0 && v < vertCount) perVertIsland[v] = island;

            // Only translate UV0. UV1/UV2/UV3 usually carry a DIFFERENT
            // layout (lightmap UVs, detail masks, AudioLink UVs, matcap
            // masks) — islands are detected on UV0, so applying UV0's
            // per-island delta to UV1+ vertices would scramble anything
            // sampled via those channels. Matches TTT's AtlasTexture, which
            // also only rewrites UV0 (see AtlasTexture.cs:301).
            {
                int ch = 0;
                var uvs = new List<Vector4>();
                mesh.GetUVs(ch, uvs);
                if (uvs.Count == 0) return;

                int n = Math.Min(uvs.Count, vertCount);
                bool changed = false;
                for (int i = 0; i < n; i++)
                {
                    var island = perVertIsland[i];
                    if (island == null || !island.IsPacked) continue;
                    var uv = uvs[i];
                    uvs[i] = new Vector4(uv.x + island.Translation.x,
                                         uv.y + island.Translation.y,
                                         uv.z, uv.w);
                    changed = true;
                }
                if (changed) mesh.SetUVs(ch, uvs);
            }
        }

        // ====================================================================
        // Texture rearrangement — per-island copy + edge-replicate skirt.
        // ====================================================================

        private static Texture2D BuildRearrangedTexture(BuildContext context,
            Texture2D src, List<MeshIsland> islandPairs)
        {
            if (src == null) return null;
            int w = src.width;
            int h = src.height;
            if (w <= 0 || h <= 0) return null;

            int padPxX = Mathf.Max(MinPaddingPixels, Mathf.RoundToInt(PaddingUv * w));
            int padPxY = Mathf.Max(MinPaddingPixels, Mathf.RoundToInt(PaddingUv * h));

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
                // Identity backing — UVs not in any packed island still
                // sample correctly; inside-island regions get overwritten
                // below, with the surrounding skirt's edge-replicate
                // overwriting any neighbour-island bleed.
                var dstPixels = (Color[])srcPixels.Clone();
                Object.DestroyImmediate(cpuSrc);

                // Apply each packed island in-order. Targets do not overlap
                // (NFDH guarantees that), so paint order is irrelevant.
                foreach (var mi in islandPairs)
                {
                    var island = mi.Island;
                    if (!island.IsPacked) continue;

                    PaintIsland(srcPixels, dstPixels, w, h, island, padPxX, padPxY);
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
        /// Copy the island's source bbox (and a padding skirt around it,
        /// filled by clamping each off-bbox sample back to the bbox edge —
        /// classic edge-replicate dilation) to the island's translated
        /// target position. The skirt absorbs bilinear / mip filter taps
        /// that fall outside the strict content rect, so they pull a
        /// smoothly-extended version of the island's own border colour
        /// instead of a neighbouring island's pixels.
        /// </summary>
        private static void PaintIsland(Color[] src, Color[] dst, int w, int h,
            UvIsland island, int padPxX, int padPxY)
        {
            // Source pixel rect (inclusive of bbox edges via floor/ceil).
            int sx0 = Mathf.Clamp(Mathf.FloorToInt(island.Bbox.xMin * w), 0, w - 1);
            int sy0 = Mathf.Clamp(Mathf.FloorToInt(island.Bbox.yMin * h), 0, h - 1);
            int sx1 = Mathf.Clamp(Mathf.CeilToInt (island.Bbox.xMax * w), sx0 + 1, w);
            int sy1 = Mathf.Clamp(Mathf.CeilToInt (island.Bbox.yMax * h), sy0 + 1, h);
            int sw = sx1 - sx0;
            int sh = sy1 - sy0;
            if (sw <= 0 || sh <= 0) return;

            // Target pixel position (top-left of content). Must equal
            // sx0/sy0 + round(Translation*texSize) so that the per-pixel
            // offset we apply to the painted rect matches the per-pixel
            // offset the GPU applies when sampling with the translated UVs
            // (uv += Translation). Computing round((Bbox.min + Translation) *
            // size) instead would disagree with floor(Bbox.min * size) by up
            // to one pixel whenever Bbox.min*size has a fractional part >=
            // 0.5, producing a 1-pixel shift between the painted island and
            // its sampling UVs — visible as garbled texture content.
            int dx0 = sx0 + Mathf.RoundToInt(island.Translation.x * w);
            int dy0 = sy0 + Mathf.RoundToInt(island.Translation.y * h);

            // Iterate the padded target rect; for each target pixel, sample
            // the source at (sx0 + clamped_dx, sy0 + clamped_dy). Inside
            // the content rect this is a 1-to-1 copy; outside it's the
            // edge-replicate skirt.
            int yStart = -padPxY;
            int yEnd   = sh + padPxY;
            int xStart = -padPxX;
            int xEnd   = sw + padPxX;

            for (int dy = yStart; dy < yEnd; dy++)
            {
                int targetY = dy0 + dy;
                if (targetY < 0 || targetY >= h) continue;

                int srcY = sy0 + Mathf.Clamp(dy, 0, sh - 1);
                int dstRowStart = targetY * w;
                int srcRowStart = srcY * w;

                for (int dx = xStart; dx < xEnd; dx++)
                {
                    int targetX = dx0 + dx;
                    if (targetX < 0 || targetX >= w) continue;

                    int srcX = sx0 + Mathf.Clamp(dx, 0, sw - 1);
                    dst[dstRowStart + targetX] = src[srcRowStart + srcX];
                }
            }
        }

        // ====================================================================
        // Material build (unchanged from v0.2.4)
        // ====================================================================

        private static void EnumerateMaterialTextures(Material mat, HashSet<Texture2D> sink,
            HashSet<Texture2D> nonIdentityStSink)
        {
            if (mat == null || mat.shader == null) return;
            int propCount = mat.shader.GetPropertyCount();
            for (int p = 0; p < propCount; p++)
            {
                if (mat.shader.GetPropertyType(p) != ShaderPropertyType.Texture) continue;
                var propName = mat.shader.GetPropertyName(p);
                var t = mat.GetTexture(propName);
                if (!(t is Texture2D t2d) || IsHdrFormat(t2d.format)) continue;

                // Skip textures whose per-material _ST is non-identity. The
                // GPU samples at sampleUV = UV * Scale + Offset. Our pass
                // shifts mesh UVs by Translation, which shifts the sample
                // position by Translation * Scale — but we paint the
                // rearranged texture shifted by Translation alone. When
                // Scale != 1 or Offset != 0 those two shifts disagree and
                // the mesh samples the WRONG part of the rearranged texture
                // (scrambled content on surface). Leaving such textures
                // unrearranged keeps them identical pixels and identical
                // UVs — correct render, no obfuscation for that texture.
                // Any material referencing the texture with non-identity ST
                // poisons the texture for all materials.
                var scale  = mat.GetTextureScale(propName);
                var offset = mat.GetTextureOffset(propName);
                if (!Mathf.Approximately(scale.x, 1f)  || !Mathf.Approximately(scale.y, 1f) ||
                    !Mathf.Approximately(offset.x, 0f) || !Mathf.Approximately(offset.y, 0f))
                {
                    nonIdentityStSink?.Add(t2d);
                    continue;
                }

                sink.Add(t2d);
            }
        }

        private static Material BuildRemappedMaterial(BuildContext context,
            Material src, Dictionary<Texture2D, Texture2D> rearrangedCache)
        {
            if (src == null || src.shader == null) return null;

            int propCount = src.shader.GetPropertyCount();
            bool anyChange = false;
            for (int p = 0; p < propCount; p++)
            {
                if (src.shader.GetPropertyType(p) != ShaderPropertyType.Texture) continue;
                var t = src.GetTexture(src.shader.GetPropertyName(p));
                if (t is Texture2D t2d && rearrangedCache.ContainsKey(t2d)) { anyChange = true; break; }
            }
            if (!anyChange) return null;

            // Use UnityEngine.Object.Instantiate (TTT's AtlasTexture pattern)
            // rather than `new Material(src)` (AAO's DupliacteAssets pattern).
            // The copy constructor only propagates the shader property table
            // + shader keywords + render queue; serialization-cloning via
            // Instantiate also preserves globalIlluminationFlags,
            // enableInstancing, doubleSidedGI, Material Variant resolved
            // state, and anything else shaders like lilToon / Poiyomi stash
            // outside the main property table. Without this we saw the
            // cloned material render with visibly different shader params
            // despite having the same shader + textures.
            //
            // We still wrap in BeginNoApplyMaterialPropertyDrawers so lilToon
            // / Poiyomi custom-drawer SetShader hooks don't re-run and
            // silently retune keywords / queues during the clone.
            Material copy;
            using (MaterialEditorReflection.BeginNoApplyMaterialPropertyDrawers())
                copy = Object.Instantiate(src);
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
        // Mesh helpers
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
        // Texture format helpers (carried over)
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
        /// share UV positions through the union-find. Carries a UV bounding
        /// rect, the list of contributing vertex indices, and (after NFDH)
        /// the per-island UV translation.
        /// </summary>
        internal sealed class UvIsland
        {
            public readonly int RootId;
            public Rect Bbox;
            public readonly List<int> VertIndices = new List<int>();
            /// <summary>UV-space delta — newUV = oldUV + Translation.</summary>
            public Vector2 Translation;
            /// <summary>True iff NFDH placed this island; UV / texture rewrites are gated on this.</summary>
            public bool IsPacked;

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

        /// <summary>(mesh, island) pair — used to associate textures with the islands sampling them.</summary>
        internal readonly struct MeshIsland
        {
            public readonly Mesh Mesh;
            public readonly UvIsland Island;
            public MeshIsland(Mesh mesh, UvIsland island) { Mesh = mesh; Island = island; }
        }
    }

    /// <summary>
    /// Reflection helper for <c>EditorMaterialUtility.disableApplyMaterialPropertyDrawers</c>.
    /// Mirrors AAO's DupliacteAssets pass — prevents lilToon / Poiyomi custom-drawer
    /// side effects from firing during material cloning (both <c>new Material(src)</c>
    /// and <c>Object.Instantiate(src)</c> paths).
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
