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

        [Tooltip("Comma-separated substrings (case-sensitive). Any animator / expression / PhysBone / " +
                 "contact parameter whose name CONTAINS one of these is left untouched, in addition to " +
                 "VRChat built-ins. Useful when an external system (face-tracking bridge, OSC tool, " +
                 "custom shader, ...) reads the parameter name as a string and would break if renamed.\n\n" +
                 "Default: 'OGB/,FT/,VFH/,VF/,v2/,EchoFT/,OSCm/,PBS/,DPS/,SPS/,TPS/,VRCF/,Go/,__ModularAvatarInternal/,AAO_,d4rkAvatarOptimizer/,VRCFury/,_VF/,_vrcf/'. " +
                 "Leave blank to obfuscate everything that isn't a VRChat built-in.")]
        public string skipParametersContaining = "OGB/,FT/,VFH/,VF/,v2/,EchoFT/,OSCm/,PBS/,DPS/,SPS/,TPS/,VRCF/,Go/,__ModularAvatarInternal/,AAO_,d4rkAvatarOptimizer/,VRCFury/,_VF/,_vrcf/";

        [Tooltip("Move every state, sub-state-machine, Entry / Exit / Any State / parent-link node " +
                 "in every state machine to position (0, 0, 0). The whole graph collapses into a " +
                 "single overlapping pile in the Animator window — completely unreadable to a ripper " +
                 "trying to understand the avatar's logic.\n\n" +
                 "Position values are pure editor-only cosmetic data. The runtime state machine " +
                 "treats them as no-ops, so transitions, blend trees, parameter drivers and every " +
                 "other behaviour keep working exactly as before.")]
        public bool flattenStatePositions = false;

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

        [Tooltip("Rename Material asset names. Materials referenced by renderers are cloned (the user's " +
                 "source .mat asset is NEVER modified) and the clone is renamed. Animation clip object-" +
                 "reference curves are redirected to the cloned materials automatically. Safe — Unity " +
                 "does not look up materials by name at runtime; shader keywords and properties are " +
                 "preserved by Object.Instantiate.")]
        public bool obfuscateMaterialAssetNames = true;

        [Tooltip("Rename Texture2D asset names. Textures referenced by materials are cloned (the user's " +
                 "source .png/.jpg/.tga import is NEVER modified) and the clone is renamed. The owning " +
                 "materials are also cloned so the texture reference can be redirected without mutating " +
                 "the user's material asset. Only Texture2D is handled — Cubemap, RenderTexture, " +
                 "Texture3D and Texture2DArray are left untouched.")]
        public bool obfuscateTextureAssetNames = true;

        [Tooltip("Rename AudioClip asset names. AudioClips referenced by AudioSource.clip and " +
                 "VRC_AnimatorPlayAudio.Clips are cloned (the user's source .wav/.ogg/.mp3 import is " +
                 "NEVER modified) and the clone is renamed. Animation clip object-reference curves that " +
                 "animate AudioSource.clip are also redirected.\n\n" +
                 "If you have third-party scripts that look up audio clips by name (very rare), keep " +
                 "this OFF — Unity itself never does such lookups.")]
        public bool obfuscateAudioClipAssetNames = true;

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
        [Tooltip("Optional prefix prepended to every generated obfuscated name. " +
                 "Useful for identifying which avatar produced a name in logs or " +
                 "for namespacing obfuscated parameters when multiple avatars share " +
                 "an animator controller.\n\n" +
                 "Leave blank for no prefix (the default). You can use the Inspector " +
                 "button to auto-fill from the avatar's VRC Pipeline Manager blueprint ID.")]
        public string obfuscationNamePrefix = "";

        [Tooltip("Use a custom set of 4 obfuscation symbols instead of the built-in ÌÍÎÏ alphabet. " +
                 "The default custom symbols (II, ll, Il, lI) look nearly identical in most fonts, " +
                 "making obfuscated names visually indistinguishable from each other.\n\n" +
                 "When OFF, the original Latin-1 homoglyph alphabet (Ì Í Î Ï) is used.")]
        public bool useCustomAlphabet = true;

        [Tooltip("First custom alphabet character.")]
        public string customChar0 = "II";
        [Tooltip("Second custom alphabet character.")]
        public string customChar1 = "ll";
        [Tooltip("Third custom alphabet character.")]
        public string customChar2 = "Il";
        [Tooltip("Fourth custom alphabet character.")]
        public string customChar3 = "lI";

        [Tooltip("A salt mixed into the random-name generator. A non-zero seed produces the SAME " +
                 "obfuscated names every build (useful for diffing, source-control review, and any " +
                 "downstream tool that has cached the rename map). Set to 0 for a fresh random salt " +
                 "every build (less reproducible, but harder for an attacker to predict). " +
                 "Default 5145514 — the author's Bilibili UID.")]
        public int seed = 5145514;

        [Tooltip("Length of generated obfuscated names. The alphabet has 4 symbols so each character " +
                 "is 2 bits of entropy; a length of 24 gives 48 bits / ~280 trillion unique names. " +
                 "Shorter is smaller in the asset; longer is less collision-prone and harder to read at a glance.")]
        [Range(8, 128)]
        public int generatedNameLength = 24;

        /// <summary>
        /// Returns the effective 4-character alphabet array based on current settings.
        /// When <see cref="useCustomAlphabet"/> is true, builds the alphabet from the
        /// 4 custom character fields; otherwise returns null (meaning use the built-in default).
        /// </summary>
        public string[] GetEffectiveAlphabet()
        {
            if (!useCustomAlphabet) return null;
            string s0 = !string.IsNullOrEmpty(customChar0) ? customChar0 : "I";
            string s1 = !string.IsNullOrEmpty(customChar1) ? customChar1 : "l";
            string s2 = !string.IsNullOrEmpty(customChar2) ? customChar2 : "Il";
            string s3 = !string.IsNullOrEmpty(customChar3) ? customChar3 : "lI";
            return new string[] { s0, s1, s2, s3 };
        }
    }
}
