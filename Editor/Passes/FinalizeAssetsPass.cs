using System;
using System.Collections.Generic;
using FuckRipper.AvatarObfuscator.Internal;
using nadena.dev.ndmf;
using UnityEditor;
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
    /// generated container path) — only the <see cref="UnityEngine.Object.name"/>
    /// fields, so hierarchy-window labels and inspector previews don't leak the
    /// originals.
    ///
    /// <para>Mesh asset names are skipped when the user's options ask us to keep
    /// them for MMD compatibility.</para>
    /// <para>Animation clip names are renamed when
    /// <see cref="ObfuscationOptions.obfuscateAnimationClipNames"/> is set, with
    /// VRChat proxy clips left untouched (they are referenced by name).</para>
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

            // Collect every controller reachable from the avatar — animator
            // controllers, plus the clips they reference. We use this for both
            // the controller-rename step (when parameter obfuscation is on)
            // and the clip-rename step (when clip-name obfuscation is on).
            var controllers = new HashSet<AnimatorController>();
#if FR_OBF_VRCSDK3_AVATARS
            var descriptor = context.AvatarRootObject.GetComponent<VRCAvatarDescriptor>();
            if (descriptor != null)
            {
                foreach (var layer in descriptor.baseAnimationLayers)
                    if (layer.animatorController is AnimatorController ac) controllers.Add(ac);
                foreach (var layer in descriptor.specialAnimationLayers)
                    if (layer.animatorController is AnimatorController ac) controllers.Add(ac);
            }
#endif
            foreach (var animator in context.AvatarRootObject.GetComponentsInChildren<Animator>(true))
                if (animator.runtimeAnimatorController is AnimatorController ac) controllers.Add(ac);

            // Animator controllers and their sub-assets
            if (state.Options.obfuscateParameters)
            {
                foreach (var ac in controllers)
                    if (context.IsTemporaryAsset(ac))
                        RenameController(state, ac);
            }

            // Animation clips
            if (state.Options.obfuscateAnimationClipNames)
            {
                var seen = new HashSet<AnimationClip>();
                foreach (var ac in controllers)
                {
                    if (ac == null) continue;
                    foreach (var clip in AnimatorWalker.AllClips(ac))
                    {
                        if (clip == null) continue;
                        if (!seen.Add(clip)) continue;
                        if (!context.IsTemporaryAsset(clip)) continue;
#if FR_OBF_VRCSDK3_AVATARS
                        if (IsProxyClip(clip)) continue;
#endif
                        clip.name = state.NameGen.Next();
                    }
                }

                // Also rename AvatarMask assets that are temporary — they live
                // alongside controllers and a ripper pulling the controller out
                // would otherwise see the mask's original name.
                foreach (var ac in controllers)
                {
                    if (ac == null) continue;
                    foreach (var layer in ac.layers)
                    {
                        var mask = layer.avatarMask;
                        if (mask == null) continue;
                        if (!context.IsTemporaryAsset(mask)) continue;
                        mask.name = state.NameGen.Next();
                    }
                }
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

#if FR_OBF_VRCSDK3_AVATARS
        // Same proxy detection as ObfuscateAnimationClipsPass — keep them in
        // sync. VRChat resolves proxy clips by name, so renaming them would
        // break locomotion / hand poses.
        private static bool IsProxyClip(AnimationClip clip)
        {
            if (clip == null) return false;
            var name = clip.name ?? "";
            if (name.StartsWith("proxy_", StringComparison.Ordinal)) return true;
            var path = AssetDatabase.GetAssetPath(clip);
            if (!string.IsNullOrEmpty(path) && path.Contains("/AV3 Demo Assets/"))
                return true;
            if (!string.IsNullOrEmpty(path) && path.Contains("VRCSDK"))
                return true;
            return false;
        }
#endif
    }
}
