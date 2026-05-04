using System.Collections.Generic;
using UnityEngine;

namespace FuckRipper.AvatarObfuscator.Internal
{
    /// <summary>
    /// Builds the <c>oldFullPath -&gt; newFullPath</c> table by snapshotting the
    /// hierarchy <strong>before</strong> any GameObjects are renamed, then walking
    /// the same transforms again afterwards.
    /// </summary>
    internal static class PathRemapper
    {
        /// <summary>
        /// Returns the path of <paramref name="t"/> relative to <paramref name="root"/>,
        /// using forward slashes. Returns empty string when <paramref name="t"/> ==
        /// <paramref name="root"/>. Returns null if <paramref name="t"/> is not under
        /// <paramref name="root"/>.
        /// </summary>
        public static string GetRelativePath(Transform t, Transform root)
        {
            if (t == null || root == null) return null;
            if (t == root) return "";
            var stack = new List<string>();
            var cur = t;
            while (cur != null && cur != root)
            {
                stack.Add(cur.name);
                cur = cur.parent;
            }
            if (cur != root) return null;
            stack.Reverse();
            return string.Join("/", stack);
        }

        /// <summary>
        /// Snapshots the (instanceID -> oldRelativePath) of every transform under
        /// <paramref name="root"/>. Use before renaming.
        /// </summary>
        public static Dictionary<int, string> Snapshot(Transform root)
        {
            var dict = new Dictionary<int, string>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                dict[t.GetInstanceID()] = GetRelativePath(t, root);
            }
            return dict;
        }

        /// <summary>
        /// After renaming, build oldPath -> newPath by re-querying every transform.
        /// </summary>
        public static Dictionary<string, string> BuildRenameTable(
            Transform root, Dictionary<int, string> oldPaths)
        {
            var renames = new Dictionary<string, string>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (!oldPaths.TryGetValue(t.GetInstanceID(), out var oldPath)) continue;
                var newPath = GetRelativePath(t, root);
                renames[oldPath] = newPath;
            }
            return renames;
        }
    }
}
