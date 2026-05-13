using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HateRipper.AvatarObfuscator.Internal
{
    /// <summary>
    /// Helpers for safely cloning animator assets and their sub-assets so we can
    /// freely mutate them inside the build context. Mirrors what AAO's
    /// DupliacteAssets pass does for us — but we don't want to depend on AAO,
    /// so a self-contained version lives here.
    /// </summary>
    internal static class AssetCloner
    {
        /// <summary>
        /// Returns an <see cref="AnimatorController"/> that is guaranteed to be a
        /// temporary asset. If <paramref name="source"/> is already temporary it
        /// is returned untouched; otherwise it is deep-cloned (states, transitions,
        /// blend trees, sub-state-machines, behaviours) into the build context.
        /// AnimationClips and AvatarMasks are *not* cloned here — those are
        /// duplicated lazily by ObfuscateAnimationClipsPass.
        /// </summary>
        public static AnimatorController EnsureTemporary(BuildContext ctx, RuntimeAnimatorController source)
        {
            if (source == null) return null;

            var flat = source as AnimatorController ??
                       FlattenOverrideController(ctx, source as AnimatorOverrideController);
            if (flat == null) return null;

            if (ctx.IsTemporaryAsset(flat)) return flat;

            return DeepCloneController(ctx, flat);
        }

        private static AnimatorController FlattenOverrideController(BuildContext ctx, AnimatorOverrideController over)
        {
            if (over == null) return null;

            // Walk down the AOC chain, collecting each layer into a stack.
            // Push order is outer-first (the while loop starts from `over`),
            // so Stack<T> foreach (LIFO) naturally yields inner-first iteration
            // — exactly the apply order we need.
            var stack = new Stack<AnimatorOverrideController>();
            var visited = new HashSet<RuntimeAnimatorController>();
            AnimatorController baseController = null;
            var cur = over;

            while (cur != null && visited.Add(cur))
            {
                stack.Push(cur);
                var next = cur.runtimeAnimatorController;
                if (next is AnimatorController concrete)
                {
                    baseController = concrete;
                    break;
                }
                cur = next as AnimatorOverrideController;
            }

            if (baseController == null)
            {
                Debug.LogWarning(
                    $"[AvatarObfuscator] AnimatorOverrideController '{over.name}' has no resolvable " +
                    "AnimatorController base (the chain may be broken, circular, or the base field is empty). " +
                    "This layer will be skipped — its parameters will NOT be obfuscated. " +
                    "Recommendation: replace this Override with a plain AnimatorController, or fix the base reference.");
                return null;
            }

            var clone = DeepCloneController(ctx, baseController);

            // Apply overrides layer-by-layer, inner-first then outer.
            // This ensures multi-hop chains (e.g. base motion X → inner override Y → outer override Z)
            // resolve correctly: after inner apply, states hold Y; after outer apply, states hold Z.
            foreach (var aoc in stack)
            {
                var pairs = new List<KeyValuePair<AnimationClip, AnimationClip>>(aoc.overridesCount);
                aoc.GetOverrides(pairs);
                var layerMap = new Dictionary<AnimationClip, AnimationClip>();
                foreach (var kv in pairs)
                    if (kv.Key != null && kv.Value != null)
                        layerMap[kv.Key] = kv.Value;
                if (layerMap.Count == 0) continue;

                foreach (var s in AnimatorWalker.AllStates(clone))
                    s.motion = ApplyOverrides(s.motion, layerMap);
            }

            return clone;
        }

        private static Motion ApplyOverrides(Motion motion, Dictionary<AnimationClip, AnimationClip> map)
        {
            switch (motion)
            {
                case null: return null;
                case AnimationClip clip:
                    return map.TryGetValue(clip, out var rep) ? rep : clip;
                case BlendTree bt:
                    var children = bt.children;
                    for (int i = 0; i < children.Length; i++)
                        children[i].motion = ApplyOverrides(children[i].motion, map);
                    bt.children = children;
                    return bt;
                default: return motion;
            }
        }

        private static AnimatorController DeepCloneController(BuildContext ctx, AnimatorController source)
        {
            // Use DeepClone semantics: every embedded object is duplicated, AnimationClips are kept.
            var copy = new AnimatorController { name = source.name };
            ctx.AssetSaver.SaveAsset(copy);
            ObjectRegistry.RegisterReplacedObject(source, copy);

            // Parameters first
            var parameters = source.parameters;
            var copiedParameters = new AnimatorControllerParameter[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                copiedParameters[i] = new AnimatorControllerParameter
                {
                    name = parameters[i].name,
                    type = parameters[i].type,
                    defaultBool = parameters[i].defaultBool,
                    defaultInt = parameters[i].defaultInt,
                    defaultFloat = parameters[i].defaultFloat,
                };
            }
            copy.parameters = copiedParameters;

            // Layers
            var srcLayers = source.layers;
            var dstLayers = new AnimatorControllerLayer[srcLayers.Length];

            // Controller-wide maps. Promoted from per-SM-local so that transitions
            // referencing siblings (e.g. a state inside sub-SM-A whose transition
            // points to sub-SM-B at the same level) can resolve their destinations
            // — the previous per-CloneStateMachine-local stateMap could only ever
            // contain its own SM's states, so cross-sibling transitions silently
            // lost their destination during clone.
            var smMap = new Dictionary<AnimatorStateMachine, AnimatorStateMachine>();
            var stateMap = new Dictionary<AnimatorState, AnimatorState>();

            // Phase 1: clone every SM + state + sub-SM structurally, no transitions.
            // After this loop completes, smMap and stateMap know about every SM and
            // state in every layer of the controller.
            for (int i = 0; i < srcLayers.Length; i++)
            {
                var src = srcLayers[i];
                var newSm = src.stateMachine != null
                    ? CloneStateMachine(ctx, src.stateMachine, smMap, stateMap)
                    : null;
                dstLayers[i] = new AnimatorControllerLayer
                {
                    name = src.name,
                    avatarMask = src.avatarMask,
                    blendingMode = src.blendingMode,
                    defaultWeight = src.defaultWeight,
                    iKPass = src.iKPass,
                    syncedLayerAffectsTiming = src.syncedLayerAffectsTiming,
                    syncedLayerIndex = src.syncedLayerIndex,
                    stateMachine = newSm,
                };
            }
            copy.layers = dstLayers;

            // Phase 2: now that every SM and state is in the maps, clone every
            // transition. Cross-sibling-SM and cross-state references now resolve.
            for (int i = 0; i < srcLayers.Length; i++)
            {
                var srcSm = srcLayers[i].stateMachine;
                if (srcSm == null) continue;
                if (!smMap.TryGetValue(srcSm, out var dstSm)) continue;
                CloneTransitionsRecursive(srcSm, dstSm, stateMap, smMap);
            }

            // Synced-layer overrides
            for (int i = 0; i < srcLayers.Length; i++)
            {
                if (srcLayers[i].syncedLayerIndex < 0) continue;
                var srcSync = srcLayers[srcLayers[i].syncedLayerIndex];
                if (srcSync.stateMachine == null) continue;
                foreach (var stPair in EnumerateStatePairs(srcSync.stateMachine, smMap))
                {
                    var srcState = stPair.Item1;
                    var dstState = stPair.Item2;
                    var motion = srcLayers[i].GetOverrideMotion(srcState);
                    if (motion != null)
                        dstLayers[i].SetOverrideMotion(dstState, CloneMotion(ctx, motion));
                    var beh = srcLayers[i].GetOverrideBehaviours(srcState);
                    if (beh != null && beh.Length > 0)
                    {
                        var copies = new StateMachineBehaviour[beh.Length];
                        for (int b = 0; b < beh.Length; b++)
                            copies[b] = beh[b] != null ? CloneBehaviour(ctx, beh[b]) : null;
                        dstLayers[i].SetOverrideBehaviours(dstState, copies);
                    }
                }
            }

            return copy;
        }

        private static IEnumerable<(AnimatorState, AnimatorState)> EnumerateStatePairs(
            AnimatorStateMachine srcRoot, Dictionary<AnimatorStateMachine, AnimatorStateMachine> smMap)
        {
            foreach (var srcSm in AnimatorWalker.AllStateMachines(srcRoot))
            {
                if (!smMap.TryGetValue(srcSm, out var dstSm)) continue;
                var srcStates = srcSm.states;
                var dstStates = dstSm.states;
                int n = Mathf.Min(srcStates.Length, dstStates.Length);
                for (int i = 0; i < n; i++)
                    yield return (srcStates[i].state, dstStates[i].state);
            }
        }

        private static AnimatorStateMachine CloneStateMachine(BuildContext ctx, AnimatorStateMachine src,
            Dictionary<AnimatorStateMachine, AnimatorStateMachine> smMap,
            Dictionary<AnimatorState, AnimatorState> stateMap)
        {
            if (smMap.TryGetValue(src, out var existing)) return existing;
            var copy = new AnimatorStateMachine
            {
                name = src.name,
                hideFlags = HideFlags.HideInHierarchy,
                anyStatePosition = src.anyStatePosition,
                entryPosition = src.entryPosition,
                exitPosition = src.exitPosition,
                parentStateMachinePosition = src.parentStateMachinePosition,
            };
            smMap[src] = copy;
            ctx.AssetSaver.SaveAsset(copy);
            ObjectRegistry.RegisterReplacedObject(src, copy);

            // States
            var srcStates = src.states;
            var dstStates = new ChildAnimatorState[srcStates.Length];
            for (int i = 0; i < srcStates.Length; i++)
            {
                var newState = CloneState(ctx, srcStates[i].state);
                stateMap[srcStates[i].state] = newState;
                dstStates[i] = new ChildAnimatorState
                {
                    state = newState,
                    position = srcStates[i].position,
                };
            }
            copy.states = dstStates;

            // Sub-state-machines
            var srcSubs = src.stateMachines;
            var dstSubs = new ChildAnimatorStateMachine[srcSubs.Length];
            for (int i = 0; i < srcSubs.Length; i++)
            {
                dstSubs[i] = new ChildAnimatorStateMachine
                {
                    stateMachine = CloneStateMachine(ctx, srcSubs[i].stateMachine, smMap, stateMap),
                    position = srcSubs[i].position,
                };
            }
            copy.stateMachines = dstSubs;

            // Default state (must be set after states are populated)
            if (src.defaultState != null && stateMap.TryGetValue(src.defaultState, out var defState))
                copy.defaultState = defState;

            // Behaviours on the SM itself
            var srcBeh = src.behaviours;
            var dstBeh = new StateMachineBehaviour[srcBeh.Length];
            for (int i = 0; i < srcBeh.Length; i++)
                dstBeh[i] = srcBeh[i] != null ? CloneBehaviour(ctx, srcBeh[i]) : null;
            copy.behaviours = dstBeh;

            // Transitions are NOT cloned here. The caller (DeepCloneController)
            // walks every cloned SM in a second phase, after every layer's SM
            // tree has been added to smMap/stateMap, so transitions referencing
            // sibling sub-SMs or states inside sibling sub-SMs can resolve.
            return copy;
        }

        private static void CloneTransitionsRecursive(AnimatorStateMachine src, AnimatorStateMachine dst,
            Dictionary<AnimatorState, AnimatorState> stateMap,
            Dictionary<AnimatorStateMachine, AnimatorStateMachine> smMap)
        {
            CloneTransitionsTemp(src, dst, stateMap, smMap);

            var srcSubs = src.stateMachines;
            var dstSubs = dst.stateMachines;
            for (int i = 0; i < srcSubs.Length && i < dstSubs.Length; i++)
            {
                var srcSub = srcSubs[i].stateMachine;
                var dstSub = dstSubs[i].stateMachine;
                if (srcSub == null || dstSub == null) continue;
                CloneTransitionsRecursive(srcSub, dstSub, stateMap, smMap);
            }
        }

        private static void CloneTransitionsTemp(AnimatorStateMachine src, AnimatorStateMachine dst,
            Dictionary<AnimatorState, AnimatorState> stateMap,
            Dictionary<AnimatorStateMachine, AnimatorStateMachine> smMap)
        {
            // Entry transitions
            var entry = new List<AnimatorTransition>();
            foreach (var t in src.entryTransitions)
                entry.Add(CloneEntryTransition(t, stateMap, smMap));
            dst.entryTransitions = entry.ToArray();

            // AnyState transitions
            var any = new List<AnimatorStateTransition>();
            foreach (var t in src.anyStateTransitions)
                any.Add(CloneStateTransition(t, stateMap, smMap));
            dst.anyStateTransitions = any.ToArray();

            // State -> outgoing
            var srcStates = src.states;
            var dstStates = dst.states;
            for (int i = 0; i < srcStates.Length && i < dstStates.Length; i++)
            {
                var outg = new List<AnimatorStateTransition>();
                foreach (var t in srcStates[i].state.transitions)
                    outg.Add(CloneStateTransition(t, stateMap, smMap));
                dstStates[i].state.transitions = outg.ToArray();
            }

            // Sub-SM transitions
            var srcSubs = src.stateMachines;
            var dstSubs = dst.stateMachines;
            for (int i = 0; i < srcSubs.Length && i < dstSubs.Length; i++)
            {
                var srcSub = srcSubs[i].stateMachine;
                var dstSub = dstSubs[i].stateMachine;
                if (srcSub == null || dstSub == null) continue;
                var trans = src.GetStateMachineTransitions(srcSub);
                if (trans == null || trans.Length == 0) continue;
                var clonedTrans = new AnimatorTransition[trans.Length];
                for (int j = 0; j < trans.Length; j++)
                    clonedTrans[j] = CloneEntryTransition(trans[j], stateMap, smMap);
                dst.SetStateMachineTransitions(dstSub, clonedTrans);
            }
        }

        private static AnimatorTransition CloneEntryTransition(AnimatorTransition t,
            Dictionary<AnimatorState, AnimatorState> stateMap,
            Dictionary<AnimatorStateMachine, AnimatorStateMachine> smMap)
        {
            var copy = new AnimatorTransition { hideFlags = HideFlags.HideInHierarchy };
            copy.conditions = (AnimatorCondition[])t.conditions.Clone();
            copy.solo = t.solo;
            copy.mute = t.mute;
            if (t.destinationState != null && stateMap.TryGetValue(t.destinationState, out var ds))
                copy.destinationState = ds;
            if (t.destinationStateMachine != null && smMap.TryGetValue(t.destinationStateMachine, out var dsm))
                copy.destinationStateMachine = dsm;
            copy.isExit = t.isExit;
            return copy;
        }

        private static AnimatorStateTransition CloneStateTransition(AnimatorStateTransition t,
            Dictionary<AnimatorState, AnimatorState> stateMap,
            Dictionary<AnimatorStateMachine, AnimatorStateMachine> smMap)
        {
            var copy = new AnimatorStateTransition { hideFlags = HideFlags.HideInHierarchy };
            copy.conditions = (AnimatorCondition[])t.conditions.Clone();
            copy.canTransitionToSelf = t.canTransitionToSelf;
            copy.duration = t.duration;
            copy.exitTime = t.exitTime;
            copy.hasExitTime = t.hasExitTime;
            copy.hasFixedDuration = t.hasFixedDuration;
            copy.interruptionSource = t.interruptionSource;
            copy.offset = t.offset;
            copy.orderedInterruption = t.orderedInterruption;
            copy.solo = t.solo;
            copy.mute = t.mute;
            copy.isExit = t.isExit;
            if (t.destinationState != null && stateMap.TryGetValue(t.destinationState, out var ds))
                copy.destinationState = ds;
            if (t.destinationStateMachine != null && smMap.TryGetValue(t.destinationStateMachine, out var dsm))
                copy.destinationStateMachine = dsm;
            return copy;
        }

        private static AnimatorState CloneState(BuildContext ctx, AnimatorState src)
        {
            var copy = new AnimatorState
            {
                name = src.name,
                hideFlags = HideFlags.HideInHierarchy,
                speed = src.speed,
                speedParameter = src.speedParameter,
                speedParameterActive = src.speedParameterActive,
                cycleOffset = src.cycleOffset,
                cycleOffsetParameter = src.cycleOffsetParameter,
                cycleOffsetParameterActive = src.cycleOffsetParameterActive,
                mirror = src.mirror,
                mirrorParameter = src.mirrorParameter,
                mirrorParameterActive = src.mirrorParameterActive,
                timeParameter = src.timeParameter,
                timeParameterActive = src.timeParameterActive,
                iKOnFeet = src.iKOnFeet,
                writeDefaultValues = src.writeDefaultValues,
                tag = src.tag,
                motion = CloneMotion(ctx, src.motion),
            };
            ctx.AssetSaver.SaveAsset(copy);
            ObjectRegistry.RegisterReplacedObject(src, copy);

            var beh = src.behaviours;
            var copies = new StateMachineBehaviour[beh.Length];
            for (int i = 0; i < beh.Length; i++)
                copies[i] = beh[i] != null ? CloneBehaviour(ctx, beh[i]) : null;
            copy.behaviours = copies;
            return copy;
        }

        private static Motion CloneMotion(BuildContext ctx, Motion m)
        {
            switch (m)
            {
                case null: return null;
                case AnimationClip clip: return clip; // clips cloned later if needed
                case BlendTree bt: return CloneBlendTree(ctx, bt);
                default: return m;
            }
        }

        private static BlendTree CloneBlendTree(BuildContext ctx, BlendTree src)
        {
            // Use Object.Instantiate so all Unity-internal serialised fields
            // (including those not exposed via public C# API, and any fields
            // Unity may add in future versions) are preserved bit-for-bit.
            // We still walk children explicitly below to ensure nested
            // BlendTrees go through AssetSaver/ObjectRegistry — the children
            // Instantiate auto-clones become unreachable and are GC'd.
            var copy = Object.Instantiate(src);
            copy.name = src.name;
            copy.hideFlags = HideFlags.HideInHierarchy;
            ctx.AssetSaver.SaveAsset(copy);
            ObjectRegistry.RegisterReplacedObject(src, copy);

            var srcChildren = src.children;
            var dstChildren = new ChildMotion[srcChildren.Length];
            for (int i = 0; i < srcChildren.Length; i++)
            {
                dstChildren[i] = new ChildMotion
                {
                    motion = CloneMotion(ctx, srcChildren[i].motion),
                    position = srcChildren[i].position,
                    threshold = srcChildren[i].threshold,
                    timeScale = srcChildren[i].timeScale,
                    cycleOffset = srcChildren[i].cycleOffset,
                    directBlendParameter = srcChildren[i].directBlendParameter,
                    mirror = srcChildren[i].mirror,
                };
            }
            copy.children = dstChildren;
            return copy;
        }

        private static StateMachineBehaviour CloneBehaviour(BuildContext ctx, StateMachineBehaviour src)
        {
            var copy = (StateMachineBehaviour)Object.Instantiate(src);
            copy.hideFlags = HideFlags.HideInHierarchy;
            ctx.AssetSaver.SaveAsset(copy);
            return copy;
        }
    }
}
