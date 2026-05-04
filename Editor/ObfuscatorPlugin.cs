using System;
using FuckRipper.AvatarObfuscator.Internal;
using FuckRipper.AvatarObfuscator.Passes;
using nadena.dev.ndmf;

[assembly: ExportsPlugin(typeof(FuckRipper.AvatarObfuscator.ObfuscatorPlugin))]

namespace FuckRipper.AvatarObfuscator
{
    /// <summary>
    /// Top-level NDMF plugin entry. Schedules all obfuscation passes in
    /// <see cref="BuildPhase.Optimizing"/>, after Avatar Optimizer.
    /// </summary>
    internal sealed class ObfuscatorPlugin : Plugin<ObfuscatorPlugin>
    {
        public override string QualifiedName => "dev.cocokoishi.avatar-obfuscator";

        public override string DisplayName => "Cocokoishi Avatar Obfuscator";

        protected override void Configure()
        {
            // Run in the Optimizing phase, *after* Avatar Optimizer so we obfuscate
            // the final, optimised avatar. The AfterPlugin call is benign even when
            // AAO is not installed; NDMF's solver simply has no constraint to satisfy.
            var sequence = InPhase(BuildPhase.Optimizing)
                .AfterPlugin("com.anatawa12.avatar-optimizer")
                .AfterPlugin("nadena.dev.modular-avatar");

            sequence
                // 1. Read the AvatarObfuscator component, decide whether to do anything.
                .Run(CollectStatePass.Instance)

                // 2. Material merge first — must precede animation rebinding so that
                //    AnimationClips can be redirected to the surviving material.
                .Then.Run(MergeMaterialsPass.Instance)

                // 3. Blendshape rename. Builds (smrPath, oldBSName) → newBSName mapping
                //    that the animation pass will consume.
                .Then.Run(ObfuscateBlendShapesPass.Instance)

                // 4. Parameter rename. Touches every animator referenced by
                //    VRCAvatarDescriptor + Animator components, plus VRC Expression
                //    Parameters / Menu, ParameterDriver, PhysBone, ContactReceiver.
                .Then.Run(ObfuscateParametersPass.Instance)

                // 5. Hierarchy rename. Builds oldPath → newPath mapping for every
                //    GameObject under the avatar root, then rewrites every place
                //    that stores a transform path as a string.
                .Then.Run(ObfuscateHierarchyPass.Instance)

                // 6. Now that the GameObject tree, blendshape names and material
                //    references are all in their final form, rewrite every reachable
                //    AnimationClip in one place.
                .Then.Run(ObfuscateAnimationClipsPass.Instance)

                // 7. Last: rename mesh/controller/clip asset filenames.
                .Then.Run(FinalizeAssetsPass.Instance);
        }

        protected override void OnUnhandledException(Exception e)
        {
            ErrorReport.ReportException(e);
        }
    }
}
