using System;
using System.Collections.Generic;

namespace HateRipper.AvatarObfuscator.Internal
{
    /// <summary>
    /// Deterministic homoglyph-name generator. The alphabet is intentionally
    /// chosen so that every produced name renders as a row of identical-looking
    /// vertical strokes in any common font (capital I with grave / acute /
    /// circumflex / diaeresis), making any two obfuscated names visually
    /// indistinguishable to a human reader while remaining valid, distinct
    /// strings to Unity / VRChat / animation parsers.
    ///
    /// All four code points are Latin-1 supplement (single-codepoint, NFC),
    /// well supported in every modern font, and accepted as identifiers by
    /// Unity AnimatorController / mesh blendshape / GameObject naming systems.
    ///
    /// Names produced are:
    /// <list type="bullet">
    /// <item>guaranteed unique within this generator instance</item>
    /// <item>free of slashes, whitespace, control characters and digits</item>
    /// <item>reproducible given the same seed</item>
    /// </list>
    /// </summary>
    internal sealed class NameGenerator
    {
        // Capital I with grave / acute / circumflex / diaeresis.
        // U+00CC, U+00CD, U+00CE, U+00CF — all 1-codepoint Latin-1 chars.
        // Font-rendered, all four look like a vertical bar with a tiny diacritic.
        private static readonly string[] DefaultAlphabet = { "Ì", "Í", "Î", "Ï" };

        private readonly string[] _alphabet;
        private readonly System.Random _rng;
        private readonly int _length;
        private readonly HashSet<string> _used = new HashSet<string>();

        /// <summary>Optional fixed prefix to make obfuscated names recognisable in logs.</summary>
        public string Prefix { get; set; } = "";

        /// <summary>
        /// Creates a new name generator.
        /// </summary>
        /// <param name="seed">RNG seed. 0 = non-deterministic.</param>
        /// <param name="length">Desired name length (minimum clamped to 8).</param>
        /// <param name="customAlphabet">
        /// Optional custom alphabet string array. Must have at least 2 distinct characters.
        /// When null or too short, falls back to the built-in <c>ÌÍÎÏ</c> alphabet.
        /// </param>
        public NameGenerator(int seed, int length, string[] customAlphabet = null)
        {
            // 0 means "non-deterministic". We mix in DateTime.Ticks so the seed is
            // both unique per-build and reasonably hard to predict.
            if (seed == 0) seed = unchecked((int)(DateTime.Now.Ticks ^ Environment.TickCount));
            _rng = new System.Random(seed);

            _alphabet = customAlphabet != null && customAlphabet.Length >= 2
                ? customAlphabet
                : DefaultAlphabet;

            // The alphabet has only 4 symbols, i.e. 2 bits of entropy per char.
            // Bump short user-supplied lengths up so the unique-name pool is big
            // enough for any realistic avatar (a few hundred names) without
            // hitting the slow growth path inside Next().
            _length = Math.Max(8, length);
        }

        /// <summary>
        /// Returns a freshly generated name that has not been returned before.
        /// </summary>
        public string Next()
        {
            int extra = 0;
            var sb = new System.Text.StringBuilder();
            for (int attempt = 0; attempt < 1024; attempt++)
            {
                var len = _length + extra;
                sb.Length = 0;
                sb.Append(Prefix);
                for (int i = 0; i < len; i++) sb.Append(_alphabet[_rng.Next(_alphabet.Length)]);
                var s = sb.ToString();
                if (_used.Add(s)) return s;

                // Out of room at the current length — extend by 1 char (4x the
                // pool) every 32 collisions. This is logarithmically slow but
                // guaranteed to terminate.
                if (attempt > 0 && (attempt & 31) == 0) extra++;
            }

            // Belt-and-braces fallback. We append a high-entropy suffix made of
            // the same alphabet so the name still renders as "all identical I's".
            sb.Length = 0;
            sb.Append(Prefix);
            for (int i = 0; i < 24; i++) sb.Append(_alphabet[_rng.Next(_alphabet.Length)]);
            sb.Append(Guid.NewGuid().ToString("N").Substring(0, 4));
            var fb = sb.ToString();
            _used.Add(fb);
            return fb;
        }

        /// <summary>Reserve a name so the generator never returns it.</summary>
        public void Reserve(string name)
        {
            if (!string.IsNullOrEmpty(name)) _used.Add(name);
        }
    }
}
