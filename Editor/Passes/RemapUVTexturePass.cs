using FuckRipper.AvatarObfuscator.Internal;
using nadena.dev.ndmf;

namespace FuckRipper.AvatarObfuscator.Passes
{
    /// <summary>
    /// Per-UV-island texture rearrangement pass — modelled on TexTransTool's
    /// <c>AtlasTexture</c>: every texture is treated as its own one-texture
    /// atlas group, its UV islands are detected via union-find, and each
    /// island gets a deterministic within-bbox transform (FlipH / FlipV /
    /// Rot180). Mesh UVs are rewritten in lockstep with the texture pixel
    /// transform so the visual result is unchanged but every byte of every
    /// texture differs from the source.
    ///
    /// <para>The pass clones materials (swapping rearranged textures) and
    /// meshes (swapping remapped UVs) so the original scene assets are never
    /// mutated. Material reference rewrites are recorded in
    /// <see cref="ObfuscationContext.MaterialReplacements"/>; mesh replacements
    /// in <see cref="ObfuscationContext.MeshReplacements"/>; both are consumed
    /// by <c>ObfuscateAnimationClipsPass</c> to redirect ObjectReference
    /// curves accordingly.</para>
    /// </summary>
    internal sealed class RemapUVTexturePass : Pass<RemapUVTexturePass>
    {
        public override string DisplayName => "Avatar Obfuscator: per-UV-island texture rearrangement";

        protected override void Execute(BuildContext context)
        {
            var state = context.GetState<ObfuscationContext>();
            if (!state.Enabled) return;
            if (!state.Options.remapUvTextures) return;

            UVTextureRemapper.Run(context, state);
        }
    }
}
