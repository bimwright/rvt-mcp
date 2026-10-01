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
  <a href="#tools"><img src="https://img.shields.io/badge/MCP-227%20tools-6C47FF" alt="MCP tools" /></a>
  <a href="https://github.com/bimwright/rvt-mcp/releases/latest"><img src="https://img.shields.io/github/v/release/bimwright/rvt-mcp" alt="latest release" /></a>
  <a href="CHANGELOG.md"><img src="https://img.shields.io/badge/changelog-version%20history-informational" alt="changelog" /></a>
</p>

<p align="center">
  <a href="README.md">English</a> · <a href="README.vi.md">Tiếng Việt</a> · 简体中文 · <a href="README.ja.md">日本語</a>
</p>

---

## 这是什么

`rvt-mcp` 是 MCP 客户端与正在运行的 Revit 会话之间的**本地**桥。.NET 8 server 通过 stdio 提供 MCP；每个 Revit 年份（2022–2027）一个瘦 add-in 在 Revit 内运行，经 localhost TCP（≤2024）或 named pipe（≥2025）连接。数据不出本机，全部为 C#，工具边界的长度单位为 mm。细节：[ARCHITECTURE.md](ARCHITECTURE.md)。

Agent 可以使用覆盖常见 Revit 工作的 **typed 工具面**、应对其余情况的 C# escape hatch，以及把重复模式变成个人工具的**可选**路径（ToolBaker）：从共享运行时出发，在其上长出*你的*工具。Family Editor 创作暂不在范围内。

---

## 安装

**普通用户：交给 AI agent。** 你不需要自己运行任何命令。复制下面这一行，粘贴给你的 AI agent（Claude Code、Codex、Cursor 等），然后去泡杯咖啡，等它完成即可。agent 会按照 [AGENTS.md](AGENTS.md) 操作，在安装或修改客户端配置之前先征求你的同意。

```text
帮我安装 rvt-mcp：https://github.com/bimwright/rvt-mcp
```

**或者自己运行安装程序。** 先关闭 Revit，然后在 PowerShell 中：

```powershell
$tag = (Invoke-RestMethod https://api.github.com/repos/bimwright/rvt-mcp/releases/latest).tag_name
$dir = "$env:TEMP\RvtMcp.Setup-$tag-win-x64"
Invoke-WebRequest "https://github.com/bimwright/rvt-mcp/releases/download/$tag/RvtMcp.Setup-$tag-win-x64.zip" -OutFile "$dir.zip"
Expand-Archive "$dir.zip" -DestinationPath $dir -Force
powershell -ExecutionPolicy Bypass -File "$dir\install.ps1"
```

一次运行即可配置两端：为本机所有 Revit 2022–2027 安装插件，并在检测到的每个 MCP 客户端中写入 `rvt-mcp` 条目（每个配置都会先备份）。加 `-WhatIf` 可预览，用 `-Client claude,cursor` 只连接这些客户端，或用 `-Client none` 不改动客户端配置、自己注册 server——路径为 `%LOCALAPPDATA%\Bimwright\rvt-mcp\server\current\rvt-mcp.exe`（[各客户端步骤](docs/mcp-client-wiring.md)）。

**验证：** 重启 AI 客户端，在 Revit 中打开模型，在 ribbon（**附加模块**（Add-Ins）→ **RvtMcp**）上启动 MCP，然后让 agent 调用 `revit_get_current_view_info`。返回当前视图的名称和类型即表示成功。

**升级：** 用同样方式运行新版本的安装程序——无需先卸载，客户端只需重启。**卸载：** 在同一文件夹运行 `uninstall.ps1 -Yes` 会移除插件和 server（除非加 `-Purge`，设置会保留）；如需同时删除客户端条目，请先运行 `install.ps1 -Uninstall -Client auto`。更多内容（包括开发者安装和 NuGet）：[docs/install.md](docs/install.md)。

**Claude Desktop MCPB（v1.0.0 候选版）：** 扩展启动从同一版本单独安装的 gateway。此方式请使用 `install.ps1 -Client none`，避免 Desktop 重复注册。参见 [MCPB 安装与设置](docs/install.md#claude-desktop-mcpb-v100-candidate)。

---

## 视频

社区制作的 rvt-mcp 使用视频。视频中的安装步骤可能比本 README 旧，请以上面的步骤为准。

- [Connecting ChatGPT Astra to Revit | Testing AI Changes in a Real Project](https://www.youtube.com/watch?v=J-i057B-3dI) — Revit Mentor
- [Exploring Revit + GPT 6 Astra](https://www.youtube.com/watch?v=2_W1uLn6s_I) — BIM Pure
- [GPT-6 Astra Built a Revit House in 14 Minutes](https://www.youtube.com/watch?v=pkFnSQ9Bapg) — Archi Vlogs

---

## Tools

| 模式 | Tools | 说明 |
|------|------:|------|
| 全新安装 | **227** | `install.ps1` 在 `rvtmcp.config.json` 中写入 `"toolsets": ["all"]` |
| 裸 `rvt-mcp.exe` | **42** | `query` + `create` + `view` + `meta` |
| `--toolsets all` | **227** | 完整目录 |
| `all` + adaptive bake | **230** | 再加 3 个 suggestion 生命周期工具 |

数量不含个人 baked 工具。只有当 `rvtmcp.config.json` 尚未设置 `toolsets` 时安装器才会写入默认值——你的自定义列表在升级时保留；删除该键（或设置自己的 CSV）则裸服务器回到 42 个工具。Read-only 按 `ReadOnly=true` 逐个筛选工具，因此混合 toolset 中的读取工具仍可用。即使默认 output 是 inline，能写文件的工具也会被排除。

| Toolset | 覆盖 |
|---------|------|
| `query` | 视图、选择、过滤、统计、参数、关系、workset、组/程序集 |
| `create` | 轴网、标高、房间、线/点/面构件、组 |
| `view` | 建视图、图纸布局辅助、截图、裁剪/比例 |
| `meta` | 批处理（最多 20）、多 Revit 目标、最近模型、项目信息、purge（MVP）、消息、send_code |
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

### send_code、ToolBaker、ribbon 与界面语言

- **`revit_send_code_to_revit`**（默认开启）在没有合适 typed 工具时，于 Revit 内编译并运行 C# 正文；`--read-only` 或 `--disable-send-code` 会移除它。见 [docs/send-code.md](docs/send-code.md)；楼梯见 [docs/stairs-workflow.md](docs/stairs-workflow.md)。
- **ToolBaker：** `revit_list_baked_tools` / `revit_run_baked_tool` 需要 `--toolsets toolbaker`。Adaptive bake（`--enable-adaptive-bake`，默认关闭）会根据重复调用建议工具；在你 accept 之前不会添加任何东西。Bake 在 Revit 内编译，不需要 Visual Studio。见 [docs/bake.md](docs/bake.md)。
- **Ribbon：** 启动或停止连接，打开 **History** 搜索并重跑历史调用，切换完成 **Toast**（默认开启）。
- **界面语言：** 插件 UI 支持 15 种语言，默认跟随 Revit 的界面语言；可在 ribbon 滑出面板的 **Language** 下拉框中更改。工具名与 payload 保持英文。见 [docs/localization.md](docs/localization.md)。

### 模型修改提示（尚未发布）

v1.0.0 发布候选版提供五个 MCP 提示。在客户端提示菜单中选择 `revit_change`（Claude Code：`/mcp__rvt-mcp__revit_change`），通过 `change` 指定修改要求：调查关联关系，逐项确认最小修改范围，在确认具体方案后写入，再读取结果并在对话中记录原因。需要 `query,meta`，不依赖 `send_code`；只读模式止于调查和建议。缺失或不完整的证据必须标为“未检查”。读取结果不代表完整捕获所有间接变化，对话记录也不是持久化变更数据库。提示用于指导代理，不是服务器强制执行的工作流锁。原有四个提示保持不变。

---

## 配置

优先级从高到低：**CLI → env（`BIMWRIGHT_*`）→** `%LOCALAPPDATA%\Bimwright\rvt-mcp\rvtmcp.config.json`。

| 设置 | CLI | Env | JSON |
|------|-----|-----|------|
| 目标年份 | `--target 2024` | `BIMWRIGHT_TARGET` | `target` |
| Toolsets | `--toolsets query,create` | `BIMWRIGHT_TOOLSETS` | `toolsets` |
| 只读 | `--read-only` | `BIMWRIGHT_READ_ONLY=1` | `readOnly` |
| send_code | `--enable-send-code` / `--disable-send-code` | `BIMWRIGHT_ENABLE_SEND_CODE` | `enableSendCode` |
| Call log | `--enable-call-log` / `--disable-call-log` | `BIMWRIGHT_ENABLE_CALL_LOG` | `enableCallLog` |
| Response guard | `--enable-response-guard` / `--disable-response-guard` | `BIMWRIGHT_ENABLE_RESPONSE_GUARD` | `enableResponseGuard` |
| Warn bytes | `--response-warn-bytes` | `BIMWRIGHT_RESPONSE_WARN_BYTES` | `responseWarnBytes` |
| Strong warn bytes | `--response-strong-warn-bytes` | `BIMWRIGHT_RESPONSE_STRONG_WARN_BYTES` | `responseStrongWarnBytes` |
| Budget bytes | `--response-budget-bytes` | `BIMWRIGHT_RESPONSE_BUDGET_BYTES` | `responseBudgetBytes` |
| Transport cap | `--max-response-bytes` | `BIMWRIGHT_MAX_RESPONSE_BYTES` | `maxResponseBytes` |
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

## Permissions & auto mode — 自动执行权限

这些 controls 包含在尚未发布的 v1.0.0 候选版中。已发布的 v0.8.1 包尚不包含新 switch 和按工具筛选的 read-only 模式。`send_code` 与 `run_baked_tool` 必须直接调用；`batch_execute` 会拒绝它们。

Annotations 描述每个工具对文档和文件的影响。临时 selection、active view 和 zoom 变化算作 read-only。`send_code` 没有 annotations：不要加入自动授权，并逐次确认代码执行。Claude Code 仅使用以下 read-only allow list；不要允许宽泛的 `mcp__rvt-mcp__*` wildcard。此列表对应 `--toolsets all`，所选 toolset 可能公开更少工具。

<details>
<summary>从 annotations 生成的 read-only allow list</summary>

<!-- BEGIN READ_ONLY_ALLOWLIST -->
```json
{
  "permissions": {
    "allow": [
      "mcp__rvt-mcp__revit_activate_view",
      "mcp__rvt-mcp__revit_ai_element_filter",
      "mcp__rvt-mcp__revit_analyze_geometry_complexity",
      "mcp__rvt-mcp__revit_analyze_mep_network",
      "mcp__rvt-mcp__revit_analyze_model_statistics",
      "mcp__rvt-mcp__revit_analyze_sheet_layout",
      "mcp__rvt-mcp__revit_analyze_structural_connections",
      "mcp__rvt-mcp__revit_analyze_usage_patterns",
      "mcp__rvt-mcp__revit_analyze_view_naming_patterns",
      "mcp__rvt-mcp__revit_audit_families",
      "mcp__rvt-mcp__revit_clash_detection",
      "mcp__rvt-mcp__revit_compute_element_area",
      "mcp__rvt-mcp__revit_compute_element_volume",
      "mcp__rvt-mcp__revit_detect_firm_profile",
      "mcp__rvt-mcp__revit_detect_system_elements",
      "mcp__rvt-mcp__revit_find_elements_in_volume",
      "mcp__rvt-mcp__revit_find_mep_disconnects",
      "mcp__rvt-mcp__revit_find_overlapping_elements",
      "mcp__rvt-mcp__revit_find_schedule_elements",
      "mcp__rvt-mcp__revit_find_undimensioned_elements",
      "mcp__rvt-mcp__revit_find_untagged_elements",
      "mcp__rvt-mcp__revit_get_assembly_members",
      "mcp__rvt-mcp__revit_get_available_family_types",
      "mcp__rvt-mcp__revit_get_current_target",
      "mcp__rvt-mcp__revit_get_current_view_info",
      "mcp__rvt-mcp__revit_get_element_bounding_box",
      "mcp__rvt-mcp__revit_get_element_centroid",
      "mcp__rvt-mcp__revit_get_element_details",
      "mcp__rvt-mcp__revit_get_element_geometry",
      "mcp__rvt-mcp__revit_get_element_parameters",
      "mcp__rvt-mcp__revit_get_element_relationships",
      "mcp__rvt-mcp__revit_get_family_instances",
      "mcp__rvt-mcp__revit_get_group_members",
      "mcp__rvt-mcp__revit_get_link_coordinate_system",
      "mcp__rvt-mcp__revit_get_link_elements",
      "mcp__rvt-mcp__revit_get_material_properties",
      "mcp__rvt-mcp__revit_get_material_quantities",
      "mcp__rvt-mcp__revit_get_mep_element_connectors",
      "mcp__rvt-mcp__revit_get_model_warnings_summary",
      "mcp__rvt-mcp__revit_get_panel_schedule",
      "mcp__rvt-mcp__revit_get_print_settings",
      "mcp__rvt-mcp__revit_get_project_coordinate_system",
      "mcp__rvt-mcp__revit_get_room_boundaries",
      "mcp__rvt-mcp__revit_get_room_openings",
      "mcp__rvt-mcp__revit_get_schedulable_fields",
      "mcp__rvt-mcp__revit_get_schedule_data",
      "mcp__rvt-mcp__revit_get_schedule_definition",
      "mcp__rvt-mcp__revit_get_schedule_formulas",
      "mcp__rvt-mcp__revit_get_selected_elements",
      "mcp__rvt-mcp__revit_get_structural_loads",
      "mcp__rvt-mcp__revit_get_system_inventory",
      "mcp__rvt-mcp__revit_get_titleblock_parameters",
      "mcp__rvt-mcp__revit_get_type_parameters",
      "mcp__rvt-mcp__revit_get_view_visibility",
      "mcp__rvt-mcp__revit_list_areas",
      "mcp__rvt-mcp__revit_list_assemblies",
      "mcp__rvt-mcp__revit_list_available_targets",
      "mcp__rvt-mcp__revit_list_baked_tools",
      "mcp__rvt-mcp__revit_list_export_settings",
      "mcp__rvt-mcp__revit_list_family_types_in_family",
      "mcp__rvt-mcp__revit_list_groups",
      "mcp__rvt-mcp__revit_list_keynotes",
      "mcp__rvt-mcp__revit_list_linked_cad",
      "mcp__rvt-mcp__revit_list_linked_models",
      "mcp__rvt-mcp__revit_list_loaded_families",
      "mcp__rvt-mcp__revit_list_materials",
      "mcp__rvt-mcp__revit_list_mep_systems",
      "mcp__rvt-mcp__revit_list_phases",
      "mcp__rvt-mcp__revit_list_project_parameter_bindings",
      "mcp__rvt-mcp__revit_list_project_parameters",
      "mcp__rvt-mcp__revit_list_rebar",
      "mcp__rvt-mcp__revit_list_recent_models",
      "mcp__rvt-mcp__revit_list_revisions",
      "mcp__rvt-mcp__revit_list_rooms",
      "mcp__rvt-mcp__revit_list_saved_selections",
      "mcp__rvt-mcp__revit_list_schedules",
      "mcp__rvt-mcp__revit_list_shared_parameters",
      "mcp__rvt-mcp__revit_list_sheets",
      "mcp__rvt-mcp__revit_list_titleblocks",
      "mcp__rvt-mcp__revit_list_view_filters",
      "mcp__rvt-mcp__revit_list_view_templates",
      "mcp__rvt-mcp__revit_list_worksets",
      "mcp__rvt-mcp__revit_load_selection",
      "mcp__rvt-mcp__revit_measure_distance_between_elements",
      "mcp__rvt-mcp__revit_project_point_onto_face",
      "mcp__rvt-mcp__revit_raycast_from_point",
      "mcp__rvt-mcp__revit_select_elements",
      "mcp__rvt-mcp__revit_show_element_in_view",
      "mcp__rvt-mcp__revit_show_message",
      "mcp__rvt-mcp__revit_suggest_view_name_corrections",
      "mcp__rvt-mcp__revit_switch_target",
      "mcp__rvt-mcp__revit_workflow_model_audit"
    ]
  }
}
```
<!-- END READ_ONLY_ALLOWLIST -->

</details>

send_code 默认 **开启**，独立于 ToolBaker；call-log 默认 **关闭**。优先级为 CLI > 环境变量 > JSON。经过认证的 server 设置按 request 覆盖 plugin 设置。关闭 call-log 时，server journal、plugin `mcp-calls.jsonl` 和 send-code 正文 journal 不写入；内存 History 仍可用。正文 journal 需要同时开启 call-log 和独立的 TTL opt-in。ToolBaker `usage.jsonl` 为另一类记录，由 adaptive-bake 设置控制。

Response guard 默认 **开启**：UTF-8 警告阈值 65536 byte，强警告高于 262144，budget 为 716800，transport cap 为 1048576 byte。server 测量 JSON 转义、MCP content 和 metadata。过大的读取结果返回 `RESPONSE_TOO_LARGE` 和缩小查询的提示；已完成写入返回精简摘要。任意代码输出保存到本机文件，返回 `mutation_applied: null`；查看该文件，不要重新执行命令。关闭 guard 仍保留 transport cap。阈值必须为整数 >=1024，并满足 `warn <= strong <= budget <= max`；降低 budget 时也要调整较低阈值。

## Supported Revit versions

| Revit | 插件 TFM | 传输 |
|-------|----------|------|
| 2022–2024 | .NET Framework 4.8 | TCP |
| 2025–2026 | .NET 8 (`net8.0-windows7.0`) | Named Pipe |
| 2027 | .NET 10 (`net10.0-windows7.0`) | Named Pipe |

仅支持完整 Revit 桌面版，不支持 Revit Viewer。CI 会构建全部六个插件，但运行时深度因年份而异 — baked 工具与自定义 C# 请在你使用的年份上复测。

---

## 安全与隐私

- 默认本地：loopback TCP 或本机 named pipe，`%LOCALAPPDATA%\Bimwright\rvt-mcp\` 下的 discovery 文件含每会话 auth token。
- 工具参数在 handler 运行前做 schema 校验；返回模型的错误经过脱敏。
- `send_code` 可在 Revit 进程中运行任意 C# — 强大且有风险。无法接受时请使用 `--read-only` 或 `--disable-send-code`。
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
| [CONTRIBUTING.md](CONTRIBUTING.md) | 构建、测试、新增工具 |
| [CHANGELOG.md](CHANGELOG.md) | 发布说明 |

---

## 社区贡献

代码、问题报告、复现示例和建议帮助 rvt-mcp 持续改进。感谢：

| 贡献者 | 贡献 |
|--------|------|
| [@thiagobarretosn-hue](https://github.com/thiagobarretosn-hue) | 提供 MEP 网络成员和管道系统处理问题的复现报告（[#11](https://github.com/bimwright/rvt-mcp/issues/11)、[#12](https://github.com/bimwright/rvt-mcp/issues/12)）。 |
| [@razmikb](https://github.com/razmikb) | 报告宿主族放置和楼梯/send-code 错误，推动了放置验证、辅助类支持和更全面的错误处理测试（[#13](https://github.com/bimwright/rvt-mcp/issues/13)、[#14](https://github.com/bimwright/rvt-mcp/issues/14)）。 |
| [@Thestreetarckitect](https://github.com/Thestreetarckitect) | 提出 Family Authoring Tool Suite 建议，帮助明确路线图和产品范围（[#7](https://github.com/bimwright/rvt-mcp/issues/7)）。 |
| [@PhanCongVuDuc](https://github.com/PhanCongVuDuc) | 拉取请求 [#15](https://github.com/bimwright/rvt-mcp/pull/15)：让 `send_code` 在未打开模型时也能运行的修复、`revit_switch_target` 提示的修正，以及 `revit_open_model` 提案（经重新实现后在 v0.8.1 发布）。 |

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
