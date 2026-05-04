using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;
#if FR_OBF_VRCSDK3_AVATARS
using VRC.SDK3.Avatars.Components;
#endif

namespace FuckRipper.AvatarObfuscator.Internal
{
    /// <summary>
    /// Conservative skinned-mesh merger.
    ///
    /// <para>This is intentionally a strict-heuristic implementation. It does
    /// not attempt to replicate AAO's <c>AutoMergeSkinnedMesh</c> level of
    /// dependency analysis — instead it merges only those <see cref="SkinnedMeshRenderer"/>s
    /// that satisfy ALL of the following:</para>
    /// <list type="bullet">
    /// <item>Active in hierarchy with the renderer enabled.</item>
    /// <item>Has a non-null <see cref="SkinnedMeshRenderer.sharedMesh"/> with a non-null root bone.</item>
    /// <item>Mesh has zero blend shapes (so we don't need to keep blendshape names aligned).</item>
    /// <item>Mesh is Read/Write enabled (otherwise the vertex / index access would silently fail).</item>
    /// <item>The owning <see cref="GameObject"/> has no children, no components other
    ///     than <see cref="Transform"/> + <see cref="SkinnedMeshRenderer"/>.</item>
    /// <item>No <see cref="AnimationClip"/> reachable from the avatar references this
    ///     renderer's path or any of its descendants — merging would orphan those bindings.</item>
    /// <item><see cref="SkinnedMeshRenderer.lightProbeUsage"/> / <c>reflectionProbeUsage</c>
    ///     / <c>quality</c> match across the group, so the merged renderer can adopt them.</item>
    /// </list>
    ///
    /// <para>SMRs are grouped by their root bone <see cref="Transform"/>. Each
    /// group of two or more produces one merged SkinnedMeshRenderer parented
    /// under the avatar root.</para>
    ///
    /// <para>This pass is OFF by default in <see cref="ObfuscationOptions"/>;
    /// users with Avatar Optimizer's Trace and Optimize installed should keep
    /// it that way and let AAO do the merge with full dependency tracking.</para>
    /// </summary>
    internal static class SkinnedMeshMerger
    {
        public static void Run(BuildContext context, ObfuscationContext state)
        {
            var avatarRoot = context.AvatarRootTransform;
            if (avatarRoot == null) return;

            // ---------------------------------------------------------------
            // Pre-scan: which paths are referenced in any AnimationClip on the
            // avatar? Renderers at those paths are off-limits for merge.
            // ---------------------------------------------------------------
            var animatedPaths = CollectAnimatedPaths(context);

            var allSmrs = avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (allSmrs.Length < 2) return;

            // ---------------------------------------------------------------
            // 1. Filter to mergeable SMRs.
            // ---------------------------------------------------------------
            var candidates = new List<SkinnedMeshRenderer>(allSmrs.Length);
            foreach (var smr in allSmrs)
            {
                if (!IsMergeCandidate(smr, avatarRoot, animatedPaths)) continue;
                candidates.Add(smr);
            }
            if (candidates.Count < 2) return;

            // ---------------------------------------------------------------
            // 2. Group by (rootBone, lightProbeUsage, reflectionProbeUsage,
            //    quality, shadowCastingMode, receiveShadows) — every renderer
            //    setting that the merged renderer cannot vary per submesh.
            // ---------------------------------------------------------------
            var groups = new Dictionary<MergeKey, List<SkinnedMeshRenderer>>();
            foreach (var smr in candidates)
            {
                var key = new MergeKey(smr);
                if (!groups.TryGetValue(key, out var list))
                    groups[key] = list = new List<SkinnedMeshRenderer>();
                list.Add(smr);
            }

            int mergedGroups = 0;
            int mergedRenderers = 0;
            int counter = 0;
            foreach (var kv in groups)
            {
                var group = kv.Value;
                if (group.Count < 2) continue;

                if (TryMergeGroup(context, state, kv.Key, group, ref counter))
                {
                    mergedGroups++;
                    mergedRenderers += group.Count;
                }
            }

            if (mergedGroups > 0)
            {
                Debug.Log($"[AvatarObfuscator] AutoMergeSkinnedMesh: merged {mergedRenderers} renderers " +
                          $"into {mergedGroups} group(s).");
            }
        }

        // ====================================================================
        // Filtering
        // ====================================================================

        private static bool IsMergeCandidate(SkinnedMeshRenderer smr, Transform avatarRoot,
            HashSet<string> animatedPaths)
        {
            if (smr == null) return false;
            if (!smr.gameObject.activeInHierarchy) return false;
            if (!smr.enabled) return false;
            if (smr.rootBone == null) return false;

            var mesh = smr.sharedMesh;
            if (mesh == null) return false;
            if (mesh.blendShapeCount > 0) return false;
            if (!mesh.isReadable) return false;
            if (mesh.subMeshCount == 0) return false;
            if (mesh.vertexCount == 0) return false;

            // Hard exclusions on the GameObject:
            // - Must be a "leaf" with only Transform + SMR (so we can safely
            //   delete it without orphaning constraints, PhysBones, etc).
            if (smr.transform.childCount > 0) return false;
            var components = smr.gameObject.GetComponents<Component>();
            foreach (var c in components)
            {
                if (c == null) continue;
                if (c is Transform) continue;
                if (c is SkinnedMeshRenderer) continue;
                return false;
            }

            // Path-based animation guard: if anything references this SMR's path
            // or any descendant of it (none, since childCount == 0), skip.
            var path = GetRelativePath(smr.transform, avatarRoot);
            if (path == null) return false;
            if (animatedPaths.Contains(path)) return false;

            return true;
        }

        private static string GetRelativePath(Transform t, Transform root)
        {
            if (t == root) return "";
            var stack = new List<string>();
            var cur = t;
            while (cur != null && cur != root)
            {
                stack.Add(cur.name);
                cur = cur.parent;
            }
            if (cur != root) return null; // not under the root
            stack.Reverse();
            return string.Join("/", stack);
        }

        // ====================================================================
        // Animated-path collection
        // ====================================================================

        /// <summary>
        /// Gathers the set of <see cref="AnimationClip.path"/> strings referenced
        /// by any clip reachable from the avatar's animators. Renderers whose
        /// path matches any of these are excluded from merging because the
        /// merge would orphan the binding.
        /// </summary>
        private static HashSet<string> CollectAnimatedPaths(BuildContext context)
        {
            var result = new HashSet<string>();
            var controllers = new HashSet<AnimatorController>();

#if FR_OBF_VRCSDK3_AVATARS
            var descriptor = context.AvatarRootObject.GetComponent<VRCAvatarDescriptor>();
            if (descriptor != null)
            {
                foreach (var l in descriptor.baseAnimationLayers)
                    if (l.animatorController is AnimatorController ac) controllers.Add(ac);
                foreach (var l in descriptor.specialAnimationLayers)
                    if (l.animatorController is AnimatorController ac) controllers.Add(ac);
            }
#endif
            foreach (var animator in context.AvatarRootObject.GetComponentsInChildren<Animator>(true))
                if (animator.runtimeAnimatorController is AnimatorController ac) controllers.Add(ac);

            foreach (var ac in controllers)
            {
                if (ac == null) continue;
                foreach (var clip in AnimatorWalker.AllClips(ac))
                    AddClipPaths(clip, result);
            }

            return result;
        }

        private static void AddClipPaths(AnimationClip clip, HashSet<string> sink)
        {
            if (clip == null) return;
            var floats = AnimationUtility.GetCurveBindings(clip);
            if (floats != null)
                foreach (var b in floats)
                    if (b.path != null) sink.Add(b.path);

            var objs = AnimationUtility.GetObjectReferenceCurveBindings(clip);
            if (objs != null)
                foreach (var b in objs)
                    if (b.path != null) sink.Add(b.path);
        }

        // ====================================================================
        // Per-group merge
        // ====================================================================

        private static bool TryMergeGroup(BuildContext context, ObfuscationContext state,
            MergeKey key, List<SkinnedMeshRenderer> group, ref int counter)
        {
            // Build the combined mesh. Each source SMR contributes one or more
            // submeshes; we concatenate them, baking the source-pose vertex data
            // into the same bone space as the rootBone.
            var combinedMesh = new Mesh { name = "AutoMerged_" + counter };
            combinedMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            var verts        = new List<Vector3>();
            var norms        = new List<Vector3>();
            var tans         = new List<Vector4>();
            var uvs0         = new List<Vector2>();
            var uvs1         = new List<Vector4>();
            var uvs2         = new List<Vector4>();
            var uvs3         = new List<Vector4>();
            var colors       = new List<Color32>();
            var weights      = new List<BoneWeight>();
            var bindposes    = new List<Matrix4x4>();
            var boneList     = new List<Transform>();
            var boneIndex    = new Dictionary<Transform, int>();
            bool hasNorms    = false;
            bool hasTans     = false;
            bool hasUVs0     = false;
            bool hasUVs1     = false;
            bool hasUVs2     = false;
            bool hasUVs3     = false;
            bool hasColors   = false;
            bool hasWeights  = false;

            var subMeshTriangles = new List<List<int>>();
            var subMeshMaterials = new List<Material>();

            foreach (var src in group)
            {
                var srcMesh = src.sharedMesh;
                var srcMats = src.sharedMaterials;
                int subMeshCount = Mathf.Min(srcMesh.subMeshCount, srcMats.Length);

                int prevTotal = verts.Count;

                var srcVerts   = srcMesh.vertices;
                var srcNorms   = srcMesh.normals;
                var srcTans    = srcMesh.tangents;
                var srcUVs0    = srcMesh.uv;
                var srcUVs1    = ExtractUV4(srcMesh, 1, srcVerts.Length);
                var srcUVs2    = ExtractUV4(srcMesh, 2, srcVerts.Length);
                var srcUVs3    = ExtractUV4(srcMesh, 3, srcVerts.Length);
                var srcColors  = srcMesh.colors32;
                var srcBW      = srcMesh.boneWeights;
                var srcBP      = srcMesh.bindposes;
                var srcBones   = src.bones;

                // Bone remap. The merged mesh stores bones once; per-SMR bone
                // indices need to be rewritten to point into the shared bone
                // table. Bind-poses are kept aligned with the bones array.
                var boneRemap = new int[srcBones != null ? srcBones.Length : 0];
                bool hadNullBone = false;
                for (int b = 0; b < boneRemap.Length; b++)
                {
                    var bone = srcBones[b];
                    if (bone == null)
                    {
                        boneRemap[b] = -1;
                        hadNullBone = true;
                        continue;
                    }
                    if (!boneIndex.TryGetValue(bone, out var bi))
                    {
                        bi = boneList.Count;
                        boneList.Add(bone);
                        boneIndex[bone] = bi;
                        // Copy the source bind-pose alongside.
                        if (srcBP != null && b < srcBP.Length)
                            bindposes.Add(srcBP[b]);
                        else
                            bindposes.Add(Matrix4x4.identity);
                    }
                    boneRemap[b] = bi;
                }
                if (hadNullBone)
                {
                    Debug.LogWarning(
                        $"[AvatarObfuscator] AutoMergeSkinnedMesh: source SMR " +
                        $"'{src.name}' has one or more null entries in its bones[] " +
                        $"array. Vertices weighted to those slots will be silently " +
                        $"rebound to the first bone of the merged mesh and may " +
                        $"deform incorrectly.");
                }

                verts.AddRange(srcVerts);

                // Vertex attributes: backfill defaults for previous sources if
                // we transition false→true (i.e., this is the first source to
                // contribute this attribute), and pad defaults for sources that
                // don't contribute it after we already started keeping it.
                AddOrBackfill(srcNorms, srcVerts.Length, prevTotal, ref hasNorms,
                    norms, Vector3.up);

                AddOrBackfill(srcTans, srcVerts.Length, prevTotal, ref hasTans,
                    tans, new Vector4(1, 0, 0, -1));

                AddOrBackfill(srcUVs0, srcVerts.Length, prevTotal, ref hasUVs0,
                    uvs0, Vector2.zero);

                AddOrBackfill(srcUVs1, srcVerts.Length, prevTotal, ref hasUVs1,
                    uvs1, Vector4.zero);

                AddOrBackfill(srcUVs2, srcVerts.Length, prevTotal, ref hasUVs2,
                    uvs2, Vector4.zero);

                AddOrBackfill(srcUVs3, srcVerts.Length, prevTotal, ref hasUVs3,
                    uvs3, Vector4.zero);

                AddOrBackfill(srcColors, srcVerts.Length, prevTotal, ref hasColors,
                    colors, new Color32(255, 255, 255, 255));

                // Weights: special-case because we have to remap bone indices
                // through boneRemap during the add (the remap target is the
                // shared bone table). Use a transformed array.
                if (srcBW != null && srcBW.Length == srcVerts.Length)
                {
                    if (!hasWeights && prevTotal > 0)
                    {
                        for (int i = 0; i < prevTotal; i++)
                            weights.Add(new BoneWeight { boneIndex0 = 0, weight0 = 1f });
                    }
                    foreach (var bw in srcBW)
                    {
                        weights.Add(new BoneWeight
                        {
                            boneIndex0 = RemapBone(bw.boneIndex0, boneRemap),
                            boneIndex1 = RemapBone(bw.boneIndex1, boneRemap),
                            boneIndex2 = RemapBone(bw.boneIndex2, boneRemap),
                            boneIndex3 = RemapBone(bw.boneIndex3, boneRemap),
                            weight0 = bw.weight0,
                            weight1 = bw.weight1,
                            weight2 = bw.weight2,
                            weight3 = bw.weight3,
                        });
                    }
                    hasWeights = true;
                }
                else if (hasWeights)
                {
                    var defaultBoneIndex = boneRemap.Length > 0 && boneRemap[0] >= 0 ? boneRemap[0] : 0;
                    for (int i = 0; i < srcVerts.Length; i++)
                        weights.Add(new BoneWeight { boneIndex0 = defaultBoneIndex, weight0 = 1f });
                }

                // Per-submesh triangles, indexed in the merged vertex space.
                for (int s = 0; s < subMeshCount; s++)
                {
                    var idx = srcMesh.GetTriangles(s);
                    if (idx == null || idx.Length == 0) continue;
                    var remapped = new int[idx.Length];
                    for (int i = 0; i < idx.Length; i++) remapped[i] = idx[i] + prevTotal;

                    subMeshTriangles.Add(new List<int>(remapped));
                    subMeshMaterials.Add(srcMats[s]);
                }
            }

            if (verts.Count == 0 || subMeshTriangles.Count == 0) return false;

            combinedMesh.SetVertices(verts);
            if (hasNorms)   combinedMesh.SetNormals(norms);
            if (hasTans)    combinedMesh.SetTangents(tans);
            if (hasUVs0)    combinedMesh.SetUVs(0, uvs0);
            if (hasUVs1)    combinedMesh.SetUVs(1, uvs1);
            if (hasUVs2)    combinedMesh.SetUVs(2, uvs2);
            if (hasUVs3)    combinedMesh.SetUVs(3, uvs3);
            if (hasColors)  combinedMesh.SetColors(colors);
            if (hasWeights) combinedMesh.boneWeights = weights.ToArray();
            combinedMesh.bindposes = bindposes.ToArray();

            combinedMesh.subMeshCount = subMeshTriangles.Count;
            for (int s = 0; s < subMeshTriangles.Count; s++)
                combinedMesh.SetTriangles(subMeshTriangles[s], s);

            combinedMesh.RecalculateBounds();
            context.AssetSaver.SaveAsset(combinedMesh);

            // Release the CPU-side mesh data. The merged mesh is referenced from
            // the new SMR's sharedMesh and uploaded to the GPU; no later pass
            // needs to read its vertex data (mesh-name renaming and asset-saver
            // bookkeeping work on the metadata, not the buffers). Saves
            // ~10–30 MB of transient RAM on large merges.
            combinedMesh.UploadMeshData(markNoLongerReadable: true);

            // Build the merged renderer.
            var go = new GameObject("AAObfMerged_" + counter);
            counter++;
            go.transform.SetParent(context.AvatarRootTransform, worldPositionStays: false);
            var newSmr = go.AddComponent<SkinnedMeshRenderer>();

            newSmr.sharedMesh = combinedMesh;
            newSmr.sharedMaterials = subMeshMaterials.ToArray();
            newSmr.rootBone = key.RootBone;
            newSmr.bones = boneList.ToArray();
            newSmr.lightProbeUsage = key.LightProbeUsage;
            newSmr.reflectionProbeUsage = key.ReflectionProbeUsage;
            newSmr.quality = key.Quality;
            newSmr.shadowCastingMode = key.ShadowCastingMode;
            newSmr.receiveShadows = key.ReceiveShadows;
            newSmr.skinnedMotionVectors = key.SkinnedMotionVectors;
            newSmr.updateWhenOffscreen = key.UpdateWhenOffscreen;

            // Pick a sane local bounds — the union of source bounds.
            Bounds? unionBounds = null;
            foreach (var src in group)
            {
                var b = src.localBounds;
                unionBounds = unionBounds.HasValue ? UnionBounds(unionBounds.Value, b) : b;
            }
            if (unionBounds.HasValue) newSmr.localBounds = unionBounds.Value;

            // Now retire the source renderers. We delete the (childless,
            // component-clean) GameObjects entirely — the candidate filter
            // already verified this is safe.
            foreach (var src in group)
            {
                if (src != null && src.gameObject != null)
                    Object.DestroyImmediate(src.gameObject);
            }

            return true;
        }

        /// <summary>
        /// Extracts UVs of the given channel from <paramref name="mesh"/> as a
        /// <see cref="Vector4"/> array. <see cref="Mesh.GetUVs(int, List{Vector4})"/>
        /// handles 2D / 3D / 4D source UVs uniformly (zero-padding the unused
        /// components). Returns null if the channel has no UVs OR the result
        /// length does not match the vertex count (indicating mismatch we should
        /// not propagate).
        /// </summary>
        private static Vector4[] ExtractUV4(Mesh mesh, int channel, int vertexCount)
        {
            if (mesh == null) return null;
            var list = new List<Vector4>();
            mesh.GetUVs(channel, list);
            if (list.Count != vertexCount) return null;
            return list.ToArray();
        }

        /// <summary>
        /// Generic helper that handles "this source contributes attribute X
        /// (length matches), backfill defaults for any previous sources that
        /// did NOT contribute it" together with "this source does not
        /// contribute X but a previous source did, pad defaults so the running
        /// list stays aligned with vertex count".
        /// </summary>
        private static void AddOrBackfill<T>(
            T[] srcArr, int srcVertCount, int prevTotal, ref bool has,
            List<T> sink, T defaultValue)
        {
            if (srcArr != null && srcArr.Length == srcVertCount)
            {
                if (!has && prevTotal > 0)
                {
                    for (int i = 0; i < prevTotal; i++) sink.Add(defaultValue);
                }
                sink.AddRange(srcArr);
                has = true;
            }
            else if (has)
            {
                for (int i = 0; i < srcVertCount; i++) sink.Add(defaultValue);
            }
        }

        private static int RemapBone(int srcIndex, int[] remap)
        {
            if (remap == null || srcIndex < 0 || srcIndex >= remap.Length) return 0;
            var v = remap[srcIndex];
            return v >= 0 ? v : 0;
        }

        private static Bounds UnionBounds(Bounds a, Bounds b)
        {
            var min = Vector3.Min(a.min, b.min);
            var max = Vector3.Max(a.max, b.max);
            var c = (min + max) * 0.5f;
            return new Bounds(c, max - min);
        }

        // ====================================================================
        // Group key
        // ====================================================================

        private readonly struct MergeKey : System.IEquatable<MergeKey>
        {
            public readonly Transform RootBone;
            public readonly UnityEngine.Rendering.LightProbeUsage LightProbeUsage;
            public readonly UnityEngine.Rendering.ReflectionProbeUsage ReflectionProbeUsage;
            public readonly SkinQuality Quality;
            public readonly UnityEngine.Rendering.ShadowCastingMode ShadowCastingMode;
            public readonly bool ReceiveShadows;
            public readonly bool SkinnedMotionVectors;
            public readonly bool UpdateWhenOffscreen;

            public MergeKey(SkinnedMeshRenderer smr)
            {
                RootBone = smr.rootBone;
                LightProbeUsage = smr.lightProbeUsage;
                ReflectionProbeUsage = smr.reflectionProbeUsage;
                Quality = smr.quality;
                ShadowCastingMode = smr.shadowCastingMode;
                ReceiveShadows = smr.receiveShadows;
                SkinnedMotionVectors = smr.skinnedMotionVectors;
                UpdateWhenOffscreen = smr.updateWhenOffscreen;
            }

            public bool Equals(MergeKey o) =>
                RootBone == o.RootBone &&
                LightProbeUsage == o.LightProbeUsage &&
                ReflectionProbeUsage == o.ReflectionProbeUsage &&
                Quality == o.Quality &&
                ShadowCastingMode == o.ShadowCastingMode &&
                ReceiveShadows == o.ReceiveShadows &&
                SkinnedMotionVectors == o.SkinnedMotionVectors &&
                UpdateWhenOffscreen == o.UpdateWhenOffscreen;

            public override bool Equals(object obj) => obj is MergeKey o && Equals(o);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = 17;
                    h = h * 31 + (RootBone != null ? RootBone.GetInstanceID() : 0);
                    h = h * 31 + (int)LightProbeUsage;
                    h = h * 31 + (int)ReflectionProbeUsage;
                    h = h * 31 + (int)Quality;
                    h = h * 31 + (int)ShadowCastingMode;
                    h = h * 31 + (ReceiveShadows ? 1 : 0);
                    h = h * 31 + (SkinnedMotionVectors ? 1 : 0);
                    h = h * 31 + (UpdateWhenOffscreen ? 1 : 0);
                    return h;
                }
            }
        }
    }
}
