using System.Collections.Generic;
using UnityEditor.Animations;
using UnityEngine;

namespace HateRipper.AvatarObfuscator.Internal
{
    /// <summary>
    /// Recursive walker that yields every <see cref="AnimatorState"/>,
    /// <see cref="AnimatorStateMachine"/>, <see cref="AnimatorTransitionBase"/>,
    /// <see cref="BlendTree"/> and <see cref="StateMachineBehaviour"/> reachable
    /// from a given <see cref="AnimatorController"/>. Used by both the parameter
    /// and animation passes.
    /// </summary>
    internal static class AnimatorWalker
    {
        public static IEnumerable<AnimatorStateMachine> AllStateMachines(AnimatorController controller)
        {
            if (controller == null) yield break;
            foreach (var layer in controller.layers)
            {
                if (layer.stateMachine == null) continue;
                foreach (var sm in AllStateMachines(layer.stateMachine)) yield return sm;
            }
        }

        public static IEnumerable<AnimatorStateMachine> AllStateMachines(AnimatorStateMachine root)
        {
            if (root == null) yield break;
            yield return root;
            foreach (var child in root.stateMachines)
            {
                foreach (var sm in AllStateMachines(child.stateMachine)) yield return sm;
            }
        }

        public static IEnumerable<AnimatorState> AllStates(AnimatorController controller)
        {
            foreach (var sm in AllStateMachines(controller))
            foreach (var s in sm.states)
                yield return s.state;
        }

        public static IEnumerable<AnimatorState> AllStates(AnimatorStateMachine root)
        {
            foreach (var sm in AllStateMachines(root))
            foreach (var s in sm.states)
                yield return s.state;
        }

        public static IEnumerable<AnimatorTransitionBase> AllTransitions(AnimatorController controller)
        {
            foreach (var sm in AllStateMachines(controller))
            {
                foreach (var t in sm.entryTransitions) yield return t;
                foreach (var t in sm.anyStateTransitions) yield return t;
                foreach (var child in sm.stateMachines)
                {
                    foreach (var t in sm.GetStateMachineTransitions(child.stateMachine))
                        yield return t;
                }
                foreach (var st in sm.states)
                foreach (var t in st.state.transitions)
                    yield return t;
            }
        }

        public static IEnumerable<BlendTree> AllBlendTrees(AnimatorController controller)
        {
            foreach (var st in AllStates(controller))
            {
                if (st.motion is BlendTree bt)
                    foreach (var inner in AllBlendTrees(bt))
                        yield return inner;
            }
        }

        private static IEnumerable<BlendTree> AllBlendTrees(BlendTree root)
        {
            yield return root;
            foreach (var child in root.children)
            {
                if (child.motion is BlendTree childTree)
                    foreach (var inner in AllBlendTrees(childTree))
                        yield return inner;
            }
        }

        public static IEnumerable<StateMachineBehaviour> AllBehaviours(AnimatorController controller)
        {
            foreach (var sm in AllStateMachines(controller))
            {
                foreach (var b in sm.behaviours) if (b != null) yield return b;
                foreach (var s in sm.states)
                    foreach (var b in s.state.behaviours)
                        if (b != null) yield return b;
            }
        }

        public static IEnumerable<AnimationClip> AllClips(AnimatorController controller)
        {
            foreach (var st in AllStates(controller))
            foreach (var clip in MotionClips(st.motion))
                yield return clip;

            // override-only motions on synced layers
            for (int i = 0; i < controller.layers.Length; i++)
            {
                var layer = controller.layers[i];
                if (layer.syncedLayerIndex < 0) continue;
                var src = controller.layers[layer.syncedLayerIndex];
                foreach (var st in AllStates(src.stateMachine))
                {
                    var motion = layer.GetOverrideMotion(st);
                    foreach (var clip in MotionClips(motion))
                        yield return clip;
                }
            }
        }

        public static IEnumerable<AnimationClip> MotionClips(Motion motion)
        {
            switch (motion)
            {
                case null: yield break;
                case AnimationClip clip: yield return clip; yield break;
                case BlendTree bt:
                    foreach (var c in bt.children)
                        foreach (var inner in MotionClips(c.motion))
                            yield return inner;
                    yield break;
            }
        }
    }
}
