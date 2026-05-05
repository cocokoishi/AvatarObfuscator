using System;
using UnityEngine;

namespace HateRipper.AvatarObfuscator
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

        [Tooltip("Rename animation clip asset names to homoglyph nonsense, so a ripper extracting your " +
                 "animator gets clip filenames like 'ÌÍÎÏÌÍÎÏ' instead of 'SitDown_Improved_v2'. " +
                 "VRChat proxy animations are kept untouched (they are referenced by name).")]
        public bool obfuscateAnimationClipNames = true;

        [Header("Texture")]
        [Tooltip("For every Texture2D on every material, generate a byte-different copy by " +
                 "rearranging UV islands in lockstep on both the texture pixels and the mesh UVs " +
                 "(same principle as TexTransTool's atlas: even a one-texture atlas group repacks " +
                 "the islands at build time). The output is recompressed back to the source's " +
                 "compressed format (BC7 / DXT5 / ASTC / ETC2 / etc.) so runtime VRAM and bundle " +
                 "size stay the same as the original.\n\n" +
                 "Each island gets a deterministic within-bbox transform (FlipH / FlipV / Rot180), " +
                 "applied to both the island's UVs and the corresponding pixel rect. The visual " +
                 "result is unchanged, but every byte of every texture differs from the source — " +
                 "no ripper reverse-image-search (SHA, perceptual-hash) can match the originals.\n\n" +
                 "Material per-texture scale/offset (_TextureName_ST) values are kept identical to " +
                 "the source. UV channels 0–3 are remapped in lockstep. Cubemaps, 3D textures, " +
                 "render textures and HDR formats are passed through unmodified.")]
        public bool remapUvTextures = false;

        [Header("Mesh Merge (Optional)")]
        [Tooltip("Optional. Merge skinned meshes that share root bone and a strict safety profile " +
                 "(no blendshapes, no animations referencing their path, no special components) into a " +
                 "single skinned mesh. This is a draw-call optimisation, NOT an obfuscation feature; " +
                 "it is OFF by default.\n\n" +
                 "If you also use Avatar Optimizer's Trace and Optimize, leave this OFF and let AAO " +
                 "handle the merge — its dependency analysis is much more thorough than ours.")]
        public bool autoMergeSkinnedMesh = false;

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
