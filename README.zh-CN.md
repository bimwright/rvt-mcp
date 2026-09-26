<!-- mcp-name: io.github.bimwright/rvt-mcp -->

<p align="center">
  <img src="https://raw.githubusercontent.com/bimwright/.github/master/assets/logos/rvt-mcp.png" alt="rvt-mcp" width="180" />
</p>

<h1 align="center">rvt-mcp</h1>

<p align="center">
  Autodesk Revit 的 MCP 网关 — 本地 agent 工具，可选个人 bake 循环
</p>

<p align="center">
  <a href="https://github.com/bimwright/rvt-mcp/actions/workflows/build.yml"><img src="https://github.com/bimwright/rvt-mcp/actions/workflows/build.yml/badge.svg" alt="build" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-Apache%202.0-blue.svg" alt="license" /></a>
  <a href="#supported-revit-versions"><img src="https://img.shields.io/badge/Revit-2022--2027-186BFF" alt="Revit 2022-2027" /></a>
  <a href="#tools"><img src="https://img.shields.io/badge/MCP-230%20tools-6C47FF" alt="MCP tools" /></a>
  <a href="https://github.com/bimwright/rvt-mcp/releases/latest"><img src="https://img.shields.io/github/v/release/bimwright/rvt-mcp" alt="latest release" /></a>
  <a href="CHANGELOG.md"><img src="https://img.shields.io/badge/changelog-version%20history-informational" alt="changelog" /></a>
</p>

<p align="center">
  <a href="README.md">English</a> · <a href="README.vi.md">Tiếng Việt</a> · 简体中文 · <a href="README.ja.md">日本語</a>
</p>

---

## 这是什么

`rvt-mcp` 是 MCP 客户端与正在运行的 Revit 会话之间的**本地**桥。.NET 8 server 通过 stdio 提供 MCP；每个 Revit 年份（2022–2027）一个瘦 add-in 在 Revit 内运行，经 localhost TCP（≤2024）或 named pipe（≥2025）连接。数据不出本机，全部为 C#，工具边界的长度单位为 mm。细节：[ARCHITECTURE.md](ARCHITECTURE.md)。

Agent 可以使用覆盖常见 Revit 工作的 **typed 工具面**、应对其余情况的 C# escape hatch，以及把重复模式变成个人工具的**可选**路径（ToolBaker）：从共享运行时出发，在其上长出*你的*工具。Family Editor 创作暂不在范围内（[路线图](docs/roadmap.md)）。

---

## 安装

使用 [GitHub Releases](https://github.com/bimwright/rvt-mcp/releases/latest) 中的 setup ZIP：内含自包含 server 与 Revit 2022–2027 插件，不需要 .NET SDK 或克隆源码。**AI agent：** 按 [AGENTS.md](AGENTS.md) 操作；除非用户要求开发者安装，否则不要 clone 或 build。

```powershell
$tag = (Invoke-RestMethod https://api.github.com/repos/bimwright/rvt-mcp/releases/latest).tag_name
$zip = "$env:TEMP\RvtMcp.Setup-$tag-win-x64.zip"
$dir = "$env:TEMP\RvtMcp.Setup-$tag-win-x64"
Invoke-WebRequest "https://github.com/bimwright/rvt-mcp/releases/download/$tag/RvtMcp.Setup-$tag-win-x64.zip" -OutFile $zip
Expand-Archive $zip -DestinationPath $dir -Force

powershell -ExecutionPolicy Bypass -File "$dir\install.ps1" -WhatIf
powershell -ExecutionPolicy Bypass -File "$dir\install.ps1"
```

请先关闭 Revit。安装程序会检测存在 `Revit.exe` 的 Revit 2022–2027，把对应插件和 server 安装到 `%LOCALAPPDATA%\RvtMcp\rvt\server\current\rvt-mcp.exe`，校验两者，出错时回滚。安装程序不会改动 MCP 客户端配置。细节与其他安装方式（开发者、仅 NuGet server）：[docs/install.md](docs/install.md)。

### 连接 MCP 客户端

注册一个名为 `rvt-mcp` 的 stdio server，命令为上述 server 路径（写成绝对路径），可用客户端自己的 `mcp add` 命令、设置界面或配置文件。任何 stdio MCP 客户端都可以使用（Claude Code、Claude Desktop、Codex、Cursor、VS Code、Gemini CLI、OpenCode、Kilo 等）。已验证的各客户端步骤：[docs/mcp-client-wiring.md](docs/mcp-client-wiring.md)。

### 验证是否可用

1. 打开带模型的 Revit。
2. 在 ribbon（**附加模块**（Add-Ins）选项卡 → **RvtMcp** 面板）上启动 MCP 连接。
3. 在 MCP 客户端中 list tools，再调用 `revit_get_current_view_info`。

大致应得到：

```json
{ "viewName": "Level 1", "viewType": "FloorPlan", "levelName": "Level 1", "scale": 100 }
```

失败则安装未完成 — 先修客户端配置或插件加载。

### 升级

关闭 Revit 和 MCP 客户端，把新版本 ZIP 解压到新文件夹，运行其中的 `install.ps1 -WhatIf`，再运行 `install.ps1` — 不要先卸载。server 路径保持不变，客户端只需重启。从 v0.6.2 或更早版本升级？请把客户端改指向上面的 `current` 路径，再用 `install.ps1 -PruneOldServers` 删除旧 server 文件夹。更多：[docs/install.md](docs/install.md#upgrade)。

### 卸载

在 setup ZIP 文件夹中：

```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -WhatIf
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -Yes
```

移除插件和 server，但保留设置、翻译、ToolBaker 数据和日志，除非加上 `-Purge`。请自行在 MCP 客户端中删除 `rvt-mcp` 条目。更多：[docs/install.md](docs/install.md#uninstall)。

---

## Tools

| 模式 | Tools | 说明 |
|------|------:|------|
| 默认 | **41** | `query` + `create` + `view` + `meta` |
| `--toolsets all` | **230** | 完整目录 |
| `all` + adaptive bake | **233** | 再加 3 个 suggestion 生命周期工具 |

数量不含个人 baked 工具。其余 toolset 默认关闭，需显式开启，例如 `--toolsets query,view,meta,mep` 或 `--toolsets all`；`--read-only` 会去掉所有可写 toolset（含 `create`）。

| Toolset | 覆盖 |
|---------|------|
| `query` | 视图、选择、过滤、统计、参数、关系、workset、组/程序集 |
| `create` | 轴网、标高、房间、线/点/面构件、组 |
| `view` | 建视图、图纸布局辅助、截图、裁剪/比例 |
| `meta` | 批处理（最多 20）、多 Revit 目标、项目信息、purge（MVP）、消息、send_code |
| `lint` | 视图命名、firm-profile、警告摘要 |
| `schedule` | 明细表 list/创建、字段、公式、数据 |
| `families` | 加载/卸载、类型、实例、审计、导出 `.rfa`（项目侧） |
| `modify` | 操作/着色、写参数、换类型、workset |
| `delete` | 按 id 删除 |
| `annotation` | 标记、文字、尺寸、填充、keynote、检查 |
| `export` | PDF/DWG/IFC/NWC、房间数据及相关导出 |
| `mep` | 系统、连接件、网络、风口灯具等 |
| `graphics` | 视图过滤器、覆盖、可见性/阶段 |
| `toolbaker` | list/run baked；adaptive 开才有 suggestion 工具 |
| `sheets` | 图纸、图框、修订、重编号 |
| `materials` | 材质、外观、赋值、提量 |
| `geometry` | 包围盒、测量、碰撞、体积/面积… |
| `rooms` | 房间/面积/空间、装修、分隔 |
| `links` | Revit/CAD 链接、坐标审计、获取/发布坐标 |
| `parameters` | 项目/共享参数 |
| `organization` | 保存选择、视图样板 |
| `workflows` | 碰撞/审计/图纸/提量类组合流 |
| `structural` | 柱梁基础、钢筋、荷载… |
| `kei` | KEI 项目 SQLite 路径、查询/写入（WAL 安全）、设备导入 |

### send_code、ToolBaker、ribbon 与界面语言

- **`revit_send_code_to_revit`**（默认开启）在没有合适 typed 工具时，于 Revit 内编译并运行 C# 正文；`--read-only` 或 `--disable-toolbaker` 会移除它。见 [docs/send-code.md](docs/send-code.md)；楼梯见 [docs/stairs-workflow.md](docs/stairs-workflow.md)。
- **ToolBaker：** `revit_list_baked_tools` / `revit_run_baked_tool` 需要 `--toolsets toolbaker`。Adaptive bake（`--enable-adaptive-bake`，默认关闭）会根据重复调用建议工具；在你 accept 之前不会添加任何东西。Bake 在 Revit 内编译，不需要 Visual Studio。见 [docs/bake.md](docs/bake.md)。
- **Ribbon：** 启动或停止连接，打开 **History** 搜索并重跑历史调用，切换完成 **Toast**（默认开启）。
- **界面语言：** 插件 UI 支持 15 种语言，默认跟随 Revit 的界面语言；可在 ribbon 滑出面板的 **Language** 下拉框中更改。工具名与 payload 保持英文。见 [docs/localization.md](docs/localization.md)。

---

## 配置

优先级从高到低：**CLI → env（`BIMWRIGHT_*`）→** `%LOCALAPPDATA%\RvtMcp\rvtmcp.config.json`。

| 设置 | CLI | Env | JSON |
|------|-----|-----|------|
| 目标年份 | `--target 2024` | `BIMWRIGHT_TARGET` | `target` |
| Toolsets | `--toolsets query,create` | `BIMWRIGHT_TOOLSETS` | `toolsets` |
| 只读 | `--read-only` | `BIMWRIGHT_READ_ONLY=1` | `readOnly` |
| LAN 绑定（插件） | — | `BIMWRIGHT_ALLOW_LAN_BIND=1` | `allowLanBind` |
| ToolBaker 表面 | `--enable-toolbaker` / `--disable-toolbaker` | `BIMWRIGHT_ENABLE_TOOLBAKER` | `enableToolbaker` |
| Adaptive bake | `--enable-adaptive-bake` / `--disable-adaptive-bake` | `BIMWRIGHT_ENABLE_ADAPTIVE_BAKE=1` | `enableAdaptiveBake` |
| 缓存 send_code 正文（bake 聚类） | `--cache-send-code-bodies` / `--no-…` | `BIMWRIGHT_CACHE_SEND_CODE_BODIES=1` | `cacheSendCodeBodies` |
| 持久化 send_code journal | `--persist-send-code-bodies` / `--no-…` | `BIMWRIGHT_PERSIST_SEND_CODE_BODIES=1` | `persistSendCodeBodies` |
| Journal TTL | `--persist-send-code-bodies-for 4h` | `BIMWRIGHT_PERSIST_SEND_CODE_BODIES_TTL` | `persistSendCodeBodiesUntil` |
| 完成 toast（默认开启） | ribbon **Toast** | `BIMWRIGHT_ENABLE_TOAST=0` | `enableToast` |
| 界面语言（插件） | ribbon **Language** | `BIMWRIGHT_UI_LANGUAGE` | `uiLanguage` |

改 server 标志后请重启 MCP 连接，以便客户端拿到新工具列表。

---

## Supported Revit versions

| Revit | 插件 TFM | 传输 |
|-------|----------|------|
| 2022–2024 | .NET Framework 4.8 | TCP |
| 2025–2026 | .NET 8 (`net8.0-windows7.0`) | Named Pipe |
| 2027 | .NET 10 (`net10.0-windows7.0`) | Named Pipe |

仅支持完整 Revit 桌面版，不支持 Revit Viewer。CI 会构建全部六个插件，但运行时深度因年份而异 — baked 工具与自定义 C# 请在你使用的年份上复测。

---

## 安全与隐私

- 默认本地：loopback TCP 或本机 named pipe，`%LOCALAPPDATA%\RvtMcp\` 下的 discovery 文件含每会话 auth token。
- 工具参数在 handler 运行前做 schema 校验；返回模型的错误经过脱敏。
- `send_code` 可在 Revit 进程中运行任意 C# — 强大且有风险。无法接受时请使用 `--read-only` 或 `--disable-toolbaker`。
- Adaptive bake、body cache 与 send_code journal 均为 opt-in，留在用户配置目录下；默认不把原始 send_code 正文写入长期日志。

更多：[SECURITY.md](SECURITY.md)、[docs/bake.md](docs/bake.md)。

---

## 文档

| 文档 | 主题 |
|------|------|
| [AGENTS.md](AGENTS.md) | Agent 安装协议 |
| [docs/install.md](docs/install.md) | 安装程序细节、升级、卸载、开发者与 NuGet 安装 |
| [docs/mcp-client-wiring.md](docs/mcp-client-wiring.md) | 各客户端 MCP 接入步骤 |
| [ARCHITECTURE.md](ARCHITECTURE.md) | 进程、传输、DTO 规则 |
| [docs/send-code.md](docs/send-code.md) | send_code 源码形式与失败处理 |
| [docs/bake.md](docs/bake.md) | Adaptive bake 与正文隐私 |
| [docs/localization.md](docs/localization.md) | 界面语言、覆盖、热重载 |
| [docs/roadmap.md](docs/roadmap.md) | 近期加固与 non-goals |
| [docs/kei-equipment-import.md](docs/kei-equipment-import.md) | KEI SQLite 工具（`--toolsets kei`） |
| [CONTRIBUTING.md](CONTRIBUTING.md) | 构建、测试、新增工具 |
| [CHANGELOG.md](CHANGELOG.md) | 发布说明 |

---

## 社区贡献

问题报告、复现示例和建议帮助 rvt-mcp 持续改进。感谢：

| 贡献者 | 贡献 |
|--------|------|
| [@thiagobarretosn-hue](https://github.com/thiagobarretosn-hue) | 提供 MEP 网络成员和管道系统处理问题的复现报告（[#11](https://github.com/bimwright/rvt-mcp/issues/11)、[#12](https://github.com/bimwright/rvt-mcp/issues/12)）。 |
| [@razmikb](https://github.com/razmikb) | 报告宿主族放置和楼梯/send-code 错误，推动了放置验证、辅助类支持和更全面的错误处理测试（[#13](https://github.com/bimwright/rvt-mcp/issues/13)、[#14](https://github.com/bimwright/rvt-mcp/issues/14)）。 |
| [@Thestreetarckitect](https://github.com/Thestreetarckitect) | 提出 Family Authoring Tool Suite 建议，帮助明确路线图和产品范围（[#7](https://github.com/bimwright/rvt-mcp/issues/7)）。 |

---

## bimwright

连接 AI 助手与 BIM、CAD 应用的开源工具。

**bimwright** 这个名字由 **BIM** 和 **wright** 组成。wright 是英语中表示制作者或建造者的旧词，如 *shipwright*（造船工）。

- [rvt-mcp](https://github.com/bimwright/rvt-mcp) — Revit  
- [dwg-mcp](https://github.com/bimwright/dwg-mcp) — AutoCAD  
- [nwd-mcp](https://github.com/bimwright/nwd-mcp) — Navisworks  
- [ipt-mcp](https://github.com/bimwright/ipt-mcp) — Inventor  
- [bim-wiki](https://github.com/bimwright/bim-wiki) — 越南语优先 BIM 知识库  

---

## License

Apache-2.0 — [LICENSE](LICENSE)。

欢迎 fork 和重新品牌化——只需遵守许可证条款（保留 `LICENSE` 和版权声明，并标注已修改的文件）。如果 rvt-mcp 对你有帮助，欢迎 star 或在你的产品中提及 BIMwright，但这完全是自愿的。随时欢迎 Issue 和 PR。

Revit 与 Autodesk 为 Autodesk, Inc. 商标。bimwright 为独立开源项目，与 Autodesk 无隶属关系。
