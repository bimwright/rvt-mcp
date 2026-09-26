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
  <a href="#tools"><img src="https://img.shields.io/badge/MCP-230%20tools-6C47FF" alt="MCP tools" /></a>
  <a href="https://github.com/bimwright/rvt-mcp/releases/latest"><img src="https://img.shields.io/github/v/release/bimwright/rvt-mcp" alt="latest release" /></a>
  <a href="CHANGELOG.md"><img src="https://img.shields.io/badge/changelog-version%20history-informational" alt="changelog" /></a>
</p>

<p align="center">
  <a href="README.md">English</a> · <a href="README.vi.md">Tiếng Việt</a> · <a href="README.zh-CN.md">简体中文</a> · 日本語
</p>

---

## これは何か

`rvt-mcp` は MCP クライアントと起動中の Revit セッションをつなぐ**ローカル**ブリッジです。.NET 8 のサーバが stdio で MCP を話し、Revit の年ごと（2022–2027）の薄いアドインが Revit 内で動作します。両者は localhost TCP（≤2024）または named pipe（≥2025）で接続します。すべてマシン内で完結し、全体が C# で、ツール境界の長さは mm です。詳細は [ARCHITECTURE.md](ARCHITECTURE.md)。

エージェントには、よくある Revit 作業のための **typed ツール面**、それ以外のための C# の逃げ道、繰り返すパターンを個人ツールにする**任意**の仕組み（ToolBaker）があります。共有ランタイムから始めて、*自分の*ツールを育ててください。Family Editor でのオーサリングは当面対象外です（[ロードマップ](docs/roadmap.md)）。

---

## インストール

[GitHub Releases](https://github.com/bimwright/rvt-mcp/releases/latest) の setup ZIP を使ってください。自己完結サーバと Revit 2022–2027 のアドインが入っており、.NET SDK もソースの clone も不要です。**AI エージェント:** [AGENTS.md](AGENTS.md) に従い、開発者セットアップを求められない限り clone や build はしないでください。

```powershell
$tag = (Invoke-RestMethod https://api.github.com/repos/bimwright/rvt-mcp/releases/latest).tag_name
$zip = "$env:TEMP\RvtMcp.Setup-$tag-win-x64.zip"
$dir = "$env:TEMP\RvtMcp.Setup-$tag-win-x64"
Invoke-WebRequest "https://github.com/bimwright/rvt-mcp/releases/download/$tag/RvtMcp.Setup-$tag-win-x64.zip" -OutFile $zip
Expand-Archive $zip -DestinationPath $dir -Force

powershell -ExecutionPolicy Bypass -File "$dir\install.ps1" -WhatIf
powershell -ExecutionPolicy Bypass -File "$dir\install.ps1"
```

先に Revit を閉じてください。インストーラは `Revit.exe` がある Revit 2022–2027 を検出し、該当アドインとサーバを `%LOCALAPPDATA%\RvtMcp\rvt\server\current\rvt-mcp.exe` に入れ、両方を検証し、エラー時はロールバックします。MCP クライアントの設定には触れません。詳細と他のインストール方法（開発者、NuGet サーバのみ）：[docs/install.md](docs/install.md)。

### MCP クライアントを接続

名前 `rvt-mcp` の stdio サーバを 1 つ登録し、コマンドには上記のサーバパスを絶対パスで指定します。クライアント自身の `mcp add` コマンド、設定画面、設定ファイルのいずれかを使ってください。stdio 対応の MCP クライアントならどれでも使えます（Claude Code、Claude Desktop、Codex、Cursor、VS Code、Gemini CLI、OpenCode、Kilo など）。検証済みのクライアント別手順：[docs/mcp-client-wiring.md](docs/mcp-client-wiring.md)。

### 動作確認

1. モデル付きで Revit を開く。
2. リボン（**アドイン**（Add-Ins）タブ → **RvtMcp** パネル）から MCP 接続を開始。
3. MCP クライアントで tools を列挙し、`revit_get_current_view_info` を呼ぶ。

だいたい次のような形：

```json
{ "viewName": "Level 1", "viewType": "FloorPlan", "levelName": "Level 1", "scale": 100 }
```

失敗なら未完了です — クライアント設定かアドインの読み込みを先に直してください。

### 更新

Revit と MCP クライアントを閉じ、新しいリリース ZIP を新しいフォルダに展開して、その `install.ps1 -WhatIf`、続いて `install.ps1` を実行します。先にアンインストールはしないでください。サーバのパスは変わらないため、クライアントは再起動だけで済みます。v0.6.2 以前から更新する場合は、クライアントを上記の `current` パスに向け直し、`install.ps1 -PruneOldServers` で古いサーバフォルダを削除してください。詳細：[docs/install.md](docs/install.md#upgrade)。

### アンインストール

setup ZIP のフォルダから：

```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -WhatIf
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -Yes
```

アドインとサーバを削除しますが、`-Purge` を付けない限り設定、翻訳、ToolBaker データ、ログは残ります。MCP クライアントの `rvt-mcp` エントリは各自で削除してください。詳細：[docs/install.md](docs/install.md#uninstall)。

---

## Tools

| モード | Tools | 注記 |
|--------|------:|------|
| 既定 | **41** | `query` + `create` + `view` + `meta` |
| `--toolsets all` | **230** | フルカタログ |
| `all` + adaptive bake | **233** | 提案ライフサイクル 3 ツールを追加 |

件数に個人 baked ツールは含みません。その他の toolset は明示するまでオフです（例：`--toolsets query,view,meta,mep` または `--toolsets all`）。`--read-only` は書き込み可能な toolset をすべて落とします（`create` 含む）。

| Toolset | 範囲 |
|---------|------|
| `query` | ビュー、選択、フィルタ、統計、パラメータ、関係、ワークセット、グループ/アセンブリ |
| `create` | 通り芯、レベル、部屋、線/点/面要素、グループ |
| `view` | ビュー作成、シート配置補助、キャプチャ、クロップ/縮尺 |
| `meta` | バッチ（最大 20）、複数 Revit、プロジェクト情報、purge（MVP）、メッセージ、send_code |
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
| `kei` | KEI プロジェクト DB、SQLite 照会/書き込み（WAL 安全）、設備インポート |

### send_code、ToolBaker、リボン、表示言語

- **`revit_send_code_to_revit`**（既定オン）は、合う typed ツールがないときに C# 本体を Revit 内でコンパイルして実行します。`--read-only` または `--disable-toolbaker` で外れます。[docs/send-code.md](docs/send-code.md) を参照。階段は [docs/stairs-workflow.md](docs/stairs-workflow.md)。
- **ToolBaker：** `revit_list_baked_tools` / `revit_run_baked_tool` には `--toolsets toolbaker` が必要です。Adaptive bake（`--enable-adaptive-bake`、既定オフ）は繰り返しの呼び出しからツールを提案し、accept するまで何も追加されません。Bake のコンパイルは Revit 内で行われ、Visual Studio は不要です。[docs/bake.md](docs/bake.md)。
- **リボン：** 接続の開始/停止、**History** で過去の呼び出しの検索と再実行、完了 **Toast** の切り替え（既定オン）。
- **表示言語：** アドインの UI は 15 言語に対応し、Revit の UI 言語に従います。リボンのスライドアウトにある **Language** コンボで変更できます。ツール名とペイロードは英語のままです。[docs/localization.md](docs/localization.md)。

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
| 完了トースト（既定オン） | リボン **Toast** | `BIMWRIGHT_ENABLE_TOAST=0` | `enableToast` |
| UI 言語（アドイン） | リボン **Language** | `BIMWRIGHT_UI_LANGUAGE` | `uiLanguage` |

サーバ側フラグ変更後は MCP 接続を再起動し、クライアントが新しいツール一覧を取るようにしてください。

---

## Supported Revit versions

| Revit | プラグイン TFM | トランスポート |
|-------|----------------|----------------|
| 2022–2024 | .NET Framework 4.8 | TCP |
| 2025–2026 | .NET 8 (`net8.0-windows7.0`) | Named Pipe |
| 2027 | .NET 10 (`net10.0-windows7.0`) | Named Pipe |

フル Revit デスクトップのみで、Revit Viewer は非対応です。CI は 6 つのアドインすべてをビルドしますが、実行時の深さは年で差があります — baked ツールとカスタム C# は使う年で再確認してください。

---

## セキュリティとプライバシー

- 既定はローカル：loopback TCP またはローカル named pipe で、`%LOCALAPPDATA%\RvtMcp\` の discovery ファイルにセッションごとの auth token があります。
- ツール引数はハンドラ実行前にスキーマ検証され、モデルへ返すエラーはサニタイズされます。
- `send_code` は Revit プロセス内で任意の C# を実行します — 強力で危険です。許容できなければ `--read-only` または `--disable-toolbaker` を使ってください。
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
| [docs/roadmap.md](docs/roadmap.md) | 直近の hardening と non-goals |
| [docs/kei-equipment-import.md](docs/kei-equipment-import.md) | KEI SQLite ツール（`--toolsets kei`） |
| [CONTRIBUTING.md](CONTRIBUTING.md) | ビルド、テスト、ツール追加 |
| [CHANGELOG.md](CHANGELOG.md) | リリースノート |

---

## コミュニティからの貢献

不具合報告、再現例、提案が rvt-mcp の改善につながっています。以下の方々に感謝します。

| 貢献者 | 内容 |
|--------|------|
| [@thiagobarretosn-hue](https://github.com/thiagobarretosn-hue) | MEP ネットワークの構成要素と配管システムの処理に関する、再現例付きの不具合報告（[#11](https://github.com/bimwright/rvt-mcp/issues/11)、[#12](https://github.com/bimwright/rvt-mcp/issues/12)）。 |
| [@razmikb](https://github.com/razmikb) | ホスト付きファミリの配置と階段/send-code の不具合報告。配置検証、ヘルパークラス対応、失敗処理テストの拡充につながりました（[#13](https://github.com/bimwright/rvt-mcp/issues/13)、[#14](https://github.com/bimwright/rvt-mcp/issues/14)）。 |
| [@Thestreetarckitect](https://github.com/Thestreetarckitect) | ロードマップと対応範囲の明確化につながった Family Authoring Tool Suite の提案（[#7](https://github.com/bimwright/rvt-mcp/issues/7)）。 |

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
