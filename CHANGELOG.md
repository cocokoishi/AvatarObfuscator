# Changelog

All notable changes to this package will be documented in this file.

## [0.2.9] - 2026-05-05

### Changed
- **Obfuscate Textures** option now defaults to OFF and marked as "(not working)" in
  the inspector — the v0.2.7 UV island rearrangement fix is not yet complete, and
  texture obfuscation may still produce garbled output in some cases. Users who want
  to try it can still enable it manually.

## [0.2.7] - 2026-05-05

### Fixed
- **Critical: scrambled textures on the rendered avatar after the v0.2.6
  rearrangement pass.** Three independent bugs in `UVTextureRemapper`
  were producing 1-pixel offsets, mismatched sample-vs-content shifts,
  and shared-texture target collisions — all visible as garbled texture
  content on the mesh while the painted rearranged-texture file looked
  correct on its own.
  - **Pixel rounding mismatch in `PaintIsland`.** The destination origin
    was computed as `round((Bbox.min + Translation) * size)` while the
    source rect used `floor(Bbox.min * size)`. The two rounding modes
    disagree by 1 px whenever `Bbox.min * size` has a fractional part
    `≥ 0.5`. Fixed: `dx0 = sx0 + round(Translation * size)`, so the
    painted rect's per-pixel offset matches exactly the per-pixel offset
    the GPU applies when sampling with `uv += Translation`.
  - **`_TextureName_ST` mismatch.** Mesh UVs shifted by `T`, but the GPU
    samples at `(UV + T) * Scale + Offset` — sample-position shift is
    `T * Scale`, not `T`. Detail maps, scrolling textures, lilToon
    `_MainTex_ST != (1,1,0,0)`, and any tiled-or-offset material would
    sample the wrong part of the rearranged texture. Textures with a
    non-identity `_ST` in any referencing material now skip the
    rearrangement (kept identical pixels, identical sampling, no
    obfuscation for that texture).
  - **Cross-mesh NFDH target collision.** NFDH runs per mesh, so two
    different meshes sharing a texture can independently assign their
    islands to overlapping target rects in `[0,1]²`. Painting the single
    shared rearranged texture would then overwrite one mesh's content
    with the other's. Fixed: any texture touched by islands from `≥ 2`
    distinct meshes reverts to identity for all its islands; the
    pre-existing mixed-pack-state propagation loop runs after that
    revert to maintain the cross-mesh invariant.
  - **UV1 / UV2 / UV3 scramble.** Previous code applied UV0-derived
    per-island translations to all four UV channels. UV1+ commonly
    carries an unrelated layout (lightmap UVs, detail masks, AudioLink
    UVs, matcap masks); applying UV0's island deltas to those channels
    produced nonsense sample positions. Fixed: only UV0 is rewritten,
    matching TTT's `AtlasTexture` (`AtlasTexture.cs:301`).
  - **Material clone path switched to `Object.Instantiate(src)`.**
    `new Material(src)` (AAO's DupliacteAssets pattern) only carries
    forward the main shader property table + keywords + render queue.
    `Object.Instantiate` does a serialization deep-clone that also
    preserves `globalIlluminationFlags`, `enableInstancing`,
    `doubleSidedGI`, Material Variant resolved values, and the extra
    serialized fields lilToon / Poiyomi stash outside the property
    table. The clone is still wrapped in
    `BeginNoApplyMaterialPropertyDrawers` so custom drawers can't
    re-tune keywords during the clone. Matches TTT's
    `AtlasTexture.GenerateAtlasMat` (`AtlasTexture.cs:920`).

## [0.2.6] - 2026-05-04

### Fixed
- **Critical: v0.2.4 / v0.2.5's per-island in-place involutions
  (FlipH / FlipV / Rot180) produced obfuscated textures that, when viewed
  as image files, looked virtually indistinguishable from the source.**
  Each island was just being mirrored within its own bbox, so the pixel
  histogram, dominant feature positions, and (for symmetric islands)
  most pixel content stayed put — a ripper running an inexact match
  (perceptual hash, downsampled-image diff, content-based search) could
  still link the obfuscated texture to the asset-store original.
- **Critical: bilinear-filter seams at island bbox edges.** Adjacent
  islands' transformed regions could leak each other's colour into the
  bilinear taps that crossed the bbox boundary, producing a visible
  one-pixel mismatch line on tightly packed atlases.

  Both are fixed by switching to genuine atlas-style relocation:
  - The packer is **Next-Fit Decreasing Height** (NFDH), the same
    base algorithm TexTransTool's `NFDHPlasFC` uses, minus the 90°
    rotation flag for code simplicity. Islands are sorted tall-first
    and laid out into [0,1]² UV rows.
  - Every island is **translated** to a new position — its UVs and its
    pixel content move together. The result genuinely repacks: islands
    end up in different rows, different columns, with completely
    different free-space distribution from the source. Visually
    inspecting the obfuscated texture file now shows clearly that
    it's been rearranged.
  - **Per-island padding skirt.** Each island gets a 0.005-UV (≈ 5 px
    at 1K, ≈ 20 px at 4K, floored at 2 px) edge-replicate dilation
    around its placed bbox in the rearranged texture. Bilinear and
    mip-chain taps that cross the strict bbox boundary now pull a
    smoothly-extended copy of the island's own border colour instead
    of a neighbour island's pixels — the v0.2.4/v0.2.5 seam is gone.
  - **Cross-mesh consistency.** A texture sampled by islands from
    multiple meshes only gets repainted when every contributing mesh's
    NFDH succeeded; otherwise the texture (and every island that
    touches it) reverts to identity via a fixed-point loop. This trades
    a little obfuscation aggressiveness for predictable correctness in
    the rare avatars that share texture atlases across meshes.
  - **NFDH failure fallback** (tightly packed atlases > ~80% UV
    coverage). If NFDH can't fit a mesh's islands inside [0,1]²
    (including each island's padding skirt), the entire mesh stays at
    identity — no obfuscation rather than partial / broken output. A
    future version can swap in a more aggressive packer (NFDH+rotation,
    or a guillotine packer) to handle these.

### Removed
- The `IslandTransform` enum and its FlipH / FlipV / Rot180 within-bbox
  involution branch — replaced by the single `Translation` vector and
  `IsPacked` flag on `UvIsland`. The pixel transform is now an
  index-clamping copy plus skirt, not a per-pixel mirror.

### Known limitations
- Tightly packed atlases (> ~80% UV coverage including padding) currently
  fall back to identity per the NFDH failure path. NFDH+rotation would
  raise the ceiling to ~90%; a guillotine / shelf-best-fit packer could
  push close to 95%. Not implemented here to keep the change minimal.
- UV1 / UV2 / UV3 are translated through the same per-island deltas as
  UV0 (carried over from v0.2.4). Shaders sampling auxiliary maps via
  UV1+ with a layout that differs from UV0 will misrender those maps.
- HDR formats (BC6H, RGBAFloat, ASTC HDR, …) skip the rearrangement
  pipeline entirely.

## [0.2.5] - 2026-05-04

### Added
- Per-material texture obfuscation via UV-island-accurate tile rearrangement (removes texture tearing from v0.2.3 uniform grid permute).

## [0.2.4] - 2026-05-04

### Fixed
- **Critical: triangle tearing under v0.2.3's uniform N×N grid permutation.**
  The previous pipeline split every texture into a 2×2…6×6 tile grid and
  permuted tiles independently, then remapped each mesh-UV vertex through
  the per-vertex tile lookup. Whenever a triangle's three UV vertices fell
  into different source tiles — extremely common on real avatar UV unwraps —
  the three vertices were sent to three different destination tiles, and
  the GPU's linear interpolation across the triangle then sampled disjoint
  regions of the shuffled texture. Visual result: scrambled / torn faces
  on most textures, despite the changelog claim of pixel-identical output.

  The fix is a per-UV-island transform, modelled on TexTransTool's
  `AtlasTexture` (even a "single-texture atlas group" repacks the islands
  at build time, which is the same rearrangement effect we want):
  - Per mesh, UV0 islands are detected via union-find — vertices sharing
    a UV position are merged first, then triangles unite their merged
    classes (same approach as TTT's `IslandUtility.UVtoIsland`).
  - Each island gets a deterministic within-bbox involution (FlipH /
    FlipV / Rot180), seeded from the island's bbox center, mesh
    instance ID and avatar instance ID. By construction every triangle's
    three vertices are in the same island, so all three pick up the same
    transform and the triangle moves as a unit — no tearing.
  - Texture pixels are rearranged in lockstep: each non-identity island's
    UV bbox is mapped to a pixel rectangle and that rectangle's pixels
    are flipped/rotated in place. A texture shared by N materials still
    produces exactly 1 obfuscated copy.
  - Conflicts (two islands whose UV bboxes overlap in the same texture)
    are resolved largest-island-first: the smaller / later island falls
    back to identity rather than corrupting the larger island's pixels.
  - Mesh UV channels 0–3 are all remapped through the same per-island
    transform (matches v0.2.3 behaviour for shaders that sample non-UV0
    detail/matcap masks with the same UV layout).
  - Sort tiebreak is fully deterministic (bbox xMin / yMin / mesh
    instance ID / island root id) so the same avatar produces the same
    obfuscated output run after run inside a session.

### Changed
- Inspector tooltip and `ObfuscationOptions.remapUvTextures` summary
  rewritten to describe the per-island rearrangement instead of the
  long-removed LSB jitter wording (the tooltips had been stale since
  v0.2.2; they were never updated for v0.2.3 and were doubly wrong).

### Known limitations (carried over from v0.2.3 — not regressions)
- Bilinear filtering at island bbox edges can fetch pixels from the
  adjacent island's transformed region. Most well-unwrapped avatars
  have atlas padding that absorbs this; in tightly packed atlases
  there may be a 1-pixel seam at the highest mip level.
- UV1 / UV2 / UV3 channels are remapped through the SAME per-island
  transform as UV0. Shaders that sample detail / matcap / lightmap
  textures via UV1+ with a layout DIFFERENT from UV0 will render
  those auxiliary maps with the wrong content. (Affects extremely
  few VRChat avatars in practice.)
- HDR texture formats (BC6H, RGBAFloat, ASTC HDR, …) are skipped
  rather than rearranged.

## [0.2.3] - 2026-05-04

### Changed
- **Texture obfuscation strategy switched from per-texture bit-jitter to a
  uniform tile-grid atlas rearrangement** (TTT-style repacking without merging).
  Every texture on the avatar is split into an N×N tile grid, tiles are
  deterministically permuted, and mesh UV channels (0–3) are remapped in
  lockstep. The visual result is pixel-identical while every byte of every
  texture differs from the source — no need for the previous XOR-on-raw-bytes
  approach, zero VRAM format mismatch risk, and no dependency on
  `GetRawTextureData` readback semantics.
  - Grid size is auto-derived from the largest texture (2×2 to 6×6, each
    tile ≥ 64 px). Permutation seed is deterministic (avatar instance ID),
    so the same avatar always gets the same obfuscated output across builds.
  - Texture shuffle goes through the `Graphics.Blit` → `ReadPixels` → CPU
    rearrange → `SetPixels` → `EditorUtility.CompressTexture` pipeline,
    which works for every readable and non-readable texture alike.
  - Mesh UV channels 0–3 are remapped uniformly through the same grid
    transform so rendering is correct regardless of which UV channel a
    shader samples from.
  - Shared-texture cache is global: a Texture2D referenced by N materials
    produces exactly 1 shuffled copy.

### Added
- `ObfuscationContext.MeshReplacements` + `MapMesh()` — downstream
  `ObfuscateAnimationClipsPass` now redirects `Mesh` object-reference curves
  through the remapped clones, so animations stay consistent with the
  UV-remapped meshes.

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
