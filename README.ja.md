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
</p>

<p align="center">
  <a href="README.md">English</a> · <a href="README.vi.md">Tiếng Việt</a> · <a href="README.zh-CN.md">简体中文</a> · 日本語
</p>

---

## インストール

**setup ZIP は現在公開されていません。** GitHub Releases を取り下げたため、v0.5.0 以前はダウンロード対象ではありません。v0.6.0 のクライアント ZIP もまだ出ていません。

古い tag、古い `RvtMcp.Setup-*.zip` URL、`dotnet tool install -g Bimwright.Rvt.Server` / `RvtMcp.Server` は**使わないでください**。

**AI エージェントの場合：** 停止してください。公開インストーラがないことをユーザーに伝えてください。開発者セットアップを明示的に求められない限り、clone / build / NuGet インストールはしないでください。手順は [AGENTS.md](AGENTS.md)。

新しい GitHub Release が出るまでは、サポートされるインストールはソースからのビルドだけです（下記の開発者インストール）。

AutoCAD は別製品の [dwg-mcp](https://github.com/bimwright/dwg-mcp) を別途インストールしてください。

### 動作確認

1. モデル付きで Revit を開く。
2. リボン（BIMwright / RvtMcp）から MCP 接続を開始。
3. MCP クライアントで tools を列挙し、`revit_get_current_view_info` を呼ぶ。

だいたい次のような形：

```json
{ "viewName": "Level 1", "viewType": "FloorPlan", "levelName": "Level 1", "scale": 100 }
```

失敗なら未完了 — クライアント設定 / プラグイン読み込みを先に直してください。

### アンインストール

setup ZIP（またはリポジトリの scripts）から：

```powershell
# Setup ZIP:
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -WhatIf
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -Yes

# リポジトリ clone:
powershell -ExecutionPolicy Bypass -File .\scripts\uninstall-all.ps1 -WhatIf
powershell -ExecutionPolicy Bypass -File .\scripts\uninstall-all.ps1 -Yes
```

プラグイン、自己完結サーバ、クライアント項目、discovery、ログ、ToolBaker キャッシュを削除します。

### 開発者インストール

```powershell
git clone https://github.com/bimwright/rvt-mcp.git
cd rvt-mcp
dotnet build src/RvtMcp.sln -c Debug
powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1 -SourceDir . -Client none
```

先にすべての Revit を閉じてください。ビルドはプラグイン DLL を `%APPDATA%\Autodesk\Revit\Addins\<year>\RvtMcp\` に配備します。`install.ps1` は Revit 2022–2027 を検出し、サーバを `%LOCALAPPDATA%\RvtMcp\rvt\server\<version>\` にコピーし、検出した MCP クライアントを配線します（`-Client codex|opencode|claude|kilo|none`、1 年だけなら `-Years 2024`）。

`RvtMcp.Server` や `Bimwright.Rvt.Server` を `dotnet tool install` しないでください。それは現在のインストール経路ではありません。

### `Bimwright.Rvt.*`（v0.3 以前）からの移行

v0.4+ でパッケージ/フォルダ名が `RvtMcp.*` に変わりました（リポジトリ名とブランドは bimwright のまま）。

1. すべての Revit を閉じる。
2. `pwsh scripts/uninstall-old.ps1` — 旧 `%APPDATA%\…\Bimwright\` プラグインと旧サーバ root を削除。ユーザーの bake/journal は残し、新版初回起動で `%LOCALAPPDATA%\RvtMcp\` へ移行。
3. ソースから入れる（上記の開発者インストール）。v0.6.0 の GitHub Release ZIP を待ち、古い公開パッケージは入れない。
4. MCP クライアントのエントリ名は **`rvt-mcp`**（旧 `bimwright-rvt-r22`… 年別エントリはインストーラが削除）。

---

## これは何か

`rvt-mcp` は MCP クライアント（Claude、Cursor、Codex、OpenCode など）と起動中の Revit セッションをつなぐ**ローカル**ブリッジです。

2 プロセス：

| 部品 | 役割 |
|------|------|
| **RvtMcp.Server** | .NET 8 MCP サーバ（stdio）。Revit 参照なし — どのマシンでもビルド可。 |
| **RvtMcp.Plugin** | Revit 年ごとの薄い add-in（2022–2027）。Revit 内・UI スレッドで実行。 |

Agent → MCP → server → localhost TCP（≤2024）または Named Pipe（≥2025）→ plugin → Revit API。

すべてユーザーマシン上。ゲートウェイ自体にクラウド中継は不要です。

Node/TypeScript のサイドカーはありません。サーバ、プラグイン、ハンドラ、ToolBaker はすべて C#。共有コマンドは `src/shared/`。年ごとの小さなシェルと、API 差分の `#if`。詳細は [ARCHITECTURE.md](ARCHITECTURE.md)。

---

## なぜあるか

Revit ユーザーは自動化したいことはだいたい分かっています。難しかったのはそれをソフトとして出すこと：C#/Dynamo を学ぶ、API と格闘する、add-in をパッケージする、バージョンアップを生き延びる — 外注するか、オフィスに半分しか合わない固定ツールを買うか。

エージェントは前半（作業を説明する、その場で試す）を変えます。トランザクション、単位、選択、ワークシェア、「モデルを壊したか？」は消えません。このゲートウェイはそこ用です：よくある作業の **typed ツール面**、Revit 内 ad-hoc C# の逃げ道、繰り返すローカルパターンを個人ツールにする**任意**の道（ToolBaker）。

すべての会社向け万能 add-in ではありません。オフィスは違います。共有ランタイムの上に*自分の*ツールを育てる、という賭けです。

**スコープ（正直に）：** 端ケースごとに MCP ツールを量産しません。typed があればそれ、なければ `revit_send_code_to_revit`（C# のみ）。プロジェクト側の family **管理**はある；フル Family Editor オーサリング一式と Revit Viewer ホストは当面対象外 — [docs/roadmap.md](docs/roadmap.md)。

---

## 普通のセッション

1. モデル付き Revit を開き、プラグイン接続（リボン）。
2. MCP クライアントが `rvt-mcp` / インストール済みサーバを起動。
3. エージェントがツール利用：ビュー/選択の照会、通り芯・部屋、シート、MEP、エクスポート… ツール境界の長さは **mm**。
4. 複数書き込みを 1 アンドゥ：`revit_batch_execute`。
5. 複数 Revit：`revit_list_available_targets` のあと `revit_switch_target`、年は 4 桁（`2024`、`R24` ではない）。

typed が合わないとき：

```text
revit_send_code_to_revit   # C# 本体、プラグイン内でコンパイル実行
```

このツールは既定オン（toolset `meta`）。モデル内コンパイルを嫌うなら `--read-only` または `--disable-toolbaker`。

### ToolBaker（任意）

既定面には `revit_send_code_to_revit` がある。`revit_list_baked_tools` / `revit_run_baked_tool` は `--toolsets toolbaker`（または `--toolsets all`）が必要。

**Adaptive bake**（usage から新ツール提案）は明示するまで**オフ**。オンにすると `revit_list_bake_suggestions` に繰り返しパターンが出ることがあり、accept/dismiss はあなたが決める。accept なしではリボンに勝手に載りません。

よく使うフラグ（JSON/env も — [設定](#設定)）：

| 目的 | オンにするもの |
|------|----------------|
| 繰り返し **typed** 呼び出しから学ぶ | `--enable-adaptive-bake` |
| **`send_code`** 本体もクラスタして提案 | さらに `--cache-send-code-bodies`（リダクト済み・ローカル） |
| 短命のディスク journal | `persistSendCodeBodies` + TTL（既定のプライバシーではオフ） |

Bake のコンパイルは **Revit 内** Roslyn — エンドユーザーに Visual Studio は不要。[docs/bake.md](docs/bake.md)。

### トースト（任意）

完了トーストは既定**オフ**。リボン **Toast**、`enableToast`、または `BIMWRIGHT_ENABLE_TOAST=1` でオン。**完了後**のみ表示（進行中トーストなし）。Capture 成功時、パス allowlist 内ならサムネイル可。リボン **Status** にも toast と bake/プライバシー状態が出ます。

---

## アーキテクチャ（短）

```text
MCP client (stdio)
    → RvtMcp.Server (.NET 8)
        → TCP (Revit 2022–2024) または Named Pipe (2025–2027)
            → Plugin shell（年ごと）
                → ExternalEvent → Revit API / トランザクション / アンドゥ
```

ハンドラはプレーン DTO のみ — ライブ Revit オブジェクトは線に載せません。

---

## Tools

件数（個人 baked ツールは含まない）：

| モード | Tools | 注記 |
|--------|------:|------|
| 既定 | **40** | `query` + `create` + `view` + `meta` |
| `--toolsets all` | **229** | フルカタログ |
| `all` + adaptive bake | **232** | 提案ライフサイクル 3 ツールを追加 |

MCP 名は `revit_*`。server↔plugin ワイヤ名はプレフィックスなし snake_case。

**既定オン toolset：** `query`, `create`, `view`, `meta`

**明示するまでオフ：** それ以外すべて（`export`、`geometry`、`mep`、`structural`、`toolbaker`、`modify`、`delete` など）。  
例：`--toolsets query,view,meta` または `--toolsets all`。  
`--read-only` は書き込み可能な toolset をすべて落とします（`create` 含む）。

| Toolset | 範囲 | 既定 |
|---------|------|------|
| `query` | ビュー、選択、フィルタ、統計、パラメータ、関係、ワークセット、グループ/アセンブリ | on |
| `create` | 通り芯、レベル、部屋、線/点/面要素、グループ | on |
| `view` | ビュー作成、シート配置補助、キャプチャ、クロップ/縮尺 | on |
| `meta` | バッチ（最大 20）、複数 Revit、プロジェクト情報、purge（MVP）、メッセージ、send_code | on |
| `lint` | ビュー命名、firm-profile、警告サマリ | off |
| `schedule` | 集計表 list/作成、フィールド、式、データ | off |
| `families` | ロード/アンロード、タイプ、インスタンス、監査、`.rfa` エクスポート（プロジェクト側） | off |
| `modify` | 操作/着色、パラメータ、タイプ変更、ワークセット | off |
| `delete` | id 削除 | off |
| `annotation` | タグ、文字、寸法、塗り、キーノート、検査 | off |
| `export` | PDF/DWG/IFC/NWC、部屋データなど | off |
| `mep` | システム、コネクタ、ネットワーク、端末配置など | off |
| `graphics` | ビューフィルタ、オーバーライド、可視/フェーズ | off |
| `toolbaker` | list/run baked；adaptive 時のみ提案ツール | off |
| `sheets` | シート、タイトルブロック、リビジョン、番号変更 | off |
| `materials` | マテリアル、外観、割当、拾い | off |
| `geometry` | BBox、測距、干渉、体積/面積… | off |
| `rooms` | 部屋/面積/スペース、仕上、セパレータ | off |
| `links` | Revit/CAD リンク、座標監査、座標の取得/公開 | off |
| `parameters` | プロジェクト/共有パラメータ | off |
| `organization` | 保存選択、ビューテンプレート | off |
| `workflows` | 干渉/監査/シート/拾い系の複合 | off |
| `structural` | 柱梁基礎、鉄筋、荷重… | off |
| `kei` | KEI プロジェクト DB、SQLite 照会/書き込み（WAL 安全）、設備インポート | off |

### 代表ツール

200+ スキーマの全列挙ではない — よく使う錨：

| Toolset | Tool | 役割 |
|---------|------|------|
| `query` | `revit_get_current_view_info` | アクティブビューの種類・レベル・縮尺 |
| `query` | `revit_get_selected_elements` | 現在の選択 |
| `query` | `revit_ai_element_filter` | カテゴリ + パラメータ（mm） |
| `query` | `revit_get_element_details` | 位置、bbox、ワークセット、フェーズ… |
| `create` | `revit_create_grid` / `revit_create_level` / `revit_create_room` | 基本レイアウト |
| `create` | `revit_create_point_based_element` | ドア・家具など（type id） |
| `view` | `revit_capture_view_image` | ラスタキャプチャ（パス allowlist） |
| `meta` | `revit_batch_execute` | 複数コマンドを 1 つの `TransactionGroup` |
| `meta` | `revit_list_available_targets` / `revit_switch_target` | 複数 Revit |
| `families` | `revit_load_family_from_path` | プロジェクトへ `.rfa` ロード |
| `links` | `revit_get_project_coordinate_system` / `revit_get_link_coordinate_system` | ホスト/リンク原点、敷地、変換、真北 |
| `toolbaker` | `revit_send_code_to_revit` | 逃げ道（C#） |
| `toolbaker` | `revit_list_baked_tools` / `revit_run_baked_tool` | accept 済み個人ツール |
| `toolbaker` | `revit_list_bake_suggestions` | adaptive のみ |
| `lint` | `revit_analyze_view_naming_patterns` | 命名の外れ値 |

テストの golden が正確な surface を固定。件数とコードが食い違うときはテスト/コードを信頼。

---

## Supported Revit versions

| Revit | プラグイン TFM | トランスポート |
|-------|----------------|----------------|
| 2022–2024 | .NET Framework 4.8 | TCP |
| 2025–2026 | .NET 8 (`net8.0-windows7.0`) | Named Pipe |
| 2027 | .NET 10 (`net10.0-windows7.0`) | Named Pipe |

6 シェルともコンパイル対象。実行時の深さは年で差がある — bake とカスタム C# は使う年で再確認。

**ホスト：** フル Revit デスクトップのみ。Revit Viewer は非対応。

---

## セキュリティとプライバシー

- 既定はローカル転送（loopback TCP / ローカル named pipe）。
- `%LOCALAPPDATA%\RvtMcp\` の discovery にセッションごとの auth token。
- ツール引数はハンドラ前にスキーマ検証。
- モデルへ返すエラーはサニタイズ（パス漏洩を抑制）。
- `send_code` は Revit プロセス内で任意 C# — 強力で危険。不可なら toolbaker をオフ。
- Adaptive bake、body キャッシュ、TTL journal は **opt-in** でユーザプロファイル下。既定では raw send_code 本体を長期ログに書かない。

詳細：[SECURITY.md](SECURITY.md)、[docs/bake.md](docs/bake.md)。

---

## 設定

優先度（高い方が勝つ）：**CLI → env（`BIMWRIGHT_*`）→** `%LOCALAPPDATA%\RvtMcp\rvtmcp.config.json`。

| 設定 | CLI | Env | JSON |
|------|-----|-----|------|
| ターゲット年 | `--target 2024` | `BIMWRIGHT_TARGET` | `target` |
| Toolsets | `--toolsets query,create` | `BIMWRIGHT_TOOLSETS` | `toolsets` |
| 読み取り専用 | `--read-only` | `BIMWRIGHT_READ_ONLY=1` | `readOnly` |
| LAN バインド（プラグイン） | — | `BIMWRIGHT_ALLOW_LAN_BIND=1` | `allowLanBind` |
| ToolBaker 面 | `--enable-toolbaker` / `--disable-toolbaker` | `BIMWRIGHT_ENABLE_TOOLBAKER` | `enableToolbaker` |
| Adaptive bake | `--enable-adaptive-bake` / `--disable-adaptive-bake` | `BIMWRIGHT_ENABLE_ADAPTIVE_BAKE=1` | `enableAdaptiveBake` |
| send_code 本体キャッシュ（bake クラスタ） | `--cache-send-code-bodies` / `--no-…` | `BIMWRIGHT_CACHE_SEND_CODE_BODIES=1` | `cacheSendCodeBodies` |
| send_code journal 永続化 | `--persist-send-code-bodies` / `--no-…` | `BIMWRIGHT_PERSIST_SEND_CODE_BODIES=1` | `persistSendCodeBodies` |
| Journal TTL | `--persist-send-code-bodies-for 4h` | `BIMWRIGHT_PERSIST_SEND_CODE_BODIES_TTL` | `persistSendCodeBodiesUntil` |
| 完了トースト | リボン **Toast** | `BIMWRIGHT_ENABLE_TOAST=1` | `enableToast` |

サーバ側フラグ変更後は MCP 接続を再起動し、クライアントが新しいツール一覧を取るようにしてください。

---

## MCP クライアント

| クライアント | 配線 |
|--------------|------|
| Claude Code | プロジェクト `.mcp.json` または `~/.claude.json` |
| Claude Desktop | `%APPDATA%\Claude\claude_desktop_config.json` |
| OpenCode / Codex / Kilo | `install.ps1 -Client …`（スクリプト） |
| Cursor / Cline / VS Code Copilot | ドキュメントの JSON レイアウト |
| Gemini CLI / Antigravity | `gemini mcp add` または settings JSON |

インストーラ自動検出で足りることが多い。手編集は [AGENTS.md](AGENTS.md) と `docs/mcp-config-*.md`。

---

## リポジトリ構成

```text
rvt-mcp/
├── src/
│   ├── RvtMcp.sln
│   ├── server/            # MCP server
│   ├── shared/            # Handlers, transport, ToolBaker, toast, …
│   ├── plugin-r22/ … r27/ # Revit 年ごとのシェル
├── tests/                 # xUnit + golden tool lists
├── scripts/               # install / uninstall / package
├── docs/                  # roadmap, bake, testing
├── AGENTS.md
└── ARCHITECTURE.md
```

---

## 開発

```bash
dotnet test tests/RvtMcp.Tests/RvtMcp.Tests.csproj
dotnet build src/server/RvtMcp.Server.csproj -c Release
dotnet build src/plugin-r26/RvtMcp.Plugin.R26.csproj -c Release
```

プラグインビルド前に Revit を閉じる（DLL ロック）。通常の Debug/Release は `%APPDATA%\Autodesk\Revit\Addins\<year>\RvtMcp\` へデプロイ。

```powershell
pwsh scripts/stage-plugin-zip.ps1 -Config Release
```

貢献ルールとスナップショット：[CONTRIBUTING.md](CONTRIBUTING.md)。

### 成熟度

使えるが神聖ではない。CI は 6 プラグインシェルとサーバテストをビルド。ランタイムの厚みは中間年が強め。本番モデルは慎重に、*自分の* Revit で確認。新規マシン用チェックリスト：[docs/testing/fresh-install-checklist.md](docs/testing/fresh-install-checklist.md)。

---

## その他のドキュメント

| ドキュメント | 内容 |
|--------------|------|
| [AGENTS.md](AGENTS.md) | エージェント向けインストール手順 |
| [ARCHITECTURE.md](ARCHITECTURE.md) | プロセス、転送、DTO 規約 |
| [docs/bake.md](docs/bake.md) | Adaptive bake と本体プライバシー |
| [docs/roadmap.md](docs/roadmap.md) | 直近の hardening と non-goals |
| [docs/kei-equipment-import.md](docs/kei-equipment-import.md) | KEI SQLite ツール（`--toolsets kei`） |
| [CHANGELOG.md](CHANGELOG.md) | リリースノート |

---

## bimwright

同じハウススタイルの AEC ホスト群：

- [rvt-mcp](https://github.com/bimwright/rvt-mcp) — Revit  
- [dwg-mcp](https://github.com/bimwright/dwg-mcp) — AutoCAD  
- [nwd-mcp](https://github.com/bimwright/nwd-mcp) — Navisworks  
- [ipt-mcp](https://github.com/bimwright/ipt-mcp) — Inventor  
- [bim-wiki](https://github.com/bimwright/bim-wiki) — ベトナム語優先 BIM ナレッジ  

---

## ライセンス

Apache-2.0 — [LICENSE](LICENSE)。

Revit および Autodesk は Autodesk, Inc. の商標です。bimwright は独立したオープンソースプロジェクトであり、Autodesk とは提携していません。
