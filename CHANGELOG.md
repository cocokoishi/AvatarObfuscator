# Changelog

All notable changes to this package will be documented in this file.

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
