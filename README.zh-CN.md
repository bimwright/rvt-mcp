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
  <a href="#tools"><img src="https://img.shields.io/badge/MCP-229%20tools-6C47FF" alt="MCP tools" /></a>
</p>

<p align="center">
  <a href="README.md">English</a> · <a href="README.vi.md">Tiếng Việt</a> · 简体中文 · <a href="README.ja.md">日本語</a>
</p>

---

## 安装

**目前没有已发布的 setup ZIP。** GitHub Releases 已撤下，因此不再提供 v0.5.0 及更早版本的下载。v0.6.0 客户端 ZIP 尚未发布。

**不要**下载旧 tag、旧的 `RvtMcp.Setup-*.zip` URL，或执行 `dotnet tool install -g Bimwright.Rvt.Server` / `RvtMcp.Server`。

**若你是 AI agent：** 停止。告知用户目前没有公开安装包。除非用户明确要求开发者安装，否则不要 clone、build 或从 NuGet 安装。协议见 [AGENTS.md](AGENTS.md)。

在新的 GitHub Release 发布之前，唯一受支持的安装方式是从源码构建（见下方开发者安装）。

AutoCAD 请用独立的 [dwg-mcp](https://github.com/bimwright/dwg-mcp) — 不同产品、不同安装。

### 验证是否可用

1. 打开带模型的 Revit。
2. 在 ribbon（BIMwright / RvtMcp）上启动 MCP 连接。
3. 在 MCP 客户端中 list tools，再调用 `revit_get_current_view_info`。

大致应得到：

```json
{ "viewName": "Level 1", "viewType": "FloorPlan", "levelName": "Level 1", "scale": 100 }
```

失败则安装未完成 — 先修客户端配置 / 插件加载。

### 卸载

从 setup ZIP（或仓库 scripts）：

```powershell
# Setup ZIP:
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -WhatIf
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -Yes

# Clone 仓库:
powershell -ExecutionPolicy Bypass -File .\scripts\uninstall-all.ps1 -WhatIf
powershell -ExecutionPolicy Bypass -File .\scripts\uninstall-all.ps1 -Yes
```

移除插件、自包含 server、客户端条目、discovery、日志与 ToolBaker 缓存。

### 开发者安装

```powershell
git clone https://github.com/bimwright/rvt-mcp.git
cd rvt-mcp
dotnet build src/RvtMcp.sln -c Debug
powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1 -SourceDir . -Client none
```

请先关闭所有 Revit — 构建会把插件 DLL 部署到 `%APPDATA%\Autodesk\Revit\Addins\<year>\RvtMcp\`。`install.ps1` 会检测 Revit 2022–2027，将 server 复制到 `%LOCALAPPDATA%\RvtMcp\rvt\server\<version>\`，并写入已检测到的 MCP 客户端（`-Client codex|opencode|claude|kilo|none`，只要一年可用 `-Years 2024`）。

不要对 `RvtMcp.Server` 或 `Bimwright.Rvt.Server` 执行 `dotnet tool install` — 那不是当前安装路径。

### 从 `Bimwright.Rvt.*`（v0.3 及更早）迁移

v0.4+ 将包名/目录改为 `RvtMcp.*`（仓库与品牌仍为 bimwright）。

1. 关闭所有 Revit。
2. `pwsh scripts/uninstall-old.ps1` — 删除旧 `%APPDATA%\…\Bimwright\` 插件与旧 server 根；保留用户 bake/journal，首次启动新版本时迁到 `%LOCALAPPDATA%\RvtMcp\`。
3. 从源码安装（上方开发者安装）。等待 v0.6.0 GitHub Release ZIP，不要安装任何更早的已发布包。
4. MCP 客户端入口名为 **`rvt-mcp`**（旧的按年 `bimwright-rvt-r22`… 条目由安装程序移除）。

---

## 这是什么

`rvt-mcp` 是 MCP 客户端（Claude、Cursor、Codex、OpenCode 等）与正在运行的 Revit 会话之间的**本地**桥。

两个进程：

| 组件 | 作用 |
|------|------|
| **RvtMcp.Server** | .NET 8 MCP server（stdio）。不引用 Revit — 任意机器可编。 |
| **RvtMcp.Plugin** | 每个 Revit 年一份瘦 add-in（2022–2027）。在 Revit 内、UI 线程执行。 |

Agent → MCP → server → localhost TCP（≤2024）或 Named Pipe（≥2025）→ plugin → Revit API。

全部在本机。网关本身不需要云中继。

没有 Node/TypeScript sidecar。Server、插件、handler、ToolBaker 全是 C#。共享命令在 `src/shared/`；每年一个小 shell，API 漂移用 `#if`。细节：[ARCHITECTURE.md](ARCHITECTURE.md)。

---

## 为什么存在

Revit 用户通常清楚要自动化什么。难点是把想法变成可交付软件：学够 C#/Dynamo、对抗 API、打包 add-in、扛住版本升级 — 或者外包、或买只能半匹配办公室流程的固定工具。

Agent 改善了前半段（描述任务、当场试）。它们不会消掉 transaction、单位、选择、worksharing，或「模型刚被搞坏了吗？」。本网关负责：常见工作的 **typed 工具面**、需要时在 Revit 内跑 ad-hoc C# 的 escape hatch，以及把本地重复模式变成个人工具的**可选**路径（ToolBaker）。

不是给每家公司的万能 add-in。办公室各不相同。赌注是：共享运行时，在其上长出*你的*工具。

**范围（坦白）：** 不为每个边角情况都新铸 MCP tool。有 typed 工具就用；否则 `revit_send_code_to_revit`（仅 C#）。项目内 family **管理**有覆盖；完整 Family Editor 创作套件与 Revit Viewer 宿主暂不在范围内 — 见 [docs/roadmap.md](docs/roadmap.md)。

---

## 一次正常会话

1. 打开带模型的 Revit；插件已连接（ribbon）。
2. MCP 客户端启动 `rvt-mcp` / 已安装的 server。
3. Agent 调工具：查询视图/选择、建轴网/房间、图纸、MEP、导出… 工具边界长度单位为 **mm**。
4. 多次写入一次撤销：`revit_batch_execute`。
5. 多开 Revit：`revit_list_available_targets` 再 `revit_switch_target`，年份四位数字（`2024`，不是 `R24`）。

没有合适 typed 工具时：

```text
revit_send_code_to_revit   # C# 正文，在插件内编译执行
```

该工具默认开启（toolset `meta`）。若不希望 agent 在模型里编译代码，用 `--read-only` 或 `--disable-toolbaker` 关掉。

### ToolBaker（可选）

默认表面含 `revit_send_code_to_revit`。`revit_list_baked_tools` / `revit_run_baked_tool` 需要 `--toolsets toolbaker`（或 `--toolsets all`）。

**Adaptive bake**（从 usage 建议新工具）默认**关闭**。开启后，重复模式可出现在 `revit_list_bake_suggestions`；需你显式 accept/dismiss。未 accept 不会自己上 ribbon。

常用开关（亦见 JSON/env — [配置](#配置)）：

| 目标 | 打开什么 |
|------|----------|
| 从重复 **typed** 调用学习 | `--enable-adaptive-bake` |
| 也对 **`send_code`** 正文聚类建议 | 再加 `--cache-send-code-bodies`（已脱敏，仍本地） |
| 短期磁盘 journal | `persistSendCodeBodies` + TTL（默认隐私：关） |

Bake 在 **Revit 进程内**用 Roslyn 编译 — 终端用户不需要 Visual Studio。细节与隐私：[docs/bake.md](docs/bake.md)。

### Toast（可选）

完成 toast 默认**关闭**。用 ribbon **Toast**、配置里 `enableToast`，或 `BIMWRIGHT_ENABLE_TOAST=1` 打开。只显示**已完成**调用（无进行中 toast）。Capture 成功可在路径 allowlist 内显示缩略图。Ribbon **Status** 也会列出 toast 与 bake/隐私标志，避免靠猜。

---

## 架构（短）

```text
MCP client (stdio)
    → RvtMcp.Server (.NET 8)
        → TCP (Revit 2022–2024) 或 Named Pipe (2025–2027)
            → Plugin shell（按年）
                → ExternalEvent → Revit API / 事务 / 撤销
```

Handler 只返回普通 DTO — 线上不传活的 Revit 对象。

---

## Tools

数量（不含个人 baked 工具）：

| 模式 | Tools | 说明 |
|------|------:|------|
| 默认 | **40** | `query` + `create` + `view` + `meta` |
| `--toolsets all` | **229** | 完整目录 |
| `all` + adaptive bake | **232** | 再加 3 个 suggestion 生命周期工具 |

MCP 名：`revit_*`。server↔plugin 线名：无前缀 snake_case。

**默认开启 toolset：** `query`, `create`, `view`, `meta`

**除非显式开启否则关闭：** 其余全部（`export`、`geometry`、`mep`、`structural`、`toolbaker`、`modify`、`delete` 等）。  
例：`--toolsets query,view,meta` 或 `--toolsets all`。  
`--read-only` 去掉所有可写 toolset（含 `create`）。

| Toolset | 覆盖 | 默认 |
|---------|------|------|
| `query` | 视图、选择、过滤、统计、参数、关系、workset、组/程序集 | on |
| `create` | 轴网、标高、房间、线/点/面构件、组 | on |
| `view` | 建视图、图纸布局辅助、截图、裁剪/比例 | on |
| `meta` | 批处理（最多 20）、多 Revit 目标、项目信息、purge（MVP）、消息、send_code | on |
| `lint` | 视图命名、firm-profile、警告摘要 | off |
| `schedule` | 明细表 list/创建、字段、公式、数据 | off |
| `families` | 加载/卸载、类型、实例、审计、导出 `.rfa`（项目侧） | off |
| `modify` | 操作/着色、写参数、换类型、workset | off |
| `delete` | 按 id 删除 | off |
| `annotation` | 标记、文字、尺寸、填充、keynote、检查 | off |
| `export` | PDF/DWG/IFC/NWC、房间数据及相关导出 | off |
| `mep` | 系统、连接件、网络、风口灯具等 | off |
| `graphics` | 视图过滤器、覆盖、可见性/阶段 | off |
| `toolbaker` | list/run baked；adaptive 开才有 suggestion 工具 | off |
| `sheets` | 图纸、图框、修订、重编号 | off |
| `materials` | 材质、外观、赋值、提量 | off |
| `rooms` | 房间/面积/空间、装修、分隔 | off |
| `links` | Revit/CAD 链接、坐标审计、获取/发布坐标 | off |
| `parameters` | 项目/共享参数 | off |
| `organization` | 保存选择、视图样板 | off |
| `workflows` | 碰撞/审计/图纸/提量类组合流 | off |
| `structural` | 柱梁基础、钢筋、荷载… | off |
| `kei` | KEI 项目 SQLite 路径、查询/写入（WAL 安全）、设备导入 | off |
| `geometry` | 包围盒、测量、碰撞、体积/面积… | off |

### 代表性工具

不是 200+ schema 全表 — 常用锚点：

| Toolset | Tool | 作用 |
|---------|------|------|
| `query` | `revit_get_current_view_info` | 活动视图类型、标高、比例 |
| `query` | `revit_get_selected_elements` | 当前选择 |
| `query` | `revit_ai_element_filter` | 类别 + 参数过滤（mm） |
| `query` | `revit_get_element_details` | 位置、bbox、workset、阶段… |
| `create` | `revit_create_grid` / `revit_create_level` / `revit_create_room` | 基础布局 |
| `create` | `revit_create_point_based_element` | 门家具等（type id） |
| `view` | `revit_capture_view_image` | 栅格截图（路径 allowlist） |
| `meta` | `revit_batch_execute` | 多个命令一个 `TransactionGroup` |
| `meta` | `revit_list_available_targets` / `revit_switch_target` | 多 Revit |
| `families` | `revit_load_family_from_path` | 向项目加载 `.rfa` |
| `links` | `revit_get_project_coordinate_system` / `revit_get_link_coordinate_system` | 主体/链接原点、场地、变换、真北 |
| `toolbaker` | `revit_send_code_to_revit` | Escape hatch（C#） |
| `toolbaker` | `revit_list_baked_tools` / `revit_run_baked_tool` | 已 accept 个人工具 |
| `toolbaker` | `revit_list_bake_suggestions` | 仅 adaptive |
| `lint` | `revit_analyze_view_naming_patterns` | 命名离群 |

测试中的 golden snapshot 锁定准确 surface；计数与代码冲突时以测试/代码为准。

---

## Supported Revit versions

| Revit | 插件 TFM | 传输 |
|-------|----------|------|
| 2022–2024 | .NET Framework 4.8 | TCP |
| 2025–2026 | .NET 8 (`net8.0-windows7.0`) | Named Pipe |
| 2027 | .NET 10 (`net10.0-windows7.0`) | Named Pipe |

六个 shell 均可编译。运行时深度仍因年份而异 — bake 与自定义 C# 请在目标年复测。

**宿主：** 仅完整 Revit 桌面版。不支持 Revit Viewer。

---

## 安全与隐私

- 默认本地传输（loopback TCP / 本机 named pipe）。
- `%LOCALAPPDATA%\RvtMcp\` 下 discovery 含每会话 auth token。
- 工具参数在 handler 前做 schema 校验。
- 返回模型的错误经脱敏（减少绝对路径泄露）。
- `send_code` 可在 Revit 进程跑任意 C# — 强且危险；不可接受时关闭 toolbaker。
- Adaptive bake、body cache、TTL journal 均为 **opt-in**，落在用户配置目录。默认不把原始 send_code 正文写入长期日志。

更多：[SECURITY.md](SECURITY.md)、[docs/bake.md](docs/bake.md)。

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
| 完成 toast | ribbon **Toast** | `BIMWRIGHT_ENABLE_TOAST=1` | `enableToast` |

改 server 标志后请重启 MCP 连接，以便客户端拿到新工具列表。

---

## MCP 客户端

| 客户端 | 接线 |
|--------|------|
| Claude Code | 项目 `.mcp.json` 或 `~/.claude.json` |
| Claude Desktop | `%APPDATA%\Claude\claude_desktop_config.json` |
| OpenCode / Codex / Kilo | `install.ps1 -Client …`（脚本） |
| Cursor / Cline / VS Code Copilot | 文档中的 JSON 布局 |
| Gemini CLI / Antigravity | `gemini mcp add` 或 settings JSON |

安装程序自动检测通常足够；手改见 [AGENTS.md](AGENTS.md) 与 `docs/mcp-config-*.md`。

---

## 仓库布局

```text
rvt-mcp/
├── src/
│   ├── RvtMcp.sln
│   ├── server/            # MCP server
│   ├── shared/            # Handlers, transport, ToolBaker, toast, …
│   ├── plugin-r22/ … r27/ # 每年一个 shell
├── tests/                 # xUnit + golden tool lists
├── scripts/               # install / uninstall / package
├── docs/                  # roadmap, bake, testing
├── AGENTS.md
└── ARCHITECTURE.md
```

---

## 开发

```bash
dotnet test tests/RvtMcp.Tests/RvtMcp.Tests.csproj
dotnet build src/server/RvtMcp.Server.csproj -c Release
dotnet build src/plugin-r26/RvtMcp.Plugin.R26.csproj -c Release
```

构建插件前关闭 Revit（DLL 锁定）。普通 Debug/Release 会部署到 `%APPDATA%\Autodesk\Revit\Addins\<year>\RvtMcp\`。

```powershell
pwsh scripts/stage-plugin-zip.ps1 -Config Release
```

贡献约定与 snapshot：[CONTRIBUTING.md](CONTRIBUTING.md)。

### 成熟度

可用，但不神化。CI 编六个插件 shell 与 server 测试。运行时覆盖在中间年份更强；生产模型请谨慎，并在*你的* Revit 版本上验证。新机器清单：[docs/testing/fresh-install-checklist.md](docs/testing/fresh-install-checklist.md)。

---

## 更多文档

| 文档 | 主题 |
|------|------|
| [AGENTS.md](AGENTS.md) | Agent 安装协议 |
| [ARCHITECTURE.md](ARCHITECTURE.md) | 进程、传输、DTO 规则 |
| [docs/bake.md](docs/bake.md) | Adaptive bake 与正文隐私 |
| [docs/roadmap.md](docs/roadmap.md) | 近期加固与 non-goals |
| [docs/kei-equipment-import.md](docs/kei-equipment-import.md) | KEI SQLite 工具（`--toolsets kei`） |
| [CHANGELOG.md](CHANGELOG.md) | 发布说明 |

---

## bimwright

同一套 AEC 宿主风格：

- [rvt-mcp](https://github.com/bimwright/rvt-mcp) — Revit  
- [dwg-mcp](https://github.com/bimwright/dwg-mcp) — AutoCAD  
- [nwd-mcp](https://github.com/bimwright/nwd-mcp) — Navisworks  
- [ipt-mcp](https://github.com/bimwright/ipt-mcp) — Inventor  
- [bim-wiki](https://github.com/bimwright/bim-wiki) — 越南语优先 BIM 知识库  

---

## License

Apache-2.0 — [LICENSE](LICENSE)。

Revit 与 Autodesk 为 Autodesk, Inc. 商标。bimwright 为独立开源项目，与 Autodesk 无隶属关系。
