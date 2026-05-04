using FuckRipper.AvatarObfuscator.Internal;
using nadena.dev.ndmf;
using UnityEditor.Animations;
using UnityEngine;
#if FR_OBF_VRCSDK3_AVATARS
using VRC.SDK3.Avatars.Components;
#endif

namespace FuckRipper.AvatarObfuscator.Passes
{
    /// <summary>
    /// Final pass: renames the asset names on temporary assets that were cloned by
    /// earlier passes. Asset *files* are not renamed (NDMF stores them under a
    /// generated container path) — only the <see cref="Object.name"/> fields, so
    /// hierarchy-window labels and inspector previews don't leak the originals.
    ///
    /// Mesh asset names are skipped when the user's options ask us to keep them
    /// for MMD compatibility.
    /// </summary>
    internal sealed class FinalizeAssetsPass : Pass<FinalizeAssetsPass>
    {
        public override string DisplayName => "Avatar Obfuscator: finalize asset names";

        protected override void Execute(BuildContext context)
        {
            var state = context.GetState<ObfuscationContext>();
            if (!state.Enabled) return;

            // Mesh assets
            if (state.Options.obfuscateMeshAssetNames)
            {
                var preserveMmdMesh = state.Options.preserveMmdBlendShapes;
                foreach (var smr in context.AvatarRootObject.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    var mesh = smr.sharedMesh;
                    if (mesh == null) continue;
                    if (!context.IsTemporaryAsset(mesh)) continue;
                    if (preserveMmdMesh && IsMmdBody(mesh)) continue;
                    mesh.name = state.NameGen.Next();
                }
                foreach (var mf in context.AvatarRootObject.GetComponentsInChildren<MeshFilter>(true))
                {
                    var mesh = mf.sharedMesh;
                    if (mesh == null) continue;
                    if (!context.IsTemporaryAsset(mesh)) continue;
                    mesh.name = state.NameGen.Next();
                }
            }

            // Animator controllers and their sub-assets
            if (state.Options.obfuscateParameters)
            {
#if FR_OBF_VRCSDK3_AVATARS
                var descriptor = context.AvatarRootObject.GetComponent<VRCAvatarDescriptor>();
                if (descriptor != null)
                {
                    foreach (var layer in descriptor.baseAnimationLayers)
                        if (layer.animatorController is AnimatorController ac && context.IsTemporaryAsset(ac))
                            RenameController(state, ac);
                    foreach (var layer in descriptor.specialAnimationLayers)
                        if (layer.animatorController is AnimatorController ac && context.IsTemporaryAsset(ac))
                            RenameController(state, ac);
                }
#endif
                foreach (var animator in context.AvatarRootObject.GetComponentsInChildren<Animator>(true))
                    if (animator.runtimeAnimatorController is AnimatorController ac && context.IsTemporaryAsset(ac))
                        RenameController(state, ac);
            }
        }

        private static bool IsMmdBody(Mesh mesh)
        {
            if (mesh == null || mesh.blendShapeCount == 0) return false;
            int hits = 0;
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                if (MmdBlendShapeNames.IsMmdBlendShape(mesh.GetBlendShapeName(i)))
                    if (++hits >= 4) return true;
            }
            return false;
        }

        private static void RenameController(ObfuscationContext state, AnimatorController controller)
        {
            controller.name = state.NameGen.Next();

            foreach (var sm in AnimatorWalker.AllStateMachines(controller))
                sm.name = state.NameGen.Next();

            foreach (var s in AnimatorWalker.AllStates(controller))
                s.name = state.NameGen.Next();

            foreach (var bt in AnimatorWalker.AllBlendTrees(controller))
                bt.name = state.NameGen.Next();

            // Rename layers as well
            var layers = controller.layers;
            for (int i = 0; i < layers.Length; i++)
                layers[i].name = state.NameGen.Next();
            controller.layers = layers;
        }
    }
}
