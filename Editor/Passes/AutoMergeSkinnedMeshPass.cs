using FuckRipper.AvatarObfuscator.Internal;
using nadena.dev.ndmf;

namespace FuckRipper.AvatarObfuscator.Passes
{
    /// <summary>
    /// Optional pass: merges <see cref="UnityEngine.SkinnedMeshRenderer"/>s that
    /// satisfy a strict safety profile (see <see cref="SkinnedMeshMerger"/>).
    ///
    /// <para>OFF by default. This is a draw-call optimisation, not an
    /// obfuscation feature. Users who already use Avatar Optimizer's
    /// Trace and Optimize should leave this off and rely on AAO's much
    /// more thorough analysis.</para>
    /// </summary>
    internal sealed class AutoMergeSkinnedMeshPass : Pass<AutoMergeSkinnedMeshPass>
    {
        public override string DisplayName => "Avatar Obfuscator: auto-merge skinned mesh";

        protected override void Execute(BuildContext context)
        {
            var state = context.GetState<ObfuscationContext>();
            if (!state.Enabled) return;
            if (!state.Options.autoMergeSkinnedMesh) return;

            SkinnedMeshMerger.Run(context, state);
        }
    }
}
