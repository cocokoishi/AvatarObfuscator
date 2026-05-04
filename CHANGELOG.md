# Changelog

All notable changes to this package will be documented in this file.

## [0.2.2] - 2026-05-04

### Fixed
- **Critical: VRAM bloat ~10× on avatars with the texture obfuscation pass enabled.**
  The previous pipeline forced every output Texture2D to uncompressed `RGBA32`
  with a full mip chain, kept the CPU-side copy alive (`makeNoLongerReadable: false`),
  and re-generated the same source texture once per material that referenced it.
  Combined effect on a typical PC avatar: roughly 4× from format (BC7 → RGBA32),
  ×2 from retained CPU copy, and ×k from per-material duplication of shared
  textures, landing at the ~10× users were observing in-game.
  - The new pipeline preserves the source's compressed format (BC7 / DXT5 /
    ASTC / ETC2 / etc.) — VRAM footprint matches the source.
  - The CPU-side copy is dropped via `Apply(makeNoLongerReadable: true)`.
  - The shared-texture cache is now global across the pass — a Texture2D
    referenced by N materials produces 1 obfuscated copy, not N.

### Changed
- **Texture obfuscation strategy switched from FlipX/Y/Both to single-bit
  jitter.** The output bytes still differ from the source (so a ripper cannot
  reverse-image-search them against asset-store originals via SHA / pHash),
  but the visual result is exactly preserved — the perturbation is one LSB
  on one sub-channel of one corner pixel of the smallest mip, well below
  human discrimination threshold.
- **Texture jitter is now lossless when possible** (primary path inspired by
  `com.anatawa12.avatar-optimizer`'s atlas builder): a new Texture2D is
  allocated with the source's exact format / mip count / color space,
  `Graphics.CopyTexture` performs a byte-perfect GPU copy, and the CPU-side
  raw byte buffer is mutated by a single XOR before final upload. The
  output is byte-identical to the source EXCEPT one bit, in the source's
  original compressed format. Zero re-encoding loss, zero VRAM blow-up.
  Crunch / exotic formats fall back to the lossy blit + recompress path.
- Mesh UVs are not touched, and material per-texture scale/offset
  (`_TextureName_ST`) values are kept identical to the source. The previous
  flip-and-reverse-derive-ST math is gone (~230 lines removed). Any number
  of UV channels, arbitrary tiling/offset, normal maps, detail / matcap
  masks, parallax effects all keep working unchanged with no bookkeeping.
- **Material clone upgraded to AAO's pattern** for shader-value preservation:
  - Use `new Material(src)` (the canonical Material copy constructor) instead
    of `Object.Instantiate(src)`.
  - Wrap construction in `EditorMaterialUtility.disableApplyMaterialPropertyDrawers`
    via reflection — same trick AAO uses to suppress lilToon / Poiyomi
    custom-drawer side effects (which can silently retune render queue /
    keywords / dependent properties during a clone).
  - Set `parent = null` on the clone (Unity 2022.1+) to flatten Material
    Variants — without this, a Material Variant clone retains its parent
    reference and `SetTexture` writes wouldn't override inherited values.
- Switched from editor-only `ShaderUtil.GetPropertyCount/Type/Name` to the
  modern `Shader.GetPropertyCount/Type/Name` API (matches TTT's
  `MaterialUtility.GetAllTexture` and is runtime-accessible).
- Color-space detection now prefers `Texture2D.isDataSRGB` (Unity 2022.1+,
  works for runtime / sub-asset / imported textures uniformly) over the
  legacy `AssetImporter.sRGBTexture` lookup.
- HDR texture formats (BC6H, RGBAFloat, RGBAHalf, ASTC HDR, …) are now
  skipped rather than coerced through an LDR ARGB32 pipeline (which would
  have clipped their range or mis-interpreted exponent bits if XORed).
- Cubemaps, 3D textures, render textures and 2D arrays are explicitly
  skipped (pre-existing behaviour, now documented).
- Inspector label "Remap UV Textures" → "Obfuscate Textures". The
  serialized field name `remapUvTextures` is unchanged for backwards
  compatibility with saved scenes.

### Removed
- `FlipMode` enum, deterministic flip selection, and the corresponding
  per-material `_TextureName_ST` reverse-derivation math — unused under
  the new jitter pipeline.

### Other plugin-wide audit fixes
- `SkinnedMeshMerger` now copies UV1 / UV2 / UV3 alongside UV0. The
  previous merger silently dropped those channels, breaking detail masks,
  matcap masks, lightmap UVs, and AudioLink UV channels on merged meshes.
- `SkinnedMeshMerger` releases CPU-side mesh data via
  `UploadMeshData(markNoLongerReadable: true)` after merging — saves
  ~10–30 MB of transient RAM on large merges.
- `SkinnedMeshMerger` now logs a warning when a source SkinnedMeshRenderer
  has a `null` bone slot (previously silently bound the unbound vertex to
  the first bone of the merged mesh).
- `ObfuscateAnimationClipsPass` no longer leaks the plugin's identity into
  the obfuscated clip output. The clip-length padding placeholder used to
  bind to a literal `$ObfuscatorClipLengthDummy$` path string — easily
  greppable in extracted bundles. It is now a homoglyph path generated
  from the same name pool as everything else.
- `AssetCloner.FixTransitionTargets` (a dead empty method plus its dead
  call site) removed. The misleading "deferred" comment that referred to
  it has been corrected.

## [0.2.1] - 2026-05-04

### Fixed
- **Material UV-flip remap rewritten to bake the flip into the material's
  per-texture scale/offset (`_TextureName_ST`) instead of into mesh UV0.**
  The old approach was visually correct only for materials whose textures all
  used UV0 with identity tiling/offset (`(1, 1, 0, 0)`); on complex avatars
  this caused widespread visual breakage:
  - Materials with non-default tiling/offset (detail maps, scrolling textures,
    atlased UVs, lilToon/Poiyomi shaders with tiled normal maps, etc.)
    rendered with the wrong sampling coordinate, producing visible offsets
    and stretching.
  - Textures bound to UV1 / UV2 / UV3 (detail masks, matcap masks, AudioLink
    UVs, dissolve masks) were flipped while their UV channel was not, mirroring
    them.
  - Tangent-space normal maps and parallax effects appeared inverted because
    flipping mesh UV0 inverts the sign of `ddx(uv)` / `ddy(uv)` and flips
    tangent-frame handedness.
  - Cubemaps, render textures, etc. on the same material got UV-flipped
    sampling even though they are not sampled with the material UV.

  The flip is now applied purely on the material side — mesh UVs are never
  modified — so any number of UV channels, arbitrary per-texture tiling/offset
  values, normal maps, parallax, detail masks and matcap masks all stay
  correct. Cubemaps and non-2D textures are left untouched in both pixel data
  and sampling.

### Removed
- The "vertex sharing across submeshes" safety check and the resulting
  console warning are no longer needed (the flip never touches mesh data, so
  there is nothing to conflict).

## [0.2.0] - 2026-05-04

### Changed
- **BREAKING**: Replaced the material-merge feature with a per-material
  UV-flip texture remap (`Remap UV Textures`). Each material on the avatar now
  receives a deterministic flip mode (FlipX / FlipY / FlipBoth) that is baked
  into freshly generated Texture2D assets and into the affected mesh's UV0,
  producing byte-different texture files while preserving the visual result.
  Materials are NEVER merged — every input material produces exactly one output
  material.
- Removed all dependencies on TexTransTool and Avatar Optimizer source code.
  The plugin is now fully self-contained — installing or not installing those
  other plugins does not affect this one.

### Added
- New `Auto-Merge Skinned Mesh` option (default OFF) — a strict-heuristic
  merger for SkinnedMeshRenderers that share a root bone, have no blendshapes,
  no animations referencing their path, and live on a leaf GameObject with no
  components beyond Transform + SkinnedMeshRenderer. Users with Avatar
  Optimizer's Trace and Optimize installed should leave this OFF and let AAO
  handle the merge with full dependency tracking.
- New `Animation Clip Asset Names` option (default ON) — renames AnimationClip
  asset names so a ripper extracting your animator gets clip filenames like
  `ÌÍÎÏÌÍÎÏ` instead of `SitDown_Improved_v2.anim`. VRChat proxy clips are
  kept untouched (they are referenced by name).
- AvatarMask asset names are now also renamed alongside animation clips.
- Inspector footer with project + author links (GitHub / bilibili).

### Removed
- `Merge Identical Materials`, `Atlas-merge Texture Variants`, manual merge
  groups, preferred reference material, and the bundled atlas-builder. These
  were replaced by `Remap UV Textures`.

## [0.1.0] - 2026-05-04

### Added
- Initial release.
- NDMF-based plugin that runs in `BuildPhase.Optimizing` after Avatar Optimizer
  and Modular Avatar (when present).
- `AvatarObfuscator` MonoBehaviour to drop on the avatar root.
- Per-category obfuscation toggles:
  - VRC + animator parameters (with VRChat built-in whitelist)
  - VRC Expression Parameters and Expression Menu (parameter references only,
    user-visible labels are kept)
  - Blendshape names (with optional MMD-name preservation)
  - Hierarchy / GameObject names (with avatar root, Armature and MMD body
    preservation)
  - Mesh asset names (with MMD body preservation)
  - Identical-material merging
  - Animation Clip path / property / object-curve rebinding
  - PhysBone parameter prefix and ContactReceiver parameter renaming, kept in
    lockstep with their suffixed forms (`_IsGrabbed`, `_Angle`, etc.)
- Custom inspector with grouped sections and an error banner that warns when
  rewrite-clips is off while at least one rename option is on.
