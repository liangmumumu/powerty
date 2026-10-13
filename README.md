# Qwaacty

Windows 桌面中英实时翻译 / 查词工具：**键入、划词、截图** 三路取词，本地词库优先，AI 辅助翻译与语境增强。C# / .NET 8 + WPF，VS Code 深色风格界面，常驻系统托盘。

## 功能

- **键入实时翻译**：打字停顿约 0.5 秒后自动查询，浮窗跟随输入光标；英文显示音标与分组释义，中文显示英文反查候选
- **划词翻译**：拖选即译（自动模式），或选中文本后按快捷键查询；支持"AI 上下文增强"按语境给出情境化译文
- **截图翻译**：框选屏幕任意区域，Windows 离线 OCR 识别后翻译；适用于图片、视频字幕等无法复制的文字
- **专业词库（电子信息）**：按 `Ctrl+Alt+Shift+P` 开启专业模式；开启后划词/截图优先查询专业词库（约 5,700 条中英术语、概念与缩略语：学术性中文详解 + 中英同字号对照 + 百科链接，来源包括 ADI 官方术语表等），顶部「AI 增强翻译」按钮按需生成 AI 译文
- **三路查询链路**：本地词库（约 43 万词条，完全离线）→ AI（OpenAI 兼容接口，流式输出）→ MyMemory 在线兜底
- **词形还原**：running→run、studies→study 等自动还原；中英混排自动判定翻译方向
- **隐私**：在线兜底默认开启、可在设置中关闭；只发送当前查询片段（开启「AI 上下文增强」时会附带选区前后少量语境，最多 360 字符）；终端窗口（PuTTY/Windows Terminal/mintty 等）内的键入不会联网，仅本地词库；专业词库查询完全离线（仅当你点击百科链接时才打开浏览器）；不保存键入内容与查询历史；AI Key 经 Windows 加密后存于本机设置

## 使用

构建产物提供三种形式：

| 形式 | 说明 |
| --- | --- |
| `Qwaacty-Setup.exe` | 安装包：当前用户安装，可选开机自启 |
| `Qwaacty-Green-x64.zip` | 绿色版：自包含单文件，解压即用，无需安装 .NET |
| `artifacts\publish\` | 免安装运行版：需要 .NET 8 桌面运行时 |

默认快捷键（均在设置页可改）：

| 功能 | 快捷键 |
| --- | --- |
| 键入实时翻译 开关 | `Ctrl+Alt+Shift+T` |
| 自动划词翻译 开关 | `Ctrl+Alt+Shift+A` |
| 快捷键划词翻译 触发 / 开关 | `Ctrl+Alt+Shift+D` / `Ctrl+Alt+Shift+S` |
| 截图翻译 | `Ctrl+Alt+Shift+X` |
| 专业词库 开关 | `Ctrl+Alt+Shift+P` |

设置与诊断日志位于 `%LOCALAPPDATA%\Qwaacty\`（绿色版为程序目录）。关闭主窗口后程序驻留托盘，右键托盘图标可退出。

**首次使用请先下载词库**：词库不再随安装包分发，在主窗口「词库」页点击下载即可（中英词库约 34MB、专业词库约 1MB，下载后立即生效；下载源为本项目的 GitHub release `dicts-v1`）。

备注：
- 微信 / QQ 等无法读取文本的应用，请开启「兼容模式」（缓冲最近键入字符，仅内存）
- 密码输入框自动跳过，不查词、不联网
- 程序只允许运行一个实例
- 单文件自包含版首次启动会把原生库解压到 `%TEMP%\.net`（.NET 标准行为）

## 构建

要求：Windows 10/11、.NET 8 SDK。

```powershell
.\build.ps1                  # 运行核心检查并发布到 artifacts\publish
.\build.ps1 -SelfContained   # 自包含单文件版
.\build.ps1 -Portable        # 绿色版文件夹 + zip
```

运行测试：

```powershell
dotnet run --project .\tests\Qwaacty.Core.Tests\Qwaacty.Core.Tests.csproj -c Release
```

生成安装包：安装 [Inno Setup 6](https://jrsoftware.org/isinfo.php) 后，`build.ps1` 会自动编译 `installer\Qwaacty.iss`（或手动用 ISCC 编译）。

## 离线词库（可选）

仓库不包含词库数据；缺失时应用使用内置演示词条。完整词库由 `build.ps1` 合并以下开源数据源生成 `dicts\lexicon.ttlx`（约 34 MB）：

- [qwerty-learner](https://github.com/Kaiyiwing/qwerty-learner)（GPL-3.0）——英语词库
- [ECDICT](https://github.com/skywind3000/ECDICT)（MIT）——词典数据
- `tools\Qwaacty.LexiconBuilder\curated.tsv`——少量人工校对的高频对话词补充

将数据源放到本机任意位置，通过参数指定即可：

```powershell
.\build.ps1 -LexiconSource <qwerty-learner 仓库路径> -EcdictSource <ecdict.csv 路径>
```

词库数据版权归上述开源项目所有，本项目仅作个人学习用途；如需再分发词库产物请遵守其许可要求。许可与来源明细见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

专业词库 `eedict.json`（约 5,700 条）由 ADI 官方术语表（`analog.com`，经社区抓取整理）、Phillweston/EE_Vocabulary《电子类专业英语词汇》、安徽大学课程词表与项目整理术语合并生成，英文释义兜底来自 ECDICT；数据同步脚本为 `tools\Qwaacty.LexiconBuilder\data\sync-external-vocab.ps1`。

## 项目结构

| 目录 | 内容 |
| --- | --- |
| `src\Qwaacty.App` | WPF 应用：界面、全局热键、键入 / 划词 / 截图模块 |
| `src\Qwaacty.Core` | 核心库：词库查询、AI 翻译、文本解析、OCR 清理 |
| `tests\Qwaacty.Core.Tests` | 核心行为检查（约 40 项） |
| `tools\` | 词库构建器、界面预览工具 |
| `installer\` | Inno Setup 安装脚本 |
