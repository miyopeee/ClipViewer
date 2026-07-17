# ClipViewer 開発引き継ぎノート

> このドキュメントは **新セッション（AI）への申し送り用** である。
> 既存の仕様ドキュメントに書かれていない開発経緯・地雷・積み残し情報を集約する。
>
> **作成日**: 2026/07/05  
> **対象バージョン**: v0.8.5（現行最新）

---

## 0. まず読むべきドキュメント一覧

| ファイル名 | 内容 | 優先度 |
|---|---|---|
| `ClipViewer_統合要件定義書.md` | 機能要件・キーバインド表・ini仕様・全機能リスト（v0.6.0完全版） | ★★★ 必読 |
| `ClipViewer_実装仕様書.md` | クラス設計・主要メソッド・XAML構造・実装詳細（v0.6.0完全版） | ★★★ 必読 |
| `ClipViewer向けモアレ軽減フィルタ_機能仕様書_ドラフト.md` | 次期実装予定のモアレフィルタ仕様（未実装） | ★★ 実装時に必読 |
| `ClipViewer向けモアレ軽減フィルタ：技術設計リサーチ_ja.md` | 同上の技術調査（WPFシェーダー限界・2段パイプライン設計） | ★★ 実装時に必読 |
| `ClipViewer プロジェクトロードマップ v0.1.md` | 草案（現状と大きく乖離、参考程度） | ★ 読まなくてよい |

---

## 1. バージョン変更履歴

### v0.1（草案フェーズ）
- Phase1〜4の初期計画を策定
- 実際の実装とは大きく乖離しているため参考価値は低い

### v0.5.x（基礎機能整備フェーズ）
各バージョンの詳細な機能リストは`ClipViewer_統合要件定義書.md` の「機能一覧」参照。  
以下は特記すべき経緯のみ。

- **基本ビューア**: `.clip` / `.psd` / `.jpg` / `.jpeg` / `.png` / `.webp` / `.gif` 対応
- **GIF再生機能**: F5/F6/F7/F8 キー割り当て、ループ/自動送りモード
- **情報パネル**: `InfoDisplayMode` enum（Off / Basic / Detailed）3状態
- **回転・反転**: `ImageTransformContainer` に `ScaleTransform + RotateTransform` を重ねた実装
- **状態保存**: ウィンドウ位置・表示モード・見開き状態等を ini に保存して再起動時に復元
- **ファイル操作**: ごみ箱削除（`Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile`）、ファイルコピー保存、名前を付けて保存

### v0.5.4
- 終了時の保存ステータスに「見開き/単ページ切替状態」を追加（`LastMode`）
- 操作通知の表示時間: 成功時 0.5秒 → **1.0秒**（短すぎるとの使用感フィードバック）、エラー時 3.0秒

### v0.5.5
- 詳細は `ClipViewer_統合要件定義書.md` v0.5.5 差分を参照

### v0.6.0（2026/07/03）
- **AVIF静止画対応** — 拡張子 `.avif` を3箇所に追加するのみで対応（WIC の透過的デコードを利用）
- **SaveDirectory デフォルト変更** — 旧個人環境パスから変更（v0.8.0 で「ピクチャ\ClipViewer」に中立化）
- **複数キー割り当て** — `Key` → `Key[]`、ini でカンマ区切り記述（`Left,A` 等）
- **マウスバインド新設** — `[MouseBindings]` セクション追加、XButton1/XButton2/Middle に操作割り当て

### v0.7.0（2026/07/05）
- **モアレ軽減フィルタ（F49）** — Lanczos-3 CPUダウンスケール（Stage1）。`ImageFilters.cs` 新設、`[Filters]` iniセクション新設。仕様書の Phase1+2 に相当
- **シャープ化フィルタ（F50）** — 閾値付きアンシャープマスク（Stage2）
- **キー再配置** — F9=モアレフィルタトグル / F11=シャープ化トグル（新設）、OpenIniFile: F9→F2、ToggleWindowMode: F11→Enter(ini表記は`Return`)。F10/F12 はフィルタ種切り替え等の将来用に予約
- **フィルタトグル状態の永続化** — F9/F11 の ON/OFF は `[Filters]` の Enabled 値として終了時に保存
- **フィルタデフォルト値のチューニング** — ユーザー試用の結果、Strength=40 / SharpenAmount=100 / SharpenThreshold=20 をデフォルトに採用
- **情報パネルの折り返し対応** — 長いファイル名が画面右で見切れる問題を修正。情報パネル・操作通知の全TextBlockを `TextWrapping="Wrap"` に変更（パネル幅=画面右20%内で折り返し）
- **起動遅延対策（2026/07/07）** — 「起動がやたら遅くなる」問題の調査で、多重インスタンス（ダブルクリック連打）＋起動時の先読みフルデコードがディスク/CPUを食い合う雪だるまを確認。対策: ①起動診断ログ新設（`%TEMP%\ClipViewer\startup.log`、他インスタンス数と各起動フェーズの所要時間を記録）②フィルタ全OFF時の起動時再デコード排除（署名を固定値化）③先読みを Window_Loaded 後に開始。シングルインスタンス化は検討中（未実装）
- **PageUp/PageDown の入れ替え（2026/07/07）** — PageSkipForward=PageDown（ini表記 `Next`）、PageSkipBack=PageUp に変更（使用感フィードバック）

### v0.8.0（2026/07/09）
- **シークバー（F51）** — 画面下端48pxホバーでフェード表示。オレンジ系（#FF9500）、バー8px・輪郭1px・中央80%幅・円サム14px。右綴じは右起点で読了分を塗る。ドラッグ中はページ番号のみ更新、離した時にジャンプ。仕様は統合要件定義書 §20
- **アーカイブ閲覧位置の自動復元（F52）** — `ArchiveHistory.cs` 新設。`ClipViewer_history.txt`（exe横）にアーカイブ終了/切替時の表示位置を記録し、再度開いたとき前回位置から再開+1.5秒通知。登録件数はデフォルト30・ini `ArchiveHistoryCount` で変更可。ON/OFF は ini `ArchiveHistoryEnabled` または `ToggleArchiveHistory` キー（デフォルト None=未割り当て）。仕様は統合要件定義書 §21
- 起動遅延問題は v0.7.0 末の対策（先読み遅延・フィルタOFF時再デコード排除）以降、再発報告なし
- **検討中（未実装）**: タスクトレイ常駐+グローバルホットキー（Shift+Ctrl+任意キー）でのメニューポップ。RegisterHotKey P/Invoke + NotifyIcon で実現可能と回答済み。メニュー内容の仕様待ち
- **GitHubで公開開始（2026/07/11）** — https://github.com/miyopeee/ClipViewer （MIT、公開用スナップショットは `cowork\ClipViewer-github\`）

### v0.8.5（2026/07/14-17）
- **BF03: 長パスでのクラッシュ修正（2026/07/17）** — フルパス260文字超のファイルがあるフォルダを開くと NaturalSort 比較器内の `Path.GetDirectoryName` が `PathTooLongException` → Sortごとプロセス中止していた。比較器を純粋文字列操作化+App.configで長パスサポート有効化（地雷リスト参照）。**配布物に ClipViewer.exe.config が追加された**
- **loop名アニメのループ固定（F53）** — ファイル名に`loop`を含むアニメは再生モード無視でループ固定。再生中のページ送りはループ末尾まで保留し切れ目で遷移（再押下で即時、一時停止中は即時、戻し/ジャンプは即時）。`_gifForceLoop`/`_gifAdvancePending` フラグで実装、設定の変更・保存はしない
- **デフォルト再生モードを AutoAdvance に変更** — loop名固定との組み合わせで「連番は流れ、ループ物は滞留」が既定動作に。既存iniのLastGifPlayModeはそのまま尊重される
- **検証ノウハウ**: UI自動操作は WScript.Shell SendKeys ではなく **PostMessage(WM_KEYDOWN) をウィンドウハンドル宛てに直接送る**こと（SendKeysはフォアグラウンド奪取に失敗すると他アプリへキーが漏れる）。ウィンドウ単体キャプチャは PrintWindow(flags=2)。検証スクリプト例はAIセッションのメモリ参照
- **フィルタトグルの応答改善** — 旧実装はF9/F11押下時にUIスレッドで現在画像を同期再デコードしており、大きい画像で「押しても無反応→フリーズ後に反映」だった。①通知は即時表示し、画像は背景で新パラメータ版を生成後に差し替え（`ApplyFilterToggle`）②適用対象外（縮小表示でない）画像では通知に「この画像は適用外」を明示 ③先読みタスクがトグル後に旧設定の画像をキャッシュへ書き戻す競合をフィルタ署名チェックで遮断（`PrefetchOne` に署名引数追加）
- **トグル状態の即時保存（全モード系に拡大）** — 従来の終了時のみ保存だと、**多重起動の「最後に閉じたインスタンス勝ち」で他インスタンスのトグルが巻き戻る**・強制終了で消える。`PersistStateNow()`（現在状態を[State]へ同期→ini保存）を新設し、**全トグル操作**（Space見開き/F3綴じ/F1・Tab情報/F4表紙/F5再生モード/Enter画面モード/F9・F11フィルタ/履歴トグル）の直後に呼ぶ。F9・Space それぞれ「トグル→強制kill→ini反映済み」を実測確認。ウィンドウ位置・サイズのみ移動毎には保存しない（トグル時・終了時に同乗）
- **情報パネル拡張** — アニメ再生中は「フレーム: 再生中番号/総数」を表示（毎フレーム追従、コマ送り時も更新）。回転・反転中は「回転 90° / 反転 左右」等のステータスを表示（無変換時は非表示）。いずれも Basic 表示以上（`InfoAnimFrame`/`InfoTransform`、`UpdateAnimFrameInfo`/`UpdateTransformInfo`）

### v0.8.4（2026/07/13）
- **アーカイブの遅延展開（zip/cbz）** — 従来は全展開完了まで表示がブロックされていた（無圧縮の大型アーカイブで顕著）。エントリ列挙のみで即表示を開始し、`EnsureExtracted`（読み取り直前のオンデマンド実体化）+ 背景スイープに変更。206MB無圧縮zipで表示開始0.9秒（サイズ非依存）を実測。ZipArchive はスレッド非安全のため `_zipLock` で直列化、「.part→リネーム」で書きかけ読みを防止。rar/lzh/7z は従来どおり一括展開
- **WebPアニメ判定のZIP直読み** — `ReadFileHead` がアーカイブから直接ヘッダ24バイトを読む（判定のために実体化しない）
- **起動時の一時フォルダ掃除** — 強制終了で残った %TEMP%\ClipViewer 配下の残骸を起動時に背景削除（24時間より古いもののみ＝多重起動保護）
- 設計原則§6.5の適用例: 「展開」という重い処理を表示経路から排除し、必要になった瞬間に個別実行する

### v0.8.3（2026/07/12）
- **回転・反転のページ跨ぎ保持** — ページ遷移時のリセットを廃止（横倒し画像の連続閲覧用。アニメ・静止画共通、表示コンテナごと変換するため自動的に同一挙動）。回転を戻すのは手動（Alt+矢印）
- **回転時FIT補正** — 90/270°回転時は縦横入替の見た目サイズでビューポートにフィット（`RotateFitScale` を `ImageTransformContainer` に追加、`SingleImage.SizeChanged` でページ遷移後も追従）。単ページ表示のみ対象、見開きの全体回転は従来どおり
- **ズーム倍率保持オプション** — ini `[Settings] KeepZoomOnNavigate`（既定 False=従来どおりリセット）。True でページ移動後も倍率維持、パン位置は常に中央リセット
- 留意: 回転表示中のズーム%表示（原寸基準）は回転FIT補正を含まない近似値

### v0.8.2（2026/07/11-12）
- **アニメWebP/GIFのプログレッシブ再生** — 全フレームのUIスレッド同期デコード（大きいファイルで数秒フリーズ）を、背景デコード+先頭フレーム確定時点で即再生開始のストリーミング式に変更。再生がデコードを追い越したら現フレームで待機し、追い付き後は目標時刻をリベース。コマ送り/一時停止/AutoAdvance/クリップボード/evict との整合維持。`_gifAvailCache`（再生可能枚数）と `_animDecoding`（重複起動ガード）を新設
- **アーカイブ+見開きモードでのアニメ開始遅延を改善** — ①WebPのアニメ有無をヘッダ21バイト（VP8Xフラグ）で即時判定する `EnsureWebPAnimKnown` を新設。見開きモードの「一旦見開き表示→背景判定→単ページ再表示」の遠回りを排除し、初回表示から単ページ+デコード直行に ②アニメWebPの静止フレームへの無駄なモアレフィルタ適用を省略 ③現在ファイルのアニメ初回フレーム確定まで先読みを保留（`StartPrefetchSmart`/`ResumeDeferredPrefetch`）し、コールドキャッシュ時のCPU/ディスク競合を回避

### v0.8.1（2026/07/11）
- **BF02: 多階層アーカイブの表示順修正** — `NaturalSort.Comparer` がファイル名のみで比較していたため、複数フォルダ構成のアーカイブ（A/01.jpg, B/01.jpg）で A/01→B/01 と交錯していた。「フォルダ階層→ファイル名」の2段自然順比較に修正し、フォルダAを読み終えてからBに進む読書順にした。通常のフォルダ閲覧・兄弟フォルダ移動の挙動は不変
- ※ 仕様書ドラフト（モアレフィルタ機能仕様書）の「F2でフィルタトグル」案はこのキー配置で上書きされた

---

## 2. 積み残し・保留機能バックログ

### 2-1. モアレ軽減フィルタ【Phase1+2 実装済み（v0.7.0）】
- **状態**: Phase1（Lanczos-3 ダウンスケール）+ Phase2（閾値付きUSM）を v0.7.0 で実装済み
- **キー割り当て**: F9（Stage1）/ F11（Stage2）— 仕様書ドラフトのF2案から変更
- **残タスク（Phase3）**: Stage1 のフィルタ種別追加（Area平均・Gaussian強デスクリーン）。
  `MoireFilterMode=Area/Gaussian` は ini 上受理されるが現状 Lanczos で代替動作。
  `ImageFilters.ResampleAxis()` はカーネル差し替えで拡張できる構造になっている
- **残タスク（Phase4・任意）**: GPU仕上げ（ShaderEffect）、GUIプリセット、ライブプレビュー
- **仕様書**: `ClipViewer向けモアレ軽減フィルタ_機能仕様書_ドラフト.md`（キーバインド案のみ実装と相違）

### 2-2. アニメーションAVIF対応
- **状態**: 保留（「必要が出てきたら検討」）
- **備考**: WIC の AVIF デコーダはアニメーション AVIF に対応しているが、フレーム列挙のコストが高い。静止 AVIF は v0.6.0 で対応済み。

### 2-3. 旧ロードマップ（v0.1）の消化
- **状態**: 現実装と大きく乖離、参照価値低
- **備考**: Phase1〜4 の計画はほぼ別の形で実装済み。ロードマップの更新は未実施。

---

## 3. 実装上のハマりポイント集（地雷リスト）

### 🚨 Alt+Key が取得できない問題
**症状**: Alt+Right 等の Alt 修飾キーを押すと `e.Key == Key.System` になり、実キーが取れない。  
**原因**: WPF の仕様。Alt キー押下時は `e.Key` が `Key.System` に上書きされる。  
**解決策**: 以下のように `e.SystemKey` で実キーを取得する。

```csharp
Key k = (e.Key == Key.System) ? e.SystemKey : e.Key;
```

---

### 🚨 `SearchOption` 名前空間競合
**症状**: `using Microsoft.VisualBasic.FileIO;` を追加すると `SearchOption` が `System.IO.SearchOption` と `Microsoft.VisualBasic.FileIO.SearchOption` で ambiguous エラー (CS0104) になる。  
**解決策**: `using` を追加せず、`DeleteFile` を完全修飾名で呼ぶ。

```csharp
// NG: using Microsoft.VisualBasic.FileIO; を追加する
// OK: 完全修飾名で呼ぶ
Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
    path,
    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin
);
```

---

### 🚨 `ParseDisplayMode` バグ（v0.5.x 当時）
**症状**: ini から DisplayMode を読み込む際、"Spread" しかマッチしなかった。  
**原因**: `Enum.TryParse` を使わず独自比較していた。  
**解決策**: `Enum.TryParse<DisplayMode>(val, ignoreCase: true, out var mode)` を使用。現在は修正済み。

---

### 🚨 `BuildActionMap` の重複キー
**症状**: v0.6.0 実装時、`"DeleteFile"` のエントリが `_actionMap` に2回定義されビルドエラー。  
**原因**: `BuildActionMap()` 追加時のコピペミス。  
**注意**: `_actionMap` の文字列キーはケースインセンシティブ（`StringComparer.OrdinalIgnoreCase`）だが、重複は実行時例外になる。

---

### 🚨 `IniFileManager.BuildLines()` の可変長問題
**症状**: v0.5.x 時代は `new[] {}` でライン配列を生成していたため、`[MouseBindings]` セクションの動的な行数に対応できなかった。  
**解決策**: v0.6.0 で `List<string>` + `AddRange` 方式に変更。`[MouseBindings]` エントリは foreach でループ追加。

---

### 🚨 AI生成画像の巨大メタデータで UI が数十秒フリーズする問題【2026/07/10 根治】
**症状**: 情報パネルが Detailed モード（EXIF表示）のとき、AI生成画像（Stable Diffusion / ComfyUI 等の出力）を含むフォルダで起動やページ送りが30〜40秒フリーズする。
**原因**: AI生成画像はメタデータ（Comment等）に数百KB級のプロンプト/ワークフローJSONを埋め込む。それをそのまま `TextBlock`（`TextWrapping=Wrap`）に流すと、WPFのテキストレイアウトがUIスレッドを数十秒占有する。
**解決策**: `ReadExifSummary()` で各メタデータ項目を切り詰めてから表示する。**メタデータ由来の文字列を無制限に TextBlock へ渡さないこと。**
（上限は使用感フィードバックで調整済み: 撮影日40/機種60/タイトル800/コメント800文字、全体1000文字。1000字程度ならレイアウトは1秒未満で問題ない）
**補足**: AI生成画像のプロンプト/ワークフローJSONは **Title（EXIF ImageDescription、タグ270）に入っていることが多い**（ComfyUI 等、実測5万文字）。Comment は空のことが多いので、切り詰め調整時は Title 側を忘れないこと。
**教訓**: この問題は長らく「exeをコピーし直すと直る謎の遅延」として誤認されていた。実際は復旧儀式の ini 削除で `LastInfoMode` が Basic にリセットされていたのが「治った」理由。症状と対処の因果は startup.log（起動診断ログ）で確定した。

---

### 🚨 MAX_PATH超のパスで Path API がクラッシュ（v0.8.5で修正）
**症状**: 特定フォルダを開くとアプリが即クラッシュ（ウィンドウすら出ない）。
**原因**: AI生成画像の長大ファイル名でフルパスが260文字を超えると、`Path.GetDirectoryName` 等が `PathTooLongException` を投げる。NaturalSort の比較器内で発生したため `List.Sort` ごと未処理例外でプロセス中止していた。
**解決策**: ①比較器はパスAPIを使わず `LastIndexOf('\\')` + `Substring` の純粋文字列操作に変更 ②App.config の `AppContextSwitchOverrides`（UseLegacyPathHandling=false; BlockLongPaths=false）で長パス対応を有効化（.NET 4.6.2以降ランタイムで有効。**ClipViewer.exe.config を必ず exe と一緒に配布すること**）。
**教訓**: ホットパス（比較器・イベントハンドラ）では例外を投げ得る Path API を避ける。ファイル名処理は文字列操作で。

---

### 🚨 Claude側制約: Write前にRead必須
**症状**: `Write` ツールで既存ファイルを上書きしようとすると `File has not been read yet. Read it first` エラーになる。  
**回避策**: 既存ファイルの書き直し前に必ず `Read` を1回実行してからWrite。新規ファイルなら不要。  
※ これは Claude/Cowork の制約であり、コード側の問題ではない。

---

## 4. ini 移行メモ

### v0.5.x → v0.6.0 における手動更新が必要な項目

#### SaveDirectory のデフォルト変更

v0.6.0 から **デフォルト値が変更**された。既存 ini を引き継ぐ場合、手動で以下を書き換えること。

```ini
; 旧値 (v0.5.x)
SaveDirectory=C:\# mega\temp\0000

; 新値 (v0.6.0〜)
SaveDirectory=C:\Users\(ユーザー名)\Pictures\ClipViewer
```

> **注意**: アプリが ini の旧値をそのまま読み込むため、自動更新はされない。  
> ini を削除して再生成するか、テキストエディタで直接編集すること。

#### [MouseBindings] セクションが自動追加される

v0.6.0 以降、ini 書き込み時に `[MouseBindings]` セクションが自動付与される。  
既存 ini への初回書き込み時（設定変更・正常終了時）に追記される。

---

## 5. 空きキー一覧（次期機能割り当て用）

現時点で **未使用の Fキー**（v0.7.0 で F2 は OpenIniFile、F9/F11 はフィルタトグルに使用済み）:

| キー | 用途案 |
|---|---|
| **F10** | フィルタ種切り替え等の割り当てを想定（予約） |
| **F12** | フィルタ種切り替え等の割り当てを想定（予約） |

※ F10 は Windows のシステムキー（メニュー起動）だが、`Window_KeyDown` の `SystemKey` 変換で取得可能。
　割り当て時は必要に応じて `e.Handled = true` を設定してフォーカス移動を抑止すること。

---

## 6. プロジェクト構成（ファイル一覧）

```
ClipViewer/
├── ClipViewer.sln
├── ClipViewer.csproj           (.NET Framework 4.6, x64)
├── App.xaml / App.xaml.cs
├── MainWindow.xaml             (レイアウト定義, Title: "ClipViewer v0.7.0")
├── MainWindow.xaml.cs          (メインロジック, ~2500行超)
├── AppSettings.cs              (設定クラス / Key[] / MouseBindings / Filters / enum定義)
├── IniFileManager.cs           (ini 読み書き / ParseKeys / セクション追跡)
├── ImageFilters.cs             (モアレ軽減/シャープ化パイプライン, v0.7.0新設)
│
├── ClipViewer_統合要件定義書.md   (v0.6.0 機能仕様)
├── ClipViewer_実装仕様書.md       (v0.6.0 実装詳細)
├── ClipViewer_開発引き継ぎノート.md  ← このファイル
├── ClipViewer プロジェクトロードマップ v0.1.md  (旧草案, 参考価値低)
├── ClipViewer向けモアレ軽減フィルタ_機能仕様書_ドラフト.md
└── ClipViewer向けモアレ軽減フィルタ：技術設計リサーチ_ja.md
```

**ビルド環境**:
- Visual Studio 2022 Community
- .NET Framework 4.6
- プラットフォーム: x64
- BepInEx: 不使用（スタンドアロン WPF アプリ）

---

## 6.5 設計原則（ユーザー承認済み・2026/07/12）

**「重い処理が必要になるかの判定を最優先で済ませ、その後に他の処理を行う」**

表示経路を分岐させる判定（例: アニメか静止画か）は、可能な限り安く・早く確定させる。
- 判定が安価にできる場合（WebP=ヘッダ21バイトのVP8Xフラグ）→ **同期で即判定**し、初回表示から正しい経路に直行する
- 判定自体が重い場合（GIF=ファイル構造の走査が必要）→ **暫定表示+背景判定→確定後に再表示**へ逃がす
- 重い処理（アニメの初回フレームデコード等）が始まったら、競合する背景処理（先読み等）は**確定まで保留**して最優先リソースを確保する

v0.8.2 のアーカイブ+見開きモードでのアニメ開始遅延改善は、この原則の適用例。新フォーマット対応や新機能でも同じ基準で設計すること。

---

## 7. 次期セッションへの申し送り

1. **モアレ軽減フィルタは v0.7.0 で Phase1+2 実装済み**（F9/F11トグル、[Filters]セクション）。
   次の候補は Phase3（Area平均・Gaussian のフィルタ種追加、F10/F12 への種切り替え割り当て）。
   詳細仕様は `ClipViewer_統合要件定義書.md` §19 を参照。

2. **AVIF アニメーション対応は保留**。ユーザーが「必要が出てきたら検討」と明示している。

3. **ユーザーはコーディング経験が少ない**。実装はAIが担当し、細かいシンタックスエラー等は GitHub Copilot 等他ツールと分担する形で効率化している。

4. **iniは `ClipViewer.ini`**（アプリと同一ディレクトリ）。F9キーで直接エディタ起動可能。

5. **ユーザー環境**: Windows 11 Pro / .NET Framework 4.6 / Visual Studio 2022 Community / GTX 1080 (CUDA対応済み環境)

---

*最終更新: 2026/07/05（v0.7.0 フィルタ実装を反映）*
