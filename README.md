# Avatar Obfuscator

> Anti-ripper VRChat avatar obfuscator. Replaces every human-readable name on
> your avatar with a soup of homoglyph characters, and re-encodes every
> material's textures so they can no longer be matched against asset stores
> by content hash — all without changing your scene assets.

[![Unity](https://img.shields.io/badge/Unity-2022.3-black.svg?logo=unity)](https://unity.com/)
[![NDMF](https://img.shields.io/badge/NDMF-%5E1.6.0-blue.svg)](https://github.com/bdunderscore/ndmf)
[![VRChat SDK](https://img.shields.io/badge/VRCSDK-%E2%89%A53.7-orange.svg)](https://creators.vrchat.com/)
[![License](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

---

## What is this

This project aims to obfuscate VRChat avatars to defend against ripper
intrusion. Once your avatar is uploaded, the server-side copy that rippers
can pull contains no human-readable hints about what the original was —
no GameObject names, no parameter names, no blendshape names, no animation
clip names, and no recognisable texture bytes. The avatar still works
exactly the same in-game.

Obfuscation runs at upload time on the cloned avatar that NDMF gives us.
**Your scene avatar and source assets are never modified.** Removing the
component fully reverts the change because nothing was ever changed.

---

## Acknowledgments

This project is rewritten / inspired based on these excellent projects:

- [`net.rs64.tex-trans-tool`](https://github.com/ReinaS-64892/TexTransTool) — the texture-and-UV transform pipeline that motivated our `Remap UV Textures` pass.
- [`nadena.dev.ndmf`](https://github.com/bdunderscore/ndmf) — the non-destructive modular framework this plugin is built on top of.
- [`com.anatawa12.avatar-optimizer`](https://github.com/anatawa12/AvatarOptimizer) — the avatar build pipeline whose `AutoMergeSkinnedMesh` and `AssetCloner` patterns inspired our equivalents.

We do not link or depend on any of these at runtime — the obfuscator is
fully self-contained. Installing or not installing those plugins does not
affect this one, and our pass runs cleanly after theirs.

---

## Features

The plugin currently provides the following obfuscation categories. Each
toggle is independent — turning a flag off keeps the corresponding name /
asset readable.

### Identifier obfuscation (homoglyph rename)

The character set is `Ì Í Î Ï` (U+00CC / U+00CD / U+00CE / U+00CF) — four
"capital I with diacritic" code points. In any common font they all render
as a vertical stroke with a tiny dot or accent on top, visually
indistinguishable from each other, but to Unity / VRChat / animation
parsers they are four distinct identifiers.

> If you have ever poked around an uploaded avatar and noticed certain
> identifier strings already look like rows of identical-looking vertical
> bars — yes, that is the same family of trick. We just apply it to a wider
> set of names, before the upload, on a clone, with full animation
> rebinding so nothing breaks.

- **Animator Parameters** — every animator parameter referenced by every
  playable layer is renamed, with transitions, blend trees,
  `VRCAvatarParameterDriver`, PhysBone parameter prefixes, and
  ContactReceiver names rewritten to match. VRChat built-in parameters
  (`IsLocal`, `Viseme`, `GestureLeft`, etc.) are kept untouched.
- **VRC Expression Parameters & Menu** — the parameter entries in the
  expression list are renamed, and the parameter references inside menu
  controls are rewritten. The user-visible labels in the menu are kept.
- **Blendshape Names** — every blendshape on every SkinnedMesh is renamed.
  Animation curves, the `VRCAvatarDescriptor` viseme list, and the JawFlap
  mouth-open shape are all rewritten in lockstep. MMD-significant blendshape
  names (Japanese / EN aliases) are preserved when MMD compatibility is on.
- **GameObject Names** — every GameObject under the avatar root is renamed.
  Humanoid bones plus their ancestor chain, the avatar root, the `Armature`,
  and (optionally) the MMD `Body` GameObject are preserved.
- **Mesh Asset Names** — the `name` field on temporary mesh assets is
  rewritten. The MMD body mesh is preserved when MMD compatibility is on.
- **Animation Clip Asset Names** — every reachable AnimationClip is renamed
  in place. VRC SDK proxy clips (resolved by name at runtime) are kept
  untouched. AvatarMask names are renamed alongside.

### Texture obfuscation

- **Obfuscate Textures** — for every Texture2D on every material, generates a
  byte-different copy by perturbing a single sub-pixel low-significance bit and
  then recompressing the result back to the source's compressed format
  (BC7 / DXT5 / ASTC / ETC2 / etc.). The visual result is identical — the
  perturbation is well below human discrimination threshold — but every byte
  in the bundle differs from the source, so a ripper extracting your avatar
  can no longer match its textures against asset-store originals by content
  hash (SHA, perceptual-hash variants, reverse-image-search, etc.).

  Mesh UVs and material `_TextureName_ST` values are NEVER modified, so any
  number of UV channels, arbitrary tiling/offset, normal maps, detail / matcap
  masks and parallax effects remain correct. A texture shared by N materials
  produces exactly 1 obfuscated copy in VRAM. Cubemaps, 3D textures, render
  textures and HDR formats are passed through unmodified.

### Optional optimisation

- **Auto-Merge Skinned Mesh** *(default OFF)* — a strict-heuristic merger
  that combines SkinnedMeshRenderers sharing a root bone, with no
  blendshapes, no animations referencing their path, and no extra
  components on their GameObject. This is a draw-call optimisation, not an
  obfuscation feature. **If you have Avatar Optimizer's Trace and Optimize
  installed, leave this off and let AAO handle the merge** — its
  dependency tracking is far more thorough than ours.

### Animation rewiring (always required)

- **Rewrite Animation Clip Bindings** *(default ON, must stay ON whenever
  any rename option is on)* — walks every reachable AnimationClip and
  rewrites its `path`, `propertyName`, blendshape bindings, and material
  ObjectReference curves so they continue to point at the renamed targets.

---

## What this is NOT

- **NOT a mesh encryption tool.** The geometry (vertices / triangles /
  bones) is left intact, and so are mesh UVs. We only rewrite texture
  pixel bytes (one LSB perturbation per texture). Pair this with a
  dedicated mesh-encryption tool (HE-Vrcat / IRIS / Bake Defender /
  Anti-Ripper Toon) if you want geometry protection too.
- **NOT a performance optimiser.** The only optimisation-flavoured option
  (`Auto-Merge Skinned Mesh`) is opt-in.
- **NOT a silver bullet against piracy.** It only raises the bar — a ripper
  can still pull your avatar, they just can't tell at a glance which
  original asset it is.

> **It defends against rip but cannot defend against hotswap.**
> A hotswap attack replaces an avatar wholesale at runtime — no metadata
> matching needed, the entire avatar is taken. Obfuscation does nothing
> against that. For hotswap defence you need a different class of tool.

---

## Installation

### Option A — VPM (recommended)

Add the listing in [VRChat Creator Companion](https://vcc.docs.vrchat.com/):

```
https://cocokoishi.github.io/AvatarObfuscator/index.json
```

Then in VCC tick **Cocokoishi Avatar Obfuscator** for your project; the
NDMF dependency is pulled in automatically.

### Option B — Manual zip

1. Grab the latest `dev.cocokoishi.avatar-obfuscator-x.y.z.zip` from
   [Releases](https://github.com/cocokoishi/AvatarObfuscator/releases).
2. Extract into `<your project>/Packages/dev.cocokoishi.avatar-obfuscator/`.
3. Make sure NDMF (≥ 1.6.0) is installed — most AAO / Modular Avatar users
   already have it.

### Option C — `.unitypackage`

1. Download `dev.cocokoishi.avatar-obfuscator-x.y.z.unitypackage` from
   Releases.
2. Drag it into your Unity project; it lands in
   `Assets/dev.cocokoishi.avatar-obfuscator/`.
3. NDMF still needs to be installed first.

---

## Quick start

1. Select the avatar root (the GameObject with `VRCAvatarDescriptor`).
2. **Add Component → Cocokoishi → Avatar Obfuscator**.
3. Defaults are sensible — every obfuscation category is on, except
   `Auto-Merge Skinned Mesh`. If you build MMD avatars, leave
   `Preserve MMD Blendshapes` and `Preserve MMD 'Body' Object` ticked.
4. **Build & Publish** (or Play). NDMF takes over and obfuscates the
   cloned avatar; your scene avatar stays exactly as-is.

---

## NDMF pipeline placement

```
NDMF BuildPhase pipeline
═══════════════════════════════════════════
Resolving      ░░ built-in RemoveEditorOnly etc.
Generating     ░░
Transforming   ░░ Modular Avatar / TexTransTool / ...
Optimizing     ░░ Avatar Optimizer / TexTransTool / ...
                  ┊
                  └─ AfterPlugin: Avatar Obfuscator
                     1) CollectStatePass
                     2) AutoMergeSkinnedMeshPass (optional)
                     3) RemapUVTexturePass
                     4) ObfuscateBlendShapesPass
                     5) ObfuscateParametersPass
                     6) ObfuscateHierarchyPass
                     7) ObfuscateAnimationClipsPass
                     8) FinalizeAssetsPass
```

`InPhase(BuildPhase.Optimizing).AfterPlugin("net.rs64.tex-trans-tool").AfterPlugin("com.anatawa12.avatar-optimizer").AfterPlugin("nadena.dev.modular-avatar")` — we run last on purpose, so the avatar we obfuscate is the final, post-optimised form. When TTT / AAO / MA are not installed, the corresponding `AfterPlugin` constraint is just dropped by NDMF's solver.

---

## Troubleshooting

### "Avatar validation failed" / "could not locate bone"

Almost always means a humanoid bone got renamed. File an issue with the
avatar's humanoid bone list and the seed value (set `Advanced → Seed` to a
non-zero value to make the obfuscation reproducible).

### "Lipsync viseme '...' was not found"

The descriptor's viseme blendshape name list is auto-rewritten — if you
still see this, please file an issue with the lipsync mode and the avatar's
blendshape list.

### MMD mouth shapes broken in MMD worlds

Make sure `Preserve MMD Blendshapes` is on (default ON). If the world
looks for a `Body` GameObject, also tick `Preserve MMD 'Body' Object`.

### Some menu buttons or gestures stop responding after upload

98% of the time this is `Animator Parameters` ON but
`VRC Expression Parameters` OFF — they have to be on together so both
sides agree on the parameter names.

### Animations break

`Rewrite Animation Clip Bindings` must be ON whenever any rename option is
on. The inspector will show a red warning when you accidentally turn it
off.

### Console warns "Bit-jitter failed for texture …"

The texture is in an exotic format the editor's recompression path cannot
handle on the current build target. Skipped textures pass through to the
build unmodified — the avatar still uploads fine, those specific textures
just won't be obfuscated. Common cause: HDR / floating-point formats are
deliberately skipped (BC6H, RGBAFloat, ASTC HDR, …).

---

## Limitations

- Requires NDMF ≥ 1.6.0 (just like AAO / Modular Avatar).
- Obfuscation is build-time. In Edit mode your hierarchy / params look
  exactly like before; in Play mode they're obfuscated.
- `Obfuscate Textures` may skip individual textures whose source format is
  HDR or whose recompression path is unavailable on the current build target.
  Skipped textures pass through unmodified — the avatar still uploads fine,
  those specific textures just don't get the byte-level obfuscation.
- `Auto-Merge Skinned Mesh` is a strict-heuristic implementation. It
  refuses to merge meshes with blendshapes, animation references, extra
  components, or different root bones. For complex merges, use AAO's
  Trace and Optimize.
- We do NOT obfuscate: mesh geometry (vertex positions / triangles),
  animation keyframe values (only binding paths are rewritten), shader
  keywords. Those are mesh-encryption-tool territory.
- Names use `[Ì Í Î Ï]` Latin-1 characters. Almost every Unity field
  accepts them; if you find a third-party plugin that rejects non-ASCII
  identifiers, please file an issue.

---

## Source layout

```
dev.cocokoishi.avatar-obfuscator/
├── Runtime/                       ← user-facing component
│   ├── AvatarObfuscator.cs
│   └── ObfuscationOptions.cs
└── Editor/                        ← NDMF pipeline
    ├── ObfuscatorPlugin.cs
    ├── Internal/
    │   ├── ObfuscationContext.cs   (cross-pass state)
    │   ├── NameGenerator.cs        (homoglyph generator)
    │   ├── VRChatBuiltins.cs
    │   ├── MmdBlendShapeNames.cs
    │   ├── AnimatorWalker.cs
    │   ├── PathRemapper.cs
    │   ├── AssetCloner.cs
    │   ├── UVTextureRemapper.cs    (per-texture bit-jitter)
    │   └── SkinnedMeshMerger.cs    (heuristic SMR merge)
    ├── Passes/
    │   ├── CollectStatePass.cs
    │   ├── AutoMergeSkinnedMeshPass.cs
    │   ├── RemapUVTexturePass.cs
    │   ├── ObfuscateBlendShapesPass.cs
    │   ├── ObfuscateParametersPass.cs
    │   ├── ObfuscateHierarchyPass.cs
    │   ├── ObfuscateAnimationClipsPass.cs
    │   └── FinalizeAssetsPass.cs
    └── Inspector/
        └── AvatarObfuscatorEditor.cs
```

No source dependency on TexTransTool / Avatar Optimizer / Modular Avatar.

---

## License

MIT. See [LICENSE](LICENSE).

---

## Links

- **Project / Issues**: <https://github.com/cocokoishi/AvatarObfuscator>
- **Author (bilibili)**: <https://space.bilibili.com/5145514>

---

_Plugin entry: `Add Component → Cocokoishi → Avatar Obfuscator`_
