using System.Collections.Generic;
using HateRipper.AvatarObfuscator.Internal;
using nadena.dev.ndmf;
using UnityEngine;
#if FR_OBF_VRCSDK3_AVATARS
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;
#endif

namespace HateRipper.AvatarObfuscator.Passes
{
    /// <summary>
    /// Clones every <see cref="Mesh"/> attached to a <see cref="SkinnedMeshRenderer"/>
    /// under the avatar root and rewrites blendshape names to obfuscated values.
    /// MMD-significant blendshapes are preserved when
    /// <see cref="ObfuscationOptions.preserveMmdBlendShapes"/> is on.
    ///
    /// Crucial: the SkinnedMeshRenderer uses *names* in animation curves but
    /// *indices* at runtime via <c>GetBlendShapeWeight(index)</c>. Renaming a
    /// blendshape does not require us to remap weights because the index order is
    /// preserved when we rebuild the mesh, and SetBlendShapeWeight on the SMR
    /// reads from the per-renderer weight array (also indexed).
    ///
    /// At the end of the pass we also patch <see cref="VRCAvatarDescriptor"/>
    /// fields that reference blendshapes by NAME — the lipsync viseme list and
    /// the JawFlap mouth-open shape. If we forget this step, VRChat's avatar
    /// validation rejects the upload because the descriptor refers to
    /// blendshape names that no longer exist on the mesh.
    /// </summary>
    internal sealed class ObfuscateBlendShapesPass : Pass<ObfuscateBlendShapesPass>
    {
        public override string DisplayName => "Avatar Obfuscator: blendshapes";

        protected override void Execute(BuildContext context)
        {
            var state = context.GetState<ObfuscationContext>();
            if (!state.Enabled || !state.Options.obfuscateBlendShapes) return;

            var preserveMmd = state.Options.preserveMmdBlendShapes;
            var avatarRoot = context.AvatarRootTransform;

            foreach (var smr in context.AvatarRootObject.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var oldMesh = smr.sharedMesh;
                if (oldMesh == null) continue;
                if (oldMesh.blendShapeCount == 0) continue;

                var path = PathRemapper.GetRelativePath(smr.transform, avatarRoot) ?? smr.name;

                var nameMap = new Dictionary<string, string>();
                var newNames = new string[oldMesh.blendShapeCount];

                for (int i = 0; i < oldMesh.blendShapeCount; i++)
                {
                    var oldName = oldMesh.GetBlendShapeName(i);
                    if (preserveMmd && MmdBlendShapeNames.IsMmdBlendShape(oldName))
                    {
                        newNames[i] = oldName;
                        continue;
                    }
                    var newName = state.NameGen.Next();
                    newNames[i] = newName;
                    nameMap[oldName] = newName;
                }

                if (nameMap.Count == 0) continue; // nothing to do

                var newMesh = CloneMeshWithRenamedBlendShapes(oldMesh, newNames);
                context.AssetSaver.SaveAsset(newMesh);
                ObjectRegistry.RegisterReplacedObject(oldMesh, newMesh);

                // Carry over weights — they are stored per-renderer, indexed by position,
                // so they automatically apply to the renamed shapes.
                smr.sharedMesh = newMesh;

                foreach (var kv in nameMap)
                {
                    state.BlendShapeRenamesByPath[(path, kv.Key)] = kv.Value;
                    state.BlendShapeRenamesByMesh[(newMesh, kv.Key)] = kv.Value;
                }
            }

#if FR_OBF_VRCSDK3_AVATARS
            FixupVRCAvatarDescriptor(context, state);
#endif
        }

#if FR_OBF_VRCSDK3_AVATARS
        // ------------------------------------------------------------------
        // Patch every blendshape-name string the descriptor stores. Without
        // this, VRChat's validator rejects the upload with
        //   "Lipsync viseme '<name>' was not found on the visemes blendshape"
        // because we have just renamed those blendshapes on the mesh.
        // ------------------------------------------------------------------
        private static void FixupVRCAvatarDescriptor(BuildContext context, ObfuscationContext state)
        {
            var descriptor = context.AvatarRootObject.GetComponent<VRCAvatarDescriptor>();
            if (descriptor == null) return;

            var visemeMesh = descriptor.VisemeSkinnedMesh;
            if (visemeMesh == null || visemeMesh.sharedMesh == null) return;

            // Use the post-rename mesh as the lookup key — by this point we have
            // already swapped sharedMesh to the cloned mesh.
            var mesh = visemeMesh.sharedMesh;

            switch (descriptor.lipSync)
            {
                case VRC_AvatarDescriptor.LipSyncStyle.VisemeBlendShape:
                {
                    if (descriptor.VisemeBlendShapes == null) break;
                    var arr = descriptor.VisemeBlendShapes;
                    for (int i = 0; i < arr.Length; i++)
                    {
                        if (state.BlendShapeRenamesByMesh.TryGetValue((mesh, arr[i] ?? ""), out var renamed))
                            arr[i] = renamed;
                    }
                    descriptor.VisemeBlendShapes = arr;
                    break;
                }

                case VRC_AvatarDescriptor.LipSyncStyle.JawFlapBlendShape:
                {
                    if (!string.IsNullOrEmpty(descriptor.MouthOpenBlendShapeName)
                        && state.BlendShapeRenamesByMesh.TryGetValue(
                            (mesh, descriptor.MouthOpenBlendShapeName), out var renamed))
                    {
                        descriptor.MouthOpenBlendShapeName = renamed;
                    }
                    break;
                }
            }
        }
#endif

        private static Mesh CloneMeshWithRenamedBlendShapes(Mesh src, string[] newNames)
        {
            // Mesh.Instantiate copies geometry + bind poses + bone weights, but we need
            // to re-add blendshapes individually because there is no public API to
            // rename one in place.
            var copy = Object.Instantiate(src);
            copy.name = src.name;
            copy.ClearBlendShapes();

            // Reuse vertex/normal/tangent buffers from src — they are independent of
            // blendshape data.
            int vertCount = src.vertexCount;
            var dv = new Vector3[vertCount];
            var dn = new Vector3[vertCount];
            var dt = new Vector3[vertCount];

            for (int i = 0; i < src.blendShapeCount; i++)
            {
                int frames = src.GetBlendShapeFrameCount(i);
                for (int f = 0; f < frames; f++)
                {
                    var weight = src.GetBlendShapeFrameWeight(i, f);
                    src.GetBlendShapeFrameVertices(i, f, dv, dn, dt);
                    copy.AddBlendShapeFrame(newNames[i], weight, dv, dn, dt);
                }
            }
            return copy;
        }
    }
}
