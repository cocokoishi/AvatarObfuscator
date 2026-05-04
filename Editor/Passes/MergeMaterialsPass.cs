using System.Collections.Generic;
using System.IO;
using System.Linq;
using FuckRipper.AvatarObfuscator.Internal;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;

namespace FuckRipper.AvatarObfuscator.Passes
{
    /// <summary>
    /// Detects materials whose serialized properties are byte-for-byte identical and
    /// rewires every Renderer to share a single canonical instance. Material
    /// references inside AnimationClips are rewritten in
    /// <see cref="ObfuscateAnimationClipsPass"/> using the
    /// <see cref="ObfuscationContext.MaterialReplacements"/> table this pass populates.
    /// </summary>
    internal sealed class MergeMaterialsPass : Pass<MergeMaterialsPass>
    {
        public override string DisplayName => "Avatar Obfuscator: merge identical materials";

        protected override void Execute(BuildContext context)
        {
            var state = context.GetState<ObfuscationContext>();
            if (!state.Enabled || !state.Options.mergeIdenticalMaterials) return;

            // Collect every material currently on every Renderer under the avatar.
            var allMaterials = new HashSet<Material>();
            foreach (var renderer in context.AvatarRootObject.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var m in renderer.sharedMaterials)
                    if (m != null) allMaterials.Add(m);
            }

            // Bucket by structural hash, then verify byte-equality within each bucket
            var buckets = new Dictionary<string, List<Material>>();
            foreach (var mat in allMaterials)
            {
                var hash = StructuralHash(mat);
                if (!buckets.TryGetValue(hash, out var list))
                    buckets[hash] = list = new List<Material>();
                list.Add(mat);
            }

            foreach (var bucket in buckets.Values)
            {
                if (bucket.Count <= 1) continue;
                // Pick the asset-on-disk one as canonical when possible (so persistent
                // references win). Otherwise just take the first.
                Material canonical = bucket.FirstOrDefault(m => AssetDatabase.Contains(m)) ?? bucket[0];
                foreach (var dup in bucket)
                {
                    if (dup == canonical) continue;
                    if (!StructurallyEqual(canonical, dup)) continue;
                    state.MaterialReplacements[dup] = canonical;
                }
            }

            if (state.MaterialReplacements.Count == 0) return;

            // Rewire renderers immediately. Animation curves get rewritten in the clip pass.
            foreach (var renderer in context.AvatarRootObject.GetComponentsInChildren<Renderer>(true))
            {
                var mats = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] != null && state.MaterialReplacements.TryGetValue(mats[i], out var rep))
                    {
                        mats[i] = rep;
                        changed = true;
                    }
                }
                if (changed) renderer.sharedMaterials = mats;
            }
        }

        // ------------------------------------------------------------------
        // Structural equality
        // ------------------------------------------------------------------
        private static string StructuralHash(Material m)
        {
            if (m == null || m.shader == null) return "<null>";
            using (var so = new SerializedObject(m))
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(m.shader.name);
                w.Write(m.renderQueue);
                w.Write(m.enableInstancing);
                if (m.shaderKeywords != null)
                {
                    var keywords = (string[])m.shaderKeywords.Clone();
                    System.Array.Sort(keywords, System.StringComparer.Ordinal);
                    foreach (var k in keywords) w.Write(k);
                }
                var prop = so.GetIterator();
                while (prop.NextVisible(true))
                {
                    if (prop.propertyPath == "m_Name") continue; // ignore the asset name
                    HashProperty(w, prop);
                }
                using (var sha = System.Security.Cryptography.SHA1.Create())
                {
                    var hash = sha.ComputeHash(ms.ToArray());
                    return System.Convert.ToBase64String(hash);
                }
            }
        }

        private static void HashProperty(BinaryWriter w, SerializedProperty p)
        {
            w.Write(p.propertyPath);
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer:        w.Write(p.intValue); break;
                case SerializedPropertyType.Boolean:        w.Write(p.boolValue); break;
                case SerializedPropertyType.Float:          w.Write(p.floatValue); break;
                case SerializedPropertyType.String:         w.Write(p.stringValue ?? ""); break;
                case SerializedPropertyType.Color:          w.Write(p.colorValue.ToString()); break;
                case SerializedPropertyType.Vector2:        w.Write(p.vector2Value.ToString()); break;
                case SerializedPropertyType.Vector3:        w.Write(p.vector3Value.ToString()); break;
                case SerializedPropertyType.Vector4:        w.Write(p.vector4Value.ToString()); break;
                case SerializedPropertyType.ObjectReference:
                    var instId = p.objectReferenceInstanceIDValue;
                    var path = AssetDatabase.GetAssetPath(EditorUtility.InstanceIDToObject(instId));
                    var guid = string.IsNullOrEmpty(path) ? "" : AssetDatabase.AssetPathToGUID(path);
                    w.Write(guid);
                    w.Write(instId);
                    break;
                case SerializedPropertyType.Enum:           w.Write(p.enumValueIndex); break;
                default: break;
            }
        }

        private static bool StructurallyEqual(Material a, Material b) =>
            a != null && b != null && StructuralHash(a) == StructuralHash(b);
    }
}
