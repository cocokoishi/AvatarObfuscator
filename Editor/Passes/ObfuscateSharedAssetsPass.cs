using System.Collections.Generic;
using HateRipper.AvatarObfuscator.Internal;
using nadena.dev.ndmf;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
#if FR_OBF_VRCSDK3_AVATARS
using VRC.SDK3.Avatars.Components;
#endif

namespace HateRipper.AvatarObfuscator.Passes
{
    /// <summary>
    /// Clones every Material / Texture2D / AudioClip referenced by the avatar so the
    /// later <see cref="FinalizeAssetsPass"/> can rename the clones without ever
    /// touching the user's source .mat / .png / .wav assets on disk.
    ///
    /// <para>The pass is intentionally <b>conservative</b>:</para>
    /// <list type="bullet">
    /// <item>Each asset is cloned <i>at most once</i>; multiple references collapse to
    /// the same clone via the per-category replacement dictionaries on
    /// <see cref="ObfuscationContext"/>.</item>
    /// <item>Assets that are already temporary (e.g. cloned earlier by
    /// <c>RemapUVTexturePass</c> or AAO's DuplicateAssets) are reused as-is — we
    /// never clone a clone.</item>
    /// <item>The pass runs <i>before</i> <see cref="ObfuscateAnimationClipsPass"/>, so
    /// animation-clip object-reference curves can be redirected through the
    /// freshly populated replacement maps.</item>
    /// <item>Texture renaming implicitly forces material cloning (we need a temporary
    /// material to swap the texture reference into without mutating the user's
    /// .mat). The cloned material is renamed only when material obfuscation is
    /// <i>also</i> on; otherwise it keeps its original name.</item>
    /// </list>
    /// </summary>
    internal sealed class ObfuscateSharedAssetsPass : Pass<ObfuscateSharedAssetsPass>
    {
        public override string DisplayName => "Avatar Obfuscator: clone shared assets";

        protected override void Execute(BuildContext context)
        {
            var state = context.GetState<ObfuscationContext>();
            if (!state.Enabled) return;

            bool wantMaterials = state.Options.obfuscateMaterialAssetNames;
            bool wantTextures  = state.Options.obfuscateTextureAssetNames;
            bool wantAudio     = state.Options.obfuscateAudioClipAssetNames;

            if (!wantMaterials && !wantTextures && !wantAudio) return;

            // ----------------------------------------------------------------
            // 1. Material cloning. Required whenever material OR texture
            //    obfuscation is on (texture cloning needs a temporary material
            //    to receive the swapped texture reference).
            // ----------------------------------------------------------------
            bool needMaterialClone = wantMaterials || wantTextures;
            if (needMaterialClone)
                CloneMaterials(context, state);

            // ----------------------------------------------------------------
            // 2. Texture cloning. Walks the (now possibly-cloned) materials
            //    referenced by renderers and clones each non-temporary Texture2D.
            // ----------------------------------------------------------------
            if (wantTextures)
                CloneTextures(context, state);

            // ----------------------------------------------------------------
            // 3. AudioClip cloning. Walks AudioSource.clip + every
            //    VRC_AnimatorPlayAudio.Clips array reachable from the avatar's
            //    animators, and clones each non-temporary clip.
            // ----------------------------------------------------------------
            if (wantAudio)
                CloneAudioClips(context, state);
        }

        // ====================================================================
        // Materials
        // ====================================================================

        private static void CloneMaterials(BuildContext context, ObfuscationContext state)
        {
            foreach (var r in context.AvatarRootObject.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                var mats = r.sharedMaterials;
                if (mats == null) continue;

                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null) continue;

                    // Already replaced (e.g. by RemapUVTexturePass) — just rewire.
                    if (state.MaterialReplacements.TryGetValue(m, out var existing))
                    {
                        if (!ReferenceEquals(mats[i], existing))
                        {
                            mats[i] = existing;
                            changed = true;
                        }
                        continue;
                    }

                    // Already a temporary asset (cloned by some earlier pass that did not
                    // register it in MaterialReplacements). FinalizeAssetsPass will find
                    // it via GetComponentsInChildren<Renderer>(...).sharedMaterials and
                    // rename it from there, so we don't need to track it here. Adding an
                    // identity mapping would make ObjectCurveNeedsRewrite spuriously
                    // return true for animation clips that reference this material,
                    // causing unnecessary clip clones.
                    if (context.IsTemporaryAsset(m)) continue;

                    var copy = CloneMaterial(context, m);
                    state.MaterialReplacements[m] = copy;
                    ObjectRegistry.RegisterReplacedObject(m, copy);
                    mats[i] = copy;
                    changed = true;
                }
                if (changed) r.sharedMaterials = mats;
            }
        }

        private static Material CloneMaterial(BuildContext context, Material src)
        {
            // Same pattern as UVTextureRemapper.BuildRemappedMaterial: use
            // Object.Instantiate (not `new Material(src)`) so shader-specific
            // serialized state (lilToon / Poiyomi GI flags, instancing, Material
            // Variant resolution, ...) is preserved. Suppress property-drawer
            // SetShader callbacks during the clone so keywords aren't silently
            // retuned by lilToon / Poiyomi inspectors.
            Material copy;
            using (MaterialEditorReflection.BeginNoApplyMaterialPropertyDrawers())
                copy = Object.Instantiate(src);
#if UNITY_2022_1_OR_NEWER
            copy.parent = null;
#endif
            copy.name = src.name; // FinalizeAssetsPass renames later when material flag is on
            context.AssetSaver.SaveAsset(copy);
            return copy;
        }

        // ====================================================================
        // Textures
        // ====================================================================

        private static void CloneTextures(BuildContext context, ObfuscationContext state)
        {
            // After CloneMaterials, every renderer's sharedMaterials array points
            // at the canonical (cloned-when-needed) materials. Walk them and clone
            // every Texture2D referenced by any of their shader texture properties.
            var materialsToScan = new HashSet<Material>();
            foreach (var r in context.AvatarRootObject.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || r.sharedMaterials == null) continue;
                foreach (var m in r.sharedMaterials)
                    if (m != null) materialsToScan.Add(m);
            }

            foreach (var mat in materialsToScan)
            {
                if (mat == null || mat.shader == null) continue;
                if (!context.IsTemporaryAsset(mat)) continue; // safety: never mutate non-temp materials

                int propCount = mat.shader.GetPropertyCount();
                for (int p = 0; p < propCount; p++)
                {
                    if (mat.shader.GetPropertyType(p) != ShaderPropertyType.Texture) continue;
                    var propName = mat.shader.GetPropertyName(p);
                    var tex = mat.GetTexture(propName);
                    if (!(tex is Texture2D t2d)) continue;

                    // Already replaced.
                    if (state.TextureReplacements.TryGetValue(t2d, out var existingRep))
                    {
                        if (existingRep != null && !ReferenceEquals(existingRep, t2d))
                            mat.SetTexture(propName, existingRep);
                        continue;
                    }

                    // Already a temporary asset — FinalizeAssetsPass discovers it
                    // by walking renderers → materials → texture properties, so we
                    // do not need to track it here. Avoiding the identity mapping
                    // keeps ObjectCurveNeedsRewrite from spuriously triggering on
                    // animation clips whose texture refs already point at temps.
                    if (context.IsTemporaryAsset(t2d)) continue;

                    var copy = CloneTexture2D(context, t2d);
                    if (copy == null) continue;
                    state.TextureReplacements[t2d] = copy;
                    ObjectRegistry.RegisterReplacedObject(t2d, copy);
                    mat.SetTexture(propName, copy);
                }
            }
        }

        private static Texture2D CloneTexture2D(BuildContext context, Texture2D src)
        {
            if (src == null) return null;
            // Object.Instantiate on a Texture2D produces a deep copy of the pixel
            // data preserving format / mip chain / sRGB / wrap+filter modes. This
            // is what we want: same bytes in the bundle, just a different asset
            // name. The clone shares no GPU resource with the original after the
            // copy completes.
            Texture2D copy;
            try { copy = Object.Instantiate(src); }
            catch (System.Exception e)
            {
                Debug.LogWarning(
                    $"[AvatarObfuscator] Failed to clone Texture2D '{src.name}' ({src.format}, {src.width}x{src.height}): " +
                    $"{e.GetType().Name}: {e.Message}. Texture will keep its original name.");
                return null;
            }
            if (copy == null) return null;
            copy.name = src.name; // FinalizeAssetsPass renames
            context.AssetSaver.SaveAsset(copy);
            return copy;
        }

        // ====================================================================
        // AudioClips
        // ====================================================================

        private static void CloneAudioClips(BuildContext context, ObfuscationContext state)
        {
            // AudioSource.clip
            foreach (var src in context.AvatarRootObject.GetComponentsInChildren<AudioSource>(true))
            {
                if (src == null) continue;
                var clip = src.clip;
                if (clip == null) continue;
                var rep = EnsureClonedAudio(context, state, clip);
                if (rep != null && !ReferenceEquals(rep, clip)) src.clip = rep;
            }

#if FR_OBF_VRCSDK3_AVATARS
            // VRC_AnimatorPlayAudio.Clips. A VRC_AnimatorPlayAudio behaviour is a
            // sub-asset of its owning AnimatorController, so mutating its Clips
            // array writes back to the controller asset. We MUST only do this on
            // controllers we have already cloned — otherwise the user's source
            // .controller on disk is silently modified. The check is paired with
            // the same guard inside FinalizeAssetsPass's audio-rename loop.
            foreach (var ac in CollectControllers(context))
            {
                if (ac == null || !context.IsTemporaryAsset(ac)) continue;
                foreach (var beh in AnimatorWalker.AllBehaviours(ac))
                {
                    if (!(beh is VRC.SDKBase.VRC_AnimatorPlayAudio play)) continue;
                    var clips = play.Clips;
                    if (clips == null || clips.Length == 0) continue;
                    bool changed = false;
                    for (int i = 0; i < clips.Length; i++)
                    {
                        if (clips[i] == null) continue;
                        var rep = EnsureClonedAudio(context, state, clips[i]);
                        if (rep != null && !ReferenceEquals(rep, clips[i]))
                        {
                            clips[i] = rep;
                            changed = true;
                        }
                    }
                    if (changed) play.Clips = clips;
                }
            }
#endif
        }

        private static AudioClip EnsureClonedAudio(BuildContext context, ObfuscationContext state, AudioClip src)
        {
            if (src == null) return null;
            if (state.AudioReplacements.TryGetValue(src, out var existing)) return existing;
            // Already temp — FinalizeAssetsPass renames it directly via the
            // AudioSource / VRC_AnimatorPlayAudio reference walk. Do NOT add an
            // identity mapping here: that would make ObjectCurveNeedsRewrite
            // trigger on every animation clip referencing this clip, forcing a
            // wasteful clone with no actual rewrite.
            if (context.IsTemporaryAsset(src)) return src;

            var copy = CloneAudioClip(src);
            if (copy == null)
            {
                // GetData failed (streaming clip, decoder error, ...): leave the
                // reference pointing at the source so the avatar still plays the
                // right audio. FinalizeAssetsPass will skip it (non-temp). We do
                // NOT record this in AudioReplacements so callers re-attempt on
                // the next reference — but CloneAudioClip's failure modes are
                // deterministic per clip, so the cost is bounded.
                return src;
            }
            context.AssetSaver.SaveAsset(copy);
            state.AudioReplacements[src] = copy;
            ObjectRegistry.RegisterReplacedObject(src, copy);
            return copy;
        }

        private static AudioClip CloneAudioClip(AudioClip src)
        {
            // Object.Instantiate copies every serialized field on the AudioClip,
            // including the m_AudioData byte[] that holds the (already-encoded)
            // Vorbis / ADPCM / PCM payload. We do NOT need to decode the audio
            // ourselves — that was the v0.3.11-pre1 path via AudioClip.Create +
            // GetData + SetData, and it silently returned false on every clip
            // imported with the default "Compressed In Memory" load type because
            // GetData refuses to decode a compressed-in-memory buffer without a
            // streamed reader. Instantiate sidesteps that entirely: same bytes,
            // same format, same import settings, just a new asset name slot.
            //
            // The pattern matches what AAO / Modular Avatar / TexTransTool use
            // for every other Object subclass without a parameterless ctor (see
            // AAO's DeepCloneHelper.DefaultDeepCloneImpl, which falls through to
            // Object.Instantiate for any type lacking a public empty ctor — and
            // AudioClip's only constructor is the static AudioClip.Create).
            if (src == null) return null;
            try
            {
                var copy = Object.Instantiate(src);
                if (copy == null)
                {
                    Debug.LogWarning(
                        $"[AvatarObfuscator] Object.Instantiate returned null for AudioClip '{src.name}'. " +
                        "AudioClip will keep its original name.");
                    return null;
                }
                // Streaming clips keep their audio data outside m_AudioData (in
                // the on-disk file referenced via m_TheStream). Instantiate
                // cannot recreate that file backing, so the clone has zero
                // samples and would play silence. Detect that case explicitly
                // and bail — the original asset keeps its name, the avatar still
                // plays the correct audio.
                if (src.samples > 0 && copy.samples == 0)
                {
                    Debug.LogWarning(
                        $"[AvatarObfuscator] AudioClip '{src.name}' uses Streaming load type — Object.Instantiate " +
                        "produced an empty clone. Switch the import setting to 'Compressed In Memory' or " +
                        "'Decompress On Load' if you want this clip's name obfuscated. Keeping the original name.");
                    Object.DestroyImmediate(copy);
                    return null;
                }
                copy.name = src.name; // FinalizeAssetsPass renames later when audio flag is on
                return copy;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning(
                    $"[AvatarObfuscator] Failed to clone AudioClip '{src.name}': " +
                    $"{e.GetType().Name}: {e.Message}. AudioClip will keep its original name.");
                return null;
            }
        }

        // ====================================================================
        // Controller collection (same shape as FinalizeAssetsPass — minimal,
        // descriptor + Animator-component union, no AOC flattening because
        // ObfuscateParametersPass has already turned every reachable AOC into
        // a temporary AnimatorController by the time we run.)
        // ====================================================================
        private static HashSet<AnimatorController> CollectControllers(BuildContext context)
        {
            var set = new HashSet<AnimatorController>();
#if FR_OBF_VRCSDK3_AVATARS
            var descriptor = context.AvatarRootObject.GetComponent<VRCAvatarDescriptor>();
            if (descriptor != null)
            {
                foreach (var layer in descriptor.baseAnimationLayers)
                    if (layer.animatorController is AnimatorController ac) set.Add(ac);
                foreach (var layer in descriptor.specialAnimationLayers)
                    if (layer.animatorController is AnimatorController ac) set.Add(ac);
            }
#endif
            foreach (var animator in context.AvatarRootObject.GetComponentsInChildren<Animator>(true))
                if (animator.runtimeAnimatorController is AnimatorController ac) set.Add(ac);
            return set;
        }
    }
}
