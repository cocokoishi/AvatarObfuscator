using FuckRipper.AvatarObfuscator.Internal;
using nadena.dev.ndmf;

namespace FuckRipper.AvatarObfuscator.Passes
{
    /// <summary>
    /// Per-material UV-flip remap. Replaces the previous material-merge pass.
    ///
    /// <para>For every material on the avatar, picks a deterministic flip mode
    /// and rebuilds the material's textures with that flip applied, while
    /// baking the inverse flip into the material's per-texture
    /// <c>scale / offset</c> (<c>_TextureName_ST</c>). Mesh UVs are NOT
    /// touched — the flip lives entirely on the material — so any number of
    /// UV channels, arbitrary tiling/offset values, normal maps and parallax
    /// effects all stay correct simultaneously.</para>
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
