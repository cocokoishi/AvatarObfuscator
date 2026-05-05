using System.Collections.Generic;
using System.Linq;

namespace HateRipper.AvatarObfuscator.Internal
{
    /// <summary>
    /// Blendshape names VRChat uses to recognise an avatar as MMD-capable.
    /// Sourced verbatim from Avatar Optimizer's <c>AnimatorParser.cs</c>
    /// MmdBlendShapeNames constant — we only need the *set* of names, so the
    /// table is flattened. When the user opts into MMD compatibility, we keep
    /// any blendshape whose name is in this set.
    /// </summary>
    internal static class MmdBlendShapeNames
    {
        public static readonly HashSet<string> All = new HashSet<string>
        {
            // ===== Mouth =====
            "a", "Ah", "あ",
            "i", "Ch", "い",
            "u", "U",  "う",
            "e", "E",  "え",
            "o", "Oh", "お",
            "Niyari", "Grin", "にやり",
            "Mouse_2", "∧",
            "Wa", "ワ",
            "Omega", "ω",
            "Mouse_1", "▲",
            "MouseUP", "Mouth Horn Raise", "口角上げ",
            "MouseDW", "Mouth Horn Lower", "口角下げ",
            "MouseWD", "Mouth Side Widen", "口横広げ",
            "n", "ん",
            "Niyari2", "にやり２",
            "a 2", "あ２",
            "□",
            "ω□",
            "Smile", "にっこり",
            "Pero", "ぺろっ",
            "Bero-tehe", "てへぺろ",
            "Bero-tehe2", "てへぺろ２",

            // ===== Eyes =====
            "Blink", "まばたき",
            "Blink Happy", "笑い",
            "> <", "Close><", "はぅ",
            "EyeSmall", "Pupil", "瞳小",
            "Wink-c", "Wink 2 Right", "ｳｨﾝｸ２右",
            "Wink-b", "Wink 2", "ウィンク２",
            "Wink", "ウィンク",
            "Wink-a", "Wink Right", "ウィンク右",
            "Howawa", "Calm", "なごみ",
            "Jito-eye", "Stare", "じと目",
            "Ha!!!", "Surprised", "びっくり",
            "Kiri-eye", "Slant", "ｷﾘｯ",
            "EyeHeart", "Heart", "はぁと",
            "EyeStar", "Star Eye", "星目",
            "EyeFunky", "恐ろしい子！",
            "O O", "はちゅ目",
            "EyeSmall-v", "瞳縦潰れ",
            "EyeUnderli", "光下",
            "EyHi-Off", "ハイライト消",
            "EyeRef-off", "映り込み消",

            // ===== Eyebrow =====
            "Smily", "Cheerful", "にこり",
            "Up", "Upper", "上",
            "Down", "Lower", "下",
            "Serious", "真面目",
            "Trouble", "Sadness", "困る",
            "Get angry", "Anger", "怒り",
            "Front", "前",

            // ===== Eyes + Eyebrow Feeling =====
            "Joy",      "喜び",
            "Wao!?",    "わぉ!?",
            "Howawa ω", "なごみω",
            "Wail",     "悲しむ",
            "Hostility","敵意",

            // ===== Other =====
            "Blush", "照れ",
            "ToothAnon", "歯無し下",
            "ToothBnon", "歯無し上",
            "涙",

            // misc
            "しいたけ",
            "なぬ！", "はんっ！", "えー", "睨み", "睨む",
            "白目", "瞳大", "頬染め", "青ざめ",
        };

        /// <summary>True iff the given blendshape name is part of the MMD detection vocabulary.</summary>
        public static bool IsMmdBlendShape(string name) =>
            !string.IsNullOrEmpty(name) && All.Contains(name);

        /// <summary>
        /// Returns how many of the given blendshape names are MMD-significant.
        /// Used to detect the "Body" mesh: if a renderer carries a number of MMD
        /// blendshapes above the threshold, it is the MMD body.
        /// </summary>
        public static int CountMmdBlendShapes(IEnumerable<string> blendShapeNames) =>
            blendShapeNames?.Count(IsMmdBlendShape) ?? 0;
    }
}
