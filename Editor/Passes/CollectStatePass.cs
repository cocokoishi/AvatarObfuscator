using HateRipper.AvatarObfuscator.Internal;
using nadena.dev.ndmf;

namespace HateRipper.AvatarObfuscator.Passes
{
    /// <summary>
    /// Optimizing-phase gate pass. Config extraction and component destruction
    /// now happen in the Resolving phase (see <see cref="ObfuscatorPlugin"/>).
    /// This pass ensures <c>ObfuscationContext</c> is bound and checks
    /// <c>Enabled</c> so the rest of the pipeline short-circuits cleanly.
    /// </summary>
    internal sealed class CollectStatePass : Pass<CollectStatePass>
    {
        public override string DisplayName => "Avatar Obfuscator: collect state";

        protected override void Execute(BuildContext context)
        {
            // Config was extracted and the component destroyed back in the
            // Resolving phase. Every later pass checks state.Enabled
            // independently, so this pass serves only as a documentation
            // anchor and pipeline gate.
            var state = context.GetState<ObfuscationContext>();
            if (!state.Enabled)
                return;
        }
    }
}
