using UnityEditor;
using UnityEngine;

namespace HateRipper.AvatarObfuscator.Inspector
{
    [CustomEditor(typeof(AvatarObfuscator))]
    internal sealed class AvatarObfuscatorEditor : UnityEditor.Editor
    {
        private SerializedProperty _options;
        private SerializedProperty _enabled;
        private SerializedProperty _params;
        private SerializedProperty _expParams;
        private SerializedProperty _skipParams;
        private SerializedProperty _flattenStates;
        private SerializedProperty _blendShapes;
        private SerializedProperty _preserveMmd;
        private SerializedProperty _hierarchy;
        private SerializedProperty _preserveMmdBody;
        private SerializedProperty _meshAssets;
        private SerializedProperty _clipNames;
        private SerializedProperty _remapUv;
        private SerializedProperty _autoMergeMesh;
        private SerializedProperty _rewriteClips;
        private SerializedProperty _seed;
        private SerializedProperty _nameLength;
        private SerializedProperty _useCustomAlphabet;
        private SerializedProperty _customChar0;
        private SerializedProperty _customChar1;
        private SerializedProperty _customChar2;
        private SerializedProperty _customChar3;

        private bool _showAdvanced;

        private void OnEnable()
        {
            _options         = serializedObject.FindProperty(nameof(AvatarObfuscator.options));
            _enabled         = _options.FindPropertyRelative(nameof(ObfuscationOptions.enabled));
            _params          = _options.FindPropertyRelative(nameof(ObfuscationOptions.obfuscateParameters));
            _expParams       = _options.FindPropertyRelative(nameof(ObfuscationOptions.obfuscateExpressionParameters));
            _skipParams      = _options.FindPropertyRelative(nameof(ObfuscationOptions.skipParametersContaining));
            _flattenStates   = _options.FindPropertyRelative(nameof(ObfuscationOptions.flattenStatePositions));
            _blendShapes     = _options.FindPropertyRelative(nameof(ObfuscationOptions.obfuscateBlendShapes));
            _preserveMmd     = _options.FindPropertyRelative(nameof(ObfuscationOptions.preserveMmdBlendShapes));
            _hierarchy       = _options.FindPropertyRelative(nameof(ObfuscationOptions.obfuscateHierarchy));
            _preserveMmdBody = _options.FindPropertyRelative(nameof(ObfuscationOptions.preserveMmdBodyObject));
            _meshAssets      = _options.FindPropertyRelative(nameof(ObfuscationOptions.obfuscateMeshAssetNames));
            _clipNames       = _options.FindPropertyRelative(nameof(ObfuscationOptions.obfuscateAnimationClipNames));
            _remapUv         = _options.FindPropertyRelative(nameof(ObfuscationOptions.remapUvTextures));
            _autoMergeMesh   = _options.FindPropertyRelative(nameof(ObfuscationOptions.autoMergeSkinnedMesh));
            _rewriteClips    = _options.FindPropertyRelative(nameof(ObfuscationOptions.rewriteAnimationClips));
            _seed            = _options.FindPropertyRelative(nameof(ObfuscationOptions.seed));
            _nameLength      = _options.FindPropertyRelative(nameof(ObfuscationOptions.generatedNameLength));
            _useCustomAlphabet = _options.FindPropertyRelative(nameof(ObfuscationOptions.useCustomAlphabet));
            _customChar0     = _options.FindPropertyRelative(nameof(ObfuscationOptions.customChar0));
            _customChar1     = _options.FindPropertyRelative(nameof(ObfuscationOptions.customChar1));
            _customChar2     = _options.FindPropertyRelative(nameof(ObfuscationOptions.customChar2));
            _customChar3     = _options.FindPropertyRelative(nameof(ObfuscationOptions.customChar3));
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.LabelField("Avatar Obfuscator (NDMF)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Non-destructive. Runs at upload / play, on the cloned avatar that NDMF gives us. " +
                "Removing this component fully reverts the build.\n\n" +
                "Recommended pipeline placement: AFTER Avatar Optimizer / TexTransTool / Modular Avatar.",
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
                    RightAlignedToggle(_flattenStates, "Flatten State Positions (USELESS)",
                        "Move every state, sub-state-machine and Entry / Exit / Any State / parent-link " +
                        "node onto position (0, 0, 0) in every animator state machine. The Animator " +
                        "window then shows an unreadable pile of overlapping nodes — a ripper trying to " +
                        "reverse-engineer your avatar's logic loses all visual layout cues.\n\n" +
                        "Position values are pure editor-only cosmetic data; runtime behaviour is " +
                        "completely unchanged. Safe to leave on.");
                    EditorGUILayout.PropertyField(_skipParams, new GUIContent("Skip Parameters Containing",
                        "Comma-separated substrings (case-sensitive). Parameter names that CONTAIN any of " +
                        "these stay plaintext, in addition to VRChat built-ins. Use this for face-tracking " +
                        "bridges, OSC tools, custom shaders, etc. that read parameter names as strings.\n\n" +
                        "Default: 'FT,eye'."));
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

                // [Hidden] Texture obfuscation and Mesh Merge UI — features are not
                // production-ready. Backend logic is preserved; only the inspector
                // controls are suppressed so users cannot enable them.
                //
                // EditorGUILayout.Space();
                // Section("Texture");
                // RightAlignedToggle(_remapUv, "Obfuscate Textures (not working)",
                //     "For every Texture2D on every material, generate a byte-different copy by " +
                //     "rearranging UV islands in lockstep on both the texture pixels and the " +
                //     "mesh UVs — the same principle as TexTransTool's atlas (even a one-texture " +
                //     "atlas group still repacks islands). Each island gets a deterministic " +
                //     "within-bbox FlipH / FlipV / Rot180 transform. The output is recompressed " +
                //     "back to the source's compressed format (BC7 / DXT5 / ASTC / ETC2 / etc.) " +
                //     "so runtime VRAM and bundle size match the original.\n\n" +
                //     "A ripper extracting your avatar can no longer match its textures against " +
                //     "asset-store originals by content hash. Material per-texture scale/offset " +
                //     "values are not touched. Cubemaps, 3D textures, render textures and HDR " +
                //     "formats are skipped.");
                //
                // EditorGUILayout.Space();
                // Section("Mesh Merge (Optional)");
                // RightAlignedToggle(_autoMergeMesh, "Auto-Merge Skinned Mesh",
                //     "Optional draw-call optimisation. Merges SkinnedMeshRenderers that share a root " +
                //     "bone and pass a strict safety profile (no blendshapes, no animations referencing " +
                //     "their path, no special components on the GameObject).\n\n" +
                //     "OFF by default — this is NOT an obfuscation feature. If you also have Avatar " +
                //     "Optimizer's Trace and Optimize installed, leave this off and let AAO do the merge.");

                EditorGUILayout.Space();
                Section("Animation");
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

                RightAlignedToggle(_clipNames, "Animation Clip Asset Names",
                    "Rename animation clip asset names to homoglyph nonsense, so a ripper extracting " +
                    "your animator gets clip filenames like 'ÌÍÎÏÌÍÎÏ' instead of " +
                    "'SitDown_Improved_v2.anim'. VRChat proxy animations are kept untouched (they are " +
                    "referenced by name).");

                EditorGUILayout.Space();
                _showAdvanced = EditorGUILayout.Foldout(_showAdvanced, "Advanced", true);
                if (_showAdvanced)
                {
                    EditorGUI.indentLevel++;
                    RightAlignedToggle(_useCustomAlphabet, "Custom Obfuscation Symbols",
                        "Use a custom set of 4 obfuscation symbols instead of the built-in ÌÍÎÏ alphabet. " +
                        "The default custom symbols (II, ll, Il, lI) look nearly identical in most fonts.");
                    using (new EditorGUI.DisabledScope(!_useCustomAlphabet.boolValue))
                    {
                        EditorGUI.indentLevel++;
                        var charRect = EditorGUI.IndentedRect(
                            EditorGUILayout.GetControlRect(true, EditorGUIUtility.singleLineHeight));
                        float labelW = 80f;
                        float boxW = 28f;
                        float gap = 6f;
                        var labelRect2 = new Rect(charRect.x, charRect.y, labelW, charRect.height);
                        EditorGUI.LabelField(labelRect2, new GUIContent("Symbols",
                            "The 4 characters used to build obfuscated names. Each box holds one character."));
                        float startX = charRect.x + labelW + 4f;
                        SerializedProperty[] charProps = { _customChar0, _customChar1, _customChar2, _customChar3 };
                        int prevIndent = EditorGUI.indentLevel;
                        EditorGUI.indentLevel = 0;
                        for (int ci = 0; ci < 4; ci++)
                        {
                            var bx = new Rect(startX + ci * (boxW + gap), charRect.y, boxW, charRect.height);
                            EditorGUI.BeginProperty(bx, GUIContent.none, charProps[ci]);
                            EditorGUI.BeginChangeCheck();
                            string val = EditorGUI.TextField(bx, charProps[ci].stringValue);
                            if (EditorGUI.EndChangeCheck())
                            {
                                charProps[ci].stringValue = val.Length > 2 ? val.Substring(0, 2) : val;
                            }
                            EditorGUI.EndProperty();
                        }
                        EditorGUI.indentLevel = prevIndent;
                        EditorGUI.indentLevel--;
                    }
                    EditorGUILayout.PropertyField(_seed, new GUIContent("Seed",
                        "Optional fixed seed for reproducible obfuscation. 0 = random per build."));
                    EditorGUILayout.PropertyField(_nameLength, new GUIContent("Generated Name Length",
                        "Length of generated obfuscated names. Each character is one of the 4 symbols " +
                        "(2 bits of entropy), so 24 chars = 48 bits = ~280 trillion unique names."));
                    EditorGUI.indentLevel--;
                }
            }

            // ----------------------------------------------------------------
            // Project / author footer.
            // ----------------------------------------------------------------
            EditorGUILayout.Space();
            DrawFooterLinks();

            serializedObject.ApplyModifiedProperties();
        }

        private static void DrawFooterLinks()
        {
            var rect = EditorGUILayout.GetControlRect(false, 1);
            rect.height = 1;
            EditorGUI.DrawRect(rect, new Color(0.5f, 0.5f, 0.5f, 0.3f));

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Project", EditorStyles.miniLabel, GUILayout.Width(60));
                if (GUILayout.Button(AvatarObfuscator.ProjectUrl, EditorStyles.linkLabel))
                    Application.OpenURL(AvatarObfuscator.ProjectUrl);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Author", EditorStyles.miniLabel, GUILayout.Width(60));
                if (GUILayout.Button(AvatarObfuscator.AuthorUrl, EditorStyles.linkLabel))
                    Application.OpenURL(AvatarObfuscator.AuthorUrl);
            }
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

            var rect = EditorGUILayout.GetControlRect(true, EditorGUIUtility.singleLineHeight);

            var labelRect = rect;
            labelRect.width = rect.width - ToggleWidth;

            var toggleRect = new Rect(
                rect.xMax - ToggleWidth,
                rect.y,
                ToggleWidth,
                rect.height);

            EditorGUI.LabelField(labelRect, new GUIContent(label, tooltip));

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
