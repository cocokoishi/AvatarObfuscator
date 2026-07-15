using System;
using System.Collections.Generic;
using HateRipper.AvatarObfuscator.Internal;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;
#if FR_OBF_VRCSDK3_AVATARS
using VRC.SDK3.Avatars.Components;
#endif

namespace HateRipper.AvatarObfuscator.Passes
{
    /// <summary>
    /// Walks every <see cref="AnimationClip"/> reachable from the avatar's animators
    /// and rewrites its bindings:
    /// <list type="bullet">
    /// <item><c>path</c> → mapped through <see cref="ObfuscationContext.PathRenames"/></item>
    /// <item><c>blendShape.&lt;name&gt;</c> properties → mapped through <see cref="ObfuscationContext.BlendShapeRenamesByPath"/></item>
    /// <item>Object reference curves → mapped through <see cref="ObfuscationContext.MaterialReplacements"/> and <see cref="ObfuscationContext.MeshReplacements"/></item>
    /// </list>
    /// Every clip on disk is cloned into the asset container before mutation so the
    /// user's original assets remain untouched.
    /// </summary>
    internal sealed class ObfuscateAnimationClipsPass : Pass<ObfuscateAnimationClipsPass>
    {
        public override string DisplayName => "Avatar Obfuscator: animation clips";

        private const string BlendShapePropertyPrefix = "blendShape.";

        protected override void Execute(BuildContext context)
        {
            var state = context.GetState<ObfuscationContext>();
            if (!state.Enabled || !state.Options.rewriteAnimationClips) return;
            // Even if no rename happened, we still loop through but turn into no-ops; cheap.

            // Defer AssetDatabase imports until the entire pass has finished. In
            // particular, rewritten clips remain cheap in-memory objects while their
            // curves are updated and all generated assets are imported in one batch.
            using var serializationScope = context.OpenSerializationScope();

            // Collect every animator we touched in earlier passes, plus any avatar masks.
            var controllers = new HashSet<AnimatorController>();
            var clipMap = new Dictionary<AnimationClip, AnimationClip>();

#if FR_OBF_VRCSDK3_AVATARS
            var descriptor = context.AvatarRootObject.GetComponent<VRCAvatarDescriptor>();
            if (descriptor != null)
            {
                foreach (var layer in descriptor.baseAnimationLayers)
                    if (layer.animatorController is AnimatorController ac) controllers.Add(ac);
                foreach (var layer in descriptor.specialAnimationLayers)
                    if (layer.animatorController is AnimatorController ac) controllers.Add(ac);
            }
#endif
            foreach (var animator in context.AvatarRootObject.GetComponentsInChildren<Animator>(true))
                if (animator.runtimeAnimatorController is AnimatorController ac) controllers.Add(ac);

            // Rewrite each clip
            foreach (var rawAc in controllers)
            {
                var ac = rawAc;
                if (ac == null) continue;

                // Safety: every mutation below (state.motion, layer.SetOverrideMotion,
                // ac.layers, RewriteBehaviourPaths) writes back to the controller
                // asset or its sub-assets. We MUST only do this on controllers we
                // have cloned — otherwise the user's source .controller on disk is
                // silently modified.
                //
                // When obfuscateParameters is on, ParametersPass already cloned
                // every reachable controller, so this branch is a no-op. When
                // parameters are NOT being obfuscated but hierarchy/blendshape
                // renames ARE, the controller won't be temporary yet — we clone
                // it here so state.motion can be safely updated.
                if (!context.IsTemporaryAsset(ac))
                {
                    // Object.Instantiate produces a fully standalone clone that
                    // NDMF's ObjectRegistry resolves at build time, so we do not
                    // need to manually update the descriptor/animator reference.
                    var clone = Object.Instantiate(ac);
                    clone.name = ac.name;
                    context.AssetSaver.SaveAsset(clone);
                    ObjectRegistry.RegisterReplacedObject(ac, clone);
                    ac = clone;
                }

                foreach (var state2 in AnimatorWalker.AllStates(ac))
                    state2.motion = MapMotion(context, state, state2.motion, clipMap);

                // Synced layers: GetOverrideMotion / SetOverrideMotion
                for (int i = 0; i < ac.layers.Length; i++)
                {
                    var layer = ac.layers[i];
                    if (layer.syncedLayerIndex < 0) continue;
                    var srcLayer = ac.layers[layer.syncedLayerIndex];
                    foreach (var st in AnimatorWalker.AllStates(srcLayer.stateMachine))
                    {
                        var motion = layer.GetOverrideMotion(st);
                        var mapped = MapMotion(context, state, motion, clipMap);
                        if (!ReferenceEquals(motion, mapped))
                            layer.SetOverrideMotion(st, mapped);
                    }
                }

                // AvatarMask paths — ac.layers returns a struct-copy array,
                // so we must read-modify-write the whole array to persist changes.
                var layers = ac.layers;
                bool layersDirty = false;
                for (int li = 0; li < layers.Length; li++)
                {
                    var newMask = RewriteAvatarMask(context, state, layers[li].avatarMask);
                    if (!ReferenceEquals(newMask, layers[li].avatarMask))
                    {
                        layers[li].avatarMask = newMask;
                        layersDirty = true;
                    }
                }
                if (layersDirty) ac.layers = layers;

                // VRC PlayAudio behaviours store a transform path
                foreach (var beh in AnimatorWalker.AllBehaviours(ac))
                    RewriteBehaviourPaths(state, beh);
            }
        }

        // ------------------------------------------------------------------
        // Motion / clip mapping
        // ------------------------------------------------------------------
        private static Motion MapMotion(BuildContext ctx, ObfuscationContext state,
            Motion motion, Dictionary<AnimationClip, AnimationClip> clipMap)
        {
            switch (motion)
            {
                case null: return null;
                case AnimationClip clip:
                    return RewriteClip(ctx, state, clip, clipMap);
                case BlendTree bt:
                    var children = bt.children;
                    bool changed = false;
                    for (int i = 0; i < children.Length; i++)
                    {
                        var inner = MapMotion(ctx, state, children[i].motion, clipMap);
                        if (!ReferenceEquals(inner, children[i].motion))
                        {
                            children[i].motion = inner;
                            changed = true;
                        }
                    }
                    if (changed) bt.children = children;
                    return bt;
                default: return motion;
            }
        }

        private static AnimationClip RewriteClip(BuildContext ctx, ObfuscationContext state,
            AnimationClip clip, Dictionary<AnimationClip, AnimationClip> clipMap)
        {
            if (clip == null) return null;
            if (clipMap.TryGetValue(clip, out var existing)) return existing;
#if FR_OBF_VRCSDK3_AVATARS
            // Skip VRC proxy animations — they are referenced by GUID and we MUST NOT
            // clone them or the runtime will fail to recognise them.
            if (IsProxyClip(clip)) { clipMap[clip] = clip; return clip; }
#endif

            var floatBindings = AnimationUtility.GetCurveBindings(clip);
            var objectBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
#if UNITY_2023_1_OR_NEWER
            var discreteBindings = AnimationUtility.GetDiscreteCurveBindings(clip);
#else
            var discreteBindings = Array.Empty<EditorCurveBinding>();
#endif

            // Build the complete rewrite plan before cloning. Besides avoiding
            // duplicate GetObjectReferenceCurve calls, this lets us use Unity's
            // batch curve APIs below: each batch synchronises the clip once instead
            // of once per binding (which is extremely expensive on large clips).
            var oldFloatBindings = new List<EditorCurveBinding>();
            var newFloatBindings = new List<EditorCurveBinding>();
            var floatCurves = new List<AnimationCurve>();
            foreach (var binding in floatBindings)
            {
                if (!NeedsBindingRewrite(state, binding)) continue;
                oldFloatBindings.Add(binding);
                newFloatBindings.Add(MapBinding(state, binding));
                floatCurves.Add(AnimationUtility.GetEditorCurve(clip, binding));
            }

            var oldObjectBindings = new List<EditorCurveBinding>();
            var newObjectBindings = new List<EditorCurveBinding>();
            var objectCurves = new List<ObjectReferenceKeyframe[]>();
            foreach (var binding in objectBindings)
            {
                var keyframes = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                bool bindingChanged = NeedsBindingRewrite(state, binding);
                bool valuesChanged = RemapObjectCurveValues(state, keyframes);
                if (!bindingChanged && !valuesChanged) continue;

                oldObjectBindings.Add(binding);
                newObjectBindings.Add(MapBinding(state, binding));
                objectCurves.Add(keyframes);
            }

            var discreteRewrites = new List<(EditorCurveBinding Old, EditorCurveBinding New)>();
            foreach (var binding in discreteBindings)
            {
                if (NeedsBindingRewrite(state, binding))
                    discreteRewrites.Add((binding, MapBinding(state, binding)));
            }

            if (oldFloatBindings.Count == 0
                && oldObjectBindings.Count == 0
                && discreteRewrites.Count == 0)
            {
                clipMap[clip] = clip;
                return clip;
            }

            // Clone via Object.Instantiate — preserves ALL internal data
            // (muscle clips, animation events, root motion curves, etc.) that
            // new AnimationClip{} + manual curve copy would silently lose.
            var newClip = Object.Instantiate(clip);
            newClip.name = clip.name;

            // Delete all old bindings first, then add all mapped bindings. The
            // two-phase order is important when a target binding is also another
            // rewrite's source binding. Each Set*Curves call performs one clip sync.
            if (oldFloatBindings.Count > 0)
            {
                AnimationUtility.SetEditorCurves(newClip,
                    oldFloatBindings.ToArray(), new AnimationCurve[oldFloatBindings.Count]);
                AnimationUtility.SetEditorCurves(newClip,
                    newFloatBindings.ToArray(), floatCurves.ToArray());
            }

            if (oldObjectBindings.Count > 0)
            {
                AnimationUtility.SetObjectReferenceCurves(newClip,
                    oldObjectBindings.ToArray(), new ObjectReferenceKeyframe[oldObjectBindings.Count][]);
                AnimationUtility.SetObjectReferenceCurves(newClip,
                    newObjectBindings.ToArray(), objectCurves.ToArray());
            }

#if UNITY_2023_1_OR_NEWER
            // Rewrite discrete (int) curves — same remove/re-add pattern.
            foreach (var rewrite in discreteRewrites)
            {
                AnimationUtility.SetDiscreteCurve(newClip, rewrite.Old, null);
                AnimationUtility.SetDiscreteCurve(newClip, rewrite.New,
                    AnimationUtility.GetDiscreteCurve(clip, rewrite.Old));
            }
#endif

            clipMap[clip] = newClip;
            ctx.AssetSaver.SaveAsset(newClip);
            ObjectRegistry.RegisterReplacedObject(clip, newClip);

            // If the clip has an additive reference pose clip that also needs
            // rewriting, clone and replace it recursively.
            var settings = AnimationUtility.GetAnimationClipSettings(newClip);
            if (settings.additiveReferencePoseClip != null)
            {
                var rewritten = RewriteClip(ctx, state, settings.additiveReferencePoseClip, clipMap);
                if (!ReferenceEquals(rewritten, settings.additiveReferencePoseClip))
                {
                    settings.additiveReferencePoseClip = rewritten;
                    AnimationUtility.SetAnimationClipSettings(newClip, settings);
                }
            }

            return newClip;
        }

        private static bool NeedsBindingRewrite(ObfuscationContext state, EditorCurveBinding binding)
        {
            // Animator parameter curves: binding.type == Animator, propertyName is
            // the parameter name (path is usually ""). When ObfuscateParametersPass
            // renames a parameter, any clip that drives it via an Animator curve
            // must also have that curve's propertyName updated, otherwise the clip
            // writes to the old name while the controller/BlendTree read the new one.
            if (binding.type == typeof(Animator)
                && !string.IsNullOrEmpty(binding.propertyName)
                && state.ParameterRenames.TryGetValue(binding.propertyName, out var renamedParam)
                && renamedParam != binding.propertyName)
                return true;

            // B2 fix: null paths must not be coerced to "" — they are internal Unity
            // placeholders and rewriting them would break the curve.
            var bindingPath = binding.path;
            if (bindingPath == null) return false;

            if (state.PathRenames.TryGetValue(bindingPath, out var newPath) && newPath != bindingPath)
                return true;
            if (binding.propertyName != null
                && binding.propertyName.StartsWith(BlendShapePropertyPrefix))
            {
                var bsName = binding.propertyName.Substring(BlendShapePropertyPrefix.Length);
                // We don't yet know the *new* path for this binding, but the blendshape
                // map is keyed by current (pre-hierarchy-rename) path. Try both forms.
                if (state.BlendShapeRenamesByPath.ContainsKey((bindingPath, bsName)))
                    return true;
                // After hierarchy rename the path may be different; try mapped path.
                var maybeNewPath = state.MapPath(bindingPath);
                if (state.BlendShapeRenamesByPath.ContainsKey((maybeNewPath, bsName)))
                    return true;
            }
            return false;
        }

        private static bool RemapObjectCurveValues(ObfuscationContext state, ObjectReferenceKeyframe[] keys)
        {
            if (keys == null) return false;
            bool changed = false;
            for (int i = 0; i < keys.Length; i++)
            {
                var original = keys[i].value;
                Object mapped = original;
                if (original is Material m)
                    mapped = state.MapMaterial(m);
                else if (original is Mesh mesh)
                    mapped = state.MapMesh(mesh);
                else if (original is Texture2D tex)
                    mapped = state.MapTexture(tex);
                else if (original is AudioClip audio)
                    mapped = state.MapAudio(audio);

                if (!ReferenceEquals(original, mapped))
                {
                    keys[i].value = mapped;
                    changed = true;
                }
            }
            return changed;
        }

        private static EditorCurveBinding MapBinding(ObfuscationContext state, EditorCurveBinding binding)
        {
            // B2 fix: preserve null paths exactly — never coerce to "".
            var bindingPath = binding.path;
            var path = bindingPath != null ? state.MapPath(bindingPath) : null;
            var prop = binding.propertyName;

            // Animator parameter curves: rename the parameter so it stays in sync
            // with the controller's renamed parameter table. PhysBone-suffix forms
            // (e.g. "Hair_IsGrabbed") are already populated into ParameterRenames
            // by ObfuscateParametersPass when the suffixed name appears in the
            // controller's parameter list, so MapParameter handles them uniformly.
            if (binding.type == typeof(Animator) && !string.IsNullOrEmpty(prop))
            {
                prop = state.MapParameter(prop);
            }
            else if (!string.IsNullOrEmpty(prop) && prop.StartsWith(BlendShapePropertyPrefix))
            {
                var bsName = prop.Substring(BlendShapePropertyPrefix.Length);
                // Try (newPath, oldName), then (oldPath, oldName)
                if (path != null && state.BlendShapeRenamesByPath.TryGetValue((path, bsName), out var newName)
                    || bindingPath != null && state.BlendShapeRenamesByPath.TryGetValue((bindingPath, bsName), out newName))
                {
                    prop = BlendShapePropertyPrefix + newName;
                }
            }
            var newBinding = binding;
            newBinding.path = path;
            newBinding.propertyName = prop;
            return newBinding;
        }

        // ------------------------------------------------------------------
        // AvatarMask
        // ------------------------------------------------------------------
        /// <summary>
        /// Rewrites transform paths inside an <see cref="AvatarMask"/> and returns
        /// the (possibly cloned) mask. The caller is responsible for writing the
        /// returned mask back into the layer's <c>avatarMask</c> slot.
        /// </summary>
        private static AvatarMask RewriteAvatarMask(BuildContext ctx, ObfuscationContext state, AvatarMask mask)
        {
            if (mask == null) return null;
            if (!ctx.IsTemporaryAsset(mask))
            {
                // Clone first — masks live on disk too.
                var copy = Object.Instantiate(mask);
                copy.name = mask.name;
                ctx.AssetSaver.SaveAsset(copy);
                ObjectRegistry.RegisterReplacedObject(mask, copy);
                mask = copy;
            }
            int n = mask.transformCount;
            for (int i = 0; i < n; i++)
            {
                var oldPath = mask.GetTransformPath(i);
                var newPath = state.MapPath(oldPath);
                if (newPath != oldPath)
                    mask.SetTransformPath(i, newPath);
            }
            return mask;
        }

        // ------------------------------------------------------------------
        // VRC behaviour paths
        // ------------------------------------------------------------------
        private static void RewriteBehaviourPaths(ObfuscationContext state, StateMachineBehaviour beh)
        {
            if (beh == null) return;
#if FR_OBF_VRCSDK3_AVATARS
            if (beh is VRC.SDKBase.VRC_AnimatorPlayAudio playAudio
                && !string.IsNullOrEmpty(playAudio.SourcePath))
            {
                playAudio.SourcePath = state.MapPath(playAudio.SourcePath);
            }
#endif
        }

#if FR_OBF_VRCSDK3_AVATARS
        // ------------------------------------------------------------------
        // VRChat proxy detection (clips shipped with VRCSDK that the runtime
        // recognises by name — must not be cloned).
        // ------------------------------------------------------------------
        private static bool IsProxyClip(AnimationClip clip)
        {
            if (clip == null) return false;
            var name = clip.name ?? "";
            if (name.StartsWith("proxy_", StringComparison.Ordinal)) return true;
            var path = AssetDatabase.GetAssetPath(clip);
            if (!string.IsNullOrEmpty(path) && path.Contains("/AV3 Demo Assets/"))
                return true;
            if (!string.IsNullOrEmpty(path) && path.Contains("VRCSDK"))
                return true;
            return false;
        }
#endif
    }
}
