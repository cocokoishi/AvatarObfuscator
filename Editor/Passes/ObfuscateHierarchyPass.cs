using System.Collections.Generic;
using HateRipper.AvatarObfuscator.Internal;
using nadena.dev.ndmf;
using UnityEngine;

namespace HateRipper.AvatarObfuscator.Passes
{
    /// <summary>
    /// Renames every GameObject under the avatar root, except for entries in the
    /// preserve set (the avatar root itself, the MMD-significant <c>Body</c> mesh
    /// when MMD compat is on, and the Humanoid <c>Armature</c> root when present —
    /// keeping <c>Armature</c> avoids breaking unusual setups that rely on that
    /// well-known name).
    ///
    /// The pass produces an <c>oldFullPath -&gt; newFullPath</c> table on the
    /// shared <see cref="ObfuscationContext"/>, which the animation-clip pass and
    /// any later string-path-aware code consume.
    /// </summary>
    internal sealed class ObfuscateHierarchyPass : Pass<ObfuscateHierarchyPass>
    {
        public override string DisplayName => "Avatar Obfuscator: hierarchy";

        protected override void Execute(BuildContext context)
        {
            var state = context.GetState<ObfuscationContext>();
            if (!state.Enabled || !state.Options.obfuscateHierarchy) return;

            var avatarRoot = context.AvatarRootTransform;

            // 1. Determine which transforms to preserve.
            var preservedTransforms = BuildPreserveSet(context, state);

            // 2. Snapshot old paths.
            var oldPaths = PathRemapper.Snapshot(avatarRoot);

            // 3. Rename. We must iterate in order such that we don't change a parent
            //    name before traversing children, because new paths are computed
            //    relative to the new tree afterwards. Easiest: collect all transforms
            //    first, then rename.
            var transforms = new List<Transform>(avatarRoot.GetComponentsInChildren<Transform>(true));
            foreach (var t in transforms)
            {
                if (t == avatarRoot) continue;
                if (preservedTransforms.Contains(t)) continue;
                t.name = state.NameGen.Next();
            }

            // 4. Rebuild the path mapping table.
            var renames = PathRemapper.BuildRenameTable(avatarRoot, oldPaths);
            foreach (var kv in renames)
                state.PathRenames[kv.Key] = kv.Value;
        }

        // ------------------------------------------------------------------
        // Preservation
        // ------------------------------------------------------------------
        private static HashSet<Transform> BuildPreserveSet(BuildContext context, ObfuscationContext state)
        {
            var preserved = new HashSet<Transform>();
            var avatarRoot = context.AvatarRootTransform;
            preserved.Add(avatarRoot);

            // Always keep the well-known Humanoid root if present.
            var armature = FindChild(avatarRoot, "Armature");
            if (armature != null) preserved.Add(armature);

            // ----------------------------------------------------------------
            // CRITICAL: every humanoid bone must keep its name, otherwise the
            // Animator's Avatar asset (which stores transform paths recorded at
            // model-import time) cannot resolve the skeleton. VRChat's avatar
            // validation will reject the upload with errors like
            // "Avatar bone 'Hips' could not be located" if Hips has been
            // renamed.
            //
            // We additionally walk *up* from every humanoid bone to the avatar
            // root, preserving every ancestor on the way — so non-humanoid
            // intermediate transforms (e.g. twist bones, IK helpers) that sit
            // between humanoid bones in the path also keep their names. The
            // Avatar asset stores full transform paths, so dropping any single
            // ancestor's name breaks the lookup.
            // ----------------------------------------------------------------
            var humanoidBones = new List<Transform>();
            foreach (var animator in avatarRoot.GetComponentsInChildren<Animator>(true))
            {
                if (animator.avatar == null || !animator.avatar.isHuman) continue;

                for (var bone = HumanBodyBones.Hips; bone < HumanBodyBones.LastBone; bone++)
                {
                    Transform t;
                    try { t = animator.GetBoneTransform(bone); }
                    catch { continue; } // sub-animators with non-humanoid sub-rigs throw
                    if (t != null) humanoidBones.Add(t);
                }
            }
            foreach (var bone in humanoidBones)
            {
                var cur = bone;
                while (cur != null && cur != avatarRoot)
                {
                    preserved.Add(cur);
                    cur = cur.parent;
                }
            }

            // ----------------------------------------------------------------
            // VRC eye-look bones are referenced by Transform field on the
            // descriptor (so renaming the GameObject would not break the ref),
            // but to keep the inspector readable and to dodge any stricter
            // future validators, we preserve them too.
            // ----------------------------------------------------------------
#if FR_OBF_VRCSDK3_AVATARS
            var descriptor = context.AvatarRootObject.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            if (descriptor != null)
            {
                if (descriptor.lipSyncJawBone != null) preserved.Add(descriptor.lipSyncJawBone);
                var eye = descriptor.customEyeLookSettings;
                if (eye.leftEye  != null) preserved.Add(eye.leftEye);
                if (eye.rightEye != null) preserved.Add(eye.rightEye);
            }
#endif

            if (state.Options.preserveMmdBodyObject)
            {
                Transform mmdBody = FindMmdBodyTransform(context);
                if (mmdBody != null) preserved.Add(mmdBody);
            }

            return preserved;
        }

        private static Transform FindChild(Transform parent, string name)
        {
            foreach (Transform child in parent)
                if (child.name == name) return child;
            return null;
        }

        private static Transform FindMmdBodyTransform(BuildContext context)
        {
            foreach (var smr in context.AvatarRootObject.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = smr.sharedMesh;
                if (mesh == null || mesh.blendShapeCount == 0) continue;

                int mmd = 0;
                for (int i = 0; i < mesh.blendShapeCount; i++)
                {
                    if (MmdBlendShapeNames.IsMmdBlendShape(mesh.GetBlendShapeName(i)))
                        mmd++;
                    if (mmd >= 4) return smr.transform; // 4 hits is enough to call it MMD
                }
            }

            // Fallback: a child literally named "Body" is canonical.
            var avatarRoot = context.AvatarRootTransform;
            foreach (var t in avatarRoot.GetComponentsInChildren<Transform>(true))
                if (t.name == "Body") return t;
            return null;
        }
    }
}
