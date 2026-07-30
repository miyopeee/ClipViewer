using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Input;

namespace ClipViewer
{
    /// <summary>
    /// ClipViewer.ini の読み書きを担当する。
    ///
    /// セクション構成:
    ///   [KeyBindings]   キー割当（カンマ区切りで複数指定可）
    ///   [MouseBindings] マウスボタン割当
    ///   [Filters]       モアレ軽減 / シャープ化パイプライン設定（v0.7.0）
    ///   [Settings]      起動時固定設定
    ///   [State]         終了時保存・起動時復元する動作状態
    /// </summary>
    public static class IniFileManager
    {
        private const string FileName = "ClipViewer.ini";

        private static string IniPath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);

        /// <summary>
        /// 直近に自分が書き込んだ（または再読み込みで取り込んだ）ini の更新時刻。
        /// これとファイルの実 mtime の差で「外部編集の有無」を判定する（F54, v0.8.6）。
        /// </summary>
        private static DateTime _lastWrittenUtc = DateTime.MinValue;

        // =========================================================
        // Load
        // =========================================================

        public static AppSettings Load()
        {
            var s = new AppSettings();

            if (!File.Exists(IniPath))
            {
                Save(s);
                return s;
            }

            try
            {
                ParseInto(s);
            }
            catch
            {
                return new AppSettings();
            }

            // 常に最新フォーマット（コメント・新キー）で上書きして反映
            Save(s);
            return s;
        }

        /// <summary>直近の自己書き込み（または取り込み）以降に ini が外部から変更されているか。</summary>
        public static bool HasExternalChange()
        {
            try
            {
                return File.Exists(IniPath)
                    && File.GetLastWriteTimeUtc(IniPath) != _lastWrittenUtc;
            }
            catch { return false; }
        }

        /// <summary>
        /// 実行中の設定再読み込み用パース（F54, v0.8.6）。
        /// Load() と異なりファイルへの書き戻しを行わない
        /// （エディタで開いたままのファイルを裏で書き換えない）。
        /// 読めない場合（書き込み途中のロック等）は null を返し、呼び出し側は現状維持する。
        /// </summary>
        public static AppSettings TryParseForReload()
        {
            try
            {
                if (!File.Exists(IniPath)) return null;
                var s = new AppSettings();
                ParseInto(s);
                _lastWrittenUtc = File.GetLastWriteTimeUtc(IniPath);  // この変更は取り込み済み
                return s;
            }
            catch { return null; }
        }

        private static void ParseInto(AppSettings s)
        {
            {
                string section = "";

                foreach (string rawLine in File.ReadAllLines(IniPath))
                {
                    string line = rawLine.Trim();
                    if (line.StartsWith(";") || line.StartsWith("#")) continue;

                    // セクションヘッダ
                    if (line.StartsWith("["))
                    {
                        section = line.Trim('[', ']').ToLowerInvariant();
                        continue;
                    }

                    if (!line.Contains("=")) continue;

                    int    eq  = line.IndexOf('=');
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();

                    // [MouseBindings] セクション: MouseButton → アクション名
                    if (section == "mousebindings")
                    {
                        if (Enum.TryParse<MouseButton>(key, ignoreCase: true, out MouseButton mb))
                            s.MouseBindings[mb] = val;
                        continue;
                    }

                    // [KeyBindings] / [Settings] / [State]
                    switch (key)
                    {
                        // --- [KeyBindings] ---
                        case "NextPage":          s.NextPage          = ParseKeys(val, new[] { Key.Left });      break;
                        case "PrevPage":          s.PrevPage          = ParseKeys(val, new[] { Key.Right });     break;
                        case "ToggleSpread":      s.ToggleSpread      = ParseKeys(val, new[] { Key.Space });     break;
                        case "SingleStep":        s.SingleStep        = ParseKeys(val, new[] { Key.NumPad0 });   break;
                        case "ToggleBinding":     s.ToggleBinding     = ParseKeys(val, new[] { Key.F3 });        break;
                        case "Exit":              s.Exit              = ParseKeys(val, new[] { Key.Escape });    break;
                        case "ToggleInfo":        s.ToggleInfo        = ParseKeys(val, new[] { Key.F1 });        break;
                        case "CycleInfo":         s.CycleInfo         = ParseKeys(val, new[] { Key.Tab });       break;
                        case "ToggleFirstSingle": s.ToggleFirstSingle = ParseKeys(val, new[] { Key.F4 });        break;
                        case "JumpFirst":         s.JumpFirst         = ParseKeys(val, new[] { Key.Home });      break;
                        case "JumpLast":          s.JumpLast          = ParseKeys(val, new[] { Key.End });       break;
                        case "PageSkipForward":   s.PageSkipForward   = ParseKeys(val, new[] { Key.Next });      break;
                        case "PageSkipBack":      s.PageSkipBack      = ParseKeys(val, new[] { Key.PageUp });    break;
                        case "FileSave":          s.FileSave          = ParseKeys(val, new[] { Key.Insert });    break;
                        case "NavigateDirUp":     s.NavigateDirUp     = ParseKeys(val, new[] { Key.Up });        break;
                        case "NavigateDirDown":   s.NavigateDirDown   = ParseKeys(val, new[] { Key.Down });      break;
                        case "OpenIniFile":       s.OpenIniFile       = ParseKeys(val, new[] { Key.F2 });        break;
                        case "ReloadCache":       s.ReloadCache       = ParseKeys(val, new[] { Key.R });         break;
                        case "ToggleWindowMode":  s.ToggleWindowMode  = ParseKeys(val, new[] { Key.Return });    break;
                        case "ToggleMoireFilter": s.ToggleMoireFilter = ParseKeys(val, new[] { Key.F9 });        break;
                        case "ToggleSharpen":     s.ToggleSharpen     = ParseKeys(val, new[] { Key.F10 });       break;
                        case "ToggleArchiveHistory": s.ToggleArchiveHistory = ParseKeys(val, new[] { Key.None }); break;
                        case "ToggleGifMode":     s.ToggleGifMode     = ParseKeys(val, new[] { Key.F5 });        break;
                        case "GifPausePlay":      s.GifPausePlay      = ParseKeys(val, new[] { Key.F6 });        break;
                        case "GifStepForward":    s.GifStepForward    = ParseKeys(val, new[] { Key.F7 });        break;
                        case "GifStepBackward":   s.GifStepBackward   = ParseKeys(val, new[] { Key.F8 });        break;
                        case "CopyToClipboard":   s.CopyToClipboard   = ParseKeys(val, new[] { Key.C });         break;
                        case "SaveFileAs":        s.SaveFileAs        = ParseKeys(val, new[] { Key.S });         break;
                        case "DeleteFile":        s.DeleteFile        = ParseKeys(val, new[] { Key.Delete });    break;
                        case "RotateRight":       s.RotateRight       = ParseKeys(val, new[] { Key.Right });     break;
                        case "RotateLeft":        s.RotateLeft        = ParseKeys(val, new[] { Key.Left });      break;
                        case "FlipH":             s.FlipH             = ParseKeys(val, new[] { Key.Up });        break;
                        case "FlipV":             s.FlipV             = ParseKeys(val, new[] { Key.Down });      break;

                        // --- [Filters] ---（レンジ外はクランプして黙って適用＝起動を阻害しない）
                        case "MoireFilterEnabled":
                            s.MoireFilterEnabled = ParseBool(val, true);
                            break;
                        case "MoireFilterMode":
                            s.MoireFilterMode = ParseMoireMode(val, MoireFilterAlgorithm.Lanczos);
                            break;
                        case "MoireFilterStrength":
                            s.MoireFilterStrength = Clamp(ParseInt(val, 40), 0, 100);
                            break;
                        case "MoireDownscaleThreshold":
                            s.MoireDownscaleThreshold = Clamp(ParseDouble(val, 0.95), 0.0, 1.0);
                            break;
                        case "SharpenEnabled":
                            s.SharpenEnabled = ParseBool(val, true);
                            break;
                        case "SharpenRadius":
                            s.SharpenRadius = Clamp(ParseDouble(val, 1.0), 0.1, 5.0);
                            break;
                        case "SharpenAmount":
                            s.SharpenAmount = Clamp(ParseInt(val, 100), 0, 200);
                            break;
                        case "SharpenThreshold":
                            s.SharpenThreshold = Clamp(ParseInt(val, 20), 0, 255);
                            break;

                        // --- [Settings] ---
                        case "DefaultMode":
                            s.DefaultMode = ParseDisplayMode(val, DisplayMode.Single);
                            break;
                        case "DefaultBinding":
                            s.DefaultBinding = ParseBindingDirection(val, BindingDirection.Right);
                            break;
                        case "TargetScreen":
                            s.TargetScreen = ParseInt(val, 0);
                            break;
                        case "ExternalEditor":
                            s.ExternalEditor = val.Trim('"');
                            break;
                        case "SaveDirectory":
                            s.SaveDirectory = val.Trim('"');
                            break;
                        case "PageSkipCount":
                            s.PageSkipCount = Math.Max(1, ParseInt(val, 10));
                            break;
                        case "KeepZoomOnNavigate":
                            s.KeepZoomOnNavigate = ParseBool(val, false);
                            break;
                        case "ArchiveHistoryEnabled":
                            s.ArchiveHistoryEnabled = ParseBool(val, true);
                            break;
                        case "ArchiveHistoryCount":
                            s.ArchiveHistoryCount = Clamp(ParseInt(val, 30), 1, 1000);
                            break;

                        // --- [State] ---
                        case "LastMode":
                            s.LastMode = ParseDisplayMode(val, DisplayMode.Spread);
                            break;
                        case "LastBinding":
                            s.LastBinding = ParseBindingDirection(val, BindingDirection.Right);
                            break;
                        case "LastInfoMode":
                            s.LastInfoMode = ParseInfoMode(val, InfoDisplayMode.Basic);
                            break;
                        // 旧キー互換: LastInfoDisplay=True → Basic, False → Off
                        case "LastInfoDisplay":
                            s.LastInfoMode = ParseBool(val, true) ? InfoDisplayMode.Basic : InfoDisplayMode.Off;
                            break;
                        case "LastFirstSingle":
                            s.LastFirstSingle = ParseBool(val, false);
                            break;
                        case "LastGifPlayMode":
                            s.LastGifPlayMode = ParseGifPlayMode(val, GifPlayMode.AutoAdvance);
                            break;
                        case "LastIsFullscreen":
                            s.LastIsFullscreen = ParseBool(val, true);
                            break;
                        case "LastWindowedLeft":   s.LastWindowedLeft   = ParseDouble(val, 100);  break;
                        case "LastWindowedTop":    s.LastWindowedTop    = ParseDouble(val, 100);  break;
                        case "LastWindowedWidth":  s.LastWindowedWidth  = ParseDouble(val, 1280); break;
                        case "LastWindowedHeight": s.LastWindowedHeight = ParseDouble(val, 800);  break;
                    }
                }
            }
        }

        // =========================================================
        // Save / SaveState
        // =========================================================

        public static void Save(AppSettings s)
        {
            try
            {
                File.WriteAllLines(IniPath, BuildLines(s));
                _lastWrittenUtc = File.GetLastWriteTimeUtc(IniPath);  // 自己書き込みは外部変更と区別する
            }
            catch { }
        }

        public static void SaveState(AppSettings s) => Save(s);

        // =========================================================
        // 内部ヘルパー
        // =========================================================

        private static IEnumerable<string> BuildLines(AppSettings s)
        {
            var lines = new List<string>
            {
                "; ========================================",
                "; ClipViewer v0.8.12 設定ファイル",
                "; ========================================",
                "; 対応フォーマット：.clip / .psd / .jpg / .jpeg / .png / .webp / .gif / .avif",
                "; アーカイブ：.zip / .cbz（直接対応）/ .rar / .lzh / .7z（7-Zip 要インストール）",
                "; 起動中でも編集保存→ClipViewerのウィンドウに戻った時点で自動反映される（再起動不要）",
                ";（[State] セクションはアプリが自動管理するため、起動中の手動編集は反映されない）",
                "",
                "[KeyBindings]",
                "; 複数キーはカンマ区切りで指定可（例: NextPage=Left,A）",
                "; ページ送り（次へ）",
                $"NextPage={string.Join(",", s.NextPage)}",
                "; ページ戻り（前へ）",
                $"PrevPage={string.Join(",", s.PrevPage)}",
                "; 見開き/単ページ切替",
                $"ToggleSpread={string.Join(",", s.ToggleSpread)}",
                "; 見開き時の単ページ送り",
                $"SingleStep={string.Join(",", s.SingleStep)}",
                "; 右綴じ/左綴じ切替",
                $"ToggleBinding={string.Join(",", s.ToggleBinding)}",
                "; 情報表示：Basic↔Off トグル",
                $"ToggleInfo={string.Join(",", s.ToggleInfo)}",
                "; 情報表示：Off→Basic→Detailed サイクル",
                $"CycleInfo={string.Join(",", s.CycleInfo)}",
                "; 先頭単独表示",
                $"ToggleFirstSingle={string.Join(",", s.ToggleFirstSingle)}",
                "; 先頭ファイルへジャンプ",
                $"JumpFirst={string.Join(",", s.JumpFirst)}",
                "; 末尾ファイルへジャンプ",
                $"JumpLast={string.Join(",", s.JumpLast)}",
                "; 複数ページ送り（Next = PageDown キーのini表記）",
                $"PageSkipForward={string.Join(",", s.PageSkipForward)}",
                "; 複数ページ戻り",
                $"PageSkipBack={string.Join(",", s.PageSkipBack)}",
                "; ファイル保存（指定ディレクトリへコピー）",
                $"FileSave={string.Join(",", s.FileSave)}",
                "; 前の兄弟ディレクトリへ移動",
                $"NavigateDirUp={string.Join(",", s.NavigateDirUp)}",
                "; 次の兄弟ディレクトリへ移動",
                $"NavigateDirDown={string.Join(",", s.NavigateDirDown)}",
                "; iniファイルをエディタで開く",
                $"OpenIniFile={string.Join(",", s.OpenIniFile)}",
                "; 再読み込み（フォルダのファイル一覧と画像キャッシュを破棄して読み直す）",
                $"ReloadCache={string.Join(",", s.ReloadCache)}",
                "; フルスクリーン／ウィンドウモード切替（Return = Enter キー）",
                $"ToggleWindowMode={string.Join(",", s.ToggleWindowMode)}",
                "; モアレ軽減フィルタ ON/OFF トグル",
                $"ToggleMoireFilter={string.Join(",", s.ToggleMoireFilter)}",
                "; シャープ化フィルタ ON/OFF トグル",
                $"ToggleSharpen={string.Join(",", s.ToggleSharpen)}",
                "; アーカイブ履歴機能 ON/OFF トグル（None = 未割り当て。使う場合はキー名を指定 例: F12）",
                $"ToggleArchiveHistory={string.Join(",", s.ToggleArchiveHistory)}",
                "; アニメ再生モード切替（Loop↔AutoAdvance）GIF/WebP共通",
                $"ToggleGifMode={string.Join(",", s.ToggleGifMode)}",
                "; アニメ一時停止／再生（GIF/WebP共通）",
                $"GifPausePlay={string.Join(",", s.GifPausePlay)}",
                "; アニメコマ送り（前）GIF/WebP共通",
                $"GifStepForward={string.Join(",", s.GifStepForward)}",
                "; アニメコマ戻し（後）GIF/WebP共通",
                $"GifStepBackward={string.Join(",", s.GifStepBackward)}",
                "; 画像をクリップボードへコピー（Ctrl+キー）",
                $"CopyToClipboard={string.Join(",", s.CopyToClipboard)}",
                "; ファイルをダイアログ指定の場所へコピー（Ctrl+キー）",
                $"SaveFileAs={string.Join(",", s.SaveFileAs)}",
                "; 表示中ファイルをごみ箱に送る",
                $"DeleteFile={string.Join(",", s.DeleteFile)}",
                "; 右回転（Alt+キー）",
                $"RotateRight={string.Join(",", s.RotateRight)}",
                "; 左回転（Alt+キー）",
                $"RotateLeft={string.Join(",", s.RotateLeft)}",
                "; 左右反転トグル（Alt+キー）",
                $"FlipH={string.Join(",", s.FlipH)}",
                "; 上下反転トグル（Alt+キー）",
                $"FlipV={string.Join(",", s.FlipV)}",
                "; 終了",
                $"Exit={string.Join(",", s.Exit)}",
                "",
                "[MouseBindings]",
                "; 割り当て可能ボタン: XButton1(戻る), XButton2(進む), Middle(ホイールクリック), Right(右クリック)",
                "; アクション名は [KeyBindings] のキー名と同一（NextPage, PrevPage, Exit 等）",
                "; 割り当てを解除するには行ごと削除またはコメントアウト",
            };

            // MouseBindings はエントリ数が可変のため動的追加
            foreach (var kv in s.MouseBindings)
                lines.Add($"{kv.Key}={kv.Value}");

            lines.AddRange(new[]
            {
                "",
                "[Filters]",
                "; モアレ軽減（Stage1）/ シャープ化（Stage2）の2段パイプライン",
                "; Enabled 系は F9/F11 トグルを押した瞬間に保存される",
                "; モアレ軽減フィルタ 有効/無効",
                $"MoireFilterEnabled={s.MoireFilterEnabled}",
                "; モアレ軽減アルゴリズム（Off / Area / Lanczos / Gaussian）※現状 Lanczos のみ実装",
                $"MoireFilterMode={s.MoireFilterMode}",
                "; モアレ軽減強度（0-100、カーネル幅にマッピング）",
                $"MoireFilterStrength={s.MoireFilterStrength}",
                "; ダウンスケール判定閾値（表示解像度÷ソース解像度がこの値未満の場合のみ適用。0.95推奨）",
                $"MoireDownscaleThreshold={s.MoireDownscaleThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                "; アンシャープマスク 有効/無効",
                $"SharpenEnabled={s.SharpenEnabled}",
                "; シャープ化半径（px、0.1-5.0）",
                $"SharpenRadius={s.SharpenRadius.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                "; シャープ化強度（0-200%）",
                $"SharpenAmount={s.SharpenAmount}",
                "; シャープ化閾値（0-255、リンギング防止。低コントラスト部を対象外に）",
                $"SharpenThreshold={s.SharpenThreshold}",
                "",
                "[Settings]",
                "; 起動時の表示モード（Single / Spread）",
                $"DefaultMode={s.DefaultMode}",
                "; 起動時の綴じ方向（Right / Left）",
                $"DefaultBinding={s.DefaultBinding}",
                "; 表示スクリーン番号（0=プライマリ、1=2番目...）",
                $"TargetScreen={s.TargetScreen}",
                "; 外部エディタのパス（Ctrl+E）",
                $"ExternalEditor={s.ExternalEditor}",
                "; ファイル保存先ディレクトリ（Insert）",
                $"SaveDirectory={s.SaveDirectory}",
                "; PageSkipForward/Back でのスキップ数",
                $"PageSkipCount={s.PageSkipCount}",
                "; ページ移動時にズーム倍率を保持する（True/False。パン位置は常にリセット）",
                $"KeepZoomOnNavigate={s.KeepZoomOnNavigate}",
                "; アーカイブ閲覧位置履歴の有効/無効（ToggleArchiveHistory キーでも切替可）",
                $"ArchiveHistoryEnabled={s.ArchiveHistoryEnabled}",
                "; アーカイブ閲覧位置履歴の最大登録件数（古いものから自動削除）",
                $"ArchiveHistoryCount={s.ArchiveHistoryCount}",
                "",
                "[State]",
                $"LastMode={s.LastMode}",
                $"LastBinding={s.LastBinding}",
                $"LastInfoMode={s.LastInfoMode}",
                $"LastFirstSingle={s.LastFirstSingle}",
                $"LastGifPlayMode={s.LastGifPlayMode}",
                $"LastIsFullscreen={s.LastIsFullscreen}",
                $"LastWindowedLeft={s.LastWindowedLeft}",
                $"LastWindowedTop={s.LastWindowedTop}",
                $"LastWindowedWidth={s.LastWindowedWidth}",
                $"LastWindowedHeight={s.LastWindowedHeight}",
            });

            return lines;
        }

        /// <summary>
        /// カンマ区切りのキー文字列を Key[] にパースする（単一キーも対応）。
        /// </summary>
        private static Key[] ParseKeys(string val, Key[] fallback)
        {
            var parts = val.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            var keys  = new List<Key>();
            foreach (string p in parts)
            {
                if (Enum.TryParse<Key>(p.Trim(), ignoreCase: true, out Key k))
                    keys.Add(k);
            }
            return keys.Count > 0 ? keys.ToArray() : fallback;
        }

        private static DisplayMode ParseDisplayMode(string val, DisplayMode fallback)
            => Enum.TryParse<DisplayMode>(val, ignoreCase: true, out DisplayMode r) ? r : fallback;

        private static InfoDisplayMode ParseInfoMode(string val, InfoDisplayMode fallback)
            => Enum.TryParse<InfoDisplayMode>(val, ignoreCase: true, out InfoDisplayMode r) ? r : fallback;

        private static BindingDirection ParseBindingDirection(string val, BindingDirection fallback)
            => val.Equals("Left", StringComparison.OrdinalIgnoreCase) ? BindingDirection.Left : fallback;

        private static GifPlayMode ParseGifPlayMode(string val, GifPlayMode fallback)
            => val.Equals("AutoAdvance", StringComparison.OrdinalIgnoreCase)
               ? GifPlayMode.AutoAdvance : fallback;

        private static MoireFilterAlgorithm ParseMoireMode(string val, MoireFilterAlgorithm fallback)
            => Enum.TryParse<MoireFilterAlgorithm>(val, ignoreCase: true, out MoireFilterAlgorithm r) ? r : fallback;

        private static int Clamp(int v, int min, int max)
            => v < min ? min : (v > max ? max : v);

        private static double Clamp(double v, double min, double max)
            => v < min ? min : (v > max ? max : v);

        private static bool ParseBool(string val, bool fallback)
        {
            if (bool.TryParse(val, out bool r)) return r;
            return fallback;
        }

        private static int ParseInt(string val, int fallback)
        {
            if (int.TryParse(val, out int r) && r >= 0) return r;
            return fallback;
        }

        private static double ParseDouble(string val, double fallback)
        {
            if (double.TryParse(val, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double r)) return r;
            return fallback;
        }
    }
}
