using System.Collections.Generic;
using UnityEngine;

namespace HateRipper.AvatarObfuscator.Internal
{
    /// <summary>
    /// Per-build state shared between all obfuscator passes. Stored on the
    /// <see cref="nadena.dev.ndmf.BuildContext"/> via <c>GetState&lt;T&gt;()</c>.
    ///
    /// Each pass writes the mappings it produces, and later passes consume them.
    /// </summary>
    internal sealed class ObfuscationContext
    {
        /// <summary>Whether the avatar carries an enabled <see cref="AvatarObfuscator"/> component.</summary>
        public bool Enabled;

        /// <summary>Effective options — a copy of the component's options, or defaults if disabled.</summary>
        public ObfuscationOptions Options = new ObfuscationOptions { enabled = false };

        /// <summary>Random-name source. Created lazily after <c>options</c> is known.</summary>
        public NameGenerator NameGen;

        // ------------------------------------------------------------------
        // Parameter mapping
        // ------------------------------------------------------------------
        /// <summary>oldParameterName -> newParameterName, for the union of names across every animator and the VRC parameter list.</summary>
        public readonly Dictionary<string, string> ParameterRenames = new Dictionary<string, string>();

        /// <summary>oldPhysBonePrefix -> newPhysBonePrefix. PhysBones expand the prefix into <c>_IsGrabbed</c> etc; we rewrite the suffixed forms in animators using this map.</summary>
        public readonly Dictionary<string, string> PhysBonePrefixRenames = new Dictionary<string, string>();

        // ------------------------------------------------------------------
        // Hierarchy mapping
        // ------------------------------------------------------------------
        /// <summary>oldFullPath -> newFullPath, both relative to the avatar root and using forward slashes. Includes a "" -> "" entry for the root itself.</summary>
        public readonly Dictionary<string, string> PathRenames = new Dictionary<string, string>();

        // ------------------------------------------------------------------
        // Blendshape mapping
        // ------------------------------------------------------------------
        /// <summary>(skinnedMeshNewPath, oldBlendShapeName) -> newBlendShapeName.</summary>
        public readonly Dictionary<(string Path, string Old), string> BlendShapeRenamesByPath =
            new Dictionary<(string, string), string>();

        /// <summary>
        /// Same as <see cref="BlendShapeRenamesByPath"/>, but keyed by mesh asset.
        /// Useful when an animator binding cannot resolve to a path because the
        /// hierarchy was moved.
        /// </summary>
        public readonly Dictionary<(Mesh Mesh, string Old), string> BlendShapeRenamesByMesh =
            new Dictionary<(Mesh, string), string>();

        // ------------------------------------------------------------------
        // Material replacements (RemapUVTexturePass and AutoMergeSkinnedMeshPass write here)
        // ------------------------------------------------------------------
        /// <summary>Material currently used by the avatar -> the canonical material that survives.</summary>
        public readonly Dictionary<Material, Material> MaterialReplacements = new Dictionary<Material, Material>();

        // ------------------------------------------------------------------
        // Mesh replacements (RemapUVTexturePass writes here so downstream
        // passes know the canonical mesh for each source mesh)
        // ------------------------------------------------------------------
        /// <summary>Original mesh -> UV-remapped clone.</summary>
        public readonly Dictionary<Mesh, Mesh> MeshReplacements = new Dictionary<Mesh, Mesh>();

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------
        // Cached parsed form of Options.skipParametersContaining. Lazily built on
        // first call to ShouldSkipParameter so callers don't have to remember an
        // init step.
        private string[] _skipSubstringsCache;
        private string _skipSubstringsCacheSource;

        /// <summary>
        /// True if the given parameter name should be left untouched because it
        /// matches one of the user-configured skip substrings (in addition to
        /// VRChat built-ins, which are checked separately by VRChatBuiltins).
        /// </summary>
        public bool ShouldSkipParameter(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var raw = Options?.skipParametersContaining ?? "";
            if (!ReferenceEquals(_skipSubstringsCacheSource, raw))
            {
                _skipSubstringsCacheSource = raw;
                var parts = raw.Split(',');
                var list = new List<string>(parts.Length);
                foreach (var p in parts)
                {
                    var t = p.Trim();
                    if (t.Length > 0) list.Add(t);
                }
                _skipSubstringsCache = list.ToArray();
            }
            for (int i = 0; i < _skipSubstringsCache.Length; i++)
            {
                if (name.IndexOf(_skipSubstringsCache[i], System.StringComparison.Ordinal) >= 0)
                    return true;
            }
            return false;
        }

        public string MapParameter(string original)
        {
            if (string.IsNullOrEmpty(original)) return original;
            if (ParameterRenames.TryGetValue(original, out var renamed)) return renamed;
            return original;
        }

        public Material MapMaterial(Material original)
        {
            if (original == null) return null;
            return MaterialReplacements.TryGetValue(original, out var replacement) ? replacement : original;
        }

        public Mesh MapMesh(Mesh original)
        {
            if (original == null) return null;
            return MeshReplacements.TryGetValue(original, out var replacement) ? replacement : original;
        }

        public string MapPath(string original)
        {
            if (original == null) return null;
            if (PathRenames.TryGetValue(original, out var renamed)) return renamed;
            return original;
        }

        public string MapBlendShape(string newSmrPath, string oldBlendShape)
        {
            if (string.IsNullOrEmpty(oldBlendShape)) return oldBlendShape;
            if (BlendShapeRenamesByPath.TryGetValue((newSmrPath, oldBlendShape), out var renamed))
                return renamed;
            return oldBlendShape;
        }
    }
}
