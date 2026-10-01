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
  <a href="#tools"><img src="https://img.shields.io/badge/MCP-227%20tools-6C47FF" alt="MCP tools" /></a>
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
| 新規インストール | **227** | `install.ps1` が `rvtmcp.config.json` に `"toolsets": ["all"]` をシード |
| 素の `rvt-mcp.exe` | **42** | `query` + `create` + `view` + `meta` |
| `--toolsets all` | **227** | フルカタログ |
| `all` + adaptive bake | **230** | 提案ライフサイクル 3 ツールを追加 |

件数に個人 baked ツールは含みません。インストーラーがシードするのは `rvtmcp.config.json` に `toolsets` が未設定の場合のみ——独自リストはアップグレード後も残り、キー削除（または独自 CSV）で素のサーバーは 42 ツールに戻ります。`--read-only` は出所に関わらず書き込み可能 toolset（`create` 含む）をすべて落とします。

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

- **`revit_send_code_to_revit`**（既定オン）は、合う typed ツールがないときに C# 本体を Revit 内でコンパイルして実行します。`--read-only` または `--disable-toolbaker` で外れます。[docs/send-code.md](docs/send-code.md) を参照。階段は [docs/stairs-workflow.md](docs/stairs-workflow.md)。
- **ToolBaker：** `revit_list_baked_tools` / `revit_run_baked_tool` には `--toolsets toolbaker` が必要です。Adaptive bake（`--enable-adaptive-bake`、既定オフ）は繰り返しの呼び出しからツールを提案し、accept するまで何も追加されません。Bake のコンパイルは Revit 内で行われ、Visual Studio は不要です。[docs/bake.md](docs/bake.md)。
- **リボン：** 接続の開始/停止、**History** で過去の呼び出しの検索と再実行、完了 **Toast** の切り替え（既定オン）。
- **表示言語：** アドインの UI は 15 言語に対応し、Revit の UI 言語に従います。リボンのスライドアウトにある **Language** コンボで変更できます。ツール名とペイロードは英語のままです。[docs/localization.md](docs/localization.md)。

---

## 設定

優先度（高い方が勝つ）：**CLI → env（`BIMWRIGHT_*`）→** `%LOCALAPPDATA%\Bimwright\rvt-mcp\rvtmcp.config.json`。

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

- 既定はローカル：loopback TCP またはローカル named pipe で、`%LOCALAPPDATA%\Bimwright\rvt-mcp\` の discovery ファイルにセッションごとの auth token があります。
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
