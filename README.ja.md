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
  <a href="#tools"><img src="https://img.shields.io/badge/MCP-233%20tools-6C47FF" alt="MCP tools" /></a>
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

**使用したいクライアントへのインストールを AI エージェントに依頼できます。** この Windows マシンで PowerShell の実行とローカルファイルの編集ができるエージェント（Claude Code、Codex、Cursor など）が必要です。ローカルツールのない通常のチャットでは実行できません。インストールするエージェントと、Revit ツールを使うクライアントは別でも構いません。[AGENTS.md](AGENTS.md) に従って変更をプレビューし、適用前に確認します。UI 操作やアプリの再起動はユーザーに依頼する場合があります。

**Claude Desktop** の場合は、次を貼り付けてください：

```text
この Windows マシンで Claude Desktop 用に rvt-mcp をインストールしてください。
先に https://github.com/bimwright/rvt-mcp/blob/master/AGENTS.md を読んでください。
Claude Desktop だけを設定し、他のクライアントは設定しないでください。
既存のインストールを確認し、変更をプレビューして書き込み前に確認を求めてください。
UI 操作やアプリの再起動が必要なら明示してください。
```

別のクライアントを使う場合は **Claude Desktop** をその名前に置き換えてください。**Claude Code と Claude Desktop は別の対象です：** インストーラの `claude` は Code、`claude-desktop` は Desktop を指します。

**自分で実行する場合（Claude Desktop、直接設定）。** Revit を閉じ、Claude Desktop を完全に終了（トレイのプロセスも含む）してから PowerShell で：

```powershell
$tag = (Invoke-RestMethod https://api.github.com/repos/bimwright/rvt-mcp/releases/latest).tag_name
$dir = "$env:TEMP\RvtMcp.Setup-$tag-win-x64"
Invoke-WebRequest "https://github.com/bimwright/rvt-mcp/releases/download/$tag/RvtMcp.Setup-$tag-win-x64.zip" -OutFile "$dir.zip"
Expand-Archive "$dir.zip" -DestinationPath $dir -Force
powershell -ExecutionPolicy Bypass -File "$dir\install.ps1" -WhatIf -Client claude-desktop
# プレビューを確認してから適用：
powershell -ExecutionPolicy Bypass -File "$dir\install.ps1" -Client claude-desktop
```

検出したすべての Revit 2022–2027 にアドインをインストールし、Claude Desktop だけに `rvt-mcp` を登録します（設定は先にバックアップ）。Claude Code は `-Client claude`、複数の指定クライアントはカンマ区切りです。`-Client` を省略すると検出した全クライアントを接続し、`-Client none` は設定を変更しません。サーバは `%LOCALAPPDATA%\Bimwright\rvt-mcp\server\current\rvt-mcp.exe` にあります（[クライアント別手順、Desktop classic/MSIX のパスを含む](docs/mcp-client-wiring.md)）。

**動作確認：** AI クライアントを再起動し、Revit でモデルを開き、リボン（**アドイン**（Add-Ins）→ **RvtMcp**）で MCP を開始して、エージェントに `revit_get_current_view_info` を呼ばせます。アクティブビューの名前と種類が返れば成功です。

**更新：** 新しいリリースのインストーラを同じ手順で実行します。先にアンインストールは不要で、クライアントは再起動だけで済みます。**アンインストール：** 同じフォルダで `uninstall.ps1 -Yes` を実行するとアドインとサーバが削除されます（`-Purge` を付けない限り設定は残ります）。クライアントのエントリも消すには、先に `install.ps1 -Uninstall -Client auto` を実行してください。開発者向け・NuGet を含む詳細：[docs/install.md](docs/install.md)。

**v0.8.1 または v0.6.x からの更新：** これらのバージョンは設定と ToolBaker データを `%LOCALAPPDATA%\RvtMcp\` に保存します。v1.0.0 は `%LOCALAPPDATA%\Bimwright\rvt-mcp\` を使い、インストーラが旧フォルダーをそこへ移動します。先に Revit とすべての MCP クライアントを閉じてください。実行中の旧サーバーがフォルダーをロックするためです。PowerShell で `Test-Path "$env:LOCALAPPDATA\RvtMcp"` を実行すると存在を確認できます。インストーラが `Both … exist` または `Could not move …` で停止した場合、移動した分は元に戻されています。設定と ToolBaker データがあるフォルダーを残し、もう一方は削除せず **名前を変更**（例：`RvtMcp.bak`）し、そのフォルダーを使っていたクライアントを閉じてから、インストーラを再実行してください。

**Claude Desktop 拡張（MCPB、任意）：** エージェントによるインストールでは上の直接設定を既定とします。拡張/設定 UI を使いたい場合は `-Client none` で Setup を実行し、Desktop の拡張 UI から `.mcpb` をインストールします。**どちらか一方だけを使ってください。** `-Client none` は既存の手動登録を削除しません。v1.0.0 の拡張は未署名で、gateway/アドインをインストールせず、同じ release の正確な server build を必要とします。[MCPB のインストールと設定](docs/install.md#claude-desktop-mcpb)を参照してください。

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
| 新規インストール | **233** | `install.ps1` が `rvtmcp.config.json` に `"toolsets": ["all"]` をシード |
| 素の `rvt-mcp.exe` | **46** | `query` + `create` + `view` + `meta` |
| `--toolsets all` | **233** | フルカタログ |
| `all` + adaptive bake | **236** | 提案ライフサイクル 3 ツールを追加 |

件数に個人 baked ツールは含みません。インストーラーがシードするのは `rvtmcp.config.json` に `toolsets` が未設定の場合のみ——独自リストはアップグレード後も残り、キー削除（または独自 CSV）で素のサーバーは 46 ツールに戻ります。Read-only は `ReadOnly=true` のツールだけを残すため、混在 toolset 内の読み取りツールも使えます。既定の出力が inline でも、ファイルを書けるツールは除外されます。

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
| `sheets` | シート、タイトルブロック、リビジョン、番号変更、ビューポート配置 |
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
- **表示言語：** アドインの UI は 15 言語に対応し、Revit の UI 言語に従います。リボンのスライドアウトにある **Language** ボタンで変更できます。このボタンは **Settings → General → Language** を開きます（`BIMWRIGHT_UI_LANGUAGE` が引き続き優先されます）。ツール名とペイロードは英語のままです。[docs/localization.md](docs/localization.md)。

### アクティビティトーストがある理由

トーストは飾りではなく、作業のフィードバックです。実際に必要だった 3 つの点から生まれました。

- **チャットを見張る必要をなくす。** 実際の MCP ワークフローでは、AI エージェントが長時間作業していても Revit 側には目に見える反応がほとんどありません。エージェントが動いているかを確かめるためだけにチャットを見続けるのは、注意力の無駄です。アクティビティカードは完了したツール呼び出しを知らせるので、更新の合間に別の作業へ移れます。
- **マルチタスクを支える。** メンテナーは複数のデスクトップアプリを並行して開発し、繰り返しテストしています。短い通知なら、すべてのチャットを視界に置かなくてもそれらのセッションを追えます。
- **体験を現代的にする。** Revit 側のフィードバックにより、自動化の反応が分かりやすくなり、モーダルダイアログで作業を中断することもありません。

トーストが伝えるのは **ツール 1 回分の結果** であり、実行中のツールの進捗でも、タスク全体の完了でもありません。連続した結果は 1 枚のカードにまとまります。Revit が最小化されているときやモーダルダイアログで止まっているときは、通知が待たされることがあります。エージェントの作業を確認する代わりにはなりません。

各カードにはゲートウェイ名と Revit の年（例：`rvt-mcp 2022`）、直近のツール、Success · Failed · Capture のカウントが表示されます。キャプチャのプレビューは少なくとも 5 秒間カードに残ります。

意図的にホバーすると、最新 3 件の結果と各ローカル完了時刻（`HH:mm:ss`）を示すアクティビティタイムラインが開きます。上にスクロールすると、そのカードの以前の呼び出しを読めます。新しい結果は短いスライドインで末尾に追従します（行が上がり、新しい行がフェードインし、結果のドットが弾みます。視覚効果を減らす設定ではどれも表示されません）。古い呼び出しを読んでいる間は位置が保たれます。タイムライン内をクリックしても閉じずに読み続けられ、カードのそれ以外の部分をクリックすると History が開きます。ブランディングをオフにしても動作します。要約は長さが制限され、機微な情報は伏せられ、メモリ内にのみ保持され、カードを閉じると消去されます。スクリプトのオブジェクトや配列は件数で表示し、不完全な調査は不完全と明示されます。サーバー側のみのツールは、この UI 変更ではトースト対象になりません。

トーストは **既定でオン** で、オフにもできます。**Settings → Toast** で、アイドル時間（10/20/30/60 秒、既定 20）と **Show branding**（**既定オフ**、ホバー時にワードマークを表示）を選べます。変更はすぐ反映され、Revit を再起動しても保存されます。アクティビティのフィードバックを得るためにブランディングを表示する必要はありません。

**位置：** **Settings → Toast** で **Horizontal alignment**（左/右）と **Vertical alignment**（上/下）を個別に選べます。既定は左上です。変更は表示中のカードにすぐ反映・保存され、角を変えると保存済みのドラッグ位置は消去されます。**Allow dragging the card** をオンにすると、タイトル行でカードを移動できます。通常のクリックは引き続き History を開きます。ドラッグをオフにしても保存位置は保持され、オンに戻すと復元され、**Reset position** で消去されます。位置は Revit ウィンドウからの相対値で保存され、モニターの作業領域内に収まります。下側に固定したカードは上方向に伸び、スペースがなければ反対側に切り替わります。動きは Windows のアニメーション設定に従います。保存に失敗すると設定の下に表示され、そのセッション中は選択が有効なままです。最新ツール行やタイムライン行にホバーすると、機微情報を伏せた結果、Success/Failed、完了時刻、計測できた場合は所要時間が表示されます。複数の Autodesk アプリ間でのトースト調整は対象外です。

### プロンプト

v1.0.0 には 6 つの MCP プロンプトがあります。クライアントのプロンプトメニュー（Claude Code: `/mcp__rvt-mcp__revit_<name>`）から選ぶと、エージェントは手持ちのツールでスクリプトに沿って進めます。

- `revit_getting_started` — 開いているモデルの把握（読み取り専用、既定の設定で動作）。
- `revit_drawing_layout` — `request` を指定：1 枚のシートのビューポートを整列します。まずシート座標の実位置を読み取り（`revit_get_viewport_geometry`）、整列方法を合意し、ドライラン計画を示し（`revit_align_viewports`）、確認後にのみ移動して、結果を再取得します。`query,sheets,view,meta` が必要です。読み取り専用モードでは報告と提案のみです。
- `revit_change` — `change` を指定：関連要素を調べ、要求ごとに最小限の範囲を合意し、具体的な案を確認してから変更し、結果を再取得して理由を会話に記録します。`query,meta` が必要で、`send_code` は不要です。読み取り専用モードでは調査と提案まで行います。不足・不完全な情報は「未確認」とし、再取得を間接的に変更された全要素の一覧とは扱いません。会話の記録は永続的な変更データベースではなく、プロンプトはサーバーが強制するワークフローロックでもありません。
- `revit_model_audit` — モデルの健全性監査：警告、ファミリ、ドライランの purge 候補（`workflows,families,lint,meta` が必要）。
- `revit_pre_issue_check` — 発行前に対象シートを確認します（`sheets,view,annotation,lint,meta` が必要）。シート番号/ID、明示的な番号/名前フィルター、または `all` を指定します。名前付きシートセットにはメンバーシートが必要です。サンプリングされたモデル警告や不完全な確認は、シート単位の合格ではなく **NOT VERIFIED** と報告されます。
- `revit_stairs` — `send_code` による階段作成のガイド（確認後にのみ書き込み）。トランザクション/失敗/後片付けのテンプレートを含み、ソースチェックアウトは不要です。

プロンプトに必要な toolset が有効でない場合は、追加すべき `--toolsets` の行をそのまま答えます。中途半端な構成で何かが実行されることはありません。足りないツールが許す限り read-only の保護は維持され、書き込み可能な toolset を必要とするプロンプトは、設定を黙って変えずに矛盾を説明します。プロンプトはエージェントへの指示であり、サーバーが強制するワークフローのロックではありません。

---

## 設定

v1.0.0 以降、サーバーは `_changes` とモデル別のローカル履歴 `_history` を返します。履歴は call log と独立して既定で有効です。記録を止めるには `--disable-change-history` を指定します。`meta` の `revit_record_change` は明示した call ID に理由を関連付け、`revit_get_change_records` は履歴を検索します。プライバシー、制限、復旧は [変更の記録](docs/change-tracking.md) を参照してください。

`revit_survey_change_impact` は `query` にある読み取り専用ツールです。send-code が無効でも使用できます。要求ごとに `scopeThreshold` が必要で、ビュー／集計表の走査は `maxViews > 0` の明示指定時のみ行います。不完全な結果と履歴用の信頼済みスナップショットの違いは [変更影響調査](docs/change-survey.md) を参照してください。

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
| spill ファイルの保持時間（既定 36 時間） | `--spill-retention-hours <n>` | `BIMWRIGHT_SPILL_RETENTION_HOURS` | `spillRetentionHours` |
| LAN バインド（プラグイン） | — | `BIMWRIGHT_ALLOW_LAN_BIND=1` | `allowLanBind` |
| ToolBaker 面 | `--enable-toolbaker` / `--disable-toolbaker` | `BIMWRIGHT_ENABLE_TOOLBAKER` | `enableToolbaker` |
| Adaptive bake | `--enable-adaptive-bake` / `--disable-adaptive-bake` | `BIMWRIGHT_ENABLE_ADAPTIVE_BAKE=1` | `enableAdaptiveBake` |
| send_code 本体キャッシュ（bake クラスタ） | `--cache-send-code-bodies` / `--no-…` | `BIMWRIGHT_CACHE_SEND_CODE_BODIES=1` | `cacheSendCodeBodies` |
| send_code journal 永続化 | `--persist-send-code-bodies` / `--no-…` | `BIMWRIGHT_PERSIST_SEND_CODE_BODIES=1` | `persistSendCodeBodies` |
| Journal TTL | `--persist-send-code-bodies-for 4h` | `BIMWRIGHT_PERSIST_SEND_CODE_BODIES_TTL` | `persistSendCodeBodiesUntil` |
| 完了トースト（既定オン） | リボン **Toast** | `BIMWRIGHT_ENABLE_TOAST=0` | `enableToast` |
| トースト branding（既定オフ、保存あり） | Settings → Toast → **Show branding** | — | `showBranding` |
| トーストのアイドル時間（既定 20 秒） | Settings → Toast → **Idle duration** | — | `toastIdleSeconds` |
| トーストの位置（既定は左上・ドラッグなし、保存あり） | Settings → Toast → **Horizontal / Vertical alignment**、**Allow dragging the card**、**Reset position** | — | `toastHorizontalAlign`, `toastVerticalAlign`, `toastDragEnabled`, `toastDragOffset` |
| UI 言語（アドイン） | リボン **Language** | `BIMWRIGHT_UI_LANGUAGE` | `uiLanguage` |

サーバ側フラグ変更後は MCP 接続を再起動し、クライアントが新しいツール一覧を取るようにしてください。

---

## Permissions & auto mode — 自動実行の権限

これらの controls は v1.0.0 から利用できます。v0.8.1 パッケージには新しい switch とツール単位の read-only フィルターはありません。`send_code` と `run_baked_tool` は直接呼び出してください。`batch_execute` はこれらを拒否します。

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
      "mcp__rvt-mcp__revit_get_viewport_geometry",
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
      "mcp__rvt-mcp__revit_survey_change_impact",
      "mcp__rvt-mcp__revit_switch_target",
      "mcp__rvt-mcp__revit_workflow_model_audit"
    ]
  }
}
```
<!-- END READ_ONLY_ALLOWLIST -->

</details>

send_code は ToolBaker と独立して既定 **オン**、call-log は既定 **オフ** です。優先順は CLI > 環境変数 > JSON。認証済みサーバー設定はリクエスト単位でプラグイン設定に優先します。call-log オフでは server journal、plugin `mcp-calls.jsonl`、send-code 本文 journal に書き込みません。メモリ内 History は利用可能です。本文 journal には call-log オンと別の TTL opt-in が必要です。ToolBaker `usage.jsonl` は adaptive-bake 設定に従う別の記録です。

Response guard は既定 **オン**。UTF-8 の警告は 65536 byte、強い警告は 262144 byte 超、budget は 716800 byte、transport cap は 1048576 byte。サーバーは JSON エスケープ、MCP content と metadata も測定します。過大な読み取り結果は絞り込み案付きの `RESPONSE_TOO_LARGE`、完了した書き込みは小さな要約になります。任意コードの出力はローカルファイルに保存し、`mutation_applied: null` を返します。コマンドを再実行せず、そのファイルを確認してください。spill ファイルは既定で 36 時間保持されます（`--spill-retention-hours`、1-8760。無効な値は 36 を使用）。件数上限が、それより新しいファイルを削除することはありません。guard をオフにしても transport cap は残ります。値は整数 >=1024、順序は `warn <= strong <= budget <= max`。budget を下げる場合は警告値も調整してください。

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

## bimwright ファミリー

AI アシスタントと BIM・CAD アプリケーションをつなぐオープンソースのツール。

**bimwright** は **BIM** と **wright** を組み合わせた名前です。wright は、ものを作る人や建てる人を表す古い英語で、*shipwright*（船大工）などに使われます。

[ゲートウェイ名の付け方](https://github.com/bimwright/.github/blob/master/profile/README.md#naming)を参照してください。

- [**rvt-mcp**](https://github.com/bimwright/rvt-mcp) — Autodesk® Revit®
- [**dwg-mcp**](https://github.com/bimwright/dwg-mcp) — Autodesk® AutoCAD®
- [**nwd-mcp**](https://github.com/bimwright/nwd-mcp) — Autodesk® Navisworks®
- [**ipt-mcp**](https://github.com/bimwright/ipt-mcp) — Autodesk® Inventor®
- [**bim-wiki**](https://github.com/bimwright/bim-wiki) — ベトナム語優先の BIM 知識ベース

---

## ライセンス

Apache-2.0 — [LICENSE](LICENSE)。

フォークやリブランドは歓迎します。必要なのはライセンス条項の遵守のみです（`LICENSE` と著作権表示を残し、変更したファイルを明記すること）。rvt-mcp が役に立った場合、スターや製品内での BIMwright への言及をいただけると嬉しいですが、完全に任意です。Issue や PR はいつでも歓迎します。

Revit および Autodesk は Autodesk, Inc. の商標です。bimwright は独立したオープンソースプロジェクトであり、Autodesk とは提携していません。
