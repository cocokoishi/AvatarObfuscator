# Avatar Obfuscator

> 无损、可配置、上传时自动执行的 VRChat avatar 内容混淆插件，基于 [NDMF](https://github.com/bdunderscore/ndmf)。

[![Unity](https://img.shields.io/badge/Unity-2022.3-black.svg?logo=unity)](https://unity.com/)
[![NDMF](https://img.shields.io/badge/NDMF-%5E1.6.0-blue.svg)](https://github.com/bdunderscore/ndmf)
[![VRChat SDK](https://img.shields.io/badge/VRCSDK-%E2%89%A53.7-orange.svg)](https://creators.vrchat.com/)
[![License](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

---

## 这是什么

把你的 avatar 上的几乎所有"会被 ripper 抠出来卖"的人类可读名字（参数、动画状态机、blendshape、骨骼以外的 GameObject、Mesh、Material 引用、动画 clip 绑定）替换成一坨**视觉上完全无法区分**的同形字符串：

```
Hips                        ← 保留（humanoid 骨骼）
Armature                    ← 保留
├─ Hips                     ← 保留
│  └─ Spine                 ← 保留
└─ ÌÍÎÏÌÍÎÏÌÍÎÏÌÍÎÏÌÍÎÏÌÍÎÏ ← 你原来的 "Clothes/Hat"
   └─ ÏÌÎÍÏÌÎÍÏÌÎÍÏÌÎÍÏÌÎÍ ← 你原来的某个 SkinnedMesh
      ├─ blendShape: ÍÏÎÌÍÏÎÌÍÏÎÌÍÏÎÌÍÏÎÌÍÏÎÌ
      ├─ blendShape: ÎÌÏÍÎÌÏÍÎÌÏÍÎÌÏÍÎÌÏÍÎÌÏÍ
      ├─ blendShape: あ                           ← 保留（MMD 形态键）
      └─ blendShape: Blink                        ← 保留（MMD 形态键）

Animator parameter: ÌÍÎÏÌÍÎÏÌÍÎÏÌÍÎÏÌÍÎÏÌÍÎÏ  ← 你原来的 "ClothesToggle"
Animator parameter: IsLocal                    ← 保留（VRC 内置）
Animator parameter: GestureLeft                ← 保留（VRC 内置）

Expression Menu:
  ┌── 衣服  → 参数: ÌÍÎÏÌÍÎÏÌÍÎÏÌÍÎÏÌÍÎÏÌÍÎÏ   ← 标签保留，参数引用改名
  └── 表情  → 参数: ÏÎÍÌÏÎÍÌÏÎÍÌÏÎÍÌÏÎÍÌÏÎÍÌ
```

混淆字符集是 `Ì Í Î Ï`（U+00CC / U+00CD / U+00CE / U+00CF），四个都是"带不同变音符的大写 I"。在任何主流字体里它们看起来几乎一模一样——都是一根带小帽子的竖线——但作为字符串它们是四个不同的码点，Unity / VRChat / 动画系统会把它们当作完全不同的标识符。

混淆是**在 NDMF 克隆出来的临时 avatar 上完成的**，原资产、原场景、原 prefab 一字不动。这就跟 Avatar Optimizer 的非破坏性原理完全一样——拿掉脚本一切恢复原状，因为根本没改过原版。

---

## Why

VRChat ripper 抓下你的 avatar 之后，能立即看到完整的 GameObject 名、参数名、blendshape 名、动画 clip 名、菜单标签。即使你的 mesh 受 [HE-Vrcat / IRIS / Bake Defender] 之类的工具加密，**外层的命名结构对 ripper 来说仍然是巨大的元信息**：

- 通过 GameObject 名（比如 `MyOriginalCharacterName_Hat_v3`）能快速定位是哪个原始作品
- 参数名（`Outfit_NewYear`）+ 菜单标签泄漏作者意图与 wardrobe 设计
- Blendshape 名暴露原始模型来源（特定 MMD 模型的 Blink 命名风格、特定 VRoid 工作流的命名）
- Animation clip 名（`SitDown_Improved_v2.anim`）对应作者的工作时间线

这个插件的目的是让上面这些**对 VRChat 运行毫无意义但对 ripper 极有价值的元信息**全部失效——上传后 server 端拿到的 avatar，所有用户字符串都是无意义的同形 Unicode 序列，但 avatar 在游戏内行为与原版**位级别完全等价**。

---

## 这不是什么

- **不是 mesh 加密** — 网格几何、材质、贴图都是 unmodified 的。ripper 抓下来仍然能拿到能播放的网格。请配合专门的 mesh 加密工具一起用。
- **不是防盗版的银弹** — 只是抬高 ripper "看一眼就知道偷的是谁的"的门槛。
- **不是用来给自己看的工具** — 一旦混淆完上传，你自己也辨认不出哪个 GameObject 是哪个。原版 prefab 永远保持可读。

---

## 安装

### 方案 A：VPM（推荐）

1. 在 [VRChat Creator Companion](https://vcc.docs.vrchat.com/) 里给项目添加 listing：

   ```
   https://cocokoishi.github.io/AvatarObfuscator/index.json
   ```

   _（如果你 fork 了一份自己用，把 url 改成你自己的 vpm.json）_

2. 在 VCC 里给项目勾选 **Cocokoishi Avatar Obfuscator**，自动会装 NDMF 依赖。

### 方案 B：手动 .zip 解压

1. 去 [Releases](https://github.com/cocokoishi/AvatarObfuscator/releases) 下载最新的 `dev.cocokoishi.avatar-obfuscator-x.y.z.zip`。
2. 解压到 `<你的项目>/Packages/dev.cocokoishi.avatar-obfuscator/`。
3. 确保已安装 NDMF（>= 1.6.0），通常 AAO / Modular Avatar 用户已经有了。

### 方案 C：.unitypackage 拖入

1. 去 Releases 下载 `dev.cocokoishi.avatar-obfuscator-x.y.z.unitypackage`。
2. 直接拖到 Unity 工程里，文件落到 `Assets/dev.cocokoishi.avatar-obfuscator/`。
3. 同样需要先装好 NDMF。

---

## 快速使用

1. 选中 avatar 根节点（带 `VRCAvatarDescriptor` 的那个 GameObject）。
2. **Add Component → Cocokoishi → Avatar Obfuscator**。
3. 默认所有混淆类目都开着。如果你做 MMD avatar，留意默认勾着的 "Preserve MMD Blendshapes" / "Preserve MMD 'Body' Object"。
4. **Build & Publish**（或者直接 Play）——NDMF 自动接管，混淆只发生在上传 / Play 期间的 avatar clone 上。
5. 上传完成后服务器侧的 avatar 已经混淆完毕，本地原 avatar 仍然是可读的。

不需要做任何其他配置。组件可以随时移除——因为根本没改过原资产。

---

## 配置选项

`Avatar Obfuscator` 组件 inspector 长这样：

```
┌─────────────────────────────────────────────┐
│ Enable Obfuscation                       ✓  │  总开关
│ ─────────────────────────────────────────── │
│ Parameters & Animator                       │
│ Animator Parameters                      ✓  │
│     VRC Expression Parameters            ✓  │
│ ─────────────────────────────────────────── │
│ Mesh / Blendshape                           │
│ Blendshape Names                         ✓  │
│     Preserve MMD Blendshapes             ✓  │
│ Mesh Asset Names                         ✓  │
│ ─────────────────────────────────────────── │
│ Hierarchy                                   │
│ GameObject Names                         ✓  │
│     Preserve MMD 'Body' Object           ✓  │
│ ─────────────────────────────────────────── │
│ Materials                                   │
│ Merge Identical Materials                ✓  │
│ ─────────────────────────────────────────── │
│ Animation Clips                             │
│ Rewrite Animation Clip Bindings          ✓  │
│ ─────────────────────────────────────────── │
│ ▶ Advanced                                  │
└─────────────────────────────────────────────┘
```

| 选项 | 作用 | 关掉的代价 |
|---|---|---|
| **Animator Parameters** | 重命名所有 playable layer 里的 animator 参数，并把 transition / blend tree / `VRCAvatarParameterDriver` / PhysBone 前缀 / ContactReceiver 中所有引用同步重写 | 参数名字一眼就能被 ripper 看到，作者意图清晰 |
| **VRC Expression Parameters** | 同步重写 `VRCExpressionParameters` 资产、Expression Menu 控件的参数引用（菜单的**用户可见标签**保持不变） | 菜单跟参数对不上，菜单失效 |
| **Blendshape Names** | 克隆每个 SMR 的 Mesh，重命名所有 blendshape key；同步重写 `descriptor.VisemeBlendShapes[]`、`MouthOpenBlendShapeName`、所有动画 clip 里的 `blendShape.<name>` 绑定 | blendshape 名字暴露模型来源（VRoid / MMD / Vket 摊位等） |
| ↳ **Preserve MMD Blendshapes** | 保留 MMD 世界识别用的形态键（あ / い / う / え / お / Blink / 笑い / まばたき / ...） | 在 MMD 世界里嘴型 / 表情失效 |
| **Mesh Asset Names** | 把临时容器里的 Mesh asset 名字也改了 | 几乎没代价。只是 Project 视图里显示的 mesh 名字带原始信息（如果用户能拿到 .unity3d 解包） |
| **GameObject Names** | 重命名 hierarchy 下每个 GameObject。**保留**：avatar 根、`Armature`、所有 humanoid 骨骼及它们到 root 的整条祖先链、（可选）MMD `Body` GameObject | hierarchy 直接告诉 ripper 这是哪个原始作品 |
| ↳ **Preserve MMD 'Body' Object** | 保留 MMD 检测用的 "Body" GameObject 名字 | MMD 世界找不到主 mesh，不能播放 |
| **Merge Identical Materials** | 把序列化属性按字节相等的 material 合并成一份，所有 Renderer 与 AnimationClip ObjectReference 曲线同步重定向 | 不混淆，但能减少 draw call。是个 bonus 功能 |
| **Rewrite Animation Clip Bindings** | 在所有重命名完成后扫一遍每个可达的 AnimationClip，重写 `path` / `propertyName` / `Material` 对象引用曲线 | **任何重命名都需要它**。关掉之后所有动画都会断 |
| **Advanced → Seed** | 0 = 每次 build 都用不同随机字串；非 0 = 可重现的混淆 | 调试时锁定，发布时设 0 |
| **Advanced → Generated Name Length** | 生成名字的字符数。字符集 4 个，每字符 2 bit，默认 24 → 48 bit / 281 万亿种唯一名 | 关 |

---

## 保护机制（永远不混淆）

混淆"几乎所有东西"，但有几类必须留下来，不然 avatar 直接坏：

### 1. VRChat 内置 animator 参数

完整白名单写在 `Editor/Internal/VRChatBuiltins.cs`，覆盖：

- 状态：`IsLocal`, `PreviewMode`, `TrackingType`, `VRMode`, `MuteSelf`, `InStation`, `Earmuffs`, `IsOnFriendsList`, `AvatarVersion`
- 语音：`Viseme`, `Voice`
- 手势：`GestureLeft`, `GestureRight`, `GestureLeftWeight`, `GestureRightWeight`
- 移动：`AngularY`, `VelocityX/Y/Z/Magnitude`, `Upright`, `Grounded`, `Seated`, `AFK`
- 缩放（avatar 3.5+）：`ScaleModified`, `ScaleFactor`, `ScaleFactorInverse`, `EyeHeightAsMeters`, `EyeHeightAsPercent`
- 已弃用但部分 controller 还在用：`Supine`, `GroundProximity`, `Expression1..16`

### 2. Humanoid 骨骼

通过 `Animator.GetBoneTransform(HumanBodyBones)` 取出每根 humanoid 骨骼，再沿 `parent` 链向上保留到 avatar root——也就是说 humanoid 骨骼**和它们之间的所有非 humanoid 中间节点**（twist 骨、IK 辅助骨）都保留。

不这样做会导致 Animator 的 Avatar asset 无法解析骨骼路径，VRChat 上传报：

> Avatar bone "Hips" could not be located.

### 3. MMD 形态键（默认开）

完整列表抄自 Avatar Optimizer 的 MmdBlendShapeNames 表，覆盖 MMD 世界识别 avatar 用的全部嘴型 / 表情形态键（日文 / 旧 EN / 新 EN 三套别名，约 90 个名字）。

### 4. VRC LipSync 字段

`descriptor.VisemeBlendShapes[]`（string[]）和 `descriptor.MouthOpenBlendShapeName`（string）都是按**名字**引用 blendshape 的，混淆完会同步重写，否则 VRChat 验证时报：

> Lipsync viseme "<name>" was not found on the visemes blendshape

### 5. PhysBone / ContactReceiver 前缀联动

`VRCPhysBone.parameter = "Hair"` 在运行时展开为 `Hair_IsGrabbed`、`Hair_Angle`、`Hair_IsPosed`、`Hair_Stretch`、`Hair_Squish`。如果你的 animator 用了这些后缀形参数，重命名前缀的同时**所有后缀形也以相同前缀同步重命名**，确保 PhysBone 写入的参数和 animator 读的参数还是同一个。

---

## NDMF 管线位置

```
NDMF BuildPhase pipeline
═══════════════════════════════════════════
Resolving      ░░ 内置 RemoveEditorOnly 等
Generating     ░░
Transforming   ░░ Modular Avatar 的工作大都在这里
Optimizing     ░░ Avatar Optimizer 的工作大都在这里
                  ┊
                  └─ AfterPlugin: Avatar Obfuscator 的所有 pass
                     1) CollectStatePass
                     2) MergeMaterialsPass
                     3) ObfuscateBlendShapesPass
                     4) ObfuscateParametersPass
                     5) ObfuscateHierarchyPass
                     6) ObfuscateAnimationClipsPass
                     7) FinalizeAssetsPass
```

`InPhase(BuildPhase.Optimizing).AfterPlugin("com.anatawa12.avatar-optimizer").AfterPlugin("nadena.dev.modular-avatar")`——确保我们在所有其他主流插件之后跑，混淆的是 avatar 的**最终形态**。

如果用户没装 AAO 或 MA，NDMF 的解算器会忽略对应的约束，不影响插件运行。

---

## 故障排查

### "Avatar validation failed" / "could not locate bone"

确认 `Hierarchy` 类目开着的同时——这种情况几乎只可能是 hierarchy pass 没保留某根骨骼。打开 inspector 的 Advanced → Seed 设个固定值，重新 build 一次。如果稳定复现，去 GitHub 开 issue，附 avatar 的 humanoid bone 列表（File → Build Settings 报告 / `Animator.avatar` 信息）。

### "Lipsync viseme '...' was not found"

确认 `Blendshape Names` 开着的同时 `VRC Avatar Descriptor` 的 LipSync 模式是 `Viseme Blendshape` 或 `Jaw Flap Blendshape`。0.1.x 版本之后这种已经自动处理；如果还报错，issue 我看下。

### MMD 嘴型在 MMD 世界里失效

打开 **Preserve MMD Blendshapes**（默认就是开的）。如果还失效，打开 **Preserve MMD 'Body' Object**——某些 MMD 世界靠 GameObject 名字 `Body` 找主 mesh，而不是靠 blendshape 名字。

### 上传后某些手势 / 菜单按钮没反应

98% 是 `Animator Parameters` 开着但 `VRC Expression Parameters` 关掉了，导致两边参数名对不上。两个一起开。

### 动画播放断掉

`Rewrite Animation Clip Bindings` 是必须开的。inspector 在你关掉它而其他 rename 还开着的时候会画红色 `HelpBox` 警告。

### "VRChat 拒绝了某个我自定义的参数名"

我们生成的名字是 `[Ì Í Î Ï]+`，全是 Latin-1 supplement 单字符，VRChat 接受。如果遇到拒绝，可能是：
- 你的 avatar 已经混过一次（旧版本）然后又混了一次叠加了；删掉组件重新加
- 某个第三方组件不支持非 ASCII 参数名；issue 反馈

---

## 与其他工具的关系

| 工具 | 它管什么 | 跟我们的关系 |
|---|---|---|
| **Modular Avatar** | 服装系统、参数注入 | 在 Transforming phase 跑，先于我们。我们处理它生成出来的最终产物 |
| **Avatar Optimizer** | mesh 合并、骨骼合并、动画优化 | 在 Optimizing phase 跑，我们用 `AfterPlugin` 排在它后面 |
| **Mesh 加密工具**（HE-Vrcat / IRIS / Bake Defender / Anti-Ripper Toon） | 防止 mesh 被解码 | 完全互补——他们护 mesh，我们护元信息 |
| **VRChat 自带的 Performance Stats** | 统计 / 警告 | 不冲突。我们不增加 / 减少任何 mesh 多边形或骨骼 |

---

## 限制 / 已知约束

- **必须装 NDMF**（≥ 1.6.0），就跟 AAO 一样。
- **混淆是 build-time 的**——本地预览 (Play 模式) 可以看到混淆后的状态，但 Edit 模式下你的 hierarchy / params 都是原版可读的。
- **不混淆**：mesh 几何、材质属性、贴图、shader keyword。这些是 mesh 加密工具的领地。
- **VRChat ripper 仍然能拿到混淆后的 avatar**——这只是让他们拿到的东西**对人类无意义**。要彻底防护需要配合 mesh 加密。
- 用了 `[Ì Í Î Ï]` 这种 Latin-1 字符。绝大多数 Unity 字段接受，但**极个别第三方插件**可能用 ASCII 限制的字段验证（罕见）；遇到了请反馈。

---

## 开发 / 贡献

源码组织：

```
dev.cocokoishi.avatar-obfuscator/
├── Runtime/                     ← 用户挂的脚本
│   ├── AvatarObfuscator.cs
│   └── ObfuscationOptions.cs
└── Editor/                      ← NDMF 管线全在这里
    ├── ObfuscatorPlugin.cs       (NDMF Plugin 入口)
    ├── Internal/
    │   ├── ObfuscationContext.cs (跨 pass 共享状态)
    │   ├── NameGenerator.cs       (ÌÍÎÏ 名字发生器)
    │   ├── VRChatBuiltins.cs      (VRC 内置参数白名单)
    │   ├── MmdBlendShapeNames.cs  (MMD 形态键白名单)
    │   ├── AnimatorWalker.cs      (animator 遍历)
    │   ├── PathRemapper.cs        (GameObject 路径重映射)
    │   └── AssetCloner.cs         (animator 深克隆)
    ├── Passes/
    │   ├── CollectStatePass.cs
    │   ├── MergeMaterialsPass.cs
    │   ├── ObfuscateBlendShapesPass.cs
    │   ├── ObfuscateParametersPass.cs
    │   ├── ObfuscateHierarchyPass.cs
    │   ├── ObfuscateAnimationClipsPass.cs
    │   └── FinalizeAssetsPass.cs
    └── Inspector/
        └── AvatarObfuscatorEditor.cs
```

PR / issue / 小修复都欢迎。提 PR 之前请：
1. 确认在一个 humanoid avatar 上能 build & upload 成功
2. 确认 MMD avatar 在测试用的 MMD 世界里仍然能播嘴型

---

## License

MIT。详见 [LICENSE](LICENSE)。

---

## English summary

Avatar Obfuscator is a non-destructive, drop-in NDMF plugin that obfuscates almost every human-readable name on a VRChat avatar (animator parameters, blendshape keys, GameObject hierarchy names, mesh asset names, animation clip bindings, expression menu parameter references, PhysBone parameter prefixes, ContactReceiver names) into a soup of homoglyph characters (`Ì Í Î Ï` — capital I with grave/acute/circumflex/diaeresis), all of which render as visually-identical vertical strokes in any common font.

The plugin runs in NDMF's `Optimizing` phase, after Avatar Optimizer and Modular Avatar, on the cloned avatar that NDMF gives us — **your scene avatar and its source assets are never modified**. Removing the component fully reverts the change because nothing was ever changed.

Whitelisted (always preserved): VRChat built-in animator parameters, all humanoid bones plus their ancestor chain, MMD-significant blendshape names (configurable), the MMD `Body` GameObject (configurable), and `VRCAvatarDescriptor` viseme / jaw-flap blendshape name fields are auto-rewritten to match.

This is **not** a mesh encryption tool — it complements those by stripping all the human-meaningful metadata that rippers use to identify and re-sell avatars, while leaving the avatar functionally bit-equivalent in-game.

---

_Plugin entry: `Add Component → Cocokoishi → Avatar Obfuscator`_
