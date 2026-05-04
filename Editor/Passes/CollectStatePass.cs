using FuckRipper.AvatarObfuscator.Internal;
using nadena.dev.ndmf;

namespace FuckRipper.AvatarObfuscator.Passes
{
    /// <summary>
    /// First pass. Looks for an enabled <see cref="AvatarObfuscator"/> on the
    /// avatar root, copies its options into the build-context state, and
    /// initialises the random-name generator.
    ///
    /// Every later pass starts by checking <c>ObfuscationContext.Enabled</c>
    /// and short-circuits if the user is not using us.
    /// </summary>
    internal sealed class CollectStatePass : Pass<CollectStatePass>
    {
        public override string DisplayName => "Avatar Obfuscator: collect state";

        protected override void Execute(BuildContext context)
        {
            var state = context.GetState<ObfuscationContext>();

            var component = context.AvatarRootObject.GetComponent<AvatarObfuscator>();
            if (component == null || component.options == null || !component.options.enabled)
            {
                state.Enabled = false;
                return;
            }

            // Take a deep copy so later mutations don't bleed back into the scene component.
            var src = component.options;
            state.Options = new ObfuscationOptions
            {
                enabled = src.enabled,
                obfuscateParameters = src.obfuscateParameters,
                obfuscateExpressionParameters = src.obfuscateExpressionParameters,
                obfuscateBlendShapes = src.obfuscateBlendShapes,
                preserveMmdBlendShapes = src.preserveMmdBlendShapes,
                obfuscateHierarchy = src.obfuscateHierarchy,
                preserveMmdBodyObject = src.preserveMmdBodyObject,
                obfuscateMeshAssetNames = src.obfuscateMeshAssetNames,
                mergeIdenticalMaterials = src.mergeIdenticalMaterials,
                rewriteAnimationClips = src.rewriteAnimationClips,
                seed = src.seed,
                generatedNameLength = src.generatedNameLength,
            };

            state.Enabled = true;
            state.NameGen = new NameGenerator(state.Options.seed, state.Options.generatedNameLength);

            // Now we no longer need the component on the runtime build — strip it so it
            // doesn't ship with the avatar (it's IEditorOnly anyway, but be explicit).
            UnityEngine.Object.DestroyImmediate(component);
        }
    }
}
