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
            foreach (var ac in allControllers)
                foreach (var p in ac.parameters)
                    parameterNames.Add(p.name);

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
            CollectPhysBoneAndContactPrefixes(context, state);
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
                if (state.ParameterRenames.ContainsKey(name)) continue;
                state.ParameterRenames[name] = state.NameGen.Next();
            }

            // ------------------------------------------------------------------
            // 4. Rewrite every animator: parameter table, transitions, blendtrees,
            //    state-machine behaviours, state speed/time/cycle/mirror parameters.
            // ------------------------------------------------------------------
            foreach (var ac in allControllers)
                RewriteController(ac, state);

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
            }

            return list;
        }

        // ------------------------------------------------------------------
        // PhysBone & ContactReceiver
        // ------------------------------------------------------------------
#if FR_OBF_VRCSDK3_AVATARS
        private static void CollectPhysBoneAndContactPrefixes(BuildContext ctx, ObfuscationContext state)
        {
            foreach (var pb in ctx.AvatarRootObject.GetComponentsInChildren<VRCPhysBone>(true))
            {
                var p = pb.parameter;
                if (string.IsNullOrEmpty(p)) continue;
                if (state.PhysBonePrefixRenames.ContainsKey(p)) continue;
                state.PhysBonePrefixRenames[p] = state.NameGen.Next();
            }
            foreach (var cr in ctx.AvatarRootObject.GetComponentsInChildren<ContactReceiver>(true))
            {
                var p = cr.parameter;
                if (string.IsNullOrEmpty(p)) continue;
                if (VRChatBuiltins.IsBuiltinParameter(p)) continue;
                if (state.ParameterRenames.ContainsKey(p)) continue;
                state.ParameterRenames[p] = state.NameGen.Next();
            }
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
                    }
                    break;
                case VRC.SDKBase.VRC_AnimatorPlayAudio _:
                    // Holds a transform path; rewritten in the hierarchy pass.
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
                    control.parameter = new VRCExpressionsMenu.Control.Parameter
                    {
                        name = state.MapParameter(control.parameter.name),
                    };

                if (control.subParameters != null)
                {
                    for (int j = 0; j < control.subParameters.Length; j++)
                    {
                        var sp = control.subParameters[j];
                        if (sp != null && !string.IsNullOrEmpty(sp.name))
                            control.subParameters[j] = new VRCExpressionsMenu.Control.Parameter
                            {
                                name = state.MapParameter(sp.name),
                            };
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
