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
        private SerializedProperty _materialNames;
        private SerializedProperty _textureNames;
        private SerializedProperty _audioNames;
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

        private bool _showOptions = false;
        private bool _showAdvanced;
        private bool _showContactMe;

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
            _materialNames   = _options.FindPropertyRelative(nameof(ObfuscationOptions.obfuscateMaterialAssetNames));
            _textureNames    = _options.FindPropertyRelative(nameof(ObfuscationOptions.obfuscateTextureAssetNames));
            _audioNames      = _options.FindPropertyRelative(nameof(ObfuscationOptions.obfuscateAudioClipAssetNames));
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

            // --- Logo header ---
            var logoRect = EditorGUILayout.GetControlRect(false, 54);
            EditorGUI.DrawRect(logoRect, new Color(0.08f, 0.1f, 0.16f, 0.92f));
            EditorGUI.DrawRect(new Rect(logoRect.x, logoRect.yMax - 1, logoRect.width, 1),
                new Color(0.3f, 0.6f, 1f, 0.4f));

            // Badge
            var badgeRect = new Rect(logoRect.x + 6, logoRect.y + 6, 42, 42);
            EditorGUI.DrawRect(badgeRect, new Color(0.12f, 0.22f, 0.38f, 0.8f));
            var badgeStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 20, alignment = TextAnchor.MiddleCenter };
            badgeStyle.normal.textColor = new Color(0.5f, 0.85f, 1f);
            EditorGUI.LabelField(badgeRect, "AO", badgeStyle);

            // Title
            var titleStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 16 };
            titleStyle.normal.textColor = Color.white;
            EditorGUI.LabelField(new Rect(logoRect.x + 54, logoRect.y + 5, logoRect.width - 60, 24),
                "Avatar Obfuscator", titleStyle);

            // Version (top-right)
            var verStyle = new GUIStyle(EditorStyles.miniLabel) { fontSize = 9, alignment = TextAnchor.UpperRight };
            verStyle.normal.textColor = new Color(0.4f, 0.6f, 0.8f, 0.6f);
            EditorGUI.LabelField(new Rect(logoRect.x + logoRect.width - 54, logoRect.y + 7, 48, 14),
                "v0.4.4", verStyle);

            // Subtitle (short, always visible)
            var subStyle = new GUIStyle(EditorStyles.miniLabel) { fontSize = 10, wordWrap = true };
            subStyle.normal.textColor = new Color(0.5f, 0.75f, 1f, 0.65f);
            var shortLabel = "Non-destructive avatar protection for VRChat.";
            var subContent = new GUIContent(shortLabel);
            var subHeight = subStyle.CalcHeight(subContent, logoRect.width - 60);
            if (subHeight < 16) subHeight = 16;
            EditorGUI.LabelField(new Rect(logoRect.x + 54, logoRect.y + 30, logoRect.width - 60, subHeight),
                shortLabel, subStyle);

            // Tooltip registration — hovering the logo shows a small popup
            var tipText = "I hate rippers and hackers so i do this to confuse them. Just add a passcode prefab to prevent hotswap.";
            EditorGUI.LabelField(logoRect, new GUIContent("", tipText));

            EditorGUILayout.Space(2);

            RightAlignedToggle(_enabled, "Enable Obfuscation",
                "Master switch. When off, the plugin behaves as if the component were absent.");

            // Force-close the options foldout when the master switch is off,
            // so the inspector stays tidy when obfuscation is disabled.
            if (!_enabled.boolValue)
                _showOptions = false;

            EditorGUILayout.Space();

            // The foldout itself is greyed and non-interactive when master is off.
            using (new EditorGUI.DisabledScope(!_enabled.boolValue))
            {
                _showOptions = EditorGUILayout.Foldout(_showOptions, "Obfuscation Settings", true);
            }

            if (_showOptions)
            {
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
                    // [Hidden] Flatten State Positions — disabled from the inspector.
                    // Backend logic is preserved; only the UI control is suppressed.
                    // RightAlignedToggle(_flattenStates, "Flatten State Positions (USELESS)",
                    //     "Move every state, ...");
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

                EditorGUILayout.Space();
                Section("Material / Texture / Audio");
                RightAlignedToggle(_materialNames, "Material Asset Names",
                    "Clone every Material referenced by renderers and rename the clone. The user's " +
                    "source .mat asset is never modified. Animation clip object-reference curves are " +
                    "redirected to the cloned materials automatically. Safe — Unity does not look up " +
                    "materials by name at runtime.");
                RightAlignedToggle(_textureNames, "Texture Asset Names",
                    "Clone every Texture2D referenced by materials and rename the clone. Only Texture2D " +
                    "is handled — Cubemap, RenderTexture, Texture3D and Texture2DArray are passed through " +
                    "untouched. When this is ON, the owning materials are also cloned (without renaming, " +
                    "unless 'Material Asset Names' is also ON) so the texture pointer can be swapped " +
                    "without mutating your .mat asset.");
                RightAlignedToggle(_audioNames, "AudioClip Asset Names",
                    "Clone every AudioClip referenced by AudioSource.clip and VRC_AnimatorPlayAudio.Clips, " +
                    "and rename the clone. The clone holds the same PCM samples as the source — for " +
                    "uncompressed sources this is identical bytes; for Vorbis/MP3-compressed sources the " +
                    "bundle grows because the PCM round-trip removes the compression. Turn OFF if bundle " +
                    "size matters more than name obfuscation for your audio.");

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
                EditorGUILayout.HelpBox(
                    "Please keep the default options, otherwise the obfuscated avatar may not work.",
                    MessageType.Warning);
            }

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

                EditorGUILayout.Space();
                _showContactMe = EditorGUILayout.Foldout(_showContactMe, "Contact me", true);
                if (_showContactMe)
                {
                    EditorGUI.indentLevel++;
                    if (GUILayout.Button("Project: " + AvatarObfuscator.ProjectUrl, EditorStyles.linkLabel))
                        Application.OpenURL(AvatarObfuscator.ProjectUrl);
                    if (GUILayout.Button("Author: " + AvatarObfuscator.AuthorUrl, EditorStyles.linkLabel))
                        Application.OpenURL(AvatarObfuscator.AuthorUrl);
                    if (GUILayout.Button("Special Thanks to PuddingKC(布丁丿Pudding)", EditorStyles.linkLabel))
                        Application.OpenURL("https://github.com/Null-K/");
                    if (GUILayout.Button("Thanks to 深度求索", EditorStyles.linkLabel))
                        Application.OpenURL("https://github.com/deepseek-ai");
                    EditorGUI.indentLevel--;
                }
                EditorGUI.indentLevel--;
            }

            // ----------------------------------------------------------------
            // Footer links live exclusively under Advanced -> Contact me now.
            // ----------------------------------------------------------------

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
