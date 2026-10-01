<!-- mcp-name: io.github.bimwright/rvt-mcp -->

<p align="center">
  <img src="https://raw.githubusercontent.com/bimwright/.github/master/assets/logos/rvt-mcp.png" alt="rvt-mcp" width="180" />
</p>

<h1 align="center">rvt-mcp</h1>

<p align="center">
  Autodesk Revit 向け MCP ゲートウェイ — エージェント用ローカルツール、任意の個人 bake ループ
</p>

<p align="center">
  <a href="https://github.com/bimwright/rvt-mcp/actions/workflows/build.yml"><img src="https://github.com/bimwright/rvt-mcp/actions/workflows/build.yml/badge.svg" alt="build" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-Apache%202.0-blue.svg" alt="license" /></a>
  <a href="#supported-revit-versions"><img src="https://img.shields.io/badge/Revit-2022--2027-186BFF" alt="Revit 2022-2027" /></a>
  <a href="#tools"><img src="https://img.shields.io/badge/MCP-229%20tools-6C47FF" alt="MCP tools" /></a>
  <a href="https://github.com/bimwright/rvt-mcp/releases/latest"><img src="https://img.shields.io/github/v/release/bimwright/rvt-mcp" alt="latest release" /></a>
  <a href="CHANGELOG.md"><img src="https://img.shields.io/badge/changelog-version%20history-informational" alt="changelog" /></a>
</p>

<p align="center">
  <a href="README.md">English</a> · <a href="README.vi.md">Tiếng Việt</a> · <a href="README.zh-CN.md">简体中文</a> · 日本語
</p>

---

## これは何か

`rvt-mcp` は MCP クライアントと起動中の Revit セッションをつなぐ**ローカル**ブリッジです。.NET 8 のサーバが stdio で MCP を話し、Revit の年ごと（2022–2027）の薄いアドインが Revit 内で動作します。両者は localhost TCP（≤2024）または named pipe（≥2025）で接続します。すべてマシン内で完結し、全体が C# で、ツール境界の長さは mm です。詳細は [ARCHITECTURE.md](ARCHITECTURE.md)。

エージェントには、よくある Revit 作業のための **typed ツール面**、それ以外のための C# の逃げ道、繰り返すパターンを個人ツールにする**任意**の仕組み（ToolBaker）があります。共有ランタイムから始めて、*自分の*ツールを育ててください。Family Editor でのオーサリングは当面対象外です。

---

## インストール

**ユーザーの方へ：AI エージェントに任せてください。** 自分で何かを実行する必要はありません。下の 1 行をコピーして AI エージェント（Claude Code、Codex、Cursor など）に貼り付け、作業が終わるまでコーヒーでも飲んで待つだけです。エージェントは [AGENTS.md](AGENTS.md) に従い、インストールやクライアント設定の変更の前に必ず確認を求めます。

```text
rvt-mcp をインストールして: https://github.com/bimwright/rvt-mcp
```

**自分でインストーラを実行する場合。** Revit を閉じてから PowerShell で：

```powershell
$tag = (Invoke-RestMethod https://api.github.com/repos/bimwright/rvt-mcp/releases/latest).tag_name
$dir = "$env:TEMP\RvtMcp.Setup-$tag-win-x64"
Invoke-WebRequest "https://github.com/bimwright/rvt-mcp/releases/download/$tag/RvtMcp.Setup-$tag-win-x64.zip" -OutFile "$dir.zip"
Expand-Archive "$dir.zip" -DestinationPath $dir -Force
powershell -ExecutionPolicy Bypass -File "$dir\install.ps1"
```

1 回の実行で両側をセットアップします：マシン上のすべての Revit 2022–2027 へのアドインと、検出したすべての MCP クライアントへの `rvt-mcp` エントリです（各設定は先にバックアップされます）。`-WhatIf` でプレビュー、`-Client claude,cursor` で指定したクライアントだけを接続、`-Client none` でクライアント設定に触れずにサーバを自分で登録できます。サーバの場所は `%LOCALAPPDATA%\Bimwright\rvt-mcp\server\current\rvt-mcp.exe` です（[クライアント別手順](docs/mcp-client-wiring.md)）。

**動作確認：** AI クライアントを再起動し、Revit でモデルを開き、リボン（**アドイン**（Add-Ins）→ **RvtMcp**）で MCP を開始して、エージェントに `revit_get_current_view_info` を呼ばせます。アクティブビューの名前と種類が返れば成功です。

**更新：** 新しいリリースのインストーラを同じ手順で実行します。先にアンインストールは不要で、クライアントは再起動だけで済みます。**アンインストール：** 同じフォルダで `uninstall.ps1 -Yes` を実行するとアドインとサーバが削除されます（`-Purge` を付けない限り設定は残ります）。クライアントのエントリも消すには、先に `install.ps1 -Uninstall -Client auto` を実行してください。開発者向け・NuGet を含む詳細：[docs/install.md](docs/install.md)。

**Claude Desktop MCPB（v1.0.0 候補）：** 同じリリースから別途インストールした gateway を起動します。Desktop の重複登録を避けるため、この方法では `install.ps1 -Client none` を使います。[MCPB のインストールと設定](docs/install.md#claude-desktop-mcpb-v100-candidate)を参照してください。

---

## 動画

rvt-mcp を使ったコミュニティ動画です。動画内のインストール手順はこの README より古い場合があります。上記の手順に従ってください。

- [Connecting ChatGPT Astra to Revit | Testing AI Changes in a Real Project](https://www.youtube.com/watch?v=J-i057B-3dI) — Revit Mentor
- [Exploring Revit + GPT 6 Astra](https://www.youtube.com/watch?v=2_W1uLn6s_I) — BIM Pure
- [GPT-6 Astra Built a Revit House in 14 Minutes](https://www.youtube.com/watch?v=pkFnSQ9Bapg) — Archi Vlogs

---

## Tools

| モード | Tools | 注記 |
|--------|------:|------|
| 新規インストール | **229** | `install.ps1` が `rvtmcp.config.json` に `"toolsets": ["all"]` をシード |
| 素の `rvt-mcp.exe` | **44** | `query` + `create` + `view` + `meta` |
| `--toolsets all` | **229** | フルカタログ |
| `all` + adaptive bake | **232** | 提案ライフサイクル 3 ツールを追加 |

件数に個人 baked ツールは含みません。インストーラーがシードするのは `rvtmcp.config.json` に `toolsets` が未設定の場合のみ——独自リストはアップグレード後も残り、キー削除（または独自 CSV）で素のサーバーは 44 ツールに戻ります。Read-only は `ReadOnly=true` のツールだけを残すため、混在 toolset 内の読み取りツールも使えます。既定の出力が inline でも、ファイルを書けるツールは除外されます。

| Toolset | 範囲 |
|---------|------|
| `query` | ビュー、選択、フィルタ、統計、パラメータ、関係、ワークセット、グループ/アセンブリ |
| `create` | 通り芯、レベル、部屋、線/点/面要素、グループ |
| `view` | ビュー作成、シート配置補助、キャプチャ、クロップ/縮尺 |
| `meta` | バッチ（最大 20）、複数 Revit、最近のモデル、プロジェクト情報、purge（MVP）、メッセージ、send_code |
| `lint` | ビュー命名、firm-profile、警告サマリ |
| `schedule` | 集計表 list/作成、フィールド、式、データ |
| `families` | ロード/アンロード、タイプ、インスタンス、監査、`.rfa` エクスポート（プロジェクト側） |
| `modify` | 操作/着色、パラメータ、タイプ変更、ワークセット |
| `delete` | id 削除 |
| `annotation` | タグ、文字、寸法、塗り、キーノート、検査 |
| `export` | PDF/DWG/IFC/NWC、部屋データなど |
| `mep` | システム、コネクタ、ネットワーク、端末配置など |
| `graphics` | ビューフィルタ、オーバーライド、可視/フェーズ |
| `toolbaker` | list/run baked；adaptive 時のみ提案ツール |
| `sheets` | シート、タイトルブロック、リビジョン、番号変更 |
| `materials` | マテリアル、外観、割当、拾い |
| `geometry` | BBox、測距、干渉、体積/面積… |
| `rooms` | 部屋/面積/スペース、仕上、セパレータ |
| `links` | Revit/CAD リンク、座標監査、座標の取得/公開 |
| `parameters` | プロジェクト/共有パラメータ |
| `organization` | 保存選択、ビューテンプレート |
| `workflows` | 干渉/監査/シート/拾い系の複合 |
| `structural` | 柱梁基礎、鉄筋、荷重… |

### send_code、ToolBaker、リボン、表示言語

- **`revit_send_code_to_revit`**（既定オン）は、合う typed ツールがないときに C# 本体を Revit 内でコンパイルして実行します。`--read-only` または `--disable-send-code` で外れます。[docs/send-code.md](docs/send-code.md) を参照。階段は [docs/stairs-workflow.md](docs/stairs-workflow.md)。
- **ToolBaker：** `revit_list_baked_tools` / `revit_run_baked_tool` には `--toolsets toolbaker` が必要です。Adaptive bake（`--enable-adaptive-bake`、既定オフ）は繰り返しの呼び出しからツールを提案し、accept するまで何も追加されません。Bake のコンパイルは Revit 内で行われ、Visual Studio は不要です。[docs/bake.md](docs/bake.md)。
- **リボン：** 接続の開始/停止、**History** で過去の呼び出しの検索と再実行、完了 **Toast** の切り替え（既定オン）。
- **表示言語：** アドインの UI は 15 言語に対応し、Revit の UI 言語に従います。リボンのスライドアウトにある **Language** コンボで変更できます。ツール名とペイロードは英語のままです。[docs/localization.md](docs/localization.md)。

### モデル変更プロンプト（未リリース）

v1.0.0 リリース候補には 5 つの MCP プロンプトがあります。クライアントのプロンプトメニューで `revit_change`（Claude Code: `/mcp__rvt-mcp__revit_change`）を選び、`change` に変更内容を指定します。関連要素を調べ、要求ごとに最小限の範囲を合意し、具体的な案を確認してから変更し、結果を再取得して理由を会話に記録します。`query,meta` が必要で、`send_code` は不要です。読み取り専用モードでは調査と提案まで行います。不足・不完全な情報は「未確認」とし、再取得を間接的に変更された全要素の一覧とは扱いません。会話の記録は永続的な変更データベースではなく、プロンプトはサーバーが強制するワークフローロックでもありません。既存の 4 つのプロンプトは変更しません。

---

## 設定

開発版は `_changes` とモデル別のローカル履歴 `_history` を返します。履歴は call log と独立して既定で有効です。記録を止めるには `--disable-change-history` を指定します。`meta` の `revit_record_change` は明示した call ID に理由を関連付け、`revit_get_change_records` は履歴を検索します。プライバシー、制限、復旧は [変更の記録](docs/change-tracking.md) を参照してください。既存の候補パッケージにはまだ含まれていません。

優先度（高い方が勝つ）：**CLI → env（`BIMWRIGHT_*`）→** `%LOCALAPPDATA%\Bimwright\rvt-mcp\rvtmcp.config.json`。

| 設定 | CLI | Env | JSON |
|------|-----|-----|------|
| ターゲット年 | `--target 2024` | `BIMWRIGHT_TARGET` | `target` |
| Toolsets | `--toolsets query,create` | `BIMWRIGHT_TOOLSETS` | `toolsets` |
| 読み取り専用 | `--read-only` | `BIMWRIGHT_READ_ONLY=1` | `readOnly` |
| send_code | `--enable-send-code` / `--disable-send-code` | `BIMWRIGHT_ENABLE_SEND_CODE` | `enableSendCode` |
| Call log | `--enable-call-log` / `--disable-call-log` | `BIMWRIGHT_ENABLE_CALL_LOG` | `enableCallLog` |
| Change history (default ON) | `--enable-change-history` / `--disable-change-history` | `BIMWRIGHT_ENABLE_CHANGE_HISTORY` | `enableChangeHistory` |
| Response guard | `--enable-response-guard` / `--disable-response-guard` | `BIMWRIGHT_ENABLE_RESPONSE_GUARD` | `enableResponseGuard` |
| Warn bytes | `--response-warn-bytes` | `BIMWRIGHT_RESPONSE_WARN_BYTES` | `responseWarnBytes` |
| Strong warn bytes | `--response-strong-warn-bytes` | `BIMWRIGHT_RESPONSE_STRONG_WARN_BYTES` | `responseStrongWarnBytes` |
| Budget bytes | `--response-budget-bytes` | `BIMWRIGHT_RESPONSE_BUDGET_BYTES` | `responseBudgetBytes` |
| Transport cap | `--max-response-bytes` | `BIMWRIGHT_MAX_RESPONSE_BYTES` | `maxResponseBytes` |
| LAN バインド（プラグイン） | — | `BIMWRIGHT_ALLOW_LAN_BIND=1` | `allowLanBind` |
| ToolBaker 面 | `--enable-toolbaker` / `--disable-toolbaker` | `BIMWRIGHT_ENABLE_TOOLBAKER` | `enableToolbaker` |
| Adaptive bake | `--enable-adaptive-bake` / `--disable-adaptive-bake` | `BIMWRIGHT_ENABLE_ADAPTIVE_BAKE=1` | `enableAdaptiveBake` |
| send_code 本体キャッシュ（bake クラスタ） | `--cache-send-code-bodies` / `--no-…` | `BIMWRIGHT_CACHE_SEND_CODE_BODIES=1` | `cacheSendCodeBodies` |
| send_code journal 永続化 | `--persist-send-code-bodies` / `--no-…` | `BIMWRIGHT_PERSIST_SEND_CODE_BODIES=1` | `persistSendCodeBodies` |
| Journal TTL | `--persist-send-code-bodies-for 4h` | `BIMWRIGHT_PERSIST_SEND_CODE_BODIES_TTL` | `persistSendCodeBodiesUntil` |
| 完了トースト（既定オン） | リボン **Toast** | `BIMWRIGHT_ENABLE_TOAST=0` | `enableToast` |
| UI 言語（アドイン） | リボン **Language** | `BIMWRIGHT_UI_LANGUAGE` | `uiLanguage` |

サーバ側フラグ変更後は MCP 接続を再起動し、クライアントが新しいツール一覧を取るようにしてください。

---

## Permissions & auto mode — 自動実行の権限

これらの controls は未公開の v1.0.0 リリース候補に含まれます。公開済み v0.8.1 パッケージには新しい switch とツール単位の read-only フィルターはありません。`send_code` と `run_baked_tool` は直接呼び出してください。`batch_execute` はこれらを拒否します。

Annotations は各ツールのドキュメントとファイルへの影響を示します。一時的な選択、アクティブビュー、ズームは read-only に含まれます。`send_code` には annotations がありません。自動許可から外し、コード実行ごとに確認してください。Claude Code では下記の read-only allow list のみを使用し、広い `mcp__rvt-mcp__*` wildcard を許可しないでください。この一覧は `--toolsets all` に対応し、選択した toolset では公開数が減る場合があります。

<details>
<summary>Annotations から生成した read-only allow list</summary>

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
      "mcp__rvt-mcp__revit_get_change_records",
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

send_code は ToolBaker と独立して既定 **オン**、call-log は既定 **オフ** です。優先順は CLI > 環境変数 > JSON。認証済みサーバー設定はリクエスト単位でプラグイン設定に優先します。call-log オフでは server journal、plugin `mcp-calls.jsonl`、send-code 本文 journal に書き込みません。メモリ内 History は利用可能です。本文 journal には call-log オンと別の TTL opt-in が必要です。ToolBaker `usage.jsonl` は adaptive-bake 設定に従う別の記録です。

Response guard は既定 **オン**。UTF-8 の警告は 65536 byte、強い警告は 262144 byte 超、budget は 716800 byte、transport cap は 1048576 byte。サーバーは JSON エスケープ、MCP content と metadata も測定します。過大な読み取り結果は絞り込み案付きの `RESPONSE_TOO_LARGE`、完了した書き込みは小さな要約になります。任意コードの出力はローカルファイルに保存し、`mutation_applied: null` を返します。コマンドを再実行せず、そのファイルを確認してください。guard をオフにしても transport cap は残ります。値は整数 >=1024、順序は `warn <= strong <= budget <= max`。budget を下げる場合は警告値も調整してください。

## Supported Revit versions

| Revit | プラグイン TFM | トランスポート |
|-------|----------------|----------------|
| 2022–2024 | .NET Framework 4.8 | TCP |
| 2025–2026 | .NET 8 (`net8.0-windows7.0`) | Named Pipe |
| 2027 | .NET 10 (`net10.0-windows7.0`) | Named Pipe |

フル Revit デスクトップのみで、Revit Viewer は非対応です。CI は 6 つのアドインすべてをビルドしますが、実行時の深さは年で差があります — baked ツールとカスタム C# は使う年で再確認してください。

---

## セキュリティとプライバシー

- 既定はローカル：loopback TCP またはローカル named pipe で、`%LOCALAPPDATA%\Bimwright\rvt-mcp\` の discovery ファイルにセッションごとの auth token があります。
- ツール引数はハンドラ実行前にスキーマ検証され、モデルへ返すエラーはサニタイズされます。
- `send_code` は Revit プロセス内で任意の C# を実行します — 強力で危険です。許容できなければ `--read-only` または `--disable-send-code` を使ってください。
- Adaptive bake、body キャッシュ、send_code journal は opt-in で、ユーザプロファイル下に留まります。既定では raw send_code 本体を長期ログに書きません。

詳細：[SECURITY.md](SECURITY.md)、[docs/bake.md](docs/bake.md)。

---

## ドキュメント

| ドキュメント | 内容 |
|--------------|------|
| [AGENTS.md](AGENTS.md) | エージェント向けインストール手順 |
| [docs/install.md](docs/install.md) | インストーラ詳細、更新、アンインストール、開発者/NuGet インストール |
| [docs/mcp-client-wiring.md](docs/mcp-client-wiring.md) | クライアント別の MCP 接続手順 |
| [ARCHITECTURE.md](ARCHITECTURE.md) | プロセス、転送、DTO 規約 |
| [docs/send-code.md](docs/send-code.md) | send_code のソース形式と失敗処理 |
| [docs/bake.md](docs/bake.md) | Adaptive bake と本体プライバシー |
| [docs/localization.md](docs/localization.md) | UI 言語、オーバーライド、ホットリロード |
| [CONTRIBUTING.md](CONTRIBUTING.md) | ビルド、テスト、ツール追加 |
| [CHANGELOG.md](CHANGELOG.md) | リリースノート |

---

## コミュニティからの貢献

コード、不具合報告、再現例、提案が rvt-mcp の改善につながっています。以下の方々に感謝します。

| 貢献者 | 内容 |
|--------|------|
| [@thiagobarretosn-hue](https://github.com/thiagobarretosn-hue) | MEP ネットワークの構成要素と配管システムの処理に関する、再現例付きの不具合報告（[#11](https://github.com/bimwright/rvt-mcp/issues/11)、[#12](https://github.com/bimwright/rvt-mcp/issues/12)）。 |
| [@razmikb](https://github.com/razmikb) | ホスト付きファミリの配置と階段/send-code の不具合報告。配置検証、ヘルパークラス対応、失敗処理テストの拡充につながりました（[#13](https://github.com/bimwright/rvt-mcp/issues/13)、[#14](https://github.com/bimwright/rvt-mcp/issues/14)）。 |
| [@Thestreetarckitect](https://github.com/Thestreetarckitect) | ロードマップと対応範囲の明確化につながった Family Authoring Tool Suite の提案（[#7](https://github.com/bimwright/rvt-mcp/issues/7)）。 |
| [@PhanCongVuDuc](https://github.com/PhanCongVuDuc) | プルリクエスト [#15](https://github.com/bimwright/rvt-mcp/pull/15)：モデル未オープン時にも `send_code` を実行できるようにする修正、`revit_switch_target` のヒント修正、そして `revit_open_model` の提案（手直しのうえ v0.8.1 で公開）。 |

---

## bimwright

AI アシスタントと BIM・CAD アプリケーションをつなぐオープンソースのツール。

**bimwright** は **BIM** と **wright** を組み合わせた名前です。wright は、ものを作る人や建てる人を表す古い英語で、*shipwright*（船大工）などに使われます。

- [rvt-mcp](https://github.com/bimwright/rvt-mcp) — Revit  
- [dwg-mcp](https://github.com/bimwright/dwg-mcp) — AutoCAD  
- [nwd-mcp](https://github.com/bimwright/nwd-mcp) — Navisworks  
- [ipt-mcp](https://github.com/bimwright/ipt-mcp) — Inventor  
- [bim-wiki](https://github.com/bimwright/bim-wiki) — ベトナム語優先 BIM ナレッジ  

---

## ライセンス

Apache-2.0 — [LICENSE](LICENSE)。

フォークやリブランドは歓迎します。必要なのはライセンス条項の遵守のみです（`LICENSE` と著作権表示を残し、変更したファイルを明記すること）。rvt-mcp が役に立った場合、スターや製品内での BIMwright への言及をいただけると嬉しいですが、完全に任意です。Issue や PR はいつでも歓迎します。

Revit および Autodesk は Autodesk, Inc. の商標です。bimwright は独立したオープンソースプロジェクトであり、Autodesk とは提携していません。
