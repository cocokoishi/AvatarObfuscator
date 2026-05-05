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
            foreach (var ac in controllers)
            {
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

                // AvatarMask paths
                foreach (var layer in ac.layers)
                    RewriteAvatarMask(context, state, layer.avatarMask);

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

            // Determine if any binding actually needs rewriting; if not, skip cloning.
            bool needsRewrite = false;
            foreach (var b in floatBindings)
                if (NeedsBindingRewrite(state, b)) { needsRewrite = true; break; }
            if (!needsRewrite)
            {
                foreach (var b in objectBindings)
                    if (NeedsBindingRewrite(state, b) || ObjectCurveNeedsRewrite(state, clip, b))
                    { needsRewrite = true; break; }
            }

            if (!needsRewrite)
            {
                clipMap[clip] = clip;
                return clip;
            }

            // Clone — never mutate user assets.
            var newClip = new AnimationClip
            {
                name = clip.name,
                wrapMode = clip.wrapMode,
                legacy = clip.legacy,
                frameRate = clip.frameRate,
                localBounds = clip.localBounds,
            };
            ctx.AssetSaver.SaveAsset(newClip);
            ObjectRegistry.RegisterReplacedObject(clip, newClip);

            // Carry over m_UseHighQualityCurve (no public API)
            using (var so = new SerializedObject(clip))
            using (var nso = new SerializedObject(newClip))
            {
                var src = so.FindProperty("m_UseHighQualityCurve");
                var dst = nso.FindProperty("m_UseHighQualityCurve");
                if (src != null && dst != null) dst.boolValue = src.boolValue;
                nso.ApplyModifiedPropertiesWithoutUndo();
            }

            foreach (var binding in floatBindings)
            {
                var newBinding = MapBinding(state, binding);
                AnimationUtility.SetEditorCurve(newClip, newBinding,
                    AnimationUtility.GetEditorCurve(clip, binding));
            }

            foreach (var binding in objectBindings)
            {
                var newBinding = MapBinding(state, binding);
                var keyframes = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                if (keyframes != null)
                {
                    for (int i = 0; i < keyframes.Length; i++)
                    {
                        if (keyframes[i].value is Material m)
                            keyframes[i].value = state.MapMaterial(m);
                        else if (keyframes[i].value is Mesh mesh)
                            keyframes[i].value = state.MapMesh(mesh);
                    }
                }
                AnimationUtility.SetObjectReferenceCurve(newClip, newBinding, keyframes);
            }

            // Preserve length if the rewrite removed bindings (guards against empty clips changing length).
            if (!Mathf.Approximately(newClip.length, clip.length))
            {
                // Bind the length-padding curve to a homoglyph path generated
                // from the same name pool as everything else. Older versions
                // used a literal "$ObfuscatorClipLengthDummy$" string here
                // which a ripper could grep for to fingerprint this plugin
                // in extracted bundles. Now the placeholder is indistinguishable
                // from any other obfuscated identifier.
                var padPath = state.NameGen != null ? state.NameGen.Next() : "ÌÍÎÏ";
                newClip.SetCurve(padPath, typeof(GameObject), "m_IsActive",
                    AnimationCurve.Constant(clip.length, clip.length, 1f));
            }

            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            if (settings.additiveReferencePoseClip != null)
                settings.additiveReferencePoseClip = RewriteClip(ctx, state, settings.additiveReferencePoseClip, clipMap);
            AnimationUtility.SetAnimationClipSettings(newClip, settings);

            clipMap[clip] = newClip;
            return newClip;
        }

        private static bool NeedsBindingRewrite(ObfuscationContext state, EditorCurveBinding binding)
        {
            if (state.PathRenames.TryGetValue(binding.path ?? "", out var newPath) && newPath != binding.path)
                return true;
            if (binding.propertyName != null
                && binding.propertyName.StartsWith(BlendShapePropertyPrefix))
            {
                var bsName = binding.propertyName.Substring(BlendShapePropertyPrefix.Length);
                // We don't yet know the *new* path for this binding, but the blendshape
                // map is keyed by current (pre-hierarchy-rename) path. Try both forms.
                if (state.BlendShapeRenamesByPath.ContainsKey((binding.path ?? "", bsName)))
                    return true;
                // After hierarchy rename the path may be different; try mapped path.
                var maybeNewPath = state.MapPath(binding.path ?? "");
                if (state.BlendShapeRenamesByPath.ContainsKey((maybeNewPath, bsName)))
                    return true;
            }
            return false;
        }

        private static bool ObjectCurveNeedsRewrite(ObfuscationContext state, AnimationClip clip, EditorCurveBinding binding)
        {
            var keys = AnimationUtility.GetObjectReferenceCurve(clip, binding);
            if (keys == null) return false;
            foreach (var k in keys)
                if (k.value is Material m && state.MaterialReplacements.ContainsKey(m))
                    return true;
            return false;
        }

        private static EditorCurveBinding MapBinding(ObfuscationContext state, EditorCurveBinding binding)
        {
            var path = state.MapPath(binding.path ?? "");
            var prop = binding.propertyName;
            if (!string.IsNullOrEmpty(prop) && prop.StartsWith(BlendShapePropertyPrefix))
            {
                var bsName = prop.Substring(BlendShapePropertyPrefix.Length);
                // Try (newPath, oldName), then (oldPath, oldName)
                if (state.BlendShapeRenamesByPath.TryGetValue((path, bsName), out var newName)
                    || state.BlendShapeRenamesByPath.TryGetValue((binding.path ?? "", bsName), out newName))
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
        private static void RewriteAvatarMask(BuildContext ctx, ObfuscationContext state, AvatarMask mask)
        {
            if (mask == null) return;
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
