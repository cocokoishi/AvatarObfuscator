using UnityEditor;
using UnityEngine;

namespace FuckRipper.AvatarObfuscator.Inspector
{
    [CustomEditor(typeof(AvatarObfuscator))]
    internal sealed class AvatarObfuscatorEditor : UnityEditor.Editor
    {
        private SerializedProperty _options;

        // Cached child props
        private SerializedProperty _enabled;
        private SerializedProperty _params;
        private SerializedProperty _expParams;
        private SerializedProperty _blendShapes;
        private SerializedProperty _preserveMmd;
        private SerializedProperty _hierarchy;
        private SerializedProperty _preserveMmdBody;
        private SerializedProperty _meshAssets;
        private SerializedProperty _mergeMaterials;
        private SerializedProperty _rewriteClips;
        private SerializedProperty _seed;
        private SerializedProperty _nameLength;

        private bool _showAdvanced = false;

        private void OnEnable()
        {
            _options = serializedObject.FindProperty(nameof(AvatarObfuscator.options));
            _enabled        = _options.FindPropertyRelative(nameof(ObfuscationOptions.enabled));
            _params         = _options.FindPropertyRelative(nameof(ObfuscationOptions.obfuscateParameters));
            _expParams      = _options.FindPropertyRelative(nameof(ObfuscationOptions.obfuscateExpressionParameters));
            _blendShapes    = _options.FindPropertyRelative(nameof(ObfuscationOptions.obfuscateBlendShapes));
            _preserveMmd    = _options.FindPropertyRelative(nameof(ObfuscationOptions.preserveMmdBlendShapes));
            _hierarchy      = _options.FindPropertyRelative(nameof(ObfuscationOptions.obfuscateHierarchy));
            _preserveMmdBody= _options.FindPropertyRelative(nameof(ObfuscationOptions.preserveMmdBodyObject));
            _meshAssets     = _options.FindPropertyRelative(nameof(ObfuscationOptions.obfuscateMeshAssetNames));
            _mergeMaterials = _options.FindPropertyRelative(nameof(ObfuscationOptions.mergeIdenticalMaterials));
            _rewriteClips   = _options.FindPropertyRelative(nameof(ObfuscationOptions.rewriteAnimationClips));
            _seed           = _options.FindPropertyRelative(nameof(ObfuscationOptions.seed));
            _nameLength     = _options.FindPropertyRelative(nameof(ObfuscationOptions.generatedNameLength));
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.LabelField("Avatar Obfuscator (NDMF)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Non-destructive. Runs at upload / play, on the cloned avatar that NDMF gives us. " +
                "Removing this component fully reverts the build.\n\n" +
                "Recommended pipeline placement: AFTER Avatar Optimizer.",
                MessageType.None);

            EditorGUILayout.Space();

            RightAlignedToggle(_enabled, "Enable Obfuscation",
                "Master switch. When off, the plugin behaves as if the component were absent.");

            using (new EditorGUI.DisabledScope(!_enabled.boolValue))
            {
                EditorGUILayout.Space();
                Section("Parameters & Animator");
                RightAlignedToggle(_params, "Animator Parameters",
                    "Rename animator parameters in every playable layer + their references " +
                    "(transitions, blend trees, parameter drivers). VRChat built-in parameters are kept.");
                using (new EditorGUI.DisabledScope(!_params.boolValue))
                {
                    EditorGUI.indentLevel++;
                    RightAlignedToggle(_expParams, "VRC Expression Parameters",
                        "Rename the parameter entries in the VRC Expression Parameters list and rewrite " +
                        "the parameter references inside the Expression Menu. The user-visible labels in " +
                        "the menu are kept — only the parameter names they reference are renamed.");
                    EditorGUI.indentLevel--;
                }

                EditorGUILayout.Space();
                Section("Mesh / Blendshape");
                RightAlignedToggle(_blendShapes, "Blendshape Names",
                    "Clone every Skinned Mesh and rename its blendshape keys. Animation curves are " +
                    "rewritten to match.");
                using (new EditorGUI.DisabledScope(!_blendShapes.boolValue))
                {
                    EditorGUI.indentLevel++;
                    RightAlignedToggle(_preserveMmd, "Preserve MMD Blendshapes",
                        "Keep MMD-recognised blendshape names (Japanese / EN aliases) untouched so the " +
                        "avatar still works in MMD worlds. Recommended ON.");
                    EditorGUI.indentLevel--;
                }

                RightAlignedToggle(_meshAssets, "Mesh Asset Names",
                    "Rename the underlying Mesh asset names. The MMD body mesh is preserved when MMD " +
                    "compatibility is on.");

                EditorGUILayout.Space();
                Section("Hierarchy");
                RightAlignedToggle(_hierarchy, "GameObject Names",
                    "Rename every GameObject under the avatar root. Humanoid bones, the Armature, the " +
                    "avatar root and (when MMD compat is on) the MMD Body GameObject are preserved.");
                using (new EditorGUI.DisabledScope(!_hierarchy.boolValue))
                {
                    EditorGUI.indentLevel++;
                    RightAlignedToggle(_preserveMmdBody, "Preserve MMD 'Body' Object",
                        "When the MMD body mesh is detected, keep the GameObject name so MMD worlds can find it.");
                    EditorGUI.indentLevel--;
                }

                EditorGUILayout.Space();
                Section("Materials");
                RightAlignedToggle(_mergeMaterials, "Merge Identical Materials",
                    "Detect materials whose serialized properties are byte-for-byte identical and replace " +
                    "duplicates with a single canonical asset. Reduces draw calls without changing the look.");

                EditorGUILayout.Space();
                Section("Animation Clips");
                RightAlignedToggle(_rewriteClips, "Rewrite Animation Clip Bindings",
                    "Required whenever any of the rename options above is on. Walks every reachable " +
                    "AnimationClip and rewrites its path / property bindings. Keep ON unless you are " +
                    "deliberately disabling all renames.");
                if (_rewriteClips.boolValue == false &&
                    (_params.boolValue || _blendShapes.boolValue || _hierarchy.boolValue))
                {
                    EditorGUILayout.HelpBox(
                        "Rewriting animation clips is OFF while at least one rename option is ON. " +
                        "Animations referencing renamed paths / parameters / blendshapes WILL break.",
                        MessageType.Error);
                }

                EditorGUILayout.Space();
                _showAdvanced = EditorGUILayout.Foldout(_showAdvanced, "Advanced", true);
                if (_showAdvanced)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(_seed, new GUIContent("Seed",
                        "Optional fixed seed for reproducible obfuscation. 0 = random per build."));
                    EditorGUILayout.PropertyField(_nameLength, new GUIContent("Generated Name Length",
                        "Length of generated obfuscated names. Each character is one of {Ì Í Î Ï} " +
                        "(2 bits of entropy), so 24 chars = 48 bits = ~280 trillion unique names."));
                    EditorGUI.indentLevel--;
                }
            }

            serializedObject.ApplyModifiedProperties();
        }

        // ------------------------------------------------------------------
        // Layout helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Draws a toggle property where the LABEL is left-aligned (and respects
        /// EditorGUI.indentLevel) but the CHECKBOX is pinned to the right edge of
        /// the inspector. Looks like Unity's standard "Static" / "Mesh Bake"
        /// rows in built-in components.
        /// </summary>
        private static void RightAlignedToggle(SerializedProperty prop, string label, string tooltip)
        {
            const float ToggleWidth = 16f;

            // GetControlRect already accounts for indentLevel.
            var rect = EditorGUILayout.GetControlRect(true, EditorGUIUtility.singleLineHeight);

            // Label: takes everything up to where the toggle starts.
            var labelRect = rect;
            labelRect.width = rect.width - ToggleWidth;

            // Toggle: pinned hard to the right edge, no indent.
            var toggleRect = new Rect(
                rect.xMax - ToggleWidth,
                rect.y,
                ToggleWidth,
                rect.height);

            EditorGUI.LabelField(labelRect, new GUIContent(label, tooltip));

            // Bypass indentLevel for the toggle itself so it always sits flush right.
            int prevIndent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;
            try
            {
                EditorGUI.BeginProperty(toggleRect, GUIContent.none, prop);
                EditorGUI.BeginChangeCheck();
                bool newValue = EditorGUI.Toggle(toggleRect, prop.boolValue);
                if (EditorGUI.EndChangeCheck())
                    prop.boolValue = newValue;
                EditorGUI.EndProperty();
            }
            finally
            {
                EditorGUI.indentLevel = prevIndent;
            }
        }

        private static void Section(string title)
        {
            var rect = EditorGUILayout.GetControlRect(false, 1);
            rect.height = 1;
            EditorGUI.DrawRect(rect, new Color(0.5f, 0.5f, 0.5f, 0.3f));
            EditorGUILayout.LabelField(title, EditorStyles.miniBoldLabel);
        }
    }
}
