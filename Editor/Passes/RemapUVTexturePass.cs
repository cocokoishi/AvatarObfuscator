using FuckRipper.AvatarObfuscator.Internal;
using nadena.dev.ndmf;

namespace FuckRipper.AvatarObfuscator.Passes
{
    /// <summary>
    /// Per-texture bit-jitter material obfuscation. Replaces the previous
    /// material-merge / UV-flip pass.
    ///
    /// <para>For every <see cref="UnityEngine.Texture2D"/> referenced by every
    /// material on the avatar, generates a byte-different copy that is visually
    /// identical to the source (a single sub-pixel LSB perturbation). The output
    /// is recompressed to the source's compressed format (BC7 / DXT5 / ASTC /
    /// ETC2 / etc.) so runtime VRAM matches the source. A texture shared by N
    /// materials produces exactly 1 obfuscated copy in VRAM.</para>
    ///
    /// <para>Mesh UVs and material per-texture scale/offset are NOT touched —
    /// only the pixel bytes of the textures change. Any number of UV channels,
    /// arbitrary tiling/offset, normal maps and parallax effects remain
    /// correct.</para>
    ///
    /// <para>This pass DOES NOT merge materials — every input material produces
    /// exactly one output material. Material reference rewrites are recorded in
    /// <see cref="ObfuscationContext.MaterialReplacements"/> so that the
    /// animation-clip pass redirects ObjectReference curves accordingly.</para>
    /// </summary>
    internal sealed class RemapUVTexturePass : Pass<RemapUVTexturePass>
    {
        public override string DisplayName => "Avatar Obfuscator: bit-jitter material textures";

        protected override void Execute(BuildContext context)
        {
            var state = context.GetState<ObfuscationContext>();
            if (!state.Enabled) return;
            if (!state.Options.remapUvTextures) return;

            UVTextureRemapper.Run(context, state);
        }
    }
}
