using System;
using HateRipper.AvatarObfuscator.Internal;
using HateRipper.AvatarObfuscator.Passes;
using nadena.dev.ndmf;

[assembly: ExportsPlugin(typeof(HateRipper.AvatarObfuscator.ObfuscatorPlugin))]

namespace HateRipper.AvatarObfuscator
{
    /// <summary>
    /// Top-level NDMF plugin entry. Schedules all obfuscation passes in
    /// <see cref="BuildPhase.Optimizing"/>, after Modular Avatar, Avatar
    /// Optimizer and TexTransTool — so we obfuscate the final, post-optimised
    /// avatar that is about to be uploaded.
    ///
    /// <para>The plugin pipeline is intentionally self-contained — no runtime
    /// dependencies on TexTransTool or Avatar Optimizer. The
    /// <see cref="Passes.RemapUVTexturePass"/> and
    /// <see cref="Passes.AutoMergeSkinnedMeshPass"/> use first-party
    /// implementations that live in the <c>Internal</c> namespace, so users
    /// who do not have TTT / AAO installed can still use those features
    /// without conflict, and users who DO have TTT / AAO installed see no
    /// duplicate components or competing pipelines.</para>
    /// </summary>
    internal sealed class ObfuscatorPlugin : Plugin<ObfuscatorPlugin>
    {
        public override string QualifiedName => "dev.cocokoishi.avatar-obfuscator";

        public override string DisplayName => "Cocokoishi Avatar Obfuscator";

        protected override void Configure()
        {
            // Run in the Optimizing phase, *after* TexTransTool, Avatar Optimizer
            // and Modular Avatar — we want the final form of the avatar before
            // we obfuscate. Each AfterPlugin is a no-op when the named plugin
            // isn't installed (NDMF's solver simply has no constraint to add).
            var sequence = InPhase(BuildPhase.Optimizing)
                .AfterPlugin("net.rs64.tex-trans-tool")
                .AfterPlugin("com.anatawa12.avatar-optimizer")
                .AfterPlugin("nadena.dev.modular-avatar");

            sequence
                // 1. Read the AvatarObfuscator component, decide whether to do anything.
                .Run(CollectStatePass.Instance)

                // 2. Optional auto-merge of static skinned meshes. Runs first so the
                //    rest of the pipeline operates on the merged avatar. Default OFF.
                .Then.Run(AutoMergeSkinnedMeshPass.Instance)

                // 3. Per-material UV-flip remap. Replaces the previous material-merge
                //    pass — every material gets a byte-different texture, but no two
                //    materials are merged together.
                .Then.Run(RemapUVTexturePass.Instance)

                // 4. Blendshape rename. Builds (smrPath, oldBSName) → newBSName mapping
                //    that the animation pass will consume.
                .Then.Run(ObfuscateBlendShapesPass.Instance)

                // 5. Parameter rename. Touches every animator referenced by
                //    VRCAvatarDescriptor + Animator components, plus VRC Expression
                //    Parameters / Menu, ParameterDriver, PhysBone, ContactReceiver.
                .Then.Run(ObfuscateParametersPass.Instance)

                // 6. Hierarchy rename. Builds oldPath → newPath mapping for every
                //    GameObject under the avatar root, then rewrites every place
                //    that stores a transform path as a string.
                .Then.Run(ObfuscateHierarchyPass.Instance)

                // 7. Now that the GameObject tree, blendshape names and material
                //    references are all in their final form, rewrite every reachable
                //    AnimationClip in one place.
                .Then.Run(ObfuscateAnimationClipsPass.Instance)

                // 8. Last: rename mesh / controller / clip / mask asset names.
                .Then.Run(FinalizeAssetsPass.Instance);
        }

        protected override void OnUnhandledException(Exception e)
        {
            ErrorReport.ReportException(e);
        }
    }
}
