using System.Collections.Generic;
using System.Linq;
using HateRipper.AvatarObfuscator.Internal;
using nadena.dev.ndmf;
using UnityEditor.Animations;
using UnityEngine;
#if FR_OBF_VRCSDK3_AVATARS
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.SDKBase;
#endif

namespace HateRipper.AvatarObfuscator.Passes
{
    /// <summary>
    /// Renames every user-defined animator parameter, then walks every animator and
    /// every place a parameter name is referenced as a string and rewrites it.
    ///
    /// The pass cooperates with the parameter-prefix system used by VRChat PhysBones
    /// and ContactReceivers: PhysBone.parameter is a prefix (e.g. <c>Hair</c>) which
    /// VRChat expands into <c>Hair_IsGrabbed</c>, <c>Hair_IsPosed</c> etc. We rename
    /// the prefix and also rewrite any animator that references the suffixed forms.
    /// </summary>
    internal sealed class ObfuscateParametersPass : Pass<ObfuscateParametersPass>
    {
        public override string DisplayName => "Avatar Obfuscator: parameters & animators";

        // VRC-defined PhysBone parameter suffixes that get auto-appended to the prefix.
        private static readonly string[] PhysBoneSuffixes =
            { "_IsGrabbed", "_IsPosed", "_Angle", "_Stretch", "_Squish" };

        protected override void Execute(BuildContext context)
        {
            var state = context.GetState<ObfuscationContext>();
            if (!state.Enabled || !state.Options.obfuscateParameters) return;

#if FR_OBF_VRCSDK3_AVATARS
            var descriptor = context.AvatarRootObject.GetComponent<VRCAvatarDescriptor>();
            object descriptorObj = descriptor;
#else
            object descriptorObj = null;
#endif

            // ------------------------------------------------------------------
            // 1. Collect parameter names from every animator + VRC param list.
            // ------------------------------------------------------------------
            var allControllers = CollectControllers(context, descriptorObj);

            var parameterNames = new HashSet<string>();
            // animatorOnlyParams: parameters that exist in successfully-processed controllers.
            // Used as a guard for Contact/PhysBone renaming — if a parameter only appears in
            // an unprocessable controller (e.g. broken AOC), renaming the Contact/PhysBone side
            // without the animator side creates an asymmetry that breaks functionality.
            var animatorOnlyParams = new HashSet<string>();
            foreach (var ac in allControllers)
                foreach (var p in ac.parameters)
                {
                    parameterNames.Add(p.name);
                    animatorOnlyParams.Add(p.name);
                }

#if FR_OBF_VRCSDK3_AVATARS
            VRCExpressionParameters expParams = descriptor != null
                ? descriptor.expressionParameters
                : null;
            if (expParams != null && state.Options.obfuscateExpressionParameters)
            {
                foreach (var p in expParams.parameters)
                    if (!string.IsNullOrEmpty(p.name)) parameterNames.Add(p.name);
            }
#endif

            // ------------------------------------------------------------------
            // 2. Decide rename for each name (skip VRC built-ins).
            // ------------------------------------------------------------------
            // Reserve built-in names so the generator can never collide with one.
            foreach (var s in VRChatBuiltins.BuiltinAnimatorParameters)
                state.NameGen.Reserve(s);

            // 2a. Allocate PhysBone prefixes FIRST. PhysBone with prefix "Hair"
            //     produces "Hair_IsGrabbed", "Hair_Angle" etc. — VRChat writes to
            //     these derived names. If those derived names also exist as
            //     Expression Parameters / animator parameters, they MUST share a
            //     consistent rename, otherwise PhysBone writes to one name and the
            //     animator reads another.
#if FR_OBF_VRCSDK3_AVATARS
            var guardedParams = CollectPhysBoneAndContactPrefixes(context, state, animatorOnlyParams);
#else
            var guardedParams = new HashSet<string>();
#endif

            // 2b. Lock in the suffixed forms of any PhysBone prefix into ParameterRenames.
            foreach (var kv in state.PhysBonePrefixRenames)
            {
                foreach (var suffix in PhysBoneSuffixes)
                {
                    var oldFull = kv.Key + suffix;
                    var newFull = kv.Value + suffix;
                    // Only override if the suffixed form is actually used as a parameter
                    // somewhere; otherwise skip — no point reserving a rename for an
                    // unused name.
                    if (parameterNames.Contains(oldFull))
                    {
                        state.ParameterRenames[oldFull] = newFull;
                        // Reserve the new name so the generator can't reuse it.
                        state.NameGen.Reserve(newFull);
                    }
                }
            }

            // 2c. Generate fresh obfuscated names for everything else.
            foreach (var name in parameterNames.OrderBy(n => n)) // stable order for determinism
            {
                if (VRChatBuiltins.IsBuiltinParameter(name)) continue;
                if (state.ShouldSkipParameter(name)) continue;
                if (state.ParameterRenames.ContainsKey(name)) continue;
                // Skip parameters that were guarded in step 2a — their Contact/PhysBone
                // was deliberately left plaintext because the consuming controller could
                // not be processed. Renaming them here (e.g. via Expression Parameters)
                // would re-introduce the asymmetry the guard was designed to prevent.
                if (guardedParams.Contains(name)) continue;
                state.ParameterRenames[name] = state.NameGen.Next();
            }

            // ------------------------------------------------------------------
            // 4. Rewrite every animator: parameter table, transitions, blendtrees,
            //    state-machine behaviours, state speed/time/cycle/mirror parameters.
            // ------------------------------------------------------------------
            foreach (var ac in allControllers)
                RewriteController(ac, state);

            // ------------------------------------------------------------------
            // 4b. Optional cosmetic obfuscation: collapse every state-machine node
            //     to position (0, 0, 0). The Animator window then shows an
            //     unreadable pile of overlapping nodes — runtime behaviour is
            //     untouched because position fields are pure editor cosmetic data.
            // ------------------------------------------------------------------
            if (state.Options.flattenStatePositions)
            {
                foreach (var ac in allControllers)
                    FlattenStatePositions(ac);
            }

            // ------------------------------------------------------------------
            // 5. Rewrite VRCAvatarDescriptor parameter slots (Expression Parameters,
            //    Expression Menu).
            // ------------------------------------------------------------------
#if FR_OBF_VRCSDK3_AVATARS
            if (descriptor != null && state.Options.obfuscateExpressionParameters)
            {
                if (expParams != null)
                {
                    var clone = Object.Instantiate(expParams);
                    clone.name = expParams.name;
                    context.AssetSaver.SaveAsset(clone);
                    ObjectRegistry.RegisterReplacedObject(expParams, clone);
                    var arr = clone.parameters;
                    for (int i = 0; i < arr.Length; i++)
                        arr[i].name = state.MapParameter(arr[i].name);
                    clone.parameters = arr;
                    descriptor.expressionParameters = clone;
                }

                if (descriptor.expressionsMenu != null)
                    descriptor.expressionsMenu = RewriteMenu(context, descriptor.expressionsMenu, state,
                        new Dictionary<VRCExpressionsMenu, VRCExpressionsMenu>());
            }

            // 6. Rewrite the parameter slot on every PhysBone / ContactReceiver.
            RewritePhysBonesAndContacts(context, state);
#endif
        }

        // ------------------------------------------------------------------
        // Controller collection
        // ------------------------------------------------------------------
        private static List<AnimatorController> CollectControllers(BuildContext ctx,
            object descriptorObj)
        {
            var list = new List<AnimatorController>();

#if FR_OBF_VRCSDK3_AVATARS
            var descriptor = descriptorObj as VRCAvatarDescriptor;
            if (descriptor != null)
            {
                // Obfuscate any controller that is *assigned*, regardless of the
                // customizeAnimationLayers flag — the user may flip the flag later
                // and we don't want stale plaintext to leak.
                var baseLayers = descriptor.baseAnimationLayers;
                for (int i = 0; i < baseLayers.Length; i++)
                {
                    if (baseLayers[i].animatorController == null) continue;
                    var temp = AssetCloner.EnsureTemporary(ctx, baseLayers[i].animatorController);
                    if (temp != null)
                    {
                        baseLayers[i].animatorController = temp;
                        list.Add(temp);
                    }
                    else
                    {
                        Debug.LogWarning(
                            $"[AvatarObfuscator] baseAnimationLayers[{i}] controller '{baseLayers[i].animatorController.name}' " +
                            "could not be processed (likely an AnimatorOverrideController with a broken base). " +
                            "Parameters on this layer will NOT be obfuscated.");
                    }
                }
                descriptor.baseAnimationLayers = baseLayers;

                var specialLayers = descriptor.specialAnimationLayers;
                for (int i = 0; i < specialLayers.Length; i++)
                {
                    if (specialLayers[i].animatorController == null) continue;
                    var temp = AssetCloner.EnsureTemporary(ctx, specialLayers[i].animatorController);
                    if (temp != null)
                    {
                        specialLayers[i].animatorController = temp;
                        list.Add(temp);
                    }
                    else
                    {
                        Debug.LogWarning(
                            $"[AvatarObfuscator] specialAnimationLayers[{i}] controller '{specialLayers[i].animatorController.name}' " +
                            "could not be processed (likely an AnimatorOverrideController with a broken base). " +
                            "Parameters on this layer will NOT be obfuscated.");
                    }
                }
                descriptor.specialAnimationLayers = specialLayers;
            }
#endif
            // Plain Animator components
            foreach (var animator in ctx.AvatarRootObject.GetComponentsInChildren<Animator>(true))
            {
                if (animator.runtimeAnimatorController == null) continue;
                var temp = AssetCloner.EnsureTemporary(ctx, animator.runtimeAnimatorController);
                if (temp != null)
                {
                    animator.runtimeAnimatorController = temp;
                    if (!list.Contains(temp)) list.Add(temp);
                }
                else
                {
                    Debug.LogWarning(
                        $"[AvatarObfuscator] Animator on '{animator.gameObject.name}' controller '{animator.runtimeAnimatorController.name}' " +
                        "could not be processed (likely an AnimatorOverrideController with a broken base). " +
                        "Parameters on this animator will NOT be obfuscated.");
                }
            }

            return list;
        }

        // ------------------------------------------------------------------
        // PhysBone & ContactReceiver
        // ------------------------------------------------------------------
#if FR_OBF_VRCSDK3_AVATARS
        /// <param name="animatorOnlyParams">
        /// Parameter names that exist in at least one successfully-processed AnimatorController.
        /// Used as a guard: if a Contact/PhysBone parameter is NOT consumed by any processed
        /// controller, renaming it would create an asymmetry (contact renamed, animator not)
        /// that breaks functionality at runtime.
        /// </param>
        /// <returns>
        /// A set of parameter names that were deliberately left un-renamed because
        /// their consuming controller could not be processed. The caller must exclude
        /// these from step 2c so that Expression Parameters don't re-introduce the
        /// rename and re-create the asymmetry.
        /// </returns>
        private static HashSet<string> CollectPhysBoneAndContactPrefixes(BuildContext ctx, ObfuscationContext state,
            HashSet<string> animatorOnlyParams)
        {
            var guardedParams = new HashSet<string>();

            foreach (var pb in ctx.AvatarRootObject.GetComponentsInChildren<VRCPhysBone>(true))
            {
                var p = pb.parameter;
                if (string.IsNullOrEmpty(p)) continue;
                if (state.ShouldSkipParameter(p)) continue;
                if (state.PhysBonePrefixRenames.ContainsKey(p)) continue;

                // Guard: at least one suffixed form (e.g. prefix_IsGrabbed) must appear
                // in a successfully-processed controller. If none do, renaming the prefix
                // would break the PhysBone→animator link.
                bool anySuffixConsumed = false;
                foreach (var suffix in PhysBoneSuffixes)
                {
                    if (animatorOnlyParams.Contains(p + suffix))
                    {
                        anySuffixConsumed = true;
                        break;
                    }
                }
                if (!anySuffixConsumed)
                {
                    Debug.LogWarning(
                        $"[AvatarObfuscator] PhysBone parameter prefix '{p}' on '{pb.gameObject.name}' is not consumed " +
                        "by any successfully-processed animator; leaving plaintext to avoid breaking the PhysBone. " +
                        "If you expected obfuscation, check whether the animator that reads this parameter " +
                        "is an unresolvable AnimatorOverrideController.");
                    // Guard the prefix itself and all its suffixed forms so step 2c
                    // doesn't re-introduce the rename via Expression Parameters.
                    guardedParams.Add(p);
                    foreach (var suffix in PhysBoneSuffixes)
                        guardedParams.Add(p + suffix);
                    continue;
                }

                state.PhysBonePrefixRenames[p] = state.NameGen.Next();
            }
            foreach (var cr in ctx.AvatarRootObject.GetComponentsInChildren<ContactReceiver>(true))
            {
                var p = cr.parameter;
                if (string.IsNullOrEmpty(p)) continue;
                if (VRChatBuiltins.IsBuiltinParameter(p)) continue;
                if (state.ShouldSkipParameter(p)) continue;
                if (state.ParameterRenames.ContainsKey(p)) continue;

                // Guard: only rename if the parameter is consumed by a processed controller.
                if (!animatorOnlyParams.Contains(p))
                {
                    Debug.LogWarning(
                        $"[AvatarObfuscator] Contact parameter '{p}' on '{cr.gameObject.name}' is not consumed " +
                        "by any successfully-processed animator; leaving plaintext to avoid breaking the contact. " +
                        "If you expected obfuscation, check whether the animator that reads this parameter " +
                        "is an unresolvable AnimatorOverrideController.");
                    guardedParams.Add(p);
                    continue;
                }

                state.ParameterRenames[p] = state.NameGen.Next();
            }

            return guardedParams;
        }

        private static void RewritePhysBonesAndContacts(BuildContext ctx, ObfuscationContext state)
        {
            foreach (var pb in ctx.AvatarRootObject.GetComponentsInChildren<VRCPhysBone>(true))
            {
                if (state.PhysBonePrefixRenames.TryGetValue(pb.parameter ?? "", out var renamed))
                    pb.parameter = renamed;
            }
            foreach (var cr in ctx.AvatarRootObject.GetComponentsInChildren<ContactReceiver>(true))
            {
                if (!string.IsNullOrEmpty(cr.parameter)
                    && state.ParameterRenames.TryGetValue(cr.parameter, out var renamed))
                    cr.parameter = renamed;
            }
        }
#endif

        // ------------------------------------------------------------------
        // Controller rewrite
        // ------------------------------------------------------------------
        private static void RewriteController(AnimatorController controller, ObfuscationContext state)
        {
            // Parameter table
            var pars = controller.parameters;
            for (int i = 0; i < pars.Length; i++)
                pars[i].name = state.MapParameter(pars[i].name);
            controller.parameters = pars;

            // Transitions: AnimatorCondition.parameter
            foreach (var t in AnimatorWalker.AllTransitions(controller))
            {
                var conds = t.conditions;
                for (int i = 0; i < conds.Length; i++)
                {
                    conds[i].parameter = MapParameterOrPhysBoneSuffix(conds[i].parameter, state);
                }
                t.conditions = conds;
            }

            // States: speed/cycleOffset/mirror/time parameters
            foreach (var s in AnimatorWalker.AllStates(controller))
            {
                if (s.speedParameterActive)
                    s.speedParameter = state.MapParameter(s.speedParameter);
                if (s.cycleOffsetParameterActive)
                    s.cycleOffsetParameter = state.MapParameter(s.cycleOffsetParameter);
                if (s.mirrorParameterActive)
                    s.mirrorParameter = state.MapParameter(s.mirrorParameter);
                if (s.timeParameterActive)
                    s.timeParameter = state.MapParameter(s.timeParameter);
            }

            // Blend trees
            foreach (var bt in AnimatorWalker.AllBlendTrees(controller))
            {
                bt.blendParameter = state.MapParameter(bt.blendParameter);
                bt.blendParameterY = state.MapParameter(bt.blendParameterY);

                if (bt.blendType == BlendTreeType.Direct)
                {
                    var children = bt.children;
                    for (int i = 0; i < children.Length; i++)
                        children[i].directBlendParameter = state.MapParameter(children[i].directBlendParameter);
                    bt.children = children;
                }
            }

            // Behaviours (VRCAvatarParameterDriver, etc.)
            foreach (var b in AnimatorWalker.AllBehaviours(controller))
                RewriteBehaviour(b, state);
        }

        private static string MapParameterOrPhysBoneSuffix(string original, ObfuscationContext state)
        {
            if (string.IsNullOrEmpty(original)) return original;
            if (state.ParameterRenames.TryGetValue(original, out var renamed)) return renamed;

            // PhysBone _IsGrabbed / _Angle / etc. forms
            foreach (var suffix in PhysBoneSuffixes)
            {
                if (!original.EndsWith(suffix)) continue;
                var prefix = original.Substring(0, original.Length - suffix.Length);
                if (state.PhysBonePrefixRenames.TryGetValue(prefix, out var newPrefix))
                    return newPrefix + suffix;
            }
            return original;
        }

        // ------------------------------------------------------------------
        // State-position flattening (pure cosmetic obfuscation)
        // ------------------------------------------------------------------
        // Position fields on AnimatorStateMachine / ChildAnimatorState /
        // ChildAnimatorStateMachine are editor-only data used by the Animator
        // window to lay out nodes in the graph view. Zeroing them collapses
        // every node onto the origin so a ripper inspecting the controller
        // sees an unreadable pile, while the runtime — which only consumes
        // states, transitions and behaviours — is unaffected.
        //
        // The controllers we mutate here are temporary clones produced by
        // AssetCloner.EnsureTemporary, so this never touches the user's
        // source asset.
        private static void FlattenStatePositions(AnimatorController controller)
        {
            foreach (var sm in AnimatorWalker.AllStateMachines(controller))
            {
                sm.entryPosition = Vector3.zero;
                sm.anyStatePosition = Vector3.zero;
                sm.exitPosition = Vector3.zero;
                sm.parentStateMachinePosition = Vector3.zero;

                // ChildAnimatorState is a struct: mutate a copy of the array,
                // then assign it back to commit the position change.
                var states = sm.states;
                for (int i = 0; i < states.Length; i++)
                    states[i].position = Vector3.zero;
                sm.states = states;

                var subSMs = sm.stateMachines;
                for (int i = 0; i < subSMs.Length; i++)
                    subSMs[i].position = Vector3.zero;
                sm.stateMachines = subSMs;
            }
        }

        private static void RewriteBehaviour(StateMachineBehaviour behaviour, ObfuscationContext state)
        {
#if FR_OBF_VRCSDK3_AVATARS
            switch (behaviour)
            {
                case VRCAvatarParameterDriver driver:
                    foreach (var p in driver.parameters)
                    {
                        p.name = state.MapParameter(p.name);
                        if (!string.IsNullOrEmpty(p.source))
                            p.source = state.MapParameter(p.source);
                        if (p.destParam is string destParam && !string.IsNullOrEmpty(destParam))
                            p.destParam = state.MapParameter(destParam);
                    }
                    break;
                case VRC.SDKBase.VRC_AnimatorPlayAudio playAudio:
                    // SourcePath is a transform path, rewritten in the animation pass.
                    // ParameterName is an int animator parameter that selects which audio
                    // clip to play when PlaybackOrder == Order.Parameter — must follow the
                    // parameter rename or the cloned controller's renamed parameter and
                    // the behaviour's stale ParameterName disagree, and clip selection
                    // silently falls back to the first clip in-game.
                    if (!string.IsNullOrEmpty(playAudio.ParameterName))
                        playAudio.ParameterName = state.MapParameter(playAudio.ParameterName);
                    break;
            }
#endif
        }

        // ------------------------------------------------------------------
        // Expression Menu rewrite (clones the entire menu tree to avoid mutating
        // the user's source asset).
        // ------------------------------------------------------------------
#if FR_OBF_VRCSDK3_AVATARS
        private static VRCExpressionsMenu RewriteMenu(BuildContext ctx, VRCExpressionsMenu menu,
            ObfuscationContext state, Dictionary<VRCExpressionsMenu, VRCExpressionsMenu> cache)
        {
            if (menu == null) return null;
            if (cache.TryGetValue(menu, out var existing)) return existing;

            var clone = Object.Instantiate(menu);
            clone.name = menu.name;
            ctx.AssetSaver.SaveAsset(clone);
            ObjectRegistry.RegisterReplacedObject(menu, clone);
            cache[menu] = clone;

            for (int i = 0; i < clone.controls.Count; i++)
            {
                var control = clone.controls[i];

                // Note: control.name is the user-visible label and we DO NOT touch it.

                if (control.parameter != null && !string.IsNullOrEmpty(control.parameter.name))
                {
                    var param = control.parameter;
                    param.name = state.MapParameter(param.name);
                    control.parameter = param;
                }

                if (control.subParameters != null)
                {
                    for (int j = 0; j < control.subParameters.Length; j++)
                    {
                        var sp = control.subParameters[j];
                        if (sp != null && !string.IsNullOrEmpty(sp.name))
                        {
                            sp.name = state.MapParameter(sp.name);
                            control.subParameters[j] = sp;
                        }
                    }
                }

                if (control.subMenu != null)
                    control.subMenu = RewriteMenu(ctx, control.subMenu, state, cache);
            }

            return clone;
        }
#endif
    }
}
