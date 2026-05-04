using System;
using UnityEngine;

namespace FuckRipper.AvatarObfuscator
{
    /// <summary>
    /// Per-category toggles for what to obfuscate. Lives on <see cref="AvatarObfuscator"/>.
    /// All flags are independent; turning a flag off keeps the corresponding names readable.
    /// </summary>
    [Serializable]
    public sealed class ObfuscationOptions
    {
        [Tooltip("Master switch. When false, the plugin behaves as if no component were present.")]
        public bool enabled = true;

        [Header("Parameters")]
        [Tooltip("Rename Animator parameters across every playable layer, plus rewriting transitions, " +
                 "blend trees, VRCAvatarParameterDriver and PhysBone/Contact parameter references. " +
                 "VRChat built-in parameters (IsLocal, Viseme, Gesture*, ...) are kept untouched.")]
        public bool obfuscateParameters = true;

        [Tooltip("Rename the parameter entries inside the VRC Expression Parameters list to match " +
                 "the renamed animator parameters. The user-visible labels in the Expression Menu " +
                 "are NOT touched, only the parameter names they reference.")]
        public bool obfuscateExpressionParameters = true;

        [Header("Mesh / Blendshape")]
        [Tooltip("Rename blendshape keys on every Skinned Mesh, while updating SetBlendShapeWeight " +
                 "references and animation curves accordingly.")]
        public bool obfuscateBlendShapes = true;

        [Tooltip("Keep MMD-recognised blendshape names (Japanese / EN aliases) untouched so the " +
                 "avatar still works in MMD worlds. Recommended ON.")]
        public bool preserveMmdBlendShapes = true;

        [Header("Hierarchy")]
        [Tooltip("Rename every GameObject under the avatar root (excluding the root itself), and " +
                 "rewrite every animation clip path / Avatar Mask path / VRC SourcePath that " +
                 "references those names. Constraint sources, PhysBone roots, etc. are also rebound.")]
        public bool obfuscateHierarchy = true;

        [Tooltip("Keep the GameObject named 'Body' on the SkinnedMeshRenderer that carries MMD blendshapes, " +
                 "so MMD worlds can still find it. Recommended ON when 'preserve MMD blendshapes' is on.")]
        public bool preserveMmdBodyObject = true;

        [Header("Assets")]
        [Tooltip("Rename mesh asset files (the .asset name, not the GameObject). MMD worlds usually " +
                 "look up the GameObject named 'Body', not the mesh asset, so this is generally safe — " +
                 "but turning it off provides an extra safety net.")]
        public bool obfuscateMeshAssetNames = true;

        [Tooltip("Detect and merge materials whose serialized properties are identical. This reduces " +
                 "draw calls and material slot count without changing the visual result. " +
                 "Animation Clip material references are rewritten to point to the merged material.")]
        public bool mergeIdenticalMaterials = true;

        [Header("Animation Clips")]
        [Tooltip("Rewrite every reachable AnimationClip so that its bindings (path, property name) " +
                 "match the renamed hierarchy / blendshapes / parameters. " +
                 "This is REQUIRED whenever any of the rename options above are on; turning it off " +
                 "will break animations.")]
        public bool rewriteAnimationClips = true;

        [Header("Advanced")]
        [Tooltip("A salt mixed into the random-name generator. Two avatars with the same salt produce " +
                 "the same renaming, which is occasionally useful for diffing. Leave at 0 for a " +
                 "fresh random salt every build.")]
        public int seed = 0;

        [Tooltip("Length of generated obfuscated names. The alphabet has 4 symbols so each character " +
                 "is 2 bits of entropy; a length of 24 gives 48 bits / ~280 trillion unique names. " +
                 "Shorter is smaller in the asset; longer is less collision-prone and harder to read at a glance.")]
        [Range(8, 128)]
        public int generatedNameLength = 24;
    }
}
