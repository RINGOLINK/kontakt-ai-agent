# Kontakt AI Agent

> **Kontakt 音色库管理器 + 内嵌 AI Agent** —— 面向 Native Instruments Kontakt 音色库的本地管理工具。
> 扫描索引 · 乐器与声学特征检索 · **音色相似度地图** · 手册知识库 · **能直接试听的内嵌 AI 助手**
>
> **纯本地运行、不改动任何音色文件。**

[![License](https://img.shields.io/badge/license-PolyForm%20Noncommercial%201.0.0-blue)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11%20x64-lightgrey)
![.NET](https://img.shields.io/badge/.NET-9%20Desktop%20Runtime-512BD4)

---

## 这是什么

一个把 Kontakt 音色库「管起来」的桌面工具。**核心是让 AI 真的能读懂你的音色库** ——
不只是列文件名，而是**用语义嵌入理解音色像不像**、**读你的手册 PDF 回答问题**、**找出音色后直接放给你听**。

| 功能 | 说明 |
|---|---|
| **📚 音色库清单** | 扫描本地库目录，建立索引（库 / 乐器 / 采样 / 标签 / 封面） |
| **🎹 乐器中心** | 按类别浏览 NKI、搜索（支持英文关键词 → 语义）、收藏、试听 |
| **🤖 问问 AI** | **内嵌 Agent**：读手册、找音色、**渲染成可点播的播放器**、写并编译 KSP 脚本 |
| **🗺️ 音色地图** | **MERT 语义嵌入 + UMAP 降维 + HDBSCAN 聚类** —— 把「音色相似度」画成可框选、可批量操作的地图 |
| **🩺 健康检查** | 找缺失文件 / 杂质 / 缺封面，一键修 |
| **🧹 维护** | 孤儿文件、重复大文件、快照对比 |
| **🔐 入库** | 读 `.nicnt` 自动写注册表 / Service Center，让库出现在 Kontakt 的 Library 浏览器里 |
| **🎛️ KSP** | **内置 `kspc.exe` 编译器**（71 MB）—— Agent 写脚本后能真编译验证 |

---

## 亮点：AI 找音色是「真的在听」

普通工具只能按文件名搜。**本工具的 AI 找音色走的是声学 + 语义路径**：

```
① 文本检索（名字真含关键词 = 强证据；仅目录名命中 = 弱证据）
② 保底项优先取【名字真含关键词】的、且跨库分散
③ 剩余名额按 MERT 嵌入做「最远点采样」⇒ 挑出内在最不相似的
④ 每个入选项再从它所属库取一批同类采样，让你有挑选余地
```

**并且**：

- ✅ **只推荐【能播放】的** —— 不能试听的**不占用名额**，会如实告诉你凑到几个
- ✅ **每个推荐给一批同类变体**（不同力度 / 麦位 / 尺寸）
- ✅ **风格不限定** —— 你说「Cymbals」时，电子镲片、电影感镲片同样算
- ✅ **播放器数量 = 它说的数量**（不会多报）
- ✅ **名字是弱证据时会说明** —— 例如「这组只是所在目录名含关键词，可能不是你要的乐器」

---

## 快速开始

### 方式一：下载发行版（推荐）

1. 到 [Releases](../../releases) 下载 **`KontaktAI-Agent-x.y.z-win-x64.zip`**
2. 解压到任意位置
3. **双击 `Launch.bat`**
4. 点右上角 **`⚙ 设置`** → 添加音色库根目录
5. 回 **概览** → **开始扫描**

### 方式二：从源码构建

```bat
git clone <this-repo>
cd KontaktLibManager\src\KontaktLibManager
dotnet build -c Release
cd ..\..
Launch.bat
```

**打包可分发目录**（含隐私自检）：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-dist.ps1
:: 产出 dist\
```

---

## 系统要求

| 项 | 要求 |
|---|---|
| **操作系统** | **Windows 10 / 11（64 位）** |
| **运行时** | **.NET 9 桌面运行时**（或更高；已设 `RollForward=LatestMajor`，**.NET 10 也能跑**） |
| **WebView2** | 通常系统已自带（Win10 1803+ / Win11 预装） |
| **磁盘** | **约 200 MB**（程序 + 内置 KSP 编译器）+ 索引数据 |
| **Kontakt** | **可选** —— 仅「加载 NKI 到 Kontakt」需要 |

**⚠️ AI 功能需要自备大模型 API**（OpenAI 兼容接口）。**不配置也能用其他全部功能。**
**API Key 只保存在本地 `data\index.db`，不会写进任何源码或配置文件。**

---

## 数据放在哪里

**默认**：`<程序目录>\data\`

```
data\
├─ index.db        ← 索引（库 / 乐器 / 采样 / 标签 / 会话）  ⚠️ 可能很大
├─ kspc.exe        ← KSP 编译器（内置）
├─ audio\ covers\  ← 缓存（自动清理）
├─ kb\             ← 知识库
└─ mert\           ← MERT 模型
```

**可用环境变量覆盖**：

```bat
set KLM_DATA_DIR=D:\KLM-Data
Launch.bat
```

**⚠️ 迁移时整个文件夹一起搬即可** —— 程序内部全部用**相对目录**、不写死绝对路径。

---

## 文档

| 文件 | 内容 |
|---|---|
| **[`INSTALL.md`](INSTALL.md)** | 安装与部署（含打包分发说明） |
| **[`USER-GUIDE.zh.md`](USER-GUIDE.zh.md)** | **用户手册（中文）** —— 每个页面怎么用、AI 怎么问、地图怎么看 |

---

## 技术栈

**C# .NET 9 (WPF)** · **WebView2**（界面是本地 HTML/CSS/JS，**无外部 CDN、完全离线**）· **SQLite** ·
**ONNX Runtime**（MERT 语义嵌入）· **UMAP** + **HdbscanSharp**（降维与聚类）·
**NAudio** + **NWaves**（音频解码与分析）· **Docnet**（PDF）

**音频格式支持**：`.ncw`（自研解码器）· `.wav` · `.ogg` · `.mp3` · `.aif`（自研 AIFF 解析）· `.nkx`（需 NI 产品密钥）

---

## 贡献

**欢迎 Issue 与 PR。** 提交前请：

1. 跑回归自测：`cd tests\KontaktLibManager.SelfTest && dotnet run -c Release`（**期望退出码 0**）
2. 前端改动请跑接线审计（`--wire-audit`），确保调用的 RPC 都存在
3. **不要提交 `data\` 里的任何内容**（含索引、模型、缓存 —— 都是用户数据）

---

## 协议

**PolyForm Noncommercial License 1.0.0** —— **源码可见、免费使用，但【禁止商业用途】。**

| | |
|---|---|
| **✅ 允许** | 个人学习 / 研究 / 实验 / 爱好项目；公益组织 / 教育机构 / 公立研究机构 / 政府机构 |
| **❌ 禁止** | **任何商业用途**（含公司内部商业目的、打包进商业产品、作为付费服务的一部分） |

**如需商业授权，请开 Issue 联系。**

> **⚠️ 说明**：**「禁止商用的开源协议」在法律上是矛盾的** —— OSI 认可的开源协议都不允许限制商用。
> 因此本项目采用 **source-available（源码可见但禁商用）** 的 PolyForm Noncommercial，
> **而不是** OSI 开源协议。详见 [`LICENSE`](LICENSE)。

---

## 免责声明

**本软件按「现状」提供，不附带任何担保。** 使用前请自行评估风险：

- **入库功能会写 Windows 注册表与 Kontakt 的 `Settings.cfg`** —— **请先备份**（工具内置备份与还原，但请自行确认）
- **本工具不修改音色库文件、不伪造授权字段、不绕过任何授权校验**
- **入库前请完全关闭 Kontakt**（它在退出时会覆写 `Settings.cfg`）

---

<details>
<summary><b>English Version</b></summary>

# Kontakt AI Agent

> A local manager for Native Instruments Kontakt libraries — **with a built-in AI agent that can actually listen.**

**Pure local. Never modifies your library files.**

## Features

| Feature | Description |
|---|---|
| **Library index** | Scan library folders; index libraries / instruments / samples / tags / covers |
| **Instrument browser** | Browse NKIs by category, search (English keyword → semantic), favourite, audition |
| **Ask AI** | **Built-in agent**: reads your PDF manuals, finds sounds, **renders inline players**, writes *and compiles* KSP scripts |
| **Sound Map** | **MERT embeddings + UMAP + HDBSCAN** — timbre similarity as an explorable, box-selectable map |
| **Health check** | Missing files / junk / missing covers, one-click fixes |
| **Maintain** | Orphan files, duplicate large files, snapshots |
| **Registration** | Reads `.nicnt`, writes the registry / Service Center so libraries appear in Kontakt's browser |
| **KSP** | **Bundled `kspc.exe` (71 MB)** — the agent compiles and verifies scripts for real |

## Finding sounds is actually acoustic

Not filename matching. The agent:

1. **Text search** (filename hit = strong evidence; path-only hit = weak)
2. **Guaranteed picks**: prefer items whose **filename truly contains the keyword**, spread across libraries
3. **Remaining slots**: **farthest-point sampling on MERT embeddings** → internally most-diverse picks
4. **Each pick gets a batch** of similar variants (different velocity / mic / size)

**It only recommends what it can actually play** — non-playable items never consume a slot, and it tells you
honestly how many it managed to find. Style is **not** restricted: electronic cymbals count as cymbals.

## Quick start

**Option 1 — download a release**

1. Grab `KontaktAI-Agent-x.y.z-win-x64.zip` from Releases
2. Extract anywhere
3. Run **`Launch.bat`**
4. **Settings** (top-right) → add your library root folders
5. **Overview** → **Start Scan**

**Option 2 — build from source**

```bat
git clone <this-repo>
cd KontaktLibManager\src\KontaktLibManager
dotnet build -c Release
cd ..\..
Launch.bat
```

**Build a distributable folder** (with a built-in privacy check):

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-dist.ps1
```

## Requirements

- **Windows 10 / 11 (64-bit)**
- **.NET 9 Desktop Runtime** (or newer; `RollForward=LatestMajor`, so .NET 10 works)
- **WebView2 Runtime** (usually preinstalled)
- **~200 MB disk** (app + bundled KSP compiler) plus index data
- **Kontakt** — optional

**AI features need your own OpenAI-compatible API key.** Everything else works without it.
**The key is stored only in the local `data\index.db`** — never in source or config files.

## Data location

`<app folder>\data\` by default. Override with `KLM_DATA_DIR`.
Everything is relative-path based — move the whole folder freely.

## Docs

- **`INSTALL.md`** — installation & packaging
- **`USER-GUIDE.zh.md`** — user guide (Chinese)

## Tech stack

**C# .NET 9 (WPF)** · **WebView2** (UI is local HTML/CSS/JS, **no CDN, fully offline**) · **SQLite** ·
**ONNX Runtime** (MERT) · **UMAP** + **HdbscanSharp** · **NAudio** + **NWaves** · **Docnet** (PDF)

**Audio formats**: `.ncw` (own decoder) · `.wav` · `.ogg` · `.mp3` · `.aif` (own AIFF parser) · `.nkx` (needs NI product key)

## Contributing

Issues and PRs welcome. Before submitting:

1. Run the regression self-test: `cd tests\KontaktLibManager.SelfTest && dotnet run -c Release` (expect exit code 0)
2. For frontend changes, run the wire audit (`--wire-audit`) to confirm every called RPC exists
3. **Never commit anything under `data\`** (index, models, caches — all user data)

## License

**PolyForm Noncommercial License 1.0.0** — source-available, **noncommercial use only**.

> **Note**: "an open-source license that forbids commercial use" is a legal contradiction — OSI-approved
> licenses may not restrict commercial use. This project is therefore **source-available**, not OSI open source.

## Disclaimer

**Provided "as is", without warranty of any kind.**

- **Registration writes to the Windows registry and Kontakt's `Settings.cfg`** — **back up first**
- This tool **does not modify library files, forge licence fields, or bypass any licence check**
- **Close Kontakt completely before registering** (it overwrites `Settings.cfg` on exit)

</details>
