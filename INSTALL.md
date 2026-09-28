# 安装与使用说明

> **Kontakt 音色库管理器** —— 面向 Native Instruments Kontakt 音色库的本地管理工具。
> 本文件是**中文安装说明**。英文版见文末[折叠区](#english-version)。

---

## 一、这是什么

一个**纯本地**的 Kontakt 音色库管理工具，主要能力：

| 能力 | 说明 |
|---|---|
| **音色库清单** | 扫描本地音色库目录，建立索引（库名 / 路径 / 乐器 / 采样） |
| **乐器中心** | 按类别浏览 NKI 乐器、搜索、收藏 |
| **说明书知识库** | 把手册 PDF 转成可检索的知识库（含 OCR） |
| **问问 AI** | 内置 Agent，可查手册、找音色、推荐并**直接试听** |
| **音色地图** | 用 MERT 语义嵌入 + UMAP 降维 + HDBSCAN 聚类，可视化「音色相似度」 |
| **健康检查 / 维护** | 找缺失文件、重复库、杂质文件，一键清理 |

**⚠️ 全程离线运行**（除你主动开启 AI 联网功能）。**音色库数据只在本机处理，不上传。**

---

## 二、运行环境要求

| 项 | 要求 | 说明 |
|---|---|---|
| **操作系统** | **Windows 10 / 11（64 位）** | 依赖 WebView2 与 WPF |
| **.NET 运行时** | **.NET 9 桌面运行时**（或更高） | 项目设了 `RollForward=LatestMajor`，**装 .NET 10 也能跑** |
| **WebView2 运行时** | 通常**系统已自带** | Win10 1803+ / Win11 预装；若缺失请装 [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) |
| **磁盘空间** | **约 200 MB**（程序 + KSP 编译器）<br>**+ 索引数据**（视音色库规模） | 索引数据库会随库数量增长 |
| **Kontakt** | **可选** | 仅「加载 NKI 到 Kontakt」功能需要 |

> **获取 .NET 9 桌面运行时**：<https://dotnet.microsoft.com/download/dotnet/9.0>
> 选 **Desktop Runtime**（不是 ASP.NET 那个）。

---

## 三、安装方式

### 方式一：便携版（推荐，解压即用）

**✅ 这是首选方式 —— 无需安装、可直接放到 U 盘/移动硬盘。**

1. **解压** 整个文件夹到任意位置（例如 `D:\KontaktLibManager\`）
   - ⚠️ **路径不要含特殊符号**，中文路径可以，但建议用简短英文路径
2. **双击 `Launch.bat`**
3. 首次启动会自动创建数据目录与索引数据库

**如需从源码重新编译后再启动**，用 `Rebuild-and-Launch.bat`（它会先关掉正在运行的实例、编译、再启动）。

### 方式二：从源码构建

```bat
:: 1) 安装 .NET 9 SDK
::    https://dotnet.microsoft.com/download/dotnet/9.0

:: 2) 构建
cd src\KontaktLibManager
dotnet build -c Release

:: 3) 启动
cd ..\..
Launch.bat
```

**回归自测**（可选，验证核心链路未被改坏）：

```bat
cd tests\KontaktLibManager.SelfTest
dotnet run -c Release
:: 期望退出码 0
```

---

## 四、首次启动

1. **双击 `Launch.bat`** ⇒ 出现主窗口
2. 进入 **「概览」** 页
3. 点右上角 **「⚙ 设置」** ⇒ **添加音色库根目录**（例如 `D:\Kontakt Libraries`、`D:\Kontakt Libraries\8dio`）
4. 回 **「概览」** 点 **「开始扫描」** ⇒ 建立索引
5. 扫描完成后即可使用 **「音色库列表」「乐器中心」** 等页面

**⚠️ 扫描会读取目录结构，不会移动或修改你的音色库文件。**

---

## 五、数据放在哪里

**默认**：`<程序所在目录>\data\`

```
<程序目录>\
├─ Launch.bat
├─ KontaktLibManager.exe
├─ wwwroot\            ← 界面资源（不要删）
└─ data\               ← 全部数据在这里
   ├─ index.db         ← 索引数据库（库/乐器/采样/标签/会话）
   ├─ kspc.exe         ← KSP 编译器（内置，71 MB）
   ├─ audio\           ← 试听缓存（自动清理，超过 3 天 / 300 MB）
   ├─ covers\          ← 封面缓存
   ├─ kb\              ← 知识库（手册转出的文本与向量）
   ├─ exports\         ← 你导出的 CSV
   ├─ ksp-delivered\   ← 生成好的 KSP 脚本
   └─ mert\            ← MERT 模型（首次用音色地图时下载/放置）
```

**⚠️ 迁移时整个文件夹一起搬即可** —— 程序内部全部用**相对目录**，不写死绝对路径。

**如需把数据放到别处**（例如大容量盘），设环境变量：

```bat
set KLM_DATA_DIR=D:\KLM-Data
Launch.bat
```

**不设该变量时，默认用 `<exe 目录>\data`。**

---

## 六、配置 AI（可选）

「问问 AI」需要一个大模型 API。**不配置也能用其他全部功能。**

1. **「⚙ 设置」→ AI 设置**
2. 填 **Base URL / API Key / 模型名**
3. **⚠️ API Key 只保存在本地数据库**（`data\index.db`），**不会写进任何源码或配置文件**

**关于权限档位**（「问问 AI」页右上角）：

| 档位 | 行为 |
|---|---|
| **🔒 只读** | Agent 只能查索引与知识库（**默认**） |
| **✏️ 读写** | 可执行**只读命令**（自动放行）；**改文件会逐条问你** |
| **⚡ 完全** | 全部放行，**不再逐条确认**（请确认信任当前任务再用） |

---

## 七、打包分发说明（给维护者）

**⚠️ 打包时必须排除用户数据**，否则会把几百 MB 的索引与模型一起发出去。

**应该包含**：
```
KontaktLibManager.exe / .dll / .json 等运行文件
wwwroot\                ← 界面
data\kspc.exe           ← KSP 编译器（71 MB，按当前策略【内置】）
```

**必须排除**：
```
data\index.db           ← 用户索引（实测可达 175 MB）
data\mert\              ← MERT 模型（可达 500 MB）
data\kb\ audio\ covers\ exports\ ksp-delivered\   ← 用户数据/缓存
```

**⇒ 打好的包约 77 MB**（5.4 MB 程序 + 71 MB KSP 编译器）。

**⚠️ 若要把 `kspc.exe` 改为「首次运行时下载」**（可把包压到 ~6 MB），
需同步修改程序里的 KSP 编译调用点；**当前策略是【内置】。**

---

## 八、常见问题

**Q：双击 `Launch.bat` 窗口一闪而过？**
A：多半是缺 .NET 运行时。用命令行跑 `Launch.bat` 看报错；或先装 [.NET 9 桌面运行时](https://dotnet.microsoft.com/download/dotnet/9.0)。

**Q：界面一片空白 / 转圈？**
A：缺 **WebView2 运行时**。装一下即可（Win11 一般自带）。

**Q：扫描很慢？**
A：**首次扫描**要遍历所有库的文件（几万个），慢是正常的。之后是增量扫描。

**Q：试听没声音？**
A：① 先确认系统音量；② 部分采样是 `.nkx` 加密容器，**本机拿不到 NI 产品密钥时无法解码**（会在界面如实说明）；③ 试听缓存超时会在首次解码时慢 10~40 ms，属正常。

**Q：音色地图一直是空的？**
A：**音色地图需要先「开始分析」提取特征**（纯 CPU，较慢），再「重算地图」。三步顺序见该页按钮的提示文字。

**Q：想整个搬走（换电脑 / U 盘）？**
A：**把整个程序文件夹复制过去即可** —— 数据在 `data\` 里、路径全部相对。若用了 `KLM_DATA_DIR`，记得一并迁移并重设。

**Q：AI 说「无法解码」，是真的吗？**
A：**要看具体格式**。本机支持 `.ncw` / `.wav` / `.ogg` / `.mp3` / `.aif`；**只有 `.nkx`（加密容器）需要 NI 产品密钥**，拿不到时会失败。

---

## 九、卸载

**便携版没有安装过程** ⇒ **直接删除整个文件夹即可**。

⚠️ 如果它扫描过你的音色库，**你的音色库文件【不会被改动】** —— 本工具只读取目录结构与音频内容。

---

<details>
<summary><b>English Version</b></summary>

### Kontakt Library Manager — Installation

**Requirements**
- Windows 10/11 (64-bit)
- .NET 9 Desktop Runtime (or newer; `RollForward=LatestMajor` means .NET 10 works)
- WebView2 Runtime (usually preinstalled)

**Portable install**
1. Extract the folder anywhere
2. Run `Launch.bat`
3. Open **Settings** (top-right) → add your Kontakt library root folders
4. Go to **Overview** → **Start Scan**

**Data location**: `<app folder>\data\` by default. Override with the `KLM_DATA_DIR` environment variable.
Everything is relative-path based, so you can move the whole folder freely.

**AI (optional)**: Settings → AI. The API key is stored **only** in the local `data\index.db`.

**Uninstall**: delete the folder. Your Kontakt libraries are never modified.

</details>
