using FuckRipper.AvatarObfuscator.Internal;
using nadena.dev.ndmf;

namespace FuckRipper.AvatarObfuscator.Passes
{
    /// <summary>
    /// Per-material UV-flip remap. Replaces the previous material-merge pass.
    ///
    /// <para>For every material on the avatar, picks a deterministic flip mode
    /// and rebuilds the material's textures with that flip baked in, while
    /// rewriting mesh UV0 of the renderers that use the material so the visual
    /// result is unchanged. The new textures and materials are byte-different
    /// from the originals, breaking content-addressable asset matching.</para>
    ///
    /// <para>This pass DOES NOT merge materials — every input material
    /// produces exactly one output material.</para>
    ///
    /// <para>Material reference rewrites are recorded in
    /// <see cref="ObfuscationContext.MaterialReplacements"/> so that the
    /// animation-clip pass redirects ObjectReference curves accordingly.</para>
    /// </summary>
    internal sealed class RemapUVTexturePass : Pass<RemapUVTexturePass>
    {
        public override string DisplayName => "Avatar Obfuscator: remap UV textures";

        protected override void Execute(BuildContext context)
        {
            var state = context.GetState<ObfuscationContext>();
            if (!state.Enabled) return;
            if (!state.Options.remapUvTextures) return;

            UVTextureRemapper.Run(context, state);
        }
    }
}
