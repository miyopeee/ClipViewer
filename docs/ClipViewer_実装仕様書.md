# ClipViewer 仕様書

**バージョン:** 0.8.2
**対象ファイル:** `bin\Release\ClipViewer.exe`
**作成日:** 2026-03-01
**最終更新:** 2026-07-11

---

## 目次

1. [概要](#概要)
2. [動作環境](#動作環境)
3. [起動方法](#起動方法)
4. [対応フォーマット](#対応フォーマット)
5. [表示モード](#表示モード)
6. [キーバインド（デフォルト）](#キーバインドデフォルト)
7. [機能詳細](#機能詳細)
8. [設定ファイル（ClipViewer.ini）](#設定ファイルclipviewerini)
9. [内部アーキテクチャ](#内部アーキテクチャ)
10. [ファイル構成](#ファイル構成)
11. [ビルド方法](#ビルド方法)
12. [既知の制限事項](#既知の制限事項)

---

## 概要

ClipViewer は、Clip Studio Paint の `.clip` ファイルおよび一般的な画像ファイルを、全画面・キーボード操作で閲覧するための WPF ビューワーアプリケーション。マンガ・イラストの確認を主目的とし、見開き表示、アニメーション再生、アーカイブ内画像閲覧、外部エディタ連携などに対応する。

---

## 動作環境

| 項目 | 要件 |
|------|------|
| OS | Windows 10 / 11 |
| ランタイム | .NET Framework 4.6 |
| 依存 DLL | `System.Data.SQLite.dll`、`x64\SQLite.Interop.dll`、`x86\SQLite.Interop.dll`、`Microsoft.VisualBasic`（.NET標準、ごみ箱削除用） |
| 外部 DLL | `libwebp.dll`、`libwebpdemux.dll`、`libsharpyuv.dll`（WebP アニメ再生用） |
| 外部ツール | 7-Zip（`7z.exe`）—RAR/LZH/7z アーカイブ展開に使用（オプション） |
| ビルドツール | Visual Studio 2019 以降の MSBuild（C# 6.0 以上対応） |

---

## 起動方法

```
ClipViewer.exe <ファイルパス>
```

- 指定ファイルが属するディレクトリ内の対応ファイルを自動列挙する
- アーカイブファイル（`.zip` / `.cbz` / `.rar` / `.lzh` / `.7z`）を指定した場合は一時展開して内部画像を列挙する
- ファイル関連付けまたはドラッグ＆ドロップによる起動に対応
- 引数なし起動時はエラーメッセージを表示して待機（Esc で終了）

---

## 対応フォーマット

### 画像フォーマット

| 拡張子 | 読み込み方式 |
|--------|-------------|
| `.clip` | 内部 SQLite DB の `CanvasPreview` テーブルから PNG/JPEG を抽出 |
| `.psd` | `PsdFileReader` で Image Data セクションからフラット画像を抽出（RGB/8bit/Raw・PackBits RLE 対応） |
| `.jpg` / `.jpeg` | WPF/WIC ネイティブデコード |
| `.png` | WPF/WIC ネイティブデコード |
| `.gif`（静止） | WPF/WIC ネイティブデコード |
| `.gif`（アニメ） | `GifBitmapDecoder` フレーム合成 + `CompositionTarget.Rendering` 駆動再生 |
| `.webp`（静止） | WPF/WIC ネイティブデコード（Windows Imaging Component） |
| `.webp`（アニメ） | `libwebpdemux.dll` P/Invoke（`WebPAnimDecoderNewInternal`）による再生 |
| `.avif`（静止） | WPF/WIC ネイティブデコード（要 HEIF Image Extensions + AV1 Video Extension） |

同一ディレクトリ内の全対応ファイルをまとめて管理し、自然順ソートで並べる。

### アーカイブフォーマット

| 拡張子 | 展開方式 |
|--------|---------|
| `.zip` / `.cbz` | `System.IO.Compression.ZipFile`（追加依存なし） |
| `.rar` / `.lzh` / `.7z` | `7z.exe` 外部プロセス（7-Zip インストール必須） |

アーカイブ内の画像ファイルは `%TEMP%\ClipViewer\{名前}_{GUID8}\` に展開後、通常の画像ファイルとして処理する。

---

## 表示モード

### 単ページモード（Single）

1 ファイルを全画面に Uniform スケールで表示する。

### 見開きモード（Spread）

2 ファイルを左右に並べて表示する。綴じ方向（右綴じ / 左綴じ）を切替可能。`_currentIndex` と `_currentIndex+1` を左右に配置。

### 先頭単独表示（FirstSingle）

見開きモード時にインデックス 0（表紙）を単独表示し、以降のペアリングを 1 ずらす。

### 横長自動単ページ（F23）

見開きモード中、右側に配置される画像の幅 > 高さ（横長）の場合、自動的に単ページ表示に切り替える。

### アニメ自動単ページ（F37）

見開きモード中、現在ページまたは次ページがアニメ GIF / アニメ WebP と判定された場合、自動的に単ページ表示に切り替える。判定は非同期で行い、判定完了後に表示を再描画する（初回は一時的に見開きになる場合がある）。

### ウィンドウモード（F35）

F11 でフルスクリーン（WindowStyle=None, Cursor=None）とウィンドウモード（WindowStyle=SingleBorderWindow, Cursor=Arrow）をトグル切替する。ウィンドウ位置・サイズは終了時に保存され、次回起動時に復元する。

---

## キーバインド（デフォルト）

| キー | 機能 |
|------|------|
| `←` | 次ページへ（右綴じ基準） |
| `→` | 前ページへ |
| `Space` | 単ページ / 見開き切替 |
| `NumPad0` | 見開き時の単ページ送り（スライディングウィンドウ） |
| `F1` | 情報パネル Basic↔Off トグル |
| `F3` | 右綴じ / 左綴じ切替 |
| `F4` | 先頭単独表示トグル |
| `F5` | アニメ再生モード切替（Loop ↔ AutoAdvance） |
| `F6` | アニメ一時停止 / 再生 |
| `F7` | アニメコマ送り（前進） |
| `F8` | アニメコマ戻し（後退） |
| `F2` | ini ファイルをエディタで開く（v0.7.0 で F9 から移動） |
| `F9` | モアレ軽減フィルタ ON/OFF（v0.7.0 新設） |
| `F10` | （空き。フィルタ種切り替え等の将来用に予約） |
| `F11` | シャープ化フィルタ ON/OFF（v0.7.0 新設） |
| `F12` | （空き。フィルタ種切り替え等の将来用に予約） |
| `Enter` | フルスクリーン / ウィンドウモード切替（v0.7.0 で F11 から移動。ini 表記は `Return`） |
| `Tab` | 情報パネル Off→Basic→Detailed サイクル |
| `Home` | 先頭ファイルへジャンプ |
| `End` | 末尾ファイルへジャンプ |
| `PageDown` | 複数ページ送り（PageSkipCount 枚。ini 表記は `Next`） |
| `PageUp` | 複数ページ戻り（PageSkipCount 枚） |
| `Insert` | 現在ファイルを SaveDirectory へコピー（成功時1秒通知） |
| `Delete` | 現在ファイルをごみ箱に送る（成功時1秒通知） |
| `↑` | 前の兄弟ディレクトリ / アーカイブへ移動 |
| `↓` | 次の兄弟ディレクトリ / アーカイブへ移動 |
| `Alt+→` | 表示画像を右90°回転 |
| `Alt+←` | 表示画像を左90°回転 |
| `Alt+↑` | 表示画像を左右反転トグル |
| `Alt+↓` | 表示画像を上下反転トグル |
| `Ctrl+E` | 外部エディタで現在ファイルを開く |
| `Ctrl+C` | 現在画像をクリップボードにコピー（アニメ時は現フレーム、成功時1秒通知） |
| `Ctrl+S` | 保存先ダイアログを経由してファイルをコピー |
| `Esc` | アプリ終了 |
| マウスホイール | ズームイン / アウト |
| 左ドラッグ | ズーム中の画像パン |

すべてのキーは `ClipViewer.ini` で変更可能（Ctrl / Alt 修飾は固定、主キー部のみ変更可能）。**カンマ区切りで複数キーを割り当て可能**（例: `NextPage=Left,A`）。

> **実装メモ（Alt+Key）:** WPF では Alt 押下中に `e.Key` が `Key.System` を返すため、実際のキーは `e.SystemKey` から取得する必要がある。`Window_KeyDown` 内で `Key k = (e.Key == Key.System) ? e.SystemKey : e.Key;` として統一処理する。

### マウスボタン割り当て

`[MouseBindings]` セクションでマウスボタンにアクションを割り当て可能。

| ボタン | 説明 | デフォルトアクション |
|--------|------|-------------------|
| `XButton1` | マウス戻るボタン | `PrevPage` |
| `XButton2` | マウス進むボタン | `NextPage` |
| `Middle` | ホイールクリック | `Exit` |
| `Right` | 右クリック | （未設定） |

アクション名は `[KeyBindings]` のキー名と同一。`Window_MouseDown` でボタンを判定し、`_actionMap`（`Dictionary<string, Action>`）でメソッドを実行する。Left ボタンはドラッグ操作に使用するため割り当て不可。

---

## 機能詳細

### ナビゲーション

#### 通常ページ送り（NextPage / PrevPage）

- **単ページモード：** `FindNextValid` で `_brokenFiles` をスキップして移動
- **見開きモード（`_firstSingle` OFF）：** 2 インデックス単位移動、±2 のモジュロループ
- **見開きモード（`_firstSingle` ON）：** 以下の特殊ルールでループ

```
NavigateNext (_firstSingle ON):
  _currentIndex == 0（表紙）       → 1（最初の見開きアンカー）
  _currentIndex + 2 >= count       → 0（末尾到達→表紙へループバック）
  それ以外                          → _currentIndex + 2

NavigatePrev (_firstSingle ON):
  _currentIndex == 1（最初の見開き）→ 0（表紙へ）
  _currentIndex == 0（表紙）        → LastSpreadAnchor(count)（末尾アンカーへ）
  それ以外                           → _currentIndex - 2

LastSpreadAnchor(count):
  (count-1) が奇数 → count-1
  (count-1) が偶数 → count-2
  例: count=8→7、count=9→7、count=7→5、count=6→5

ナビゲーション順序例（8ファイル）:
  Next: 0→1→3→5→7→0→1→...
  Prev: 0→7→5→3→1→0→7→...
```

#### 単ページ送り（SingleStep）—見開きモード専用

スライディングウィンドウ方式：`_currentIndex` を +1 し、`(_currentIndex, _currentIndex+1)` を直接表示。

```
例: 1-2 表示中 → SingleStep → 2-3 表示
```

#### 先頭 / 末尾ジャンプ（JumpFirst / JumpLast）

有効ファイル確認なしで `_currentIndex` を 0 または末尾に設定。

#### 複数ページスキップ（PageSkipForward / PageSkipBack）

`PageSkipCount`（デフォルト 10）枚分移動。端点は非ループ（先頭・末尾で停止）。

#### 兄弟ディレクトリ / アーカイブ移動（NavigateDirUp / NavigateDirDown）

通常モード（ディレクトリ）：
- 現在ディレクトリの親ディレクトリ内の兄弟を自然順ソートで列挙
- 対応ファイルが存在しないディレクトリはスキップ
- 端点では通知「最上位/最下位ディレクトリです」

アーカイブモード（`_currentArchivePath != null`）：
- 同フォルダ内のアーカイブファイル（`_archiveExts`）を列挙
- 端点では通知「最初のアーカイブです / 最後のアーカイブです」

共通：移動成功時は `_clipFiles`・キャッシュ・アニメ判定キャッシュ（`_knownAnimated` / `_knownStatic`）をクリアし `SeekFirstValid` を実行。

### ズーム / パン（F19 / F20）

- マウスホイールで ±0.20 刻みズーム（範囲：0.10〜5.00）
- ズーム中心は画面中央（スクロール位置ではなく画面座標基準）
- 左ボタンドラッグでパン。TranslateTransform により移動量を加算
- ページ移動時に `ResetZoom()` でスケール・オフセットを初期化

### 情報パネル（F13 / F14 / F42 / F43 / F44）

右上（画面幅の右20%エリア）に半透明オーバーレイで表示。Off / Basic / Detailed の3モードを持つ。

**モード切替:**

| キー | 動作 |
|------|------|
| F1 | Basic↔Off トグル（`ToggleInfoDisplay()`） |
| Tab | Off→Basic→Detailed サイクル（`CycleInfoDisplay()`） |

**表示内容:**

| TextBlock | モード | 内容 |
|-----------|--------|------|
| `InfoFileName` | Basic以上 | 表示中ファイル名（見開き時は対象ページ） |
| `InfoPageNumber` | Basic以上 | ページ番号（`N / 総数` または `N - M / 総数`） |
| `InfoZoom` | Basic以上 | ズーム率。%はオリジナル原寸=100%の実表示倍率で、Fit時は `Fit (63%)` 形式で併記 — `ApplyZoom()` が直接更新 |
| `InfoPixelSize` | Detailedのみ | 画像ピクセルサイズ（`BitmapSource.PixelWidth × PixelHeight`） |
| `InfoFullPath` | Detailedのみ | 表示中ファイルのフルパス |
| `InfoExif` | Detailedのみ | EXIFサマリー（`BitmapDecoder` → `BitmapMetadata`）|

**レイアウト:**
- `InfoStackPanel.MaxWidth = ActualWidth * 0.20`（初期化時・`Window_SizeChanged` 時に設定）
- 全TextBlock（Basic情報・Detailed情報・操作通知）は `TextWrapping="Wrap"` — パネル幅で折り返し、はみ出しは下方向へ（v0.7.0: 長いファイル名が画面右で見切れる問題を修正。NoWrap→Wrap）

### 操作通知パネル

エラー / 操作結果を右上（情報パネル直下）に表示後、自動消去。`DispatcherTimer` で制御。

`ShowNotification(string message, double seconds = 3.0)` — `seconds` パラメータで表示時間を指定。

| 区分 | 秒数 | 通知内容の例 |
|------|------|------------|
| 操作成功通知 | 1.0秒 | 「クリップボードにコピーしました」「〇〇.jpg を保存しました」「〇〇.jpg を削除しました」|
| エラー/ガード | 3.0秒 | 「アーカイブ内画像は削除できません」「保存先ディレクトリが存在しません」等 |

### 外部エディタ連携（F26）

- `Ctrl+E` で `ExternalEditor` に設定されたアプリを起動
- 見開き時は右ページ（`GetOperationTargetIndex`）のファイルを引数として渡す
- エディタ未設定 / 実行ファイル不在の場合は通知表示

### ファイル保存（F27）

- `Insert` で `SaveDirectory` へ現在ファイルを同名コピー（上書き）
- 保存先未設定 / ディレクトリ不在の場合は通知表示（3秒）
- 成功時：「〇〇.xxx を保存しました」を1秒間通知表示

### クリップボードコピー（F28）

- `Ctrl+C` で現在画像を WPF Clipboard に転送
- アニメ GIF / アニメ WebP 再生中は **コピー時点のフレームを静止画像として**コピー
- `.clip` / `.psd` ファイルは非対応（通知表示、3秒）
- 見開き時は右ページのみ対象
- 成功時：「クリップボードにコピーしました」を1秒間通知表示

### ごみ箱削除（F40）

- `Delete` キーで現在表示中のファイルをごみ箱に送る
- アーカイブモード時（`_currentArchivePath != null`）は「アーカイブ内画像は削除できません」を1秒間通知表示して中断
- 削除API: `Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(filePath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin)`
- 依存: `.csproj` に `<Reference Include="Microsoft.VisualBasic" />` が必要
- 削除後: `_clipFiles` からエントリ除去 → 全キャッシュクリア → `_currentIndex` 調整 → `DisplayCurrent()` 再呼び出し
- 成功時：「〇〇.xxx を削除しました」を1秒間通知表示

> **名前空間競合の注意:** `using Microsoft.VisualBasic.FileIO;` を追加すると `System.IO.SearchOption` と `Microsoft.VisualBasic.FileIO.SearchOption` が競合してCS0104エラーになる。`using` ディレクティブは追加せず、`Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(...)` のように完全修飾名で呼び出すこと。

### ファイルをダイアログ保存（F39）

- `Ctrl+S` で Windows 標準 `SaveFileDialog` を表示し、指定先へファイルをコピー
- 初期ファイル名は現在ファイル名を引き継ぐ（リネームも可能）
- 見開き時はファイル名が若い方（`_currentIndex`）を対象

### アニメーション再生（F33 / F34）— v0.8.2 でプログレッシブ化

**背景デコード+プログレッシブ再生（v0.8.2）**: 旧実装は全フレームをUIスレッドで同期デコードしており、大きなアニメWebP/GIFでは再生開始まで数秒フリーズしていた。現行実装:

- `EnsureAnimFrames()` が `Task.Run` で `DecodeGifFramesCore` / `DecodeWebPFramesCore` を起動（重複起動は `_animDecoding` でガード）
- フレーム0確定時点で `RegisterAnimArrays()` がキャッシュ登録+`_gifAvailCache[idx]=1` → Dispatcher 経由 `OnAnimFramesReady()` が**即再生開始**
- 以降のフレームは再生と並行して `CommitAnimFrame()` が配列を埋め、`_gifAvailCache` が「再生可能枚数」を更新
- `GifRendering_Tick` は次フレームが未デコードなら現フレームを維持して待機（ストール）。200ms超のストール後は目標時刻を現在基準にリベースし、高速コマ飛びを防ぐ
- コマ送り（F7/F8）はデコード済み範囲内でループ。AutoAdvance のページ遷移は全フレームデコード完了後のループ末尾でのみ発火
- evict/リスト変更時は `CommitAnimFrame` の配列参照チェックが不一致となりデコードが自然に中断される
- フレーム数1と判明したファイルは `_knownStatic` に登録して以後スキップ

#### GIF アニメーション

`GifBitmapDecoder` でフレームを事前デコードし `_gifFrameCache` に格納。disposal メソッド（None / DoNotDispose / RestoreBackground / RestorePrevious）に対応したフレーム合成を行う。

#### WebP アニメーション

`libwebpdemux.dll` の P/Invoke（`WebPAnimDecoderNewInternal` / `WebPAnimDecoderGetNext`）でフレームを事前デコード。BGRA → BitmapSource 変換後 `_gifFrameCache` に格納（GIF と同一キャッシュ・再生エンジンを共有）。

#### 再生エンジン

`CompositionTarget.Rendering` + `Stopwatch` による vsync 駆動型。DispatcherTimer より高精度で、17ms/frame の高速アニメーションを正確に再生できる。2ms 早め判定で vsync ズレを吸収し、目標時刻を絶対値で積算してドリフトを防ぐ。

#### 再生モード（F5 切替）

| モード | 動作 |
|--------|------|
| Loop | 無限ループ再生 |
| AutoAdvance | 1 ループ完了後、次のファイルへ自動移動 |

AutoAdvance モードにより連番アニメ WebP をシームレス連続再生できる。

#### アニメ自動検出（F37）

`SpreadStepSize()` 呼び出し時に `_knownAnimated` / `_knownStatic` キャッシュを参照。未判定ファイルについては `TriggerAnimationCheckForSpread()` が非同期でフレーム数を確認し、アニメと確定したら `Dispatcher.Invoke(DisplayCurrent)` で再描画する。

- GIF: `GifBitmapDecoder.Frames.Count` で判定（ピクセルデコードなし）
- WebP: `WebPAnimDecoderGetInfo` のみ使用（`GetNext` は呼ばない）

### アーカイブ対応（F38）

起動引数のファイルがアーカイブ拡張子（`.zip` / `.cbz` / `.rar` / `.lzh` / `.7z`）に該当する場合、`LoadArchiveAsync()` が非同期で処理を開始する。

```
展開先: %TEMP%\ClipViewer\{アーカイブ名}_{GUID8}\
ZIP/CBZ: System.IO.Compression.ZipFile.OpenRead()
RAR/LZH/7z: 7z.exe 外部プロセス
  - 7z.exe 検索先: C:\Program Files\7-Zip\、C:\Program Files (x86)\7-Zip\、PATH
  - 不在時は通知を表示してアーカイブ展開をスキップ
展開完了後: 画像ファイルのみ収集し _clipFiles を構築 → SeekFirstValid
```

パストラバーサル防止として `SanitizeArchivePath()` でエントリパスの `..` および無効文字を処理する。

一時ディレクトリはアーカイブ切替時および `Window_Closing` 時に `CleanupTempDir()` で削除する。

### ウィンドウモード切替（F35）

| 状態 | WindowStyle | WindowState | Cursor |
|------|-------------|-------------|--------|
| フルスクリーン | None | Maximized | None |
| ウィンドウ | SingleBorderWindow | Normal | Arrow |

ウィンドウモード時は `Window_LocationChanged` / `Window_SizeChanged` イベントで位置・サイズを随時保存する。

### スクリーン配置（F21）

`Window_Loaded` イベントで `TargetScreen` 番号のモニタに最大化表示。存在しない番号はプライマリにフォールバック。DPI スケーリングを考慮した座標変換を適用。

### 状態の永続化（F16 / F36）

アプリ終了時（`Window_Closing`）に以下の動作状態を `ClipViewer.ini` へ保存し、次回起動時に復元する。

| 保存項目 | ini キー | 型 |
|---------|---------|-----|
| 表示モード | `LastMode` | `DisplayMode` (Single/Spread) |
| 綴じ方向 | `LastBinding` | `BindingDirection` (Right/Left) |
| 情報パネルモード | `LastInfoMode` | `InfoDisplayMode` (Off/Basic/Detailed) |
| 先頭単独 | `LastFirstSingle` | `bool` |
| アニメ再生モード | `LastGifPlayMode` | `GifPlayMode` (Loop/AutoAdvance) |
| フルスクリーン状態 | `LastIsFullscreen` | `bool` |
| ウィンドウ左位置 | `LastWindowedLeft` | `double` |
| ウィンドウ上位置 | `LastWindowedTop` | `double` |
| ウィンドウ幅 | `LastWindowedWidth` | `double` |
| ウィンドウ高 | `LastWindowedHeight` | `double` |

> **後方互換性:** 旧キー `LastInfoDisplay`（bool）は `IniFileManager` でパース時に `True→Basic` / `False→Off` へ変換して読み込む。

### 画像キャッシュ

- `Dictionary<int, BitmapSource>` でインデックスをキーに最大 8 枚をメモリキャッシュ
- キャッシュが 8 件に達した場合、全クリアして再構築
- `.clip` / `.psd` の読み込み失敗インデックスは `HashSet<int> _brokenFiles` に記録し再試行をスキップ
- アニメフレームは `Dictionary<int, BitmapSource[]> _gifFrameCache` に格納（上限: `_imageCache` とは独立）

### シークバー（F51、v0.8.0）

画面下端48pxゾーンへのマウス移動で180msフェード表示されるページ位置バー。実装は `MainWindow.xaml` の `SeekBarPanel`（透明ヒットゾーン）+ `SeekTrack`（輪郭Rectangle・塗りRectangle・サムEllipse）と、コードビハインドの `SetSeekBarVisible` / `UpdateSeekBarVisual` / `SeekIndexFromX` / `SeekBar_Mouse*` 群。

- 右綴じ時は進行率を左右反転（`SeekFracToX`）し、塗りは `HorizontalAlignment.Right` で右起点
- ドラッグは `SeekBarPanel.CaptureMouse()` で追跡し、`MouseLeftButtonUp` で確定ジャンプ（`_currentIndex` 更新 → `NormalizeAnchor` → `ResetZoom` → `DisplayCurrent`）
- `e.Handled = true` により Window レベルのパンドラッグと干渉しない
- ページ変化時は `DisplayCurrent` 末尾で `UpdateSeekBarVisual` を呼び同期。トラック幅変化は `SeekTrack_SizeChanged` で追従
- フルスクリーン時のカーソル表示は `SetSeekBarVisible` が管理（表示中=Arrow、消滅時=フルスクリーンなら None）

### アーカイブ閲覧位置の自動復元（F52、v0.8.0）

`ArchiveHistory` 静的クラス（`ClipViewer_history.txt`、TAB区切り・LRU最大 `ArchiveHistoryCount` 件＝既定30）が記録を担当。

- **記録**: `SaveArchivePosition()` — `Window_Closing` と `LoadArchiveAsync` 冒頭（アーカイブ切替時）で呼ぶ。キーはアーカイブのフルパス、値は展開先一時ディレクトリからの相対パス（`GetTempRelativePath`）
- **復元**: `LoadArchiveAsync` の展開完了ハンドラで `ArchiveHistory.Lookup` → 相対パス一致検索 → ヒットかつ先頭以外なら `_currentIndex` を差し替え、表示後に「前回の位置から再開（Home で先頭へ）」を1.5秒通知
- **ON/OFF**: `[Settings] ArchiveHistoryEnabled`（既定 True）。OFF時は記録・復元ともスキップ。`ToggleArchiveHistory` キー（既定 `None`=未割り当て）で切替可能、状態は終了時に ini へ保存

### フィルタパイプライン（F49/F50、v0.7.0）

網点スクリーントーンのダウンスケール時モアレを軽減する CPU 2段パイプライン。`ImageFilters.cs` に実装。

**処理フロー（`DecodeAndFilter()`）:**

1. `BitmapDecoder`（`DelayCreation`）でヘッダのみ読み、ソース原寸を取得
2. ゲーティング判定: `ComputeFitScale() < MoireDownscaleThreshold` かつ `< 1.0` のダウンスケール時のみ適用
3. `DecodePixelWidth = 目標幅 × 2` でデコード（WIC の高品質縮小を粗縮小ステージとして利用）
4. Stage1: 分離可能 Lanczos-3 で表示サイズへリサイズ。`MoireFilterStrength`(0-100) をカーネル幅倍率 1.0〜2.0 にマッピング（間引き前ローパスがモアレ軽減の本体）
5. Stage2: ガウシアン + 閾値付きアンシャープマスク（`SharpenThreshold` 未満のコントラスト差は対象外＝リンギング防止）

**実装上のポイント:**

- 全処理は `Parallel.For` による行/列並列。Freeze 済み `BitmapSource` を入出力とし、プリフェッチスレッドから安全に呼べる
- Stage1 OFF + Stage2 ON の場合も倍率 1.0 の標準 Lanczos 縮小を行ってからシャープ化する（フル解像度への USM は表示上無意味なため）
- フィルタ設定・表示領域・表示モードの署名（`BuildFilterSignature()`）が変わると `DisplayCurrent()` 冒頭でキャッシュを破棄して再フィルタ
- フィルタ全OFF時は署名を固定値 `"off"` にし、ウィンドウ表示・リサイズによる無駄なキャッシュ破棄＝再デコードを行わない（起動遅延対策・2026/07/07）
- 起動時の先読みは `_uiReady` フラグにより Window_Loaded 後に開始（ウィンドウ表示前はディスク/CPUを現在画像のデコードに集中させる）
- 起動直後は `ActualWidth=0` でフィルタが効かないため、`Window_Loaded` で `RefreshFilterIfNeeded()` を呼んで適用し直す
- ウィンドウリサイズは `_filterRefreshTimer`（300ms）でデバウンス
- F9/F11 トグルの状態は `_settings` に直接書くため、終了時の `SaveState()` で自動的に ini へ永続化される

---

## 設定ファイル（ClipViewer.ini）

実行ファイルと同ディレクトリに配置。起動時に読み込み、終了時に上書き保存（コメント・新キーも含む最新フォーマットで常時上書き）。

### セクション構成

```ini
; ========================================
; ClipViewer v0.7.0 設定ファイル
; ========================================
; 対応フォーマット：.clip / .psd / .jpg / .jpeg / .png / .webp / .gif / .avif
; アーカイブ：.zip / .cbz（直接対応）/ .rar / .lzh / .7z（7-Zip 要インストール）

[KeyBindings]
; ページ送り（次へ）
NextPage=Left
; ページ戻り（前へ）
PrevPage=Right
; 見開き/単ページ切替
ToggleSpread=Space
; 見開き時の単ページ送り
SingleStep=NumPad0
; 右綴じ/左綴じ切替
ToggleBinding=F3
; 情報表示：Basic↔Off トグル
ToggleInfo=F1
; 情報表示：Off→Basic→Detailed サイクル
CycleInfo=Tab
; 先頭単独表示
ToggleFirstSingle=F4
; 先頭ファイルへジャンプ
JumpFirst=Home
; 末尾ファイルへジャンプ
JumpLast=End
; 複数ページ送り（Next = PageDown キーのini表記）
PageSkipForward=Next
; 複数ページ戻り
PageSkipBack=PageUp
; ファイル保存（指定ディレクトリへコピー）
FileSave=Insert
; 前の兄弟ディレクトリへ移動
NavigateDirUp=Up
; 次の兄弟ディレクトリへ移動
NavigateDirDown=Down
; iniファイルをエディタで開く
OpenIniFile=F2
; フルスクリーン／ウィンドウモード切替（Return = Enter キー）
ToggleWindowMode=Return
; モアレ軽減フィルタ ON/OFF トグル
ToggleMoireFilter=F9
; シャープ化フィルタ ON/OFF トグル
ToggleSharpen=F11
; アニメ再生モード切替（Loop↔AutoAdvance）
ToggleGifMode=F5
; アニメ一時停止／再生
GifPausePlay=F6
; アニメコマ送り（前）
GifStepForward=F7
; アニメコマ戻し（後）
GifStepBackward=F8
; 画像をクリップボードへコピー（Ctrl+）
CopyToClipboard=C
; ファイルをダイアログ指定の場所へコピー（Ctrl+）
SaveFileAs=S
; 表示中ファイルをごみ箱に送る
DeleteFile=Delete
; 右回転（Alt+）
RotateRight=Right
; 左回転（Alt+）
RotateLeft=Left
; 左右反転トグル（Alt+）
FlipH=Up
; 上下反転トグル（Alt+）
FlipV=Down
; 終了
Exit=Escape

[MouseBindings]
; 割り当て可能ボタン: XButton1(戻る), XButton2(進む), Middle(ホイールクリック), Right(右クリック)
; アクション名は [KeyBindings] のキー名と同一（NextPage, PrevPage, Exit 等）
XButton1=PrevPage
XButton2=NextPage
Middle=Exit

[Filters]
; モアレ軽減（Stage1）/ シャープ化（Stage2）の2段パイプライン
MoireFilterEnabled=True
MoireFilterMode=Lanczos
MoireFilterStrength=40
MoireDownscaleThreshold=0.95
SharpenEnabled=True
SharpenRadius=1
SharpenAmount=100
SharpenThreshold=20

[Settings]
DefaultMode=Single
DefaultBinding=Right
TargetScreen=0
ExternalEditor=C:\Program Files\CELSYS\CLIP STUDIO 1.5\CLIP STUDIO PAINT\CLIPStudioPaint.exe
SaveDirectory=C:\Users\(ユーザー名)\Pictures\ClipViewer
PageSkipCount=10

[State]
LastMode=Spread
LastBinding=Right
LastInfoMode=Basic
LastFirstSingle=False
LastGifPlayMode=Loop
LastIsFullscreen=True
LastWindowedLeft=100
LastWindowedTop=100
LastWindowedWidth=1280
LastWindowedHeight=800
```

### 設定項目詳細

| セクション | キー | 型 | デフォルト | 説明 |
|-----------|------|----|-----------|------|
| KeyBindings | NextPage | Key | Left | 次ページ |
| KeyBindings | PrevPage | Key | Right | 前ページ |
| KeyBindings | ToggleSpread | Key | Space | 見開き切替 |
| KeyBindings | SingleStep | Key | NumPad0 | 単ページ送り |
| KeyBindings | ToggleBinding | Key | F3 | 綴じ方向切替 |
| KeyBindings | ToggleInfo | Key | F1 | 情報表示 Basic↔Offトグル |
| KeyBindings | CycleInfo | Key | Tab | 情報表示 Off→Basic→Detailedサイクル |
| KeyBindings | ToggleFirstSingle | Key | F4 | 先頭単独切替 |
| KeyBindings | JumpFirst | Key | Home | 先頭ジャンプ |
| KeyBindings | JumpLast | Key | End | 末尾ジャンプ |
| KeyBindings | PageSkipForward | Key | Next（=PageDown） | スキップ送り |
| KeyBindings | PageSkipBack | Key | PageUp | スキップ戻り |
| KeyBindings | FileSave | Key | Insert | ファイル保存 |
| KeyBindings | NavigateDirUp | Key | Up | 前ディレクトリ/アーカイブ |
| KeyBindings | NavigateDirDown | Key | Down | 次ディレクトリ/アーカイブ |
| KeyBindings | OpenIniFile | Key | F2 | ini ファイルを開く |
| KeyBindings | ToggleWindowMode | Key | Return | フルスクリーン切替（Enter キー） |
| KeyBindings | ToggleMoireFilter | Key | F9 | モアレ軽減フィルタ ON/OFF |
| KeyBindings | ToggleSharpen | Key | F11 | シャープ化フィルタ ON/OFF |
| KeyBindings | ToggleGifMode | Key | F5 | アニメモード切替 |
| KeyBindings | GifPausePlay | Key | F6 | アニメ一時停止/再生 |
| KeyBindings | GifStepForward | Key | F7 | アニメコマ送り |
| KeyBindings | GifStepBackward | Key | F8 | アニメコマ戻し |
| KeyBindings | CopyToClipboard | Key | C | クリップボードコピー（Ctrl+） |
| KeyBindings | SaveFileAs | Key | S | ダイアログ保存（Ctrl+） |
| KeyBindings | DeleteFile | Key | Delete | ごみ箱に送る |
| KeyBindings | RotateRight | Key | Right | 右90°回転（Alt+） |
| KeyBindings | RotateLeft | Key | Left | 左90°回転（Alt+） |
| KeyBindings | FlipH | Key | Up | 左右反転（Alt+） |
| KeyBindings | FlipV | Key | Down | 上下反転（Alt+） |
| KeyBindings | Exit | Key | Escape | 終了 |
| Filters | MoireFilterEnabled | bool | True | Stage1（モアレ軽減）ON/OFF。F9 トグルの状態が終了時に保存される |
| Filters | MoireFilterMode | Off/Area/Lanczos/Gaussian | Lanczos | Stage1 アルゴリズム（現状 Lanczos のみ実装、他は Lanczos で代替） |
| Filters | MoireFilterStrength | int (0-100) | 40 | Lanczos カーネル幅倍率 1.0〜2.0 にマッピング |
| Filters | MoireDownscaleThreshold | double (0.0-1.0) | 0.95 | この縮小率未満のダウンスケール時のみフィルタ適用 |
| Filters | SharpenEnabled | bool | True | Stage2（アンシャープマスク）ON/OFF。F11 トグルの状態が終了時に保存される |
| Filters | SharpenRadius | double (0.1-5.0) | 1.0 | USM ガウシアン半径 px |
| Filters | SharpenAmount | int (0-200) | 100 | USM 強度 % |
| Filters | SharpenThreshold | int (0-255) | 20 | 低コントラスト部をシャープ化対象外にする閾値（リンギング防止） |
| Settings | DefaultMode | Single/Spread | Single | 起動時表示モード（LastMode が優先） |
| Settings | DefaultBinding | Right/Left | Right | 起動時綴じ方向（LastBinding が優先） |
| Settings | TargetScreen | int | 0 | 表示モニタ番号（0=プライマリ） |
| Settings | ExternalEditor | string | CLIPStudioPaint.exe パス | 外部エディタの実行ファイルパス |
| Settings | SaveDirectory | string | ピクチャ\ClipViewer | Insert キー保存先ディレクトリ |
| Settings | PageSkipCount | int | 10 | PageSkip 1 回あたりのページ数 |
| Settings | ArchiveHistoryEnabled | bool | True | アーカイブ閲覧位置履歴（F52）の有効/無効 |
| Settings | ArchiveHistoryCount | int (1-1000) | 30 | 履歴の最大登録件数（古いものから自動削除） |
| KeyBindings | ToggleArchiveHistory | Key | None（未割り当て） | アーカイブ履歴機能 ON/OFF トグル |
| State | LastMode | Single/Spread | Spread | 終了時の表示モード（次回起動時復元） |
| State | LastBinding | Right/Left | Right | 終了時の綴じ方向 |
| State | LastInfoMode | Off/Basic/Detailed | Basic | 終了時の情報パネルモード |
| State | LastFirstSingle | bool | False | 終了時の先頭単独状態 |
| State | LastGifPlayMode | Loop/AutoAdvance | Loop | 終了時のアニメ再生モード |
| State | LastIsFullscreen | bool | True | 終了時のフルスクリーン状態 |
| State | LastWindowedLeft | double | 100 | ウィンドウ左端位置 |
| State | LastWindowedTop | double | 100 | ウィンドウ上端位置 |
| State | LastWindowedWidth | double | 1280 | ウィンドウ幅 |
| State | LastWindowedHeight | double | 800 | ウィンドウ高さ |

**注意：**
- `ExternalEditor` / `SaveDirectory` のパスに引用符を含む場合は自動的に除去される
- コメント行（`;` または `#` で始まる行）と `[セクション]` 行はパース時にスキップされる

---

## 内部アーキテクチャ

### クラス構成

```
ClipViewer/
├── App.xaml / App.xaml.cs          起動エントリポイント、引数受け取り
├── MainWindow.xaml                  UI レイアウト（全画面、ZoomContainer、各パネル）
├── MainWindow.xaml.cs               メインロジック（表示・ナビゲーション・入力処理）
├── AppSettings.cs                   設定値の POCO クラス
├── IniFileManager.cs                ini ファイルの読み書き
├── ImageFilters.cs                  モアレ軽減/シャープ化パイプライン（v0.7.0、F49/F50）
├── ClipFileReader.cs                .clip ファイルから画像を抽出
├── PsdFileReader.cs                 .psd ファイルから画像を抽出
└── NaturalSort.cs                   StrCmpLogicalW による自然順ソート（フォルダ階層→ファイル名の2段比較）
```

### UI 構造（XAML）

```
Window (Background=Black, WindowStyle=None, Cursor=None)
└── Grid
    ├── Grid[ZoomContainer]                  ScaleTransform + TranslateTransform（ズーム・パン）
    │   └── Grid[ImageTransformContainer]    ScaleTransform(FlipScale) + RotateTransform(ImageRotate)
    │       │                                RenderTransformOrigin="0.5,0.5"
    │       ├── Image[SingleImage]           単ページ表示用
    │       └── Grid[SpreadGrid]             見開き表示用
    │           ├── Image[LeftImage]         左ページ
    │           └── Image[RightImage]        右ページ
    ├── StackPanel[InfoStackPanel] (右上)    MaxWidth = ActualWidth * 0.20
    │   ├── Border[InfoPanel]                情報パネルオーバーレイ（Basic/Detailed）
    │   │   ├── TextBlock[InfoFileName]      ファイル名（Basic以上）
    │   │   ├── TextBlock[InfoPageNumber]    ページ番号（Basic以上）
    │   │   ├── TextBlock[InfoZoom]          ズーム率（Basic以上、TextWrapping=Wrap）
    │   │   ├── TextBlock[InfoPixelSize]     ピクセルサイズ（Detailedのみ、TextWrapping=Wrap）
    │   │   ├── TextBlock[InfoFullPath]      フルパス（Detailedのみ、TextWrapping=Wrap）
    │   │   └── TextBlock[InfoExif]          EXIFサマリー（Detailedのみ、TextWrapping=Wrap）
    │   └── Border[NotifyPanel]              操作通知オーバーレイ（自動消去）
    └── TextBlock[ErrorText]                 エラー・案内メッセージ
```

### 主要フィールド（MainWindow）

| フィールド | 型 | 説明 |
|-----------|-----|------|
| `_clipFiles` | `List<string>` | 対象ファイルの絶対パスリスト（自然順ソート済み） |
| `_currentIndex` | `int` | 現在表示中のインデックス |
| `_displayMode` | `DisplayMode` | Single / Spread |
| `_bindingDirection` | `BindingDirection` | Right / Left |
| `_infoMode` | `InfoDisplayMode` | 情報パネルモード（Off / Basic / Detailed） |
| `_firstSingle` | `bool` | 先頭単独表示の有効/無効 |
| `_actionMap` | `Dictionary<string, Action>` | アクション名→メソッドのマウスバインド用辞書 |
| `_rotationAngle` | `int` | 現在の回転角度（0/90/180/270、ページ移動でリセット） |
| `_flipH` | `bool` | 左右反転状態（ページ移動でリセット） |
| `_flipV` | `bool` | 上下反転状態（ページ移動でリセット） |
| `_imageCache` | `Dictionary<int, BitmapSource>` | 静止画キャッシュ（最大 8 件） |
| `_brokenFiles` | `HashSet<int>` | 読み込み失敗インデックスのセット |
| `_zoomFactor` | `double` | 現在のズーム倍率（1.0 = Fit） |
| `_notifyTimer` | `DispatcherTimer` | 通知パネルの 3 秒タイマー |
| `_gifFrameCache` | `Dictionary<int, BitmapSource[]>` | アニメフレームキャッシュ |
| `_gifDelayCache` | `Dictionary<int, int[]>` | フレーム遅延（ms）キャッシュ |
| `_gifRenderingHandler` | `EventHandler` | `CompositionTarget.Rendering` ハンドラ |
| `_gifCurrentIdx` | `int` | 現在アニメ再生中のファイルインデックス |
| `_gifFrameIndex` | `int` | 現在表示中のフレーム番号 |
| `_gifPlayMode` | `GifPlayMode` | Loop / AutoAdvance |
| `_gifPaused` | `bool` | アニメ一時停止状態 |
| `_knownAnimated` | `HashSet<int>` | アニメ確定ファイルのインデックスキャッシュ |
| `_knownStatic` | `HashSet<int>` | 静止画確定ファイルのインデックスキャッシュ |
| `_currentArchivePath` | `string` | 現在開いているアーカイブパス（null=通常モード） |
| `_currentTempDir` | `string` | アーカイブ展開先の一時ディレクトリパス |
| `_isFullscreen` | `bool` | 現在フルスクリーン状態か |
| `_windowedLeft/Top/Width/Height` | `double` | ウィンドウモード時の位置・サイズ |
| `_srcSizeCache` | `Dictionary<int, int[]>` | ソース原寸 [w,h]（フィルタ適用後も Detailed 表示用に原寸を保持） |
| `_viewportPxW/_viewportPxH` | `int` | 表示領域サイズ（デバイスピクセル、DPI換算済み） |
| `_filterSignature` | `string` | フィルタ設定+表示条件の署名（変化でキャッシュ破棄・再フィルタ） |
| `_filterRefreshTimer` | `DispatcherTimer` | ウィンドウリサイズ時の再フィルタ用デバウンス（300ms） |

### 主要ヘルパーメソッド（MainWindow）

| メソッド | 説明 |
|---------|------|
| `ResolveSpreadIndices(out leftIdx, out rightIdx)` | `_currentIndex` と `_currentIndex+1` を左右に解決 |
| `LastSpreadAnchor(int count)` | `_firstSingle` ON 時の末尾スプレッドアンカーを返す |
| `FindNextValid(from, forward, step)` | `_brokenFiles` をスキップして有効インデックスを探索 |
| `SeekFirstValid()` | 起動時・ディレクトリ変更時に最初の有効ファイルを探して表示 |
| `EvictCache(anchor)` | スライディングウィンドウ外のキャッシュを解放 |
| `StartPrefetch(anchor)` | バックグラウンドで anchor+1〜+3 を先読みキャッシュ |
| `TriggerAnimationCheckForSpread()` | バックグラウンドでアニメ判定 → `Dispatcher.Invoke(DisplayCurrent)` |
| `DetectAnimation(idx, files)` | 軽量フレーム数チェックでアニメか否かを判定 |
| `LoadArchiveAsync(archivePath)` | アーカイブを非同期展開して `_clipFiles` を構築 |
| `NavigateSiblingArchive(next)` | アーカイブモード時の隣接アーカイブへの移動 |
| `CleanupTempDir()` | 一時ディレクトリを削除 |
| `SaveCurrentFileAs()` | SaveFileDialog を介してファイルをコピー |
| `DeleteCurrentFile()` | 現在ファイルをごみ箱に送り、リストとキャッシュを更新 |
| `ToggleInfoDisplay()` | F1: Basic↔Off トグル |
| `CycleInfoDisplay()` | Tab: Off→Basic→Detailed サイクル |
| `UpdateInfoPanel()` | 情報パネルの全TextBlockを現在状態で更新 |
| `UpdateDetailedInfo(int fileIdx)` | Detailedモード用：ピクセルサイズ・パス・EXIF を取得して表示 |
| `ReadExifSummary(string path)` | `BitmapDecoder` → `BitmapMetadata` でEXIFサマリー文字列を生成。**各項目は必ず切り詰める**（AI生成画像の数万字級メタデータをそのまま TextBlock(Wrap) に渡すとレイアウトでUIが数十秒フリーズするため。タイトル/コメント各800・全体1000文字上限。AI系プロンプトは Title=EXIF ImageDescription に入ることが多い） |
| `BuildZoomText(int fileIdx)` | ズーム率表示文字列。%は**オリジナル原寸=100%の実表示倍率**（`ComputeFitScale × _zoomFactor`）。Fit時は `Fit (63%)` 形式で併記。原寸未取得時は従来表記へフォールバック |
| `ApplyImageTransform()` | `FlipScale`（ScaleX/ScaleY）と `ImageRotate`（Angle）に現在の反転・回転値を適用 |
| `RotateRight()` | `_rotationAngle = (_rotationAngle + 90) % 360` → `ApplyImageTransform()` |
| `RotateLeft()` | `_rotationAngle = (_rotationAngle + 270) % 360` → `ApplyImageTransform()` |
| `ToggleFlipH()` | `_flipH = !_flipH` → `ApplyImageTransform()` |
| `ToggleFlipV()` | `_flipV = !_flipV` → `ApplyImageTransform()` |
| `BuildActionMap()` | アクション名→メソッドの `_actionMap` を構築（コンストラクタ内で呼び出し） |
| `DecodeAndFilter(index, path, data)` | 寸法先読み → ゲーティング判定 → `DecodePixelWidth` 付きデコード → `ImageFilters.ApplyPipeline()`（F49/F50） |
| `SnapshotFilterParams()` | `_settings` から `FilterParams` のスナップショットを作成（バックグラウンド安全） |
| `ComputeFitScale(srcW, srcH)` | Fit 表示時の縮小率を計算（見開き時の縦長画像は半幅領域で計算） |
| `BuildFilterSignature()` | フィルタ設定+表示条件の署名文字列を生成 |
| `UpdateViewportSize()` | `ActualWidth/Height` を DPI 換算して `_viewportPx*` を更新 |
| `RefreshFilterIfNeeded()` | 署名が変わっていれば `DisplayCurrent()`（冒頭でキャッシュ破棄） |
| `ScheduleFilterRefresh()` | 300ms デバウンス付きで `RefreshFilterIfNeeded()` を予約 |
| `ToggleMoireFilter()` / `ToggleSharpen()` | F9/F11: Enabled をトグルして1秒通知 + 再表示 |

### ClipFileReader の処理フロー

```
1. File.ReadAllBytes(clipFilePath)
       ↓
2. FindSqliteOffset()
   "SQLite format 3\0" (16 bytes) を線形探索
       ↓ 見つからない場合 → null 返却
3. SQLite 部分を一時ファイル（Path.GetTempFileName()）に書き出し
       ↓
4. QueryImageData(tempPath)
   SELECT * FROM CanvasPreview LIMIT 1
   全カラムを走査し PNG/JPEG シグネチャで画像データを特定
       ↓ 一時ファイルを finally で削除
5. PNG/JPEG バイト列を返却
       ↓
6. MemoryStream → BitmapImage (CacheOption=OnLoad) → Freeze
```

**PNG シグネチャ:** `89 50 4E 47 0D 0A 1A 0A`  
**JPEG シグネチャ:** `FF D8 FF`

---

## ファイル構成

### ソースツリー

```
ClipViewer/
├── ClipViewer.csproj
├── App.xaml
├── App.xaml.cs
├── MainWindow.xaml
├── MainWindow.xaml.cs
├── AppSettings.cs
├── ClipFileReader.cs
├── PsdFileReader.cs
├── IniFileManager.cs
├── ImageFilters.cs
├── ArchiveHistory.cs
├── NaturalSort.cs
├── Properties/
│   └── AssemblyInfo.cs
└── packages/
    └── Stub.System.Data.SQLite.Core.NetFramework.1.0.118.0/
```

### ビルド出力（bin\Release\）

```
bin\Release\
├── ClipViewer.exe
├── ClipViewer.pdb
├── ClipViewer.ini          (初回起動時または起動毎に生成・更新)
├── ClipViewer_history.txt  (アーカイブ閲覧位置の履歴、F52。初回のアーカイブ閲覧時に生成)
├── System.Data.SQLite.dll
├── System.Data.SQLite.xml
├── libwebp.dll             (WebP アニメ用)
├── libwebpdemux.dll        (WebP アニメ用)
├── libsharpyuv.dll         (WebP アニメ用)
├── x64\
│   └── SQLite.Interop.dll
└── x86\
    └── SQLite.Interop.dll
```

---

## ビルド方法

Visual Studio 2022（MSBuild C# 6.0 対応）が必要。

```bat
REM レスポンスファイルを使用してパスの特殊文字を回避
echo "C:\path\to\ClipViewer.csproj" /p:Configuration=Release /t:Build /v:minimal > build.rsp
"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\amd64\MSBuild.exe" @build.rsp
```

出力先: `bin\Release\ClipViewer.exe`

---

## 既知の制限事項

| 項目 | 内容 |
|------|------|
| `.clip` プレビュー未生成 | CSP 未保存の `.clip` は `CanvasPreview` テーブルが空でロード不可 |
| 画像キャッシュ上限 | 8 枚超で全クリア（大量高解像度ファイルでのメモリ管理が粗い） |
| 見開きスキップ時の有効性チェック | `PageSkip` / `JumpFirst` / `JumpLast` は `_brokenFiles` をスキップしない |
| `.clip` の透明プレビュー | CSP 側でプレビューが透明画像の場合、黒画面として表示される |
| ~~アーカイブ内サブフォルダ~~ | ~~サブフォルダ内画像の順序が意図と異なる場合がある~~ → **v0.8.1で修正済み**（NaturalSort を「フォルダ階層→ファイル名」の2段比較に変更） |
| RAR/LZH/7z は 7-Zip 依存 | 7-Zip 未インストール時はこれらのアーカイブを開けない |
| 回転・反転の保存非対応 | 回転・反転状態はセッション内のみ有効（終了時に保存しない） |
| EXIF 非対応フォーマット | `.clip` / `.psd` は `BitmapDecoder` で直接 EXIF を取得できないため、Detailed モードでも EXIF 欄は空になる |
| アーカイブ内ファイルの削除非対応 | ごみ箱削除は通常モードのファイルのみ対応。アーカイブ内画像はガード通知を表示して中断 |
| アニメフレームへのフィルタ非適用 | モアレ軽減/シャープ化はアニメ GIF/WebP の再生フレームには適用されない（静止初回フレームのみ） |
| フィルタ適用中のズーム画質 | フィルタ適用画像は表示サイズの2倍解像度でデコード・縮小されるため、2倍を超えるズームインでは原寸よりディテールが劣る。原寸確認時は F9/F11 でフィルタ OFF を推奨 |
