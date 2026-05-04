using System.Collections.Generic;

namespace FuckRipper.AvatarObfuscator.Internal
{
    /// <summary>
    /// Names that VRChat itself writes into the animator each frame. They are not
    /// declared in the user's Expression Parameters list, but the animators / state
    /// machines reference them. We must NEVER rename these — VRChat keeps writing
    /// the original name regardless.
    /// </summary>
    internal static class VRChatBuiltins
    {
        /// <summary>
        /// VRC's documented and currently-shipped built-in animator parameters.
        /// Includes deprecated ones (Expression1..16) for safety on older avatars.
        /// </summary>
        public static readonly HashSet<string> BuiltinAnimatorParameters = new HashSet<string>
        {
            // State / mode
            "IsLocal",
            "PreviewMode",
            "TrackingType",
            "VRMode",
            "MuteSelf",
            "InStation",
            "Earmuffs",
            "IsOnFriendsList",
            "AvatarVersion",

            // Voice / viseme
            "Viseme",
            "Voice",

            // Gestures
            "GestureLeft",
            "GestureRight",
            "GestureLeftWeight",
            "GestureRightWeight",

            // Movement
            "AngularY",
            "VelocityX",
            "VelocityY",
            "VelocityZ",
            "VelocityMagnitude",
            "Upright",
            "Grounded",
            "Seated",
            "AFK",

            // Scaling (avatar 3.5+)
            "ScaleModified",
            "ScaleFactor",
            "ScaleFactorInverse",
            "EyeHeightAsMeters",
            "EyeHeightAsPercent",

            // Deprecated, still recognised by some controllers:
            "Supine",
            "GroundProximity",
            "Expression1",  "Expression2",  "Expression3",  "Expression4",
            "Expression5",  "Expression6",  "Expression7",  "Expression8",
            "Expression9",  "Expression10", "Expression11", "Expression12",
            "Expression13", "Expression14", "Expression15", "Expression16",
        };

        /// <summary>
        /// Animator parameter names that are technically not built-in but are
        /// commonly used as well-known integration points (e.g. cross-plugin
        /// "Toggle" sentinels). Kept conservative.
        /// </summary>
        public static readonly HashSet<string> WellKnownPrefixedParameters = new HashSet<string>
        {
        };

        /// <summary>True if the given parameter name must be left untouched.</summary>
        public static bool IsBuiltinParameter(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            if (BuiltinAnimatorParameters.Contains(name)) return true;
            if (WellKnownPrefixedParameters.Contains(name)) return true;
            return false;
        }
    }
}
