# Avatar Obfuscator

> VRChat avatar anti-ripper obfuscation tool. Replaces every
> human-readable name on your avatar with a soup of homoglyph characters so
> rippers cannot make sense of your assets — without ever touching your
> scene originals.
<img width="1498" height="811" alt="image" src="https://github.com/user-attachments/assets/7eec38c3-95a6-4f9b-9986-58cbb6c7e1e3" />

[![Unity](https://img.shields.io/badge/Unity-2022.3-black.svg?logo=unity)](https://unity.com/)
[![NDMF](https://img.shields.io/badge/NDMF-%5E1.6.0-blue.svg)](https://github.com/bdunderscore/ndmf)
[![VRChat SDK](https://img.shields.io/badge/VRCSDK-%E2%89%A51.0-orange.svg)](https://creators.vrchat.com/)
[![License](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

---

## What it does

All human-readable names on your avatar are replaced with homoglyph
gibberish. The character set is **Ì Í Î Ï** (U+00CC / U+00CD / U+00CE /
U+00CF) — four "capital I with diacritic" code points that in any common
font all render as a vertical stroke with a tiny accent on top, visually
indistinguishable from each other. Humans cannot read them; machines treat
them as four distinct identifiers. This is the same obfuscation alphabet
that VRChat itself uses internally.

---

## Installation

**Download the latest `.unitypackage` from
[Releases](https://github.com/cocokoishi/AvatarObfuscator/releases).**

The current version is **v0.4.2**. Only this version is recommended.
Older versions contain mysterious bugs.

1. Drag the `.unitypackage` into your Unity project. It lands in
   `Assets/dev.cocokoishi.avatar-obfuscator/`.
2. Make sure **NDMF ≥ 1.6.0** is installed (most AAO / Modular Avatar users
   already have it).

---

## Quick start

1. Select the avatar root (the GameObject with `VRCAvatarDescriptor`).
2. Use MA Manual Bake! (Optional but recommended to prevent errors).
3. Add Component → Search: Avatar Obfuscator.
4. Default settings are already good. For MMD avatars, make sure
   Preserve MMD... options are ticked.
5. Build & Publish. NDMF handles everything at build time on a clone.
   Your scene original is never modified.
<img width="793" height="851" alt="image" src="https://github.com/user-attachments/assets/38e03242-a824-4466-a3ae-24bc4bc0a58f" />

---

## What we obfuscate

- **All blendshape names**, on every Skinned Mesh.
- **All GameObject names**, everywhere under the avatar root.
- **All animator controllers**, their internal parameters, and the
  corresponding VRC Parameters.
- **Material / Texture2D / AudioClip asset names.** Each referenced asset
  is cloned into a temporary build-time copy so your source `.mat` /
  `.png` / `.wav` files on disk are never modified. The clones get
  homoglyph names, and every reference site (renderer materials, material
  textures, AudioSource.clip, VRC_AnimatorPlayAudio.Clips, animation clip
  object-reference curves) is redirected to the clones automatically.
- **Some asset file names** — meshes, animation clips, and more.

## What we do NOT obfuscate

- Shaders, Cubemaps, RenderTextures, Texture3Ds, Texture2DArrays — anything
  not in the list above.
- VRChat reserved parameters.
- The head mesh and its blendshapes (needed by MMD worlds).
- **Parameters whose name contains a user-configured substring.** The
  inspector exposes a comma-separated list (default: `FT,eye`) so any
  parameter referenced by an external system — face-tracking bridges, OSC
  tools, custom shaders that read parameter names as strings — stays
  plaintext. Edit the field on the component to add or remove substrings.

---

## Reproducible builds

By default the obfuscator uses a fixed seed (`5145514`), so two builds of
the same avatar produce **identical** obfuscated names. This is the right
default for source-control review and for pipelines that cache name
mappings. Set the seed to `0` in the Advanced section to get a fresh
random salt every build instead.

---

## Texture obfuscation — use TTT instead

The built-in texture obfuscation feature has been removed.

To protect your textures: use
[TexTransTool](https://github.com/ReinaS-64892/TexTransTool) to atlas your
textures. It not only improves rendering performance but also makes reverse
engineering significantly harder for rippers. After careful consideration,
we concluded TTT does it better and removed our own implementation.

---

## How to get the most out of this tool

- **Use TexTransTool and Avatar Optimizer aggressively.** Merge meshes and
  atlas textures wherever possible — the more your assets are fused
  together, the harder they are to disassemble and reuse.
- **Pair with a password lock.** An obfuscated animator state machine is
  already unreadable to humans, so your password is invisible by default.
  Combine this with an OSC password lock and the cost of cracking your
  avatar will far exceed the cost of making one from scratch.

---

## How it works

Obfuscation runs at upload time on the cloned copy that NDMF gives us.
**Your scene avatar and source assets are never modified.** Removing the
component fully reverts the build because the originals were never changed.

---

## Troubleshooting

**If it doesn't work:** run Modular Avatar's manual bake first, then attach
this component. This resolves issues in almost all cases.  

---

## Reference projects

- [`net.rs64.tex-trans-tool`](https://github.com/ReinaS-64892/TexTransTool) — the texture-and-UV transform pipeline. Use it to atlas and protect your textures.
- [`nadena.dev.ndmf`](https://github.com/bdunderscore/ndmf) — the non-destructive modular framework this plugin is built on. **Required dependency.**
- [`com.anatawa12.avatar-optimizer`](https://github.com/anatawa12/AvatarOptimizer) — the avatar build pipeline whose merge and clone patterns inspired our equivalents.

---

## Disclaimer

Do not use this tool on models sold through Chinese second-hand marketplaces
(Xianyu / 闲鱼), or on models that violate DMCA or the original author's
terms of use. The author of this plugin assumes no responsibility for any
loss caused by using this plugin.

If you are concerned about reliability, back up your project before use. Made 99% with DeepseekV4Pro(Max)+CC

---

## License

MIT. See [LICENSE](LICENSE).

---

## Links

- **Project / Issues**: <https://github.com/cocokoishi/AvatarObfuscator>
- **Author (bilibili)**: <https://space.bilibili.com/5145514>
