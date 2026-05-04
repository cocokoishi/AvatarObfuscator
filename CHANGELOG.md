# Changelog

All notable changes to this package will be documented in this file.

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
