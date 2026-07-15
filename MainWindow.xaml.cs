using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace ClipViewer
{
    public partial class MainWindow : Window
    {
        // ---- ファイルリスト ----
        private List<string> _clipFiles = new List<string>();
        private int _currentIndex = -1;

        // ---- 表示状態 ----
        private DisplayMode      _displayMode;
        private BindingDirection _bindingDirection;
        private InfoDisplayMode  _infoMode;       // 情報表示モード（Off/Basic/Detailed）
        private bool             _firstSingle;    // F15 先頭単独表示

        // ---- 回転・反転 ----
        private int  _rotationAngle = 0;   // 0, 90, 180, 270
        private bool _flipH         = false;
        private bool _flipV         = false;

        // ---- 設定 ----
        private AppSettings _settings;

        // ---- 画像キャッシュ / 先読み ----
        private const int PrefetchAhead  = 6;   // 前方先読み（最大3見開き分）
        private const int PrefetchBehind = 3;   // 後方先読み
        private readonly Dictionary<int, BitmapSource> _imageCache = new Dictionary<int, BitmapSource>();
        private readonly Dictionary<int, bool>        _wideCache  = new Dictionary<int, bool>(); // true=横長
        private readonly Dictionary<int, int[]>       _srcSizeCache = new Dictionary<int, int[]>(); // ソース原寸 [w,h]（フィルタ適用後も原寸を保持）
        private readonly HashSet<int>                  _brokenFiles = new HashSet<int>();
        private readonly object                        _cacheLock   = new object();
        private CancellationTokenSource                _prefetchCts;

        // ---- ナビゲーション履歴スタック（NavigatePrev 用）----
        private readonly Stack<int> _navHistory = new Stack<int>();

        // ---- アニメーション（GIF / WebP 共通）----
        private GifPlayMode                               _gifPlayMode;
        private readonly Dictionary<int, BitmapSource[]> _gifFrameCache = new Dictionary<int, BitmapSource[]>();
        private readonly Dictionary<int, int[]>           _gifDelayCache = new Dictionary<int, int[]>();
        // プログレッシブ再生（v0.8.2）: 配列は先頭から順に埋まり、ここが「再生可能なフレーム数」を示す。
        // 再生側は _gifAvailCache 未満のインデックスのみ参照する（それ以降は未デコード=null の可能性）。
        private readonly Dictionary<int, int>             _gifAvailCache = new Dictionary<int, int>();
        private readonly HashSet<int>                     _animDecoding  = new HashSet<int>(); // 背景デコード実行中
        private EventHandler _gifRenderingHandler;      // CompositionTarget.Rendering ハンドラ
        private long         _gifNextFrameTick;         // 次フレームの Stopwatch 目標値（ticks）
        private Image        _gifTargetImage;
        private int          _gifFrameIndex;
        private bool         _gifPaused;
        private int          _gifCurrentIdx = -1;       // アニメ中のファイルインデックス（-1=非再生）
        // loop名アニメ（v0.8.5）: ファイル名に "loop" を含むアニメは再生モード設定に関係なくループ固定。
        // その再生中のページ送りはループ末尾まで保留する（シームレス連番+ループ混在セットの最適化）
        private bool         _gifForceLoop;              // 表示中アニメがループ固定中か
        private bool         _gifAdvancePending;         // ページ送りをループ末尾まで保留中か

        // vsync と frame delay のズレを吸収する早め判定幅（2ms）
        private static readonly long _earlyAdvanceTicks = Stopwatch.Frequency * 2 / 1000;

        // アニメーション判定済みキャッシュ（見開きモード時の自動単ページ切替用）
        // _clipFiles が変わるタイミング（NavigateSiblingDirectory）でクリアする
        private readonly HashSet<int> _knownAnimated = new HashSet<int>();
        private readonly HashSet<int> _knownStatic   = new HashSet<int>();

        // ---- アーカイブモード ----
        // null = 通常モード（ディレクトリ直読み）
        // 非null = アーカイブ展開モード（_clipFiles は _currentTempDir 内の展開済みパス）
        private string _currentArchivePath = null;
        private string _currentTempDir     = null;

        // ---- 遅延展開（v0.8.4）----
        // zip/cbz は開いた時点でエントリ列挙のみ行い、ファイル実体は「読む直前」にオンデマンド展開する。
        // ZipArchive はスレッド非安全のため、アクセスは _zipLock で直列化する。
        private ZipArchive                 _lazyZip;                    // 遅延展開中のZIP（null=遅延なし）
        private readonly object            _zipLock = new object();
        private Dictionary<string, string> _zipEntryByPath;             // temp先フルパス → ZIPエントリ名

        // アーカイブ対象の画像拡張子（列挙・展開共通）
        private static readonly HashSet<string> _archiveImageExts =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".avif" };

        private static readonly string[] _archiveExts =
            { ".zip", ".cbz", ".rar", ".7z", ".lzh" };

        // ---- ズーム状態 ----
        private const double ZoomMin  = 0.10;
        private const double ZoomMax  = 5.00;
        private const double ZoomStep = 0.20;
        private double _zoomFactor = 1.0; // 1.0 = Fit（変換なし）

        // ---- ウィンドウモード ----
        private bool   _isFullscreen   = true;
        private double _windowedLeft   = 100, _windowedTop    = 100;
        private double _windowedWidth  = 1280, _windowedHeight = 800;

        // ---- ドラッグ状態 ----
        private bool   _isDragging    = false;
        private Point  _dragStart;
        private double _dragStartX;
        private double _dragStartY;

        // ---- 通知タイマー（操作エラー3秒表示） ----
        private DispatcherTimer _notifyTimer;

        // ---- F49/F50 フィルタパイプライン（モアレ軽減 / シャープ化） ----
        private int             _viewportPxW, _viewportPxH; // 表示領域サイズ（デバイスピクセル）
        private string          _filterSignature = "";       // フィルタ設定+表示条件の署名（変化でキャッシュ破棄）
        private DispatcherTimer _filterRefreshTimer;         // ウィンドウリサイズ時の再フィルタ用デバウンス

        // ---- 起動シーケンス制御 ----
        // ウィンドウ表示前はディスク/CPUを現在画像のデコードに集中させるため、
        // 先読みは Window_Loaded 後に開始する（多重起動時の遅延雪だるま対策）
        private bool _uiReady = false;
        // 現在ファイルのアニメ初回フレームを最優先にするための先読み保留フラグ（v0.8.2）
        private bool _prefetchDeferred = false;

        // ---- F51 シークバー ----
        private bool _seekBarShown = false;  // フェードイン済みか
        private bool _seekDragging = false;  // サムをドラッグ中か

        // ---- F52 アーカイブ閲覧位置の復元 ----
        private bool _resumeNotifyPending = false;  // 復元直後の通知予約

        // ---- マウスバインド用アクション辞書 ----
        private Dictionary<string, Action> _actionMap;

        // =========================================================
        // 起動診断ログ（起動遅延問題の調査用・2026/07/07〜）
        // %TEMP%\ClipViewer\startup.log に各起動の所要時間を記録する。
        // 原因特定後に削除してよい。
        // =========================================================

        private static readonly Stopwatch _startupSw = Stopwatch.StartNew();
        private bool _startupDisplayLogged = false;

        private static void StartupLog(string message)
        {
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "ClipViewer");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "startup.log");
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();  // 肥大防止
                File.AppendAllText(path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [+{_startupSw.ElapsedMilliseconds,6}ms] {message}\r\n");
            }
            catch { /* ログ失敗は本体動作を妨げない */ }
        }

        // =========================================================
        // コンストラクタ
        // =========================================================

        public MainWindow(string initialFilePath)
        {
            // プロセス生成→ctor 到達までの時間 = exe/DLLロード・セキュリティスキャン等の環境要因
            var proc = Process.GetCurrentProcess();
            StartupLog($"==== 起動 pid={proc.Id} | プロセス生成→ctor: {(DateTime.Now - proc.StartTime).TotalMilliseconds:F0}ms | 他インスタンス: {Process.GetProcessesByName("ClipViewer").Length - 1} | exe: {proc.MainModule?.FileName} ====");
            StartupLog($"引数: {initialFilePath ?? "(なし)"}");

            InitializeComponent();

            _settings = IniFileManager.Load();
            StartupLog($"ini 読み込み完了 (Mode={_settings.LastMode}, Info={_settings.LastInfoMode}, Moire={_settings.MoireFilterEnabled}, Sharpen={_settings.SharpenEnabled})");

            // [State] から前回の動作状態を復元
            _displayMode      = _settings.LastMode;
            _bindingDirection = _settings.LastBinding;
            _infoMode         = _settings.LastInfoMode;
            _firstSingle      = _settings.LastFirstSingle;
            _gifPlayMode      = _settings.LastGifPlayMode;
            _isFullscreen     = _settings.LastIsFullscreen;
            _windowedLeft     = _settings.LastWindowedLeft;
            _windowedTop      = _settings.LastWindowedTop;
            _windowedWidth    = _settings.LastWindowedWidth;
            _windowedHeight   = _settings.LastWindowedHeight;

            // 情報パネルの初期 Visibility を適用
            InfoPanel.Visibility = _infoMode != InfoDisplayMode.Off ? Visibility.Visible : Visibility.Collapsed;
            // パネル幅を右20%に設定
            InfoStackPanel.MaxWidth = ActualWidth * 0.20;

            BuildActionMap();
            LoadFileList(initialFilePath);
            StartupLog("ctor 完了");
        }

        // =========================================================
        // ファイルリスト構築
        // =========================================================

        private void LoadFileList(string initialFilePath)
        {
            if (string.IsNullOrEmpty(initialFilePath))
            {
                ShowError("使い方: ClipViewer.exe <ファイルパス>\n\nEsc で終了");
                return;
            }

            if (!File.Exists(initialFilePath))
            {
                ShowError($"ファイルが見つかりません:\n{initialFilePath}\n\nEsc で終了");
                return;
            }

            // アーカイブファイルは展開モードへ
            string initialExt = Path.GetExtension(initialFilePath).ToLowerInvariant();
            if (Array.IndexOf(_archiveExts, initialExt) >= 0)
            {
                LoadArchiveAsync(initialFilePath);
                return;
            }

            string directory = Path.GetDirectoryName(initialFilePath);

            // F18: .clip / .psd / .jpg / .jpeg / .png / .webp / .gif / .avif を対象とする
            var extensions = new[] { "*.clip", "*.psd", "*.jpg", "*.jpeg", "*.png", "*.webp", "*.gif", "*.avif" };
            var allFiles   = new List<string>();
            foreach (string ext in extensions)
                allFiles.AddRange(Directory.GetFiles(directory, ext));

            if (allFiles.Count == 0)
            {
                ShowError($"フォルダ内に表示可能なファイルが見つかりません:\n{directory}\n\nEsc で終了");
                return;
            }

            _clipFiles = allFiles;
            _clipFiles.Sort(NaturalSort.Comparer);
            StartupLog($"ファイル列挙完了: {_clipFiles.Count} 件 ({directory})");

            string normalizedInitial = Path.GetFullPath(initialFilePath);
            _currentIndex = _clipFiles.FindIndex(
                f => string.Equals(Path.GetFullPath(f), normalizedInitial, StringComparison.OrdinalIgnoreCase));

            if (_currentIndex < 0)
                _currentIndex = 0;

            SeekFirstValid();
        }

        // =========================================================
        // アーカイブ展開（ZIP/CBZ/RAR/LZH/7z）
        // =========================================================

        /// <summary>
        /// アーカイブファイルを一時ディレクトリへ非同期展開し、完了後に表示を開始する。
        /// 展開中は "展開中..." メッセージを表示する。
        /// </summary>
        private void LoadArchiveAsync(string archivePath)
        {
            ShowError($"アーカイブを展開中...\n{Path.GetFileName(archivePath)}");

            // 現在のアーカイブの表示位置を履歴に記録（F52、アーカイブ切替時）
            SaveArchivePosition();

            // 前回の一時ディレクトリを削除
            CleanupTempDir();

            // 今回の展開先を作成（一意なサブフォルダ名で衝突回避）
            string tempBase  = Path.Combine(Path.GetTempPath(), "ClipViewer");
            string uniqueId  = Guid.NewGuid().ToString("N").Substring(0, 8);
            string tempDir   = Path.Combine(tempBase,
                Path.GetFileNameWithoutExtension(archivePath) + "_" + uniqueId);
            Directory.CreateDirectory(tempDir);

            string archExt = Path.GetExtension(archivePath).ToLowerInvariant();
            bool   lazy    = (archExt == ".zip" || archExt == ".cbz");

            Task.Run(() =>
            {
                List<string> files = new List<string>();
                var entryMap = lazy ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : null;
                ZipArchive zip = null;
                string errorMsg = null;
                try
                {
                    if (lazy)
                    {
                        // 遅延展開（v0.8.4）: エントリ列挙のみで即表示を開始する。
                        // 実体化は EnsureExtracted（オンデマンド）と背景スイープに任せるため、
                        // 無圧縮ZIPの巨大アーカイブでも一瞬で開ける。
                        zip = ZipFile.OpenRead(archivePath);
                        foreach (var entry in zip.Entries)
                        {
                            if (string.IsNullOrEmpty(entry.Name)) continue;
                            if (!_archiveImageExts.Contains(
                                    Path.GetExtension(entry.Name).ToLowerInvariant())) continue;

                            string safePath = SanitizeArchivePath(entry.FullName);
                            if (string.IsNullOrEmpty(safePath)) continue;

                            string destPath = Path.Combine(tempDir, safePath);
                            if (entryMap.ContainsKey(destPath)) continue;  // サニタイズ衝突は先勝ち
                            entryMap[destPath] = entry.FullName;
                            files.Add(destPath);
                        }
                        files.Sort(NaturalSort.Comparer);
                    }
                    else
                    {
                        // RAR/LZH/7z: 7z.exe による一括展開（従来どおり）
                        files = ExtractImages(archivePath, tempDir);
                    }
                }
                catch (Exception ex)
                {
                    try { zip?.Dispose(); } catch { }
                    zip      = null;
                    files    = new List<string>();
                    errorMsg = ex.Message;
                    try { Directory.Delete(tempDir, recursive: true); } catch { }
                }

                Dispatcher.Invoke(() =>
                {
                    if (errorMsg != null)
                    {
                        ShowError($"アーカイブを展開できません:\n{errorMsg}\n\nEsc で終了");
                        return;
                    }
                    if (files.Count == 0)
                    {
                        try { zip?.Dispose(); } catch { }
                        ShowError($"アーカイブ内に画像が見つかりません:\n"
                                + $"{Path.GetFileName(archivePath)}\n\nEsc で終了");
                        try { Directory.Delete(tempDir, recursive: true); } catch { }
                        return;
                    }

                    _currentArchivePath = archivePath;
                    _currentTempDir     = tempDir;
                    _clipFiles          = files;
                    _currentIndex       = 0;
                    lock (_zipLock)
                    {
                        _lazyZip        = zip;       // null = 遅延なし（7z系）
                        _zipEntryByPath = entryMap;
                    }

                    // 履歴があれば前回の表示位置から再開（F52、機能ON時のみ）
                    string lastEntry = _settings.ArchiveHistoryEnabled
                        ? ArchiveHistory.Lookup(archivePath) : null;
                    if (lastEntry != null)
                    {
                        int resume = files.FindIndex(f => string.Equals(
                            GetTempRelativePath(f, tempDir), lastEntry,
                            StringComparison.OrdinalIgnoreCase));
                        if (resume > 0)
                        {
                            _currentIndex        = resume;
                            _resumeNotifyPending = true;
                        }
                    }

                    lock (_cacheLock)
                    {
                        _brokenFiles.Clear();
                        _imageCache.Clear();
                        _wideCache.Clear();
                        _srcSizeCache.Clear();
                        _gifFrameCache.Clear();
                        _gifDelayCache.Clear();
                        _gifAvailCache.Clear();
                        _knownAnimated.Clear();
                        _knownStatic.Clear();
                    }
                    ClearNavHistory();
                    NormalizeAnchor();
                    SeekFirstValid();

                    if (_resumeNotifyPending)
                    {
                        _resumeNotifyPending = false;
                        ShowNotification("前回の位置から再開（Home で先頭へ）", 1.5);
                    }

                    // 背景で残り全エントリを順次実体化（シークバー大ジャンプ・保存操作の先回り）
                    if (lazy) StartLazyExtractionSweep(files, zip);
                });
            });
        }

        // =========================================================
        // 遅延展開（v0.8.4）
        // =========================================================

        /// <summary>
        /// 遅延展開対象のパスなら、その場でZIPから実体化する（実体化済み・対象外なら何もしない）。
        /// ファイル読み取りを行う処理の直前に呼ぶこと。UI/背景どちらのスレッドからも呼び出し可能。
        /// 「.part に展開 → 完成後リネーム」により、File.Exists=true は常に完全なファイルを意味する。
        /// </summary>
        private void EnsureExtracted(string path)
        {
            var zip = _lazyZip;
            var map = _zipEntryByPath;
            if (zip == null || map == null) return;
            if (!map.TryGetValue(path, out string entryName)) return;  // 遅延対象外（通常ファイル等）
            if (File.Exists(path)) return;

            lock (_zipLock)
            {
                if (File.Exists(path)) return;
                if (!ReferenceEquals(_lazyZip, zip)) return;  // アーカイブ切替/終了済み → 中断

                var entry = zip.GetEntry(entryName);
                if (entry == null) return;

                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string part = path + ".part";
                entry.ExtractToFile(part, overwrite: true);
                File.Move(part, path);
            }
        }

        /// <summary>
        /// ファイル先頭 count バイトを読む。未展開の遅延ZIPエントリは実体化せず
        /// アーカイブから直接読む（WebPアニメ判定等のヘッダスニッフ用・低コスト）。
        /// 読めなければ null。
        /// </summary>
        private byte[] ReadFileHead(string path, int count)
        {
            try
            {
                if (File.Exists(path))
                {
                    var buf = new byte[count];
                    int n;
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                        n = fs.Read(buf, 0, count);
                    if (n < count) Array.Resize(ref buf, Math.Max(0, n));
                    return buf;
                }

                var zip = _lazyZip;
                var map = _zipEntryByPath;
                if (zip != null && map != null && map.TryGetValue(path, out string entryName))
                {
                    lock (_zipLock)
                    {
                        if (!ReferenceEquals(_lazyZip, zip)) return null;
                        var entry = zip.GetEntry(entryName);
                        if (entry == null) return null;
                        var buf = new byte[count];
                        int total = 0, n;
                        using (var s = entry.Open())
                            while (total < count && (n = s.Read(buf, total, count - total)) > 0) total += n;
                        if (total < count) Array.Resize(ref buf, total);
                        return buf;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 遅延展開の背景スイープ。先頭から順に全エントリを実体化しておく。
        /// アーカイブが切り替わったら参照不一致で自動中断する。
        /// </summary>
        private void StartLazyExtractionSweep(List<string> files, ZipArchive zip)
        {
            Task.Run(() =>
            {
                Thread.Sleep(1000);  // 初回表示・アニメデコードにIOを譲る
                foreach (string f in files)
                {
                    if (!ReferenceEquals(_lazyZip, zip)) return;
                    try { EnsureExtracted(f); } catch { }
                }
            });
        }

        /// <summary>アーカイブモード中なら現在の表示位置を履歴に記録する（F52）。機能OFF時は何もしない。</summary>
        private void SaveArchivePosition()
        {
            if (!_settings.ArchiveHistoryEnabled) return;
            if (_currentArchivePath == null) return;
            if (_currentIndex < 0 || _currentIndex >= _clipFiles.Count) return;
            ArchiveHistory.Record(_currentArchivePath,
                GetTempRelativePath(_clipFiles[_currentIndex], _currentTempDir),
                _settings.ArchiveHistoryCount);
        }

        // アーカイブ履歴機能 ON/OFF（キーは ini の ToggleArchiveHistory で割り当て。状態は終了時に保存）
        private void ToggleArchiveHistory()
        {
            _settings.ArchiveHistoryEnabled = !_settings.ArchiveHistoryEnabled;
            ShowNotification("アーカイブ履歴: " + (_settings.ArchiveHistoryEnabled ? "ON" : "OFF"), 1.0);
        }

        /// <summary>展開先一時ディレクトリからの相対パスを返す（履歴の照合キー）。</summary>
        private static string GetTempRelativePath(string fullPath, string tempDir)
        {
            if (string.IsNullOrEmpty(tempDir)) return Path.GetFileName(fullPath);
            string full    = Path.GetFullPath(fullPath);
            string baseDir = Path.GetFullPath(tempDir).TrimEnd('\\') + "\\";
            return full.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(baseDir.Length)
                : Path.GetFileName(fullPath);
        }

        /// <summary>
        /// アーカイブから画像ファイルのみを展開してパスリストを返す。
        /// ZIP/CBZ は System.IO.Compression を使用。
        /// RAR/LZH/7z は 7z.exe（7-Zip）を外部プロセスで呼び出す。
        /// バックグラウンドスレッドから呼び出し可能。
        /// </summary>
        private static List<string> ExtractImages(string archivePath, string destDir)
        {
            string ext = Path.GetExtension(archivePath).ToLowerInvariant();

            var imageExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".avif" };

            if (ext == ".zip" || ext == ".cbz")
            {
                // 組み込みライブラリで展開（追加依存なし）
                using (var zip = ZipFile.OpenRead(archivePath))
                {
                    foreach (var entry in zip.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;
                        if (!imageExts.Contains(
                                Path.GetExtension(entry.Name).ToLowerInvariant())) continue;

                        string safePath = SanitizeArchivePath(entry.FullName);
                        if (string.IsNullOrEmpty(safePath)) continue;

                        string destPath = Path.Combine(destDir, safePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(destPath));
                        entry.ExtractToFile(destPath, overwrite: true);
                    }
                }
            }
            else
            {
                // RAR / LZH / 7z: 7-Zip CLI で展開
                string sevenZip = Find7ZipExe();
                if (sevenZip == null)
                    throw new Exception(
                        "7-Zip が見つかりません。\n"
                      + "https://www.7-zip.org/ からインストールしてください。\n"
                      + "（.rar / .lzh / .7z の展開に必要です）");

                var psi = new ProcessStartInfo(sevenZip,
                    $"x \"{archivePath}\" -o\"{destDir}\" -y")
                {
                    WindowStyle     = ProcessWindowStyle.Hidden,
                    CreateNoWindow  = true,
                    UseShellExecute = false
                };
                using (var p = Process.Start(psi))
                    p.WaitForExit();
            }

            // 展開されたファイルから画像のみ収集
            var files = new List<string>();
            foreach (string f in Directory.GetFiles(destDir, "*.*", SearchOption.AllDirectories))
                if (imageExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    files.Add(f);

            files.Sort(NaturalSort.Comparer);
            return files;
        }

        /// <summary>
        /// 7z.exe のパスを検索して返す。見つからない場合は null。
        /// </summary>
        private static string Find7ZipExe()
        {
            // 一般的なインストール先を確認
            var candidates = new[]
            {
                @"C:\Program Files\7-Zip\7z.exe",
                @"C:\Program Files (x86)\7-Zip\7z.exe",
            };
            foreach (string c in candidates)
                if (File.Exists(c)) return c;

            // PATH 環境変数も確認
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in pathEnv.Split(';'))
            {
                try
                {
                    string full = Path.Combine(dir.Trim(), "7z.exe");
                    if (File.Exists(full)) return full;
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// アーカイブエントリのパスを OS の安全なパスに変換する。
        /// パストラバーサル（../）除去・無効文字をアンダースコアに置換。
        /// </summary>
        private static string SanitizeArchivePath(string entryPath)
        {
            if (string.IsNullOrEmpty(entryPath)) return string.Empty;

            entryPath = entryPath.Replace('/', Path.DirectorySeparatorChar)
                                 .Replace('\\', Path.DirectorySeparatorChar);

            char[]     invalid   = Path.GetInvalidFileNameChars();
            var        safeParts = new List<string>();

            foreach (string part in entryPath.Split(Path.DirectorySeparatorChar))
            {
                if (part.Length == 0 || part == ".." || part == ".") continue;
                var sb = new StringBuilder(part.Length);
                foreach (char c in part)
                    sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
                safeParts.Add(sb.ToString());
            }

            return string.Join(Path.DirectorySeparatorChar.ToString(), safeParts);
        }

        /// <summary>現在の一時展開ディレクトリを削除してフィールドをクリアする。</summary>
        /// <summary>
        /// %TEMP%\ClipViewer 配下の古い展開フォルダを削除する（強制終了等の残骸対策、v0.8.4）。
        /// 24時間以内のフォルダと自プロセスの現行フォルダは残す（多重起動中の他インスタンスを保護）。
        /// startup.log などフォルダ直下のファイルには触れない。
        /// </summary>
        private void CleanupStaleTempDirs()
        {
            try
            {
                string baseDir = Path.Combine(Path.GetTempPath(), "ClipViewer");
                if (!Directory.Exists(baseDir)) return;
                foreach (string d in Directory.GetDirectories(baseDir))
                {
                    try
                    {
                        if (string.Equals(d, _currentTempDir, StringComparison.OrdinalIgnoreCase)) continue;
                        if (Directory.GetLastWriteTimeUtc(d) > DateTime.UtcNow.AddHours(-24)) continue;
                        Directory.Delete(d, recursive: true);
                    }
                    catch { /* 使用中フォルダ等は無視 */ }
                }
            }
            catch { }
        }

        private void CleanupTempDir()
        {
            // 遅延展開中のZIPを閉じる（オンデマンド展開・背景スイープは参照不一致で自動中断する）
            lock (_zipLock)
            {
                try { _lazyZip?.Dispose(); } catch { }
                _lazyZip        = null;
                _zipEntryByPath = null;
            }

            if (_currentTempDir != null && Directory.Exists(_currentTempDir))
            {
                try { Directory.Delete(_currentTempDir, recursive: true); }
                catch { /* ロック中は無視 */ }
            }
            _currentTempDir = null;
        }

        private void SeekFirstValid()
        {
            int start = _currentIndex;
            int count = _clipFiles.Count;

            for (int i = 0; i < count; i++)
            {
                int idx = (start + i) % count;
                if (LoadImage(idx) != null)
                {
                    _currentIndex = idx;
                    ClearNavHistory();
                    NormalizeAnchor();
                    DisplayCurrent();
                    return;
                }
            }

            ShowError("表示できるファイルがありません。\n\nEsc で終了");
        }

        // =========================================================
        // 画像表示
        // =========================================================

        private void DisplayCurrent()
        {
            if (_clipFiles.Count == 0) return;

            ErrorText.Visibility = Visibility.Collapsed;

            StopGifAnimation();

            // フィルタ設定・表示条件が変わっていたらキャッシュを作り直す（F49/F50）
            string sig = BuildFilterSignature();
            if (sig != _filterSignature)
            {
                _filterSignature = sig;
                lock (_cacheLock) { _imageCache.Clear(); }
            }

            EvictCache(_currentIndex);

            // SpreadStepSize がすべての単独表示条件を包括する:
            //   単ページモード / firstSingle+先頭 / 最終ページ / 横長 / 次が横長
            if (_displayMode == DisplayMode.Single || SpreadStepSize(_currentIndex) == 1)
                DisplaySingle();
            else
                DisplaySpread();

            UpdateInfoPanel();

            // 先読み開始（起動中はウィンドウ表示優先で Loaded 後に開始）。
            // 現在ファイルのアニメ初回フレームをデコード中の場合は、
            // CPU/ディスクをそちらに集中させるため確定まで先読みを保留する（v0.8.2）。
            if (_uiReady) StartPrefetchSmart();

            if (_seekBarShown) UpdateSeekBarVisual(_currentIndex, showLabel: false);

            if (!_startupDisplayLogged)
            {
                _startupDisplayLogged = true;
                StartupLog($"初回表示完了 idx={_currentIndex}");
            }
        }

        // ---- 単ページ表示 ----
        private void DisplaySingle()
        {
            SpreadGrid.Visibility  = Visibility.Collapsed;
            SingleImage.Visibility = Visibility.Visible;
            SingleImage.Source     = LoadImage(_currentIndex); // まず最初のフレームを静止表示

            // アニメーション処理（v0.8.2 プログレッシブ化）:
            // フレームは背景スレッドでデコードし、先頭フレームが用意でき次第
            // OnAnimFramesReady 経由で再生を開始する（UIをブロックしない）。
            if (_currentIndex >= 0 && _currentIndex < _clipFiles.Count)
            {
                string ext = Path.GetExtension(_clipFiles[_currentIndex]).ToLowerInvariant();
                if (ext == ".gif" || ext == ".webp")
                {
                    if (IsAnimatedGif(_currentIndex))
                        StartGifAnimation(_currentIndex, SingleImage);  // デコード済み/進行中 → 即再生
                    else
                        EnsureAnimFrames(_currentIndex, _clipFiles[_currentIndex], ext);
                }
            }
        }

        // ---- 見開き表示 ----
        // DisplayCurrent から SpreadStepSize==2 のときのみ呼ばれる。
        // 単独表示への分岐は DisplayCurrent 側で完結しているため、ここでは常に2ページ表示する。
        private void DisplaySpread()
        {
            SingleImage.Visibility = Visibility.Collapsed;
            SpreadGrid.Visibility  = Visibility.Visible;

            int leftIdx, rightIdx;
            ResolveSpreadIndices(out leftIdx, out rightIdx);

            LeftImage.Source  = (leftIdx  >= 0) ? LoadImage(leftIdx)  : null;
            RightImage.Source = (rightIdx >= 0) ? LoadImage(rightIdx) : null;

            // 未分類の .gif / .webp があれば背景でアニメ判定し、確定次第 DisplayCurrent を再呼び出し
            TriggerAnimationCheckForSpread();
        }

        /// <summary>
        /// 見開き表示時に .gif / .webp ファイルの「アニメか否か」を背景スレッドで判定する。
        /// 判定済み（_knownAnimated / _knownStatic）のファイルはスキップ。
        /// アニメと判明した場合は Dispatcher 経由で DisplayCurrent() を再呼び出しして単独表示に切替える。
        /// </summary>
        private void TriggerAnimationCheckForSpread()
        {
            int idx  = _currentIndex;
            int next = (idx + 1 < _clipFiles.Count) ? idx + 1 : -1;

            var toCheck = new List<int>();
            lock (_cacheLock)
            {
                if (IsAnimExtFile(idx) && !_knownAnimated.Contains(idx) && !_knownStatic.Contains(idx))
                    toCheck.Add(idx);
                if (next >= 0 && IsAnimExtFile(next) && !_knownAnimated.Contains(next) && !_knownStatic.Contains(next))
                    toCheck.Add(next);
            }

            if (toCheck.Count == 0) return;

            List<string> files = _clipFiles;
            Task.Run(() =>
            {
                bool needRedraw = false;
                foreach (int i in toCheck)
                {
                    bool animated = DetectAnimation(i, files);
                    lock (_cacheLock)
                    {
                        if (animated)
                        {
                            if (!_knownAnimated.Contains(i)) { _knownAnimated.Add(i); needRedraw = true; }
                        }
                        else
                        {
                            _knownStatic.Add(i);
                        }
                    }
                }
                if (needRedraw)
                {
                    Dispatcher.Invoke(() =>
                    {
                        // ファイルリストが変わっていなければ再描画（単独表示へ自動切替）
                        if (ReferenceEquals(_clipFiles, files))
                            DisplayCurrent();
                    });
                }
            });
        }

        /// <summary>
        /// 指定インデックスのファイルが「アニメーション画像（2フレーム以上）」かどうかを判定する。
        /// ピクセルデータはデコードせずフレーム数のみチェック（軽量）。
        /// バックグラウンドスレッドから呼び出し可能。
        /// </summary>
        private bool DetectAnimation(int idx, List<string> files)
        {
            string path = files[idx];
            string ext  = Path.GetExtension(path).ToLowerInvariant();
            try
            {
                EnsureExtracted(path);  // 遅延展開ZIPのエントリなら実体化（v0.8.4）

                if (ext == ".gif")
                {
                    // GifBitmapDecoder はメタデータ読み取りのみ（ピクセルデコードなし）で軽量
                    using (var fs = File.OpenRead(path))
                    {
                        var dec = new GifBitmapDecoder(fs,
                            BitmapCreateOptions.DelayCreation,
                            BitmapCacheOption.None);
                        return dec.Frames.Count > 1;
                    }
                }
                else if (ext == ".webp")
                {
                    // WebPAnimDecoderGetInfo のみ呼び出し（GetNext は呼ばない = フレームデコードなし）
                    byte[]   data = File.ReadAllBytes(path);
                    GCHandle pin  = GCHandle.Alloc(data, GCHandleType.Pinned);
                    try
                    {
                        var webpData = new WebPData
                        {
                            bytes = pin.AddrOfPinnedObject(),
                            size  = (UIntPtr)data.Length
                        };
                        var opts = new WebPAnimDecoderOptions { color_mode = 3, use_threads = 0 };
                        IntPtr dec = WebPAnimDecoderNew(ref webpData, ref opts, WebPDemuxAbiVersion);
                        if (dec == IntPtr.Zero) return false;
                        try
                        {
                            return WebPAnimDecoderGetInfo(dec, out WebPAnimInfo info) != 0
                                && info.frame_count > 1;
                        }
                        finally { WebPAnimDecoderDelete(dec); }
                    }
                    finally { pin.Free(); }
                }
            }
            catch { }
            return false;
        }

        /// <summary>指定インデックスのファイルが .gif または .webp 拡張子かどうかを返す（高速）。</summary>
        private bool IsAnimExtFile(int idx)
        {
            if (idx < 0 || idx >= _clipFiles.Count) return false;
            string ext = Path.GetExtension(_clipFiles[idx]).ToLowerInvariant();
            return ext == ".gif" || ext == ".webp";
        }

        /// <summary>
        /// 見開き時の左右インデックスを解決する。
        /// _currentIndex を起点に (_currentIndex, _currentIndex+1) をペアとする。
        /// </summary>
        private void ResolveSpreadIndices(out int leftIdx, out int rightIdx)
        {
            int count     = _clipFiles.Count;
            int secondIdx = (_currentIndex + 1 < count) ? _currentIndex + 1 : -1;

            if (_bindingDirection == BindingDirection.Right)
            {
                rightIdx = _currentIndex;
                leftIdx  = secondIdx;
            }
            else
            {
                leftIdx  = _currentIndex;
                rightIdx = secondIdx;
            }
        }

        // =========================================================
        // 情報パネル更新（F13/F14）
        // =========================================================

        private void UpdateInfoPanel()
        {
            if (_infoMode == InfoDisplayMode.Off || _clipFiles.Count == 0)
                return;

            // SpreadStepSize がすべての単独表示条件（firstSingle+先頭 / 最終 / 横長）を包括
            bool isSingleDisplay =
                (_displayMode == DisplayMode.Single) ||
                (_displayMode == DisplayMode.Spread && SpreadStepSize(_currentIndex) == 1);

            string displayFileName;
            string pageNum;
            int    detailFileIdx;

            if (isSingleDisplay)
            {
                displayFileName = Path.GetFileName(_clipFiles[_currentIndex]);
                pageNum         = $"{_currentIndex + 1} / {_clipFiles.Count}";
                detailFileIdx   = _currentIndex;
            }
            else
            {
                // F17: 見開き時は右ページのファイル名を表示
                int leftIdx, rightIdx;
                ResolveSpreadIndices(out leftIdx, out rightIdx);
                int nameIdx = (rightIdx >= 0) ? rightIdx : leftIdx;
                displayFileName = (nameIdx >= 0)
                    ? Path.GetFileName(_clipFiles[nameIdx])
                    : Path.GetFileName(_clipFiles[_currentIndex]);
                pageNum       = BuildPageNumberText();
                detailFileIdx = (nameIdx >= 0) ? nameIdx : _currentIndex;
            }

            InfoFileName.Text   = displayFileName;
            InfoPageNumber.Text = pageNum;
            InfoZoom.Text       = BuildZoomText(detailFileIdx);

            bool detailed = (_infoMode == InfoDisplayMode.Detailed);
            var detailVis = detailed ? Visibility.Visible : Visibility.Collapsed;
            InfoPixelSize.Visibility = detailVis;
            InfoFullPath.Visibility  = detailVis;
            InfoExif.Visibility      = detailVis;

            if (detailed)
                UpdateDetailedInfo(detailFileIdx);
        }

        /// <summary>
        /// 見開き表示時のページ番号テキストを返す。
        /// 例: "3 - 4 / 20"
        /// </summary>
        private string BuildPageNumberText()
        {
            int total = _clipFiles.Count;
            int leftIdx, rightIdx;
            ResolveSpreadIndices(out leftIdx, out rightIdx);

            int pageA = (leftIdx  >= 0) ? leftIdx  + 1 : -1;
            int pageB = (rightIdx >= 0) ? rightIdx + 1 : -1;

            if (pageA >= 0 && pageB >= 0)
            {
                int lo = Math.Min(pageA, pageB);
                int hi = Math.Max(pageA, pageB);
                return $"{lo} - {hi} / {total}";
            }
            else if (pageA >= 0) return $"{pageA} / {total}";
            else if (pageB >= 0) return $"{pageB} / {total}";
            else                 return $"? / {total}";
        }

        /// <summary>
        /// 明示的な左右インデックスで情報パネルを更新する。
        /// SingleStep 表示時に使用。
        /// </summary>
        private void UpdateInfoPanelWithIndices(int leftIdx, int rightIdx)
        {
            if (_infoMode == InfoDisplayMode.Off || _clipFiles.Count == 0) return;

            int total = _clipFiles.Count;

            int nameIdx = (rightIdx >= 0) ? rightIdx
                        : (leftIdx  >= 0) ? leftIdx
                        : _currentIndex;
            InfoFileName.Text = Path.GetFileName(_clipFiles[nameIdx]);

            int pageA = leftIdx  >= 0 ? leftIdx  + 1 : -1;
            int pageB = rightIdx >= 0 ? rightIdx + 1 : -1;

            if (pageA >= 0 && pageB >= 0)
                InfoPageNumber.Text = $"{Math.Min(pageA, pageB)} - {Math.Max(pageA, pageB)} / {total}";
            else if (pageB >= 0)
                InfoPageNumber.Text = $"{pageB} / {total}";
            else if (pageA >= 0)
                InfoPageNumber.Text = $"{pageA} / {total}";
            else
                InfoPageNumber.Text = $"? / {total}";

            InfoZoom.Text = BuildZoomText(nameIdx);

            bool detailed = (_infoMode == InfoDisplayMode.Detailed);
            var detailVis = detailed ? Visibility.Visible : Visibility.Collapsed;
            InfoPixelSize.Visibility = detailVis;
            InfoFullPath.Visibility  = detailVis;
            InfoExif.Visibility      = detailVis;

            if (detailed)
                UpdateDetailedInfo(nameIdx >= 0 ? nameIdx : _currentIndex);
        }

        /// <summary>Detailed モード時の追加情報（ピクセルサイズ・パス・EXIF）を更新する。</summary>
        private void UpdateDetailedInfo(int fileIdx)
        {
            string path = _clipFiles[fileIdx];

            // ピクセルサイズ：表示中のソースから取得（フィルタ適用時は 原寸 → 表示サイズ を併記）
            BitmapSource src = (SingleImage.Visibility == Visibility.Visible)
                ? SingleImage.Source as BitmapSource
                : (RightImage.Source ?? LeftImage.Source) as BitmapSource;
            if (src == null)
            {
                InfoPixelSize.Text = "";
            }
            else
            {
                int[] orig;
                lock (_cacheLock) { _srcSizeCache.TryGetValue(fileIdx, out orig); }
                InfoPixelSize.Text =
                    (orig != null && (orig[0] != src.PixelWidth || orig[1] != src.PixelHeight))
                    ? $"{orig[0]} × {orig[1]} px → {src.PixelWidth} × {src.PixelHeight} px (フィルタ適用)"
                    : $"{src.PixelWidth} × {src.PixelHeight} px";
            }

            // フルパス
            InfoFullPath.Text = path;

            // EXIF
            InfoExif.Text = ReadExifSummary(path);
        }

        /// <summary>
        /// JPEG / PNG 等から EXIF メタデータを簡易取得する。
        ///
        /// 🚨 各項目は必ず切り詰めること: AI生成画像（Stable Diffusion / ComfyUI 等）は
        /// メタデータに数百KB級のプロンプト/ワークフローJSONを埋め込むことがあり、
        /// 長文をそのまま TextBlock(TextWrapping=Wrap) に流すとレイアウト処理で
        /// UIスレッドが数十秒フリーズする（起動遅延問題の根本原因だった・2026/07/10判明）。
        /// </summary>
        private string ReadExifSummary(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".clip" || ext == ".psd") return "";

            try
            {
                EnsureExtracted(path);  // 遅延展開ZIPのエントリなら実体化（v0.8.4）

                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var decoder = BitmapDecoder.Create(
                        stream,
                        BitmapCreateOptions.None,
                        BitmapCacheOption.None);
                    if (decoder.Frames.Count == 0) return "";
                    var meta = decoder.Frames[0].Metadata as BitmapMetadata;
                    if (meta == null) return "";

                    var sb = new StringBuilder();
                    try { if (!string.IsNullOrEmpty(meta.DateTaken))   sb.Append($"撮影日: {Truncate(meta.DateTaken, 40)}  "); } catch { }
                    try { if (!string.IsNullOrEmpty(meta.CameraModel)) sb.Append($"機種: {Truncate(meta.CameraModel, 60)}  "); } catch { }
                    // AI生成画像のプロンプト/ワークフローは Title（EXIF ImageDescription）に
                    // 入っていることが多い（ComfyUI 等）。Comment と同枠の 800 字まで表示する。
                    try { if (!string.IsNullOrEmpty(meta.Title))       sb.Append($"タイトル: {Truncate(meta.Title, 800)}  "); } catch { }
                    try { if (!string.IsNullOrEmpty(meta.Comment))     sb.Append($"コメント: {Truncate(meta.Comment, 800)}  "); } catch { }
                    string result = sb.ToString().TrimEnd();
                    return result.Length > 0 ? Truncate(result, 1000) : "(EXIF なし)";
                }
            }
            catch { return ""; }
        }

        /// <summary>表示用文字列を最大 max 文字に切り詰める（超過分は「…」）。</summary>
        private static string Truncate(string s, int max)
            => (s != null && s.Length > max) ? s.Substring(0, max) + "…" : s;

        /// <summary>
        /// ズーム率の表示文字列を作る（v0.8.0改良）。
        /// % は「オリジナル画像の原寸 = 100%」とした実表示倍率。
        /// Fit 時は "Fit (63%)" のように縮小倍率を併記する。
        /// 原寸が未取得（起動直後・読込失敗）のときは従来の Fit 基準表記にフォールバック。
        /// </summary>
        private string BuildZoomText(int fileIdx)
        {
            // ビューポート未確定（起動直後）では正しい倍率を計算できないためフォールバック
            double pct = 0;
            if (fileIdx >= 0 && _viewportPxW > 0 && _viewportPxH > 0)
            {
                int[] src = null;
                lock (_cacheLock) { _srcSizeCache.TryGetValue(fileIdx, out src); }
                if (src != null && src[0] > 0 && src[1] > 0)
                    pct = ComputeFitScale(src[0], src[1]) * _zoomFactor * 100.0;
            }
            if (pct <= 0)
                return _zoomFactor == 1.0 ? "Fit" : $"{_zoomFactor * 100:F0}%";
            return _zoomFactor == 1.0 ? $"Fit ({pct:F0}%)" : $"{pct:F0}%";
        }

        /// <summary>情報パネルの表示対象インデックス（見開き時は右ページ優先＝ファイル名表示と同じ対象）。</summary>
        private int GetInfoTargetIndex()
        {
            if (_clipFiles.Count == 0 || _currentIndex < 0) return -1;
            if (SingleImage.Visibility == Visibility.Visible) return _currentIndex;
            ResolveSpreadIndices(out int leftIdx, out int rightIdx);
            return rightIdx >= 0 ? rightIdx : (leftIdx >= 0 ? leftIdx : _currentIndex);
        }

        /// <summary>
        /// ホイール1ステップが「Fit（倍率1.0）」または「原寸100%相当の倍率」をまたぐ場合、
        /// 通り過ぎずにその点へ吸着させる（v0.8.0改良）。
        /// 既にスナップ点上にいるときは通過扱いにしない（次のステップで離脱できる）。
        /// 両方をまたぐ場合はスクロール方向で先に出会う方（現在値に近い方）へ吸着する。
        /// </summary>
        private double SnapZoom(double oldFactor, double newFactor)
        {
            var snaps = new List<double> { 1.0 };  // Fit

            // 原寸100% に相当するズーム倍率（= 1 / Fit縮小率）
            int idx = GetInfoTargetIndex();
            if (idx >= 0 && _viewportPxW > 0 && _viewportPxH > 0)
            {
                int[] src = null;
                lock (_cacheLock) { _srcSizeCache.TryGetValue(idx, out src); }
                if (src != null && src[0] > 0 && src[1] > 0)
                {
                    double fit = ComputeFitScale(src[0], src[1]);
                    if (fit > 0) snaps.Add(1.0 / fit);
                }
            }

            double lo = Math.Min(oldFactor, newFactor);
            double hi = Math.Max(oldFactor, newFactor);
            double best = newFactor, bestDist = double.MaxValue;
            foreach (double s in snaps)
            {
                if (Math.Abs(oldFactor - s) < 0.0001) continue;  // スナップ点上からの離脱を許可
                if (s > lo && s < hi)
                {
                    double d = Math.Abs(s - oldFactor);
                    if (d < bestDist) { bestDist = d; best = s; }
                }
            }
            return best;
        }

        // =========================================================
        // 画像ロード（キャッシュ付き）F18: 拡張子で分岐
        // =========================================================

        private BitmapSource LoadImage(int index)
        {
            if (index < 0 || index >= _clipFiles.Count) return null;

            lock (_cacheLock)
            {
                if (_brokenFiles.Contains(index)) return null;
                if (_imageCache.TryGetValue(index, out BitmapSource cached)) return cached;
            }

            BitmapSource bmp = LoadImageCore(index, _clipFiles);
            if (bmp != null)
            {
                lock (_cacheLock)
                {
                    _imageCache[index] = bmp;
                    _wideCache[index]  = bmp.PixelWidth > bmp.PixelHeight;
                }
            }
            return bmp;
        }

        /// <summary>
        /// ファイルから画像を読み込んで Freeze 済み BitmapSource を返す。
        /// フィルタ有効時はモアレ軽減 / シャープ化パイプライン（F49/F50）を適用する。
        /// UI スレッド / バックグラウンドスレッドどちらからも呼び出し可能。
        /// </summary>
        private BitmapSource LoadImageCore(int index, List<string> files)
        {
            string path = files[index];
            string ext  = Path.GetExtension(path).ToLowerInvariant();

            try
            {
                EnsureExtracted(path);  // 遅延展開ZIPのエントリなら実体化（v0.8.4）

                byte[] data = null;
                if (ext == ".clip" || ext == ".psd")
                {
                    data = (ext == ".clip")
                        ? ClipFileReader.ExtractPreviewImage(path)
                        : PsdFileReader.ExtractPreviewImage(path);

                    if (data == null || data.Length == 0)
                    {
                        lock (_cacheLock) { _brokenFiles.Add(index); }
                        return null;
                    }
                }

                return DecodeAndFilter(index, path, data);
            }
            catch
            {
                lock (_cacheLock) { _brokenFiles.Add(index); }
                return null;
            }
        }

        // =========================================================
        // フィルタパイプライン（F49/F50: モアレ軽減 / シャープ化）
        // =========================================================

        /// <summary>
        /// 画像をデコードし、ダウンスケール条件を満たせばフィルタパイプラインを適用する。
        /// data が null ならファイルパスから、非 null ならメモリ上の PNG データからデコードする。
        /// バックグラウンドスレッドから呼び出し可能。
        /// </summary>
        private BitmapSource DecodeAndFilter(int index, string path, byte[] data)
        {
            // 1) ヘッダのみ読んでソース原寸を取得（フルデコードはしない）
            int srcW = 0, srcH = 0;
            try
            {
                if (data != null)
                {
                    using (var ms = new MemoryStream(data, writable: false))
                    {
                        var dec = BitmapDecoder.Create(ms, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                        srcW = dec.Frames[0].PixelWidth;
                        srcH = dec.Frames[0].PixelHeight;
                    }
                }
                else
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                        srcW = dec.Frames[0].PixelWidth;
                        srcH = dec.Frames[0].PixelHeight;
                    }
                }
            }
            catch { /* 寸法取得に失敗した場合はフィルタなしの通常デコードにフォールバック */ }

            // 2) ゲーティング判定（表示スケール < 閾値 のダウンスケール時のみ適用）
            // アニメWebPの静止フレームはフィルタ対象外（直後にアニメ再生で置き換わるため無駄。
            // ヘッダ判定の副作用として _knownAnimated/_knownStatic も即時確定する。v0.8.2）
            bool animatedWebP = (data == null) && EnsureWebPAnimKnown(index, path);
            FilterParams p = SnapshotFilterParams();
            bool applyFilter = false;
            int  tw = 0, th = 0;
            if (!animatedWebP && p.AnyEnabled && srcW > 0 && srcH > 0 && _viewportPxW > 0 && _viewportPxH > 0)
            {
                double fit = ComputeFitScale(srcW, srcH);
                if (fit < _settings.MoireDownscaleThreshold && fit < 1.0)
                {
                    tw = Math.Max(1, (int)Math.Round(srcW * fit));
                    th = Math.Max(1, (int)Math.Round(srcH * fit));
                    applyFilter = true;
                }
            }

            // 3) デコード。フィルタ適用時は目標幅の2倍までコーデック側で事前縮小
            //    （WIC の高品質縮小 = 2段パイプラインの粗縮小ステージ。メモリ・速度とも大幅改善）
            BitmapImage bmp = new BitmapImage();
            if (data != null)
            {
                using (var ms = new MemoryStream(data, writable: false))
                {
                    bmp.BeginInit();
                    bmp.StreamSource = ms;
                    bmp.CacheOption  = BitmapCacheOption.OnLoad;
                    if (applyFilter && (long)tw * 2 < srcW) bmp.DecodePixelWidth = tw * 2;
                    bmp.EndInit();
                    bmp.Freeze();
                }
            }
            else
            {
                bmp.BeginInit();
                bmp.UriSource   = new Uri(path, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                if (applyFilter && (long)tw * 2 < srcW) bmp.DecodePixelWidth = tw * 2;
                bmp.EndInit();
                bmp.Freeze();
            }

            // 4) ソース原寸を記録（Detailed 情報表示用）
            lock (_cacheLock)
            {
                _srcSizeCache[index] = new[]
                {
                    srcW > 0 ? srcW : bmp.PixelWidth,
                    srcH > 0 ? srcH : bmp.PixelHeight
                };
            }

            if (!applyFilter) return bmp;

            // 5) CPU パイプライン適用（Stage1: Lanczos-3 / Stage2: アンシャープマスク）
            return ImageFilters.ApplyPipeline(bmp, tw, th, p);
        }

        /// <summary>現在の AppSettings からフィルタパラメータのスナップショットを作る。</summary>
        private FilterParams SnapshotFilterParams()
        {
            AppSettings s = _settings;
            return new FilterParams
            {
                MoireEnabled     = s.MoireFilterEnabled && s.MoireFilterMode != MoireFilterAlgorithm.Off,
                MoireMode        = s.MoireFilterMode,
                MoireStrength    = s.MoireFilterStrength,
                SharpenEnabled   = s.SharpenEnabled,
                SharpenRadius    = s.SharpenRadius,
                SharpenAmount    = s.SharpenAmount,
                SharpenThreshold = s.SharpenThreshold,
            };
        }

        /// <summary>
        /// ソース原寸に対する Fit 表示時の縮小率を返す。
        /// 見開きモードでは縦長画像は半幅領域、横長画像（単独表示になる）は全幅領域で計算する。
        /// </summary>
        private double ComputeFitScale(int srcW, int srcH)
        {
            int vw = _viewportPxW, vh = _viewportPxH;
            if (vw <= 0 || vh <= 0) return 1.0;
            bool half = (_displayMode == DisplayMode.Spread) && (srcW <= srcH);
            double areaW = half ? vw / 2.0 : vw;
            return Math.Min(areaW / srcW, vh / (double)srcH);
        }

        /// <summary>フィルタ設定＋表示条件の署名。変化したらキャッシュを破棄して再フィルタする。</summary>
        private string BuildFilterSignature()
        {
            FilterParams p = SnapshotFilterParams();
            // フィルタ全OFF時は表示条件に依存しない固定署名にする。
            // ウィンドウ表示・リサイズのたびに無駄なキャッシュ破棄＝現在画像のフルデコードが
            // 走るのを防ぐ（起動遅延の一因だった）。
            if (!p.AnyEnabled) return "off";
            return $"{p.Signature}|{_settings.MoireDownscaleThreshold}|{_viewportPxW}x{_viewportPxH}|{_displayMode}";
        }

        /// <summary>表示領域サイズ（デバイスピクセル）を更新する。UI スレッドから呼ぶこと。</summary>
        private void UpdateViewportSize()
        {
            double dpiX = 1.0, dpiY = 1.0;
            var ps = PresentationSource.FromVisual(this);
            if (ps?.CompositionTarget != null)
            {
                dpiX = ps.CompositionTarget.TransformToDevice.M11;
                dpiY = ps.CompositionTarget.TransformToDevice.M22;
            }
            _viewportPxW = (int)Math.Round(ActualWidth  * dpiX);
            _viewportPxH = (int)Math.Round(ActualHeight * dpiY);
        }

        /// <summary>フィルタ署名が変わっていれば現在ページを再表示する（キャッシュは DisplayCurrent 冒頭で破棄）。</summary>
        private void RefreshFilterIfNeeded()
        {
            if (_clipFiles.Count == 0 || _currentIndex < 0) return;
            if (BuildFilterSignature() != _filterSignature)
                DisplayCurrent();
            // ビューポート寸法に依存する表示（原寸基準ズーム% 等）を確定値で再計算
            UpdateInfoPanel();
        }

        /// <summary>ウィンドウリサイズ連続発火をデバウンスして再フィルタを予約する。</summary>
        private void ScheduleFilterRefresh()
        {
            if (_filterRefreshTimer == null)
            {
                _filterRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                _filterRefreshTimer.Tick += (s, e) =>
                {
                    _filterRefreshTimer.Stop();
                    RefreshFilterIfNeeded();
                };
            }
            _filterRefreshTimer.Stop();
            _filterRefreshTimer.Start();
        }

        // F9: モアレ軽減フィルタ ON/OFF（状態は終了時に ini へ保存）
        private void ToggleMoireFilter()
        {
            _settings.MoireFilterEnabled = !_settings.MoireFilterEnabled;
            ShowNotification("モアレ軽減フィルタ: " + (_settings.MoireFilterEnabled ? "ON" : "OFF"), 1.0);
            DisplayCurrent();  // 署名変化でキャッシュが再構築される
        }

        // F11: シャープ化フィルタ ON/OFF（状態は終了時に ini へ保存）
        private void ToggleSharpen()
        {
            _settings.SharpenEnabled = !_settings.SharpenEnabled;
            ShowNotification("シャープ化フィルタ: " + (_settings.SharpenEnabled ? "ON" : "OFF"), 1.0);
            DisplayCurrent();
        }

        /// <summary>スライディングウィンドウ外の画像キャッシュを削除する。</summary>
        private void EvictCache(int anchor)
        {
            lock (_cacheLock)
            {
                var toRemove = new List<int>();
                foreach (int key in _imageCache.Keys)
                {
                    if (key < anchor - PrefetchBehind || key > anchor + PrefetchAhead)
                        toRemove.Add(key);
                }
                foreach (int key in toRemove)
                {
                    _imageCache.Remove(key);
                    _gifFrameCache.Remove(key);  // GIF フレームキャッシュも同期 evict
                    _gifDelayCache.Remove(key);
                    _gifAvailCache.Remove(key);  // 背景デコード中なら CommitAnimFrame の参照チェックで自然に中断される
                }
                // _wideCache はサイズが小さいため evict しない（ファイルリスト変更時のみクリア）
            }
        }

        /// <summary>
        /// 前後方向に非同期先読みしてキャッシュに格納する。
        /// 新たに呼ばれるたびに前回のタスクをキャンセルする。
        /// </summary>
        private void StartPrefetch(int anchor)
        {
            _prefetchCts?.Cancel();
            _prefetchCts = new CancellationTokenSource();
            CancellationToken token = _prefetchCts.Token;
            List<string>      files = _clipFiles;

            Task.Run(() =>
            {
                // 前方先読み（優先）
                for (int i = 1; i <= PrefetchAhead; i++)
                {
                    if (token.IsCancellationRequested) return;
                    int idx = anchor + i;
                    if (idx >= files.Count) break;
                    PrefetchOne(idx, files, token);
                }
                // 後方先読み
                for (int i = 1; i <= PrefetchBehind; i++)
                {
                    if (token.IsCancellationRequested) return;
                    int idx = anchor - i;
                    if (idx < 0) break;
                    PrefetchOne(idx, files, token);
                }
            }, token);
        }

        private void PrefetchOne(int idx, List<string> files, CancellationToken token)
        {
            bool skip;
            lock (_cacheLock)
                skip = _brokenFiles.Contains(idx) || _imageCache.ContainsKey(idx);
            if (skip) return;

            BitmapSource bmp = LoadImageCore(idx, files);
            if (bmp != null && !token.IsCancellationRequested)
            {
                lock (_cacheLock)
                {
                    if (ReferenceEquals(_clipFiles, files))
                    {
                        _imageCache[idx] = bmp;
                        _wideCache[idx]  = bmp.PixelWidth > bmp.PixelHeight;
                    }
                }
            }
        }

        // =========================================================
        // アスペクト比・先読みルール
        // =========================================================

        /// <summary>
        /// 指定インデックスの画像が横長（幅 > 高さ）かどうかを返す。
        /// _wideCache にヒットすれば即時返却（画像ロード不要）。
        /// </summary>
        private bool IsWideImage(int idx)
        {
            if (idx < 0 || idx >= _clipFiles.Count) return false;

            lock (_cacheLock)
            {
                if (_wideCache.TryGetValue(idx, out bool wide)) return wide;
            }

            // キャッシュミス: フルロードして登録
            BitmapSource bmp = LoadImage(idx);  // LoadImage が _wideCache に登録する
            if (bmp == null) return false;

            lock (_cacheLock)
            {
                return _wideCache.TryGetValue(idx, out bool w) && w;
            }
        }

        /// <summary>
        /// 見開きモードでアンカー idx を表示する際に消費するファイル数（1 or 2）を返す。
        ///
        /// 判定順序（フローチャート準拠）:
        ///   1. _firstSingle かつ idx==0  → 1（先頭単独表示：表紙を単ページで表示）
        ///   2. idx が末尾ページ          → 1（最終ページは常に単独表示）
        ///   3. file[idx] が横長          → 1（横長は単体表示）
        ///   4. file[idx+1] が横長        → 1（次が横長なので今は単体表示）
        ///   5. それ以外                  → 2（縦長ペアとして見開き表示）
        /// </summary>
        private int SpreadStepSize(int idx)
        {
            if (idx < 0 || idx >= _clipFiles.Count) return 1;
            if (_firstSingle && idx == 0) return 1;          // 先頭表紙（firstSingle=ON のみ）

            int count = _clipFiles.Count;
            if (idx == count - 1) return 1;                  // 最終ページは常に単独表示

            if (IsWideImage(idx)) return 1;

            int next = idx + 1;
            if (IsWideImage(next)) return 1;

            // アニメGIF/WebP は単独表示。
            // WebP はヘッダで即時判定できる（v0.8.2）ため初回表示から正しく単ページになる。
            // GIF・判定不能ファイルは従来どおり背景判定（TriggerAnimationCheckForSpread）で自動切替。
            if (EnsureWebPAnimKnown(idx,  _clipFiles[idx]))  return 1;
            if (EnsureWebPAnimKnown(next, _clipFiles[next])) return 1;
            lock (_cacheLock)
            {
                if (_knownAnimated.Contains(idx))  return 1;
                if (_knownAnimated.Contains(next)) return 1;
            }

            return 2;
        }

        /// <summary>
        /// 見開きモードのアンカーを正規化する。
        /// 単ページモードから見開きモードへの切り替えや起動時に呼ぶ。
        ///
        /// _firstSingle=OFF: 偶数アンカー（0,2,4...）に丸める
        /// _firstSingle=ON : 0（表紙）または奇数アンカー（1,3,5...）に丸める
        ///
        /// preferForward=false（既定）: パリティ補正は後退方向（idx-1）
        ///   → 初期ロード・モード切替・ページスキップ時に使用。
        ///     現在ファイルが見開きに含まれるよう後退して丸める。
        /// preferForward=true: _firstSingle=ON への補正のみ前進方向（idx+1）
        ///   → ToggleFirstSingle の OFF→ON 時に使用。
        ///     往路+1 / 復路-1 で往復トグルのドリフトをキャンセルする。
        /// </summary>
        private void NormalizeAnchor(bool preferForward = false)
        {
            if (_displayMode != DisplayMode.Spread || _clipFiles.Count == 0) return;

            // 末尾端数ページ / 横長画像 は SpreadStepSize=1 で単独アンカーとして有効。
            // _wideCache / ファイル末尾のみで判断（画像ロードなし）。
            if (SpreadStepSizeCachedOnly(_currentIndex) == 1) return;

            if (_firstSingle)
            {
                if (_currentIndex == 0) return;                          // 表紙はそのまま
                if (_currentIndex % 2 == 0)                              // 偶数 > 0 → 奇数に丸める
                {
                    if (preferForward)
                        _currentIndex = _currentIndex + 1;               // 前進方向（ToggleFirstSingle OFF→ON 用）
                    else
                        _currentIndex = Math.Max(1, _currentIndex - 1);  // 後退方向（既定）
                }
            }
            else
            {
                if (_currentIndex % 2 == 1)                              // 奇数 → 偶数に丸める（常に後退）
                    _currentIndex = _currentIndex - 1;
            }
        }

        /// <summary>
        /// _wideCache のみ参照（画像ロードなし）の軽量 SpreadStepSize。
        /// NormalizeAnchor での有効アンカー判定に使用する。
        /// SpreadStepSize と同じ判定順を踏襲し、キャッシュなし横長は縦長とみなす。
        /// </summary>
        private int SpreadStepSizeCachedOnly(int idx)
        {
            if (idx < 0 || idx >= _clipFiles.Count) return 1;
            if (_firstSingle && idx == 0) return 1;          // 先頭表紙

            int count = _clipFiles.Count;
            int next  = idx + 1;
            if (next >= count) return 1;                      // 末尾ページ

            lock (_cacheLock)
            {
                if (_wideCache.TryGetValue(idx,  out bool curWide)  && curWide)  return 1;
                if (_wideCache.TryGetValue(next, out bool nextWide) && nextWide) return 1;
            }

            return 2;  // キャッシュなし or 両方縦長 → デフォルト step=2
        }

        /// <summary>フルスクリーン ↔ ウィンドウモードを切替える。</summary>
        private void ToggleWindowMode()
        {
            if (_isFullscreen)
            {
                // フルスクリーン → ウィンドウ
                _isFullscreen = false;
                WindowStyle   = WindowStyle.SingleBorderWindow;
                WindowState   = WindowState.Normal;
                Left          = _windowedLeft;
                Top           = _windowedTop;
                Width         = _windowedWidth;
                Height        = _windowedHeight;
                Cursor        = Cursors.Arrow;
            }
            else
            {
                // ウィンドウ → フルスクリーン（現サイズを記憶してから復元）
                _windowedLeft   = Left;
                _windowedTop    = Top;
                _windowedWidth  = Width;
                _windowedHeight = Height;
                _isFullscreen   = true;
                WindowStyle     = WindowStyle.None;
                ApplyTargetScreen(_settings.TargetScreen);
                Cursor          = Cursors.None;
            }
        }

        /// <summary>ナビゲーション履歴スタックをクリアする。</summary>
        private void ClearNavHistory() => _navHistory.Clear();

        // =========================================================
        // libwebp P/Invoke（アニメーション WebP 対応）
        // =========================================================

        private const string LibWebP = "libwebpdemux.dll";

        [StructLayout(LayoutKind.Sequential)]
        private struct WebPData
        {
            public IntPtr  bytes; // const uint8_t*
            public UIntPtr size;  // size_t
        }

        // color_mode(4) + use_threads(4) + padding[7×4=28] = 36 bytes — 明示フィールドで確実に
        [StructLayout(LayoutKind.Sequential)]
        private struct WebPAnimDecoderOptions
        {
            public int  color_mode;                              // WEBP_CSP_MODE: 3 = MODE_BGRA
            public int  use_threads;
            private uint _p0, _p1, _p2, _p3, _p4, _p5, _p6;   // padding[7]
        }

        // canvas_width…frame_count(各4) + pad[4×4=16] = 36 bytes
        [StructLayout(LayoutKind.Sequential)]
        private struct WebPAnimInfo
        {
            public uint canvas_width;
            public uint canvas_height;
            public uint loop_count;
            public uint bgcolor;
            public uint frame_count;
            private uint _p0, _p1, _p2, _p3;                   // pad[4]
        }

        // タイマー精度向上（winmm.dll）
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint p);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint p);

        // WebPAnimDecoderNew はヘッダー上のマクロ → 実体は WebPAnimDecoderNewInternal
        private const int WebPDemuxAbiVersion = 0x0107; // WEBP_DEMUX_ABI_VERSION (libwebp 1.x)

        [DllImport(LibWebP, CallingConvention = CallingConvention.Cdecl,
                   EntryPoint = "WebPAnimDecoderNewInternal")]
        private static extern IntPtr WebPAnimDecoderNew(
            ref WebPData webp_data, ref WebPAnimDecoderOptions dec_options, int abi_version);

        [DllImport(LibWebP, CallingConvention = CallingConvention.Cdecl)]
        private static extern int WebPAnimDecoderGetInfo(
            IntPtr dec, out WebPAnimInfo info);

        [DllImport(LibWebP, CallingConvention = CallingConvention.Cdecl)]
        private static extern int WebPAnimDecoderGetNext(
            IntPtr dec, out IntPtr buf, out int timestamp);

        [DllImport(LibWebP, CallingConvention = CallingConvention.Cdecl)]
        private static extern void WebPAnimDecoderDelete(IntPtr dec);

        // =========================================================
        // アニメーション制御（GIF / WebP 共通）
        // =========================================================

        // =========================================================
        // アニメフレームの背景デコード（v0.8.2 プログレッシブ再生）
        //
        // 旧実装は全フレームをUIスレッドで同期デコードしており、大きい
        // アニメWebP/GIFでは再生開始まで数秒フリーズしていた。
        // 新実装: Task.Run でデコードし、フレーム0確定時点でキャッシュに登録
        // → OnAnimFramesReady が即再生開始。以降のフレームは再生と並行して
        // 配列を埋めていき、_gifAvailCache が「再生可能な枚数」を通知する。
        // =========================================================

        /// <summary>
        /// WebP のアニメ有無をヘッダ（VP8X 拡張フラグ）で即時判定し、_knownAnimated/_knownStatic へ登録する。
        /// 先頭21バイトの読み取りのみでフルデコードを伴わない（v0.8.2）。
        /// これにより見開きモードでも「一旦見開き表示→背景判定→単ページへ再表示」の遠回りをせず、
        /// 最初から単ページ表示+アニメデコード開始に直行できる。
        /// 戻り値: アニメ確定なら true。webp以外・判定失敗は false（既存の背景判定フローに任せる）。
        /// </summary>
        private bool EnsureWebPAnimKnown(int index, string path)
        {
            lock (_cacheLock)
            {
                if (_knownAnimated.Contains(index)) return true;
                if (_knownStatic.Contains(index))   return false;
            }
            if (!path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)) return false;

            // 遅延展開ZIPのエントリは実体化せずアーカイブから直接ヘッダを読む（低コスト、v0.8.4）
            var buf = ReadFileHead(path, 24);
            if (buf == null || buf.Length < 21) return false;

            // RIFF/WEBP + VP8X 拡張ヘッダのアニメーションフラグ（byte20 の 0x02）
            // VP8X でない（VP8 / VP8L 直格納の）WebP は構造上アニメ不可 → 静止確定
            bool riff =
                   buf[0]  == (byte)'R' && buf[1]  == (byte)'I' && buf[2]  == (byte)'F' && buf[3]  == (byte)'F'
                && buf[8]  == (byte)'W' && buf[9]  == (byte)'E' && buf[10] == (byte)'B' && buf[11] == (byte)'P';
            if (!riff) return false; // WebPですらない/読めない → 未登録のまま

            bool vp8x = buf[12] == (byte)'V' && buf[13] == (byte)'P' && buf[14] == (byte)'8' && buf[15] == (byte)'X';
            bool animated = vp8x && (buf[20] & 0x02) != 0;

            lock (_cacheLock)
            {
                if (animated) _knownAnimated.Add(index);
                else          _knownStatic.Add(index);
            }
            return animated;
        }

        /// <summary>
        /// アニメフレームの背景デコードを開始する（キャッシュ済み・デコード中・静止確定なら何もしない）。
        /// </summary>
        private void EnsureAnimFrames(int index, string path, string ext)
        {
            lock (_cacheLock)
            {
                if (_gifFrameCache.ContainsKey(index)) return;
                if (_knownStatic.Contains(index))      return;
                if (!_animDecoding.Add(index))         return; // 既にデコード中
            }

            List<string> files = _clipFiles;
            Task.Run(() =>
            {
                try
                {
                    if (ext == ".gif") DecodeGifFramesCore(index, path, files);
                    else               DecodeWebPFramesCore(index, path, files);
                }
                catch { /* 破損ファイル・DLL欠落など → 静止表示のまま */ }
                finally
                {
                    lock (_cacheLock) { _animDecoding.Remove(index); }
                    // 先読みを保留していた場合は再開（静止確定・失敗時もここで確実に解除される）
                    Dispatcher.BeginInvoke(new Action(ResumeDeferredPrefetch));
                }
            });
        }

        /// <summary>
        /// 背景デコードで先頭フレームが用意できたときの再生開始（Dispatcher経由でUIスレッドから呼ばれる）。
        /// ユーザーが既に別ページへ移動していた場合は何もしない。
        /// </summary>
        private void OnAnimFramesReady(int index)
        {
            ResumeDeferredPrefetch();  // 初回フレーム確定 → 保留していた先読みを再開

            if (index != _currentIndex) return;
            if (SingleImage.Visibility != Visibility.Visible) return; // 見開き時は再描画フローに任せる
            if (_gifCurrentIdx == index) return;                       // 既に再生中
            StartGifAnimation(index, SingleImage);
        }

        /// <summary>
        /// 先読みを開始する。ただし現在ファイルのアニメ初回フレームが未確定なら保留し、
        /// OnAnimFramesReady / デコード終了時に ResumeDeferredPrefetch で再開する。
        /// </summary>
        private void StartPrefetchSmart()
        {
            bool animPending;
            lock (_cacheLock)
            {
                animPending = _animDecoding.Contains(_currentIndex)
                           && !_gifFrameCache.ContainsKey(_currentIndex);
            }
            _prefetchDeferred = animPending;
            if (!animPending) StartPrefetch(_currentIndex);
        }

        /// <summary>保留していた先読みを再開する（多重呼び出し可・保留なしなら何もしない）。</summary>
        private void ResumeDeferredPrefetch()
        {
            if (!_prefetchDeferred) return;
            _prefetchDeferred = false;
            StartPrefetch(_currentIndex);
        }

        /// <summary>
        /// フレーム0確定時の初回登録。キャッシュに配列を登録して再生可能数=1とし、UIへ再生開始を通知する。
        /// ファイルリストが変わっていたら登録せず false（デコード中断）。
        /// </summary>
        private bool RegisterAnimArrays(int index, List<string> files, BitmapSource[] frames, int[] delays)
        {
            lock (_cacheLock)
            {
                if (!ReferenceEquals(_clipFiles, files)) return false;
                _gifFrameCache[index] = frames;
                _gifDelayCache[index] = delays;
                _gifAvailCache[index] = 1;
                _knownAnimated.Add(index);
            }
            Dispatcher.BeginInvoke(new Action(() => OnAnimFramesReady(index)));
            return true;
        }

        /// <summary>
        /// フレーム i（i≥1）確定時のコミット。破棄済み（evict等）なら false（デコード中断）。
        /// frames[i]・delays[i] を書き込んでから呼ぶこと。
        /// </summary>
        private bool CommitAnimFrame(int index, BitmapSource[] frames, int i)
        {
            lock (_cacheLock)
            {
                if (!_gifFrameCache.TryGetValue(index, out BitmapSource[] cur)
                    || !ReferenceEquals(cur, frames))
                    return false;
                _gifAvailCache[index] = i + 1;
            }
            return true;
        }

        /// <summary>
        /// GIF の全フレームをキャンバス合成しながらプログレッシブに登録する（背景スレッド用）。
        /// GIF の差分フレーム・disposal メソッドを正しく処理してノイズを防ぐ。
        /// </summary>
        private void DecodeGifFramesCore(int index, string path, List<string> files)
        {
            EnsureExtracted(path);  // 遅延展開ZIPのエントリなら実体化（v0.8.4）

            GifBitmapDecoder decoder;
            using (var fs = File.OpenRead(path))
            {
                decoder = new GifBitmapDecoder(fs,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
            }

            int count = decoder.Frames.Count;
            if (count <= 1)
            {
                lock (_cacheLock) { if (ReferenceEquals(_clipFiles, files)) _knownStatic.Add(index); }
                return; // 静止GIF は通常キャッシュのみで十分
            }

            // GIF 全体サイズをグローバルメタデータから取得（フォールバック: フレーム0 サイズ）
            int gifW = decoder.Frames[0].PixelWidth;
            int gifH = decoder.Frames[0].PixelHeight;
            var gm   = decoder.Metadata as BitmapMetadata;
            if (gm != null)
            {
                var qw = gm.GetQuery("/logscrdesc/Width");
                var qh = gm.GetQuery("/logscrdesc/Height");
                if (qw is ushort sw) gifW = sw;
                if (qh is ushort sh) gifH = sh;
            }

            var composited = new BitmapSource[count];
            var delays     = new int[count];
            BitmapSource canvas  = null; // 現在のキャンバス合成状態
            BitmapSource restore = null; // disposal=3 用の保存スナップショット

            for (int i = 0; i < count; i++)
            {
                var frame = decoder.Frames[i];
                var meta  = frame.Metadata as BitmapMetadata;

                int left     = GetGifMetaUShort(meta, "/imgdesc/Left",      0);
                int top      = GetGifMetaUShort(meta, "/imgdesc/Top",       0);
                int disposal = GetGifMetaByte  (meta, "/grctlext/Disposal", 0);

                delays[i] = GetGifFrameDelay(frame);

                // disposal=3: 次フレームで戻すため現在キャンバスを保存
                if (disposal == 3) restore = canvas;

                // 前フレームのキャンバスに今フレームを重ねて合成
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    if (canvas != null)
                        dc.DrawImage(canvas, new Rect(0, 0, gifW, gifH));
                    dc.DrawImage(frame,  new Rect(left, top, frame.PixelWidth, frame.PixelHeight));
                }
                var rtb = new RenderTargetBitmap(gifW, gifH, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                rtb.Freeze();
                composited[i] = rtb;

                // disposal メソッドに従い次フレームのベースを更新
                switch (disposal)
                {
                    case 2:  canvas = null;    break; // 背景色（透明）に戻す
                    case 3:  canvas = restore; break; // 1フレーム前の状態に戻す
                    default: canvas = rtb;    break; // 合成結果をそのまま維持
                }

                // プログレッシブ登録（破棄されていたら中断）
                if (i == 0) { if (!RegisterAnimArrays(index, files, composited, delays)) return; }
                else        { if (!CommitAnimFrame(index, composited, i)) return; }
            }
        }

        /// <summary>GIF フレームのメタデータから表示時間（ms）を取得する。</summary>
        private static int GetGifFrameDelay(BitmapFrame frame)
        {
            const int defaultDelay = 100;
            const int minDelay     = 20;
            try
            {
                var meta = frame.Metadata as BitmapMetadata;
                if (meta == null) return defaultDelay;
                var obj = meta.GetQuery("/grctlext/Delay");
                if (obj is ushort cs && cs > 0)
                    return Math.Max(minDelay, (int)cs * 10); // センチ秒 → ms
                return defaultDelay;
            }
            catch { return defaultDelay; }
        }

        /// <summary>GIF メタデータから ushort 値を取得するヘルパー。</summary>
        private static int GetGifMetaUShort(BitmapMetadata meta, string query, int fallback)
        {
            try { var v = meta?.GetQuery(query); return v is ushort u ? (int)u : fallback; }
            catch { return fallback; }
        }

        /// <summary>GIF メタデータから byte 値を取得するヘルパー。</summary>
        private static int GetGifMetaByte(BitmapMetadata meta, string query, int fallback)
        {
            try { var v = meta?.GetQuery(query); return v is byte b ? (int)b : fallback; }
            catch { return fallback; }
        }

        /// <summary>
        /// アニメーション WebP の全フレームをプログレッシブに登録する（背景スレッド用）。
        /// libwebp.dll が未配置の場合は呼び出し元（EnsureAnimFrames）が例外を握りつぶし静止表示にフォールバック。
        /// </summary>
        private void DecodeWebPFramesCore(int index, string path, List<string> files)
        {
            EnsureExtracted(path);  // 遅延展開ZIPのエントリなら実体化（v0.8.4）

            byte[]   data = File.ReadAllBytes(path);
            GCHandle pin  = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                var webpData = new WebPData
                {
                    bytes = pin.AddrOfPinnedObject(),
                    size  = (UIntPtr)data.Length
                };
                var opts = new WebPAnimDecoderOptions
                {
                    color_mode  = 3, // MODE_BGRA → PixelFormats.Bgra32 と一致
                    use_threads = 1
                };

                IntPtr dec = WebPAnimDecoderNew(ref webpData, ref opts, WebPDemuxAbiVersion);
                if (dec == IntPtr.Zero) return;

                try
                {
                    if (WebPAnimDecoderGetInfo(dec, out WebPAnimInfo info) == 0) return;

                    int count = (int)info.frame_count;
                    if (count <= 1)
                    {
                        lock (_cacheLock) { if (ReferenceEquals(_clipFiles, files)) _knownStatic.Add(index); }
                        return; // 静止 WebP
                    }

                    int w = (int)info.canvas_width;
                    int h = (int)info.canvas_height;

                    var composited = new BitmapSource[count];
                    var delays     = new int[count];
                    int prevTs     = 0;

                    for (int i = 0; i < count; i++)
                    {
                        if (WebPAnimDecoderGetNext(dec, out IntPtr buf, out int ts) == 0) break;

                        delays[i] = Math.Max(1, ts - prevTs); // WebPはms精度なので下限1ms（GIFの20ms制限は不要）
                        prevTs    = ts;

                        // libwebp が出力する合成済み BGRA バッファ → BitmapSource（即時コピー）
                        int stride = w * 4;
                        var bmp = BitmapSource.Create(
                            w, h, 96, 96, PixelFormats.Bgra32, null,
                            buf, h * stride, stride);
                        bmp.Freeze();
                        composited[i] = bmp;

                        // プログレッシブ登録（破棄されていたら中断）
                        if (i == 0) { if (!RegisterAnimArrays(index, files, composited, delays)) return; }
                        else        { if (!CommitAnimFrame(index, composited, i)) return; }
                    }
                }
                finally { WebPAnimDecoderDelete(dec); }
            }
            finally { pin.Free(); }
        }

        /// <summary>
        /// 指定インデックスがアニメーション画像（GIF/WebP、2フレーム以上）かどうかを返す。
        /// </summary>
        private bool IsAnimatedGif(int index)
        {
            lock (_cacheLock)
            {
                return _gifFrameCache.TryGetValue(index, out BitmapSource[] frames)
                    && frames != null && frames.Length > 1;
            }
        }

        /// <summary>GIF アニメーションを開始する。</summary>
        private void StartGifAnimation(int fileIndex, Image targetImage)
        {
            StopGifAnimation();

            BitmapSource[] frames;
            int[]          delays;
            lock (_cacheLock)
            {
                if (!_gifFrameCache.TryGetValue(fileIndex, out frames)) return;
                if (!_gifDelayCache.TryGetValue(fileIndex, out delays)) return;
            }
            if (frames == null || frames.Length <= 1) return;

            _gifCurrentIdx  = fileIndex;
            _gifTargetImage = targetImage;
            _gifFrameIndex  = 0;
            _gifPaused      = false;
            // loop名アニメはループ固定（v0.8.5）。設定自体は変更・保存しない
            _gifForceLoop   = Path.GetFileName(_clipFiles[fileIndex])
                                  .IndexOf("loop", StringComparison.OrdinalIgnoreCase) >= 0;
            _gifAdvancePending = false;

            targetImage.Source = frames[0];

            // vsync 駆動アニメ開始（DispatcherTimer より高精度）
            _gifNextFrameTick    = Stopwatch.GetTimestamp() + MsToTicks(delays[0]);
            _gifRenderingHandler = GifRendering_Tick;
            CompositionTarget.Rendering += _gifRenderingHandler;
        }

        /// <summary>現在のアニメーションを停止してリセットする。</summary>
        private void StopGifAnimation()
        {
            if (_gifRenderingHandler != null)
            {
                CompositionTarget.Rendering -= _gifRenderingHandler;
                _gifRenderingHandler = null;
            }
            _gifCurrentIdx  = -1;
            _gifTargetImage = null;
            _gifPaused      = false;
            _gifForceLoop      = false;
            _gifAdvancePending = false;
        }

        /// <summary>
        /// vsync 毎に呼ばれるアニメーションフレーム更新ハンドラ。
        /// Stopwatch で実時間を計測し、期限に達したら次フレームへ進む。
        /// 2ms の早め判定で vsync 周期（16.67ms）とフレーム遅延のズレを吸収する。
        /// </summary>
        private void GifRendering_Tick(object sender, EventArgs e)
        {
            if (_gifCurrentIdx < 0 || _gifTargetImage == null || _gifPaused) return;
            long now = Stopwatch.GetTimestamp();
            if (now + _earlyAdvanceTicks < _gifNextFrameTick) return;

            BitmapSource[] frames;
            int[]          delays;
            int            avail;
            lock (_cacheLock)
            {
                if (!_gifFrameCache.TryGetValue(_gifCurrentIdx, out frames)) return;
                if (!_gifDelayCache.TryGetValue(_gifCurrentIdx, out delays)) return;
                _gifAvailCache.TryGetValue(_gifCurrentIdx, out avail);
            }
            if (avail <= 0) return;

            int next = _gifFrameIndex + 1;
            if (next >= frames.Length)
            {
                if (avail < frames.Length) return; // 最終フレーム未デコード（通常起きない）

                // 1ループ完了。
                // loop名アニメ: 保留中のページ送りがあればここ（ループの切れ目）で遷移、なければループ継続。
                // 通常アニメ:   AutoAdvance モードなら自動遷移。
                bool advance = _gifForceLoop
                    ? _gifAdvancePending
                    : (_gifPlayMode == GifPlayMode.AutoAdvance);
                if (advance)
                {
                    StopGifAnimation();
                    NavigateNext();
                    return;
                }
                next = 0; // Loop: 先頭フレームへ折り返し
            }
            else if (next >= avail)
            {
                // 次フレームが未デコード → 現フレームを維持して待つ（デコード追い付き待ちストール）
                return;
            }

            _gifFrameIndex         = next;
            _gifTargetImage.Source = frames[next];

            // 目標時刻を「前回目標 + 次フレーム遅延」で進める（誤差蓄積防止）。
            // ただしストールで大幅に遅れた場合は現在時刻基準にリベースする
            // （遅延分を取り戻そうとする高速コマ飛びを防ぐ）。
            long target = _gifNextFrameTick + MsToTicks(delays[next]);
            if (target < now - MsToTicks(200))
                target = now + MsToTicks(delays[next]);
            _gifNextFrameTick = target;
        }

        /// <summary>ミリ秒を Stopwatch の ticks に変換する。</summary>
        private static long MsToTicks(int ms)
            => (long)((double)ms * Stopwatch.Frequency / 1000.0);

        /// <summary>GIF再生モードを切替える（Loop ↔ AutoAdvance）。</summary>
        private void ToggleGifMode()
        {
            _gifPlayMode = (_gifPlayMode == GifPlayMode.Loop)
                ? GifPlayMode.AutoAdvance
                : GifPlayMode.Loop;

            ShowNotification(_gifPlayMode == GifPlayMode.Loop
                ? "アニメ: 無限ループ"
                : "アニメ: 1ループ→自動遷移");
        }

        /// <summary>アニメの一時停止／再生を切替える。非表示中は無視。</summary>
        private void ToggleGifPausePlay()
        {
            if (_gifCurrentIdx < 0 || _gifRenderingHandler == null) return;

            _gifPaused = !_gifPaused;
            if (!_gifPaused)
            {
                // ポーズ解除時は目標時刻をリセット（長時間停止後の高速コマ飛びを防ぐ）
                int[] delays;
                lock (_cacheLock)
                {
                    if (_gifDelayCache.TryGetValue(_gifCurrentIdx, out delays))
                        _gifNextFrameTick = Stopwatch.GetTimestamp() + MsToTicks(delays[_gifFrameIndex]);
                }
            }

            ShowNotification(_gifPaused ? "アニメ: 一時停止" : "アニメ: 再生");
        }

        /// <summary>
        /// アニメをコマ送り（forward=true: 前進、false: 後退）する。
        /// 再生中の場合は自動的に一時停止してからステップする。非表示中は無視。
        /// </summary>
        private void GifStepFrame(bool forward)
        {
            if (_gifCurrentIdx < 0) return;

            BitmapSource[] frames;
            int[]          delays;
            int            avail;
            lock (_cacheLock)
            {
                if (!_gifFrameCache.TryGetValue(_gifCurrentIdx, out frames)) return;
                if (!_gifDelayCache.TryGetValue(_gifCurrentIdx, out delays)) return;
                _gifAvailCache.TryGetValue(_gifCurrentIdx, out avail);
            }
            if (avail <= 0) return;

            // 再生中なら自動的に一時停止
            if (!_gifPaused) _gifPaused = true;

            // デコード済み範囲内でループ（プログレッシブ再生中は範囲が伸びていく）
            _gifFrameIndex = forward
                ? (_gifFrameIndex + 1) % avail
                : (_gifFrameIndex - 1 + avail) % avail;

            if (_gifTargetImage != null)
                _gifTargetImage.Source = frames[_gifFrameIndex];

            // ステップ後に再生再開した場合の目標時刻をリセット
            _gifNextFrameTick = Stopwatch.GetTimestamp() + MsToTicks(delays[_gifFrameIndex]);
        }

        // =========================================================
        // ナビゲーション
        // =========================================================

        private void NavigateNext()
        {
            if (_clipFiles.Count == 0) return;

            // loop名アニメの再生中はページ送りをループ末尾まで保留する（v0.8.5）。
            // 2回目のページ送りで即時遷移（保留のキャンセル）。一時停止中は即時。
            if (_gifForceLoop && _gifCurrentIdx >= 0 && !_gifPaused && !_gifAdvancePending)
            {
                _gifAdvancePending = true;
                ShowNotification("ループ末尾で次へ（再押下で即時）", 1.0);
                return;
            }

            if (_displayMode == DisplayMode.Single)
            {
                int next = FindNextValid(_currentIndex, forward: true, step: 1);
                if (next >= 0) _currentIndex = next;
            }
            else
            {
                // 移動前に現在位置を履歴へ積む
                _navHistory.Push(_currentIndex);

                // SpreadStepSize がすべての条件（firstSingle先頭/最終ページ/横長）を包括するため
                // 特殊ケースの個別分岐は不要。step=1 なら +1、step=2 なら +2 で進む。
                int cnt  = _clipFiles.Count;
                int step = SpreadStepSize(_currentIndex);
                int next = _currentIndex + step;

                if (next < cnt)
                    _currentIndex = next;
                else
                    _currentIndex = 0;  // 末尾を越えたら先頭へラップ
            }

            ResetZoom();
            DisplayCurrent();
        }

        private void NavigatePrev()
        {
            if (_clipFiles.Count == 0) return;

            if (_displayMode == DisplayMode.Single)
            {
                int prev = FindNextValid(_currentIndex, forward: false, step: 1);
                if (prev >= 0) _currentIndex = prev;
            }
            else
            {
                if (_navHistory.Count > 0)
                {
                    // 履歴あり: 正確に前のアンカーへ戻る
                    _currentIndex = _navHistory.Pop();
                }
                else
                {
                    // 履歴なし（Home/End/PageSkip 後など）: IsWideImage で前ステップを逆算
                    int cnt = _clipFiles.Count;

                    if (_currentIndex == 0)
                    {
                        // 先頭 → 末尾へラップ
                        _currentIndex = cnt - 1;
                    }
                    else
                    {
                        // 直前アンカーの逆算:
                        //   現在が横長 or 直前が横長 → step=1 で来た（currentIndex-1）
                        //   それ以外                 → step=2 で来た（currentIndex-2）
                        // _firstSingle && idx==1 の場合も Math.Max(0, 1-2)=0 で正しく表紙へ戻る
                        int prev;
                        if (IsWideImage(_currentIndex))
                            prev = _currentIndex - 1;
                        else if (_currentIndex > 0 && IsWideImage(_currentIndex - 1))
                            prev = _currentIndex - 1;
                        else
                            prev = _currentIndex - 2;

                        _currentIndex = Math.Max(0, prev);
                    }
                }
            }

            ResetZoom();
            DisplayCurrent();
        }

        private void SingleStep()
        {
            if (_clipFiles.Count == 0) return;

            _currentIndex = (_currentIndex + 1) % _clipFiles.Count;
            ResetZoom();
            if (_displayMode == DisplayMode.Spread)
                DisplaySpreadSingleStep();
            else
                DisplayCurrent();
        }

        /// <summary>
        /// 単ページ送り（NumPad0）用の見開き表示。
        /// _currentIndex を起点に (_currentIndex, _currentIndex+1) を
        /// スライディングウィンドウとして表示する。
        /// SpreadStepSize による先読みルールを適用。
        /// </summary>
        private void DisplaySpreadSingleStep()
        {
            int count   = _clipFiles.Count;
            int idx     = _currentIndex;
            int pairIdx = (idx + 1 < count) ? idx + 1 : -1;

            ErrorText.Visibility = Visibility.Collapsed;

            // SpreadStepSize による表示判定
            if (SpreadStepSize(idx) == 1)
            {
                SpreadGrid.Visibility  = Visibility.Collapsed;
                SingleImage.Visibility = Visibility.Visible;
                SingleImage.Source     = LoadImage(idx);
                UpdateInfoPanel();
                return;
            }

            BitmapSource idxBmp  = LoadImage(idx);
            BitmapSource pairBmp = (pairIdx >= 0) ? LoadImage(pairIdx) : null;

            SpreadGrid.Visibility  = Visibility.Visible;
            SingleImage.Visibility = Visibility.Collapsed;

            if (_bindingDirection == BindingDirection.Right)
            {
                RightImage.Source = idxBmp;
                LeftImage.Source  = pairBmp;
                UpdateInfoPanelWithIndices(pairIdx, idx);
            }
            else
            {
                LeftImage.Source  = idxBmp;
                RightImage.Source = pairBmp;
                UpdateInfoPanelWithIndices(idx, pairIdx);
            }
        }

        private int FindNextValid(int from, bool forward, int step)
        {
            int count = _clipFiles.Count;
            int dir   = forward ? 1 : -1;

            for (int i = 1; i <= count; i++)
            {
                int idx = ((from + dir * i * step) % count + count) % count;
                if (!_brokenFiles.Contains(idx))
                    return idx;
            }
            return -1;
        }

        // =========================================================
        // ズーム操作（F19）
        // =========================================================

        /// <summary>
        /// ページ遷移時の表示リセット（v0.8.3改定）。
        /// - 回転・反転は**保持**する（連続して横倒し画像を見る用途。アニメ・静止画共通）
        /// - ズーム倍率は ini の KeepZoomOnNavigate=True のとき保持、False（既定）でリセット
        /// - パン位置は常にリセット（倍率保持時も次ページは中央から）
        /// </summary>
        private void ResetZoom()
        {
            if (!_settings.KeepZoomOnNavigate)
                _zoomFactor = 1.0;
            ZoomScale.ScaleX   = _zoomFactor;
            ZoomScale.ScaleY   = _zoomFactor;
            ApplyImageTransform();  // 保持している回転・反転と回転FIT補正を再適用
            ZoomScale.CenterX  = 0;
            ZoomScale.CenterY  = 0;
            ZoomTranslate.X    = 0;
            ZoomTranslate.Y    = 0;
        }

        // =========================================================
        // 回転・反転（F3x）
        // =========================================================

        private void ApplyImageTransform()
        {
            ImageRotate.Angle = _rotationAngle;
            FlipScale.ScaleX  = _flipH ? -1.0 : 1.0;
            FlipScale.ScaleY  = _flipV ? -1.0 : 1.0;

            // 回転時FIT補正（v0.8.3）: 90/270°回転時は縦横が入れ替わるため、
            // 回転後の見た目サイズがビューポートに収まる（かつ最大化される）倍率を掛ける。
            // 単ページ表示のみ対象（見開きの全体回転は従来どおり補正なし）。
            double k = 1.0;
            if ((_rotationAngle == 90 || _rotationAngle == 270)
                && SingleImage.Visibility == Visibility.Visible
                && SingleImage.ActualWidth  > 0 && SingleImage.ActualHeight > 0
                && ActualWidth > 0 && ActualHeight > 0)
            {
                k = Math.Min(ActualWidth  / SingleImage.ActualHeight,
                             ActualHeight / SingleImage.ActualWidth);
            }
            RotateFitScale.ScaleX = k;
            RotateFitScale.ScaleY = k;
        }

        /// <summary>
        /// 表示画像のレイアウトサイズ確定時に回転FIT補正を再計算する
        /// （ページ遷移でソースが変わった直後は ActualWidth が旧値のため、ここで追従させる）。
        /// </summary>
        private void SingleImage_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_rotationAngle == 90 || _rotationAngle == 270)
                ApplyImageTransform();
        }

        private void RotateRight()
        {
            _rotationAngle = (_rotationAngle + 90) % 360;
            ApplyImageTransform();
        }

        private void RotateLeft()
        {
            _rotationAngle = (_rotationAngle + 270) % 360;
            ApplyImageTransform();
        }

        private void ToggleFlipH()
        {
            _flipH = !_flipH;
            ApplyImageTransform();
        }

        private void ToggleFlipV()
        {
            _flipV = !_flipV;
            ApplyImageTransform();
        }

        private void ApplyZoom(double delta, Point screenCenter)
        {
            double newFactor = _zoomFactor + delta;
            // Fit(倍率1.0)と原寸100%は、ホイールで通過するとき必ず踏む（スナップ）
            newFactor = SnapZoom(_zoomFactor, newFactor);
            newFactor = Math.Max(ZoomMin, Math.Min(ZoomMax, newFactor));

            if (Math.Abs(newFactor - _zoomFactor) < 0.001) return;

            double cx = screenCenter.X;
            double cy = screenCenter.Y;

            double scaleRatio  = newFactor / _zoomFactor;
            double newTransX   = cx - scaleRatio * (cx - ZoomTranslate.X);
            double newTransY   = cy - scaleRatio * (cy - ZoomTranslate.Y);

            _zoomFactor       = newFactor;
            ZoomScale.ScaleX  = newFactor;
            ZoomScale.ScaleY  = newFactor;
            ZoomScale.CenterX = 0;
            ZoomScale.CenterY = 0;
            ZoomTranslate.X   = newTransX;
            ZoomTranslate.Y   = newTransY;
            // 情報パネル表示中なら拡大率を即時反映
            if (_infoMode != InfoDisplayMode.Off)
                InfoZoom.Text = BuildZoomText(GetInfoTargetIndex());
        }

        // =========================================================
        // モード切替
        // =========================================================

        private void ToggleDisplayMode()
        {
            _displayMode = (_displayMode == DisplayMode.Single)
                ? DisplayMode.Spread : DisplayMode.Single;
            lock (_cacheLock) { _imageCache.Clear(); }
            ClearNavHistory();
            NormalizeAnchor();
            ResetZoom();
            DisplayCurrent();
        }

        private void ToggleBindingDirection()
        {
            _bindingDirection = (_bindingDirection == BindingDirection.Right)
                ? BindingDirection.Left : BindingDirection.Right;
            DisplayCurrent();
        }

        // F1: Basic ↔ Off トグル
        private void ToggleInfoDisplay()
        {
            _infoMode = (_infoMode == InfoDisplayMode.Off)
                ? InfoDisplayMode.Basic
                : InfoDisplayMode.Off;
            InfoPanel.Visibility = _infoMode != InfoDisplayMode.Off
                ? Visibility.Visible : Visibility.Collapsed;
            if (_infoMode != InfoDisplayMode.Off) UpdateInfoPanel();
        }

        // Tab: Off → Basic → Detailed サイクル
        private void CycleInfoDisplay()
        {
            if      (_infoMode == InfoDisplayMode.Off)      _infoMode = InfoDisplayMode.Basic;
            else if (_infoMode == InfoDisplayMode.Basic)    _infoMode = InfoDisplayMode.Detailed;
            else                                             _infoMode = InfoDisplayMode.Off;

            InfoPanel.Visibility = _infoMode != InfoDisplayMode.Off
                ? Visibility.Visible : Visibility.Collapsed;
            if (_infoMode != InfoDisplayMode.Off) UpdateInfoPanel();
        }

        private void ToggleFirstSingle()
        {
            bool turningOn = !_firstSingle;  // トグル後に ON になるか
            _firstSingle = !_firstSingle;
            ClearNavHistory();
            // OFF→ON: preferForward=true（前進補正）で往復トグルのドリフトをキャンセル
            // ON→OFF: preferForward=false（後退補正、既定）
            NormalizeAnchor(preferForward: turningOn);
            DisplayCurrent();
        }

        // =========================================================
        // スクリーン配置
        // =========================================================

        private void ApplyTargetScreen(int screenIndex)
        {
            var screens = WinForms.Screen.AllScreens;

            WinForms.Screen target = (screenIndex >= 0 && screenIndex < screens.Length)
                ? screens[screenIndex]
                : WinForms.Screen.PrimaryScreen;

            var source = PresentationSource.FromVisual(this);
            double dpiX = 1.0, dpiY = 1.0;
            if (source?.CompositionTarget != null)
            {
                dpiX = source.CompositionTarget.TransformFromDevice.M11;
                dpiY = source.CompositionTarget.TransformFromDevice.M22;
            }

            WindowState = WindowState.Normal;
            Left   = target.Bounds.Left   * dpiX;
            Top    = target.Bounds.Top    * dpiY;
            Width  = target.Bounds.Width  * dpiX;
            Height = target.Bounds.Height * dpiY;
            WindowState = WindowState.Maximized;
        }

        // =========================================================
        // ナビゲーション拡張（F24/F29/F30）
        // =========================================================

        // F24: 先頭ファイルへジャンプ
        private void NavigateHome()
        {
            if (_clipFiles.Count == 0) return;
            _currentIndex = 0;
            ClearNavHistory();
            ResetZoom();
            DisplayCurrent();
        }

        // F24: 末尾ファイルへジャンプ
        private void NavigateEnd()
        {
            if (_clipFiles.Count == 0) return;
            _currentIndex = _clipFiles.Count - 1;
            ClearNavHistory();
            ResetZoom();
            DisplayCurrent();
        }

        // F29: 複数ページスキップ（非ループ）
        private void NavigatePageSkip(bool forward)
        {
            if (_clipFiles.Count == 0) return;
            int skip   = _settings.PageSkipCount;
            int target = forward
                ? Math.Min(_currentIndex + skip, _clipFiles.Count - 1)
                : Math.Max(_currentIndex - skip, 0);
            if (target == _currentIndex) return;
            _currentIndex = target;
            ClearNavHistory();
            NormalizeAnchor();
            ResetZoom();
            DisplayCurrent();
        }

        // F30: 兄弟ディレクトリ（またはアーカイブモード時は兄弟アーカイブ）へ移動
        private void NavigateSiblingDirectory(bool next)
        {
            if (_clipFiles.Count == 0) return;

            // アーカイブモード: 同フォルダ内の隣接アーカイブへ移動
            if (_currentArchivePath != null)
            {
                NavigateSiblingArchive(next);
                return;
            }

            string currentDir = Path.GetDirectoryName(_clipFiles[_currentIndex]);
            if (string.IsNullOrEmpty(currentDir)) return;

            string parentDir = Path.GetDirectoryName(currentDir);
            if (string.IsNullOrEmpty(parentDir)) return;

            List<string> siblings;
            try
            {
                siblings = new List<string>(Directory.GetDirectories(parentDir));
            }
            catch { return; }

            siblings.Sort(NaturalSort.Comparer);

            int currentPos = siblings.FindIndex(
                d => string.Equals(
                    Path.GetFullPath(d),
                    Path.GetFullPath(currentDir),
                    StringComparison.OrdinalIgnoreCase));

            if (currentPos < 0) return;

            var extensions = new[] { "*.clip", "*.psd", "*.jpg", "*.jpeg", "*.png", "*.webp", "*.gif", "*.avif" };
            int pos = currentPos;

            while (true)
            {
                pos = next ? pos + 1 : pos - 1;

                if (next && pos >= siblings.Count)
                {
                    ShowNotification("最下位ディレクトリです");
                    return;
                }
                if (!next && pos < 0)
                {
                    ShowNotification("最上位ディレクトリです");
                    return;
                }

                try
                {
                    var files = new List<string>();
                    foreach (string ext in extensions)
                        files.AddRange(Directory.GetFiles(siblings[pos], ext));

                    if (files.Count > 0)
                    {
                        files.Sort(NaturalSort.Comparer);
                        _clipFiles = files;
                        lock (_cacheLock)
                        {
                            _brokenFiles.Clear();
                            _imageCache.Clear();
                            _wideCache.Clear();
                            _srcSizeCache.Clear();
                            _gifFrameCache.Clear();
                            _gifDelayCache.Clear();
                            _gifAvailCache.Clear();
                            _knownAnimated.Clear();
                            _knownStatic.Clear();
                        }
                        ClearNavHistory();
                        _currentIndex = 0;
                        ResetZoom();
                        SeekFirstValid();
                        return;
                    }
                }
                catch { /* アクセス不能ディレクトリはスキップ */ }
            }
        }

        /// <summary>
        /// アーカイブモード時、同フォルダ内の前後アーカイブへ移動する。
        /// Up/Down キーで兄弟ディレクトリと同様に動作。
        /// </summary>
        private void NavigateSiblingArchive(bool next)
        {
            string parentDir = Path.GetDirectoryName(_currentArchivePath);
            if (string.IsNullOrEmpty(parentDir)) return;

            var archives = new List<string>();
            try
            {
                foreach (string ae in _archiveExts)
                    archives.AddRange(Directory.GetFiles(parentDir, "*" + ae));
            }
            catch { return; }

            archives.Sort(NaturalSort.Comparer);

            int pos = archives.FindIndex(
                a => string.Equals(Path.GetFullPath(a),
                                   Path.GetFullPath(_currentArchivePath),
                                   StringComparison.OrdinalIgnoreCase));
            if (pos < 0) return;

            int newPos = next ? pos + 1 : pos - 1;
            if (next  && newPos >= archives.Count) { ShowNotification("最後のアーカイブです"); return; }
            if (!next && newPos < 0)               { ShowNotification("最初のアーカイブです"); return; }

            StopGifAnimation();
            ResetZoom();
            LoadArchiveAsync(archives[newPos]);
        }

        // =========================================================
        // 外部連携（F26/F27/F28）
        // =========================================================

        // 操作対象ファイルインデックス取得（見開き時は右ページ優先）
        // SpreadStepSize==1（単独表示）の場合は _currentIndex そのものを返す
        private int GetOperationTargetIndex()
        {
            if (_displayMode == DisplayMode.Spread && SpreadStepSize(_currentIndex) == 2)
            {
                int leftIdx, rightIdx;
                ResolveSpreadIndices(out leftIdx, out rightIdx);
                return (rightIdx >= 0) ? rightIdx : (leftIdx >= 0 ? leftIdx : _currentIndex);
            }
            return _currentIndex;
        }

        // F26: 外部エディタで開く（Ctrl+E）
        private void OpenExternalEditor()
        {
            if (string.IsNullOrWhiteSpace(_settings.ExternalEditor))
            {
                ShowNotification("エラー：外部エディタが設定されていません");
                return;
            }
            if (!File.Exists(_settings.ExternalEditor))
            {
                ShowNotification("エラー：外部エディタが見つかりません");
                return;
            }

            int targetIdx = GetOperationTargetIndex();
            if (targetIdx < 0 || targetIdx >= _clipFiles.Count) return;

            try
            {
                EnsureExtracted(_clipFiles[targetIdx]);  // 遅延展開ZIPのエントリなら実体化（v0.8.4）
                Process.Start(_settings.ExternalEditor, $"\"{_clipFiles[targetIdx]}\"");
            }
            catch
            {
                ShowNotification("エラー：外部エディタの起動に失敗しました");
            }
        }

        // F27: ファイルを SaveDirectory にコピー（Insert）
        private void SaveCurrentFile()
        {
            if (string.IsNullOrWhiteSpace(_settings.SaveDirectory))
            {
                ShowNotification("エラー：保存先が設定されていません");
                return;
            }
            if (!Directory.Exists(_settings.SaveDirectory))
            {
                ShowNotification("エラー：保存先ディレクトリが見つかりません");
                return;
            }

            int targetIdx = GetOperationTargetIndex();
            if (targetIdx < 0 || targetIdx >= _clipFiles.Count) return;

            try
            {
                string src  = _clipFiles[targetIdx];
                EnsureExtracted(src);  // 遅延展開ZIPのエントリなら実体化（v0.8.4）
                string dest = Path.Combine(_settings.SaveDirectory, Path.GetFileName(src));
                File.Copy(src, dest, overwrite: true);
                ShowNotification($"{Path.GetFileName(src)} を保存しました", 1.0);
            }
            catch
            {
                ShowNotification("エラー：ファイルの保存に失敗しました");
            }
        }

        // Ctrl+S: 表示中ファイルをダイアログ指定の場所へコピー
        // 見開きモードは _currentIndex（ファイル名が若い方）を対象とする
        private void SaveCurrentFileAs()
        {
            if (_clipFiles.Count == 0) return;

            string sourcePath = _clipFiles[_currentIndex];

            // 前回の保存先（SaveDirectory）をダイアログの初期ディレクトリに使用
            string initDir = (!string.IsNullOrWhiteSpace(_settings.SaveDirectory)
                              && Directory.Exists(_settings.SaveDirectory))
                           ? _settings.SaveDirectory
                           : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title           = "ファイルのコピー先を指定",
                FileName        = Path.GetFileName(sourcePath),
                Filter          = "All Files (*.*)|*.*",
                InitialDirectory = initDir,
            };

            if (dlg.ShowDialog(this) != true) return;

            try
            {
                EnsureExtracted(sourcePath);  // 遅延展開ZIPのエントリなら実体化（v0.8.4）
                File.Copy(sourcePath, dlg.FileName, overwrite: true);
            }
            catch
            {
                ShowNotification("エラー：ファイルのコピーに失敗しました");
            }
        }

        // F40: 表示中ファイルをごみ箱に送る（Delete）
        private void DeleteCurrentFile()
        {
            if (_clipFiles.Count == 0) return;

            // アーカイブモード時はガード
            if (_currentArchivePath != null)
            {
                ShowNotification("アーカイブ内画像は削除できません", 1.0);
                return;
            }

            int    targetIdx = _currentIndex;
            string filePath  = _clipFiles[targetIdx];
            string fileName  = Path.GetFileName(filePath);

            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    filePath,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            catch
            {
                ShowNotification("エラー：ファイルの削除に失敗しました");
                return;
            }

            // アニメ再生中なら停止
            StopGifAnimation();

            // プリフェッチ停止 → インデックスずれを防ぐためキャッシュ全クリア
            _prefetchCts?.Cancel();
            _clipFiles.RemoveAt(targetIdx);
            lock (_cacheLock)
            {
                _imageCache.Clear();
                _wideCache.Clear();
                _srcSizeCache.Clear();
                _brokenFiles.Clear();
                _gifFrameCache.Clear();
                _gifDelayCache.Clear();
                _gifAvailCache.Clear();
                _knownAnimated.Clear();
                _knownStatic.Clear();
            }

            ShowNotification($"{fileName} を削除しました", 1.0);

            if (_clipFiles.Count == 0)
            {
                _currentIndex = -1;
                ResetZoom();
                SingleImage.Source = null;
                LeftImage.Source   = null;
                RightImage.Source  = null;
                ShowError("表示できるファイルがありません。\n\nEsc で終了");
                return;
            }

            // インデックスを有効範囲内に調整
            if (_currentIndex >= _clipFiles.Count)
                _currentIndex = _clipFiles.Count - 1;

            ResetZoom();
            DisplayCurrent();
        }

        // F28: 画像をクリップボードにコピー（Ctrl+C）
        // アニメ再生中の場合はコピー時点のフレーム静止画をコピーする
        private void CopyToClipboard()
        {
            int targetIdx = GetOperationTargetIndex();
            if (targetIdx < 0 || targetIdx >= _clipFiles.Count) return;

            string ext = Path.GetExtension(_clipFiles[targetIdx]).ToLowerInvariant();
            if (ext == ".clip" || ext == ".psd")
            {
                ShowNotification("エラー：このファイル形式はクリップボードにコピーできません");
                return;
            }

            BitmapSource copySource = null;

            // アニメ再生中（対象インデックスがアニメ再生中）なら現フレームを使用
            if (_gifCurrentIdx == targetIdx && _gifRenderingHandler != null)
            {
                lock (_cacheLock)
                {
                    if (_gifFrameCache.TryGetValue(targetIdx, out BitmapSource[] frames)
                        && frames != null && _gifFrameIndex < frames.Length)
                    {
                        copySource = frames[_gifFrameIndex];
                    }
                }
            }

            // アニメ未再生 or フレーム取得失敗 → 通常の静止画
            if (copySource == null)
                copySource = LoadImage(targetIdx);

            if (copySource == null)
            {
                ShowNotification("エラー：画像を読み込めません");
                return;
            }

            try
            {
                Clipboard.SetImage(copySource);
                ShowNotification("クリップボードにコピーしました", 1.0);
            }
            catch
            {
                ShowNotification("エラー：クリップボードへのコピーに失敗しました");
            }
        }

        // ini ファイルをエディタで開く（F32）
        private void OpenIniFile()
        {
            string iniPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "ClipViewer.ini");
            if (!File.Exists(iniPath))
            {
                ShowNotification("エラー：iniファイルが見つかりません");
                return;
            }
            try
            {
                Process.Start(iniPath);
            }
            catch
            {
                ShowNotification("エラー：iniファイルを開けませんでした");
            }
        }

        // =========================================================
        // 操作エラー通知（3秒自動消去）
        // =========================================================

        private void ShowNotification(string message, double seconds = 3.0)
        {
            NotifyText.Text        = message;
            NotifyPanel.Visibility = Visibility.Visible;

            _notifyTimer?.Stop();
            _notifyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            _notifyTimer.Tick += (s, e) =>
            {
                NotifyPanel.Visibility = Visibility.Collapsed;
                _notifyTimer.Stop();
            };
            _notifyTimer.Start();
        }

        // =========================================================
        // エラー表示
        // =========================================================

        private void ShowError(string message)
        {
            SingleImage.Visibility = Visibility.Collapsed;
            SpreadGrid.Visibility  = Visibility.Collapsed;
            ErrorText.Text         = message;
            ErrorText.Visibility   = Visibility.Visible;
        }

        // =========================================================
        // 起動時スクリーン配置（Loaded イベント）
        // =========================================================

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (_isFullscreen)
            {
                ApplyTargetScreen(_settings.TargetScreen);
            }
            else
            {
                WindowStyle = WindowStyle.SingleBorderWindow;
                WindowState = WindowState.Normal;
                Left        = _windowedLeft;
                Top         = _windowedTop;
                Width       = _windowedWidth;
                Height      = _windowedHeight;
                Cursor      = Cursors.Arrow;
            }

            // 表示領域が確定したのでフィルタパイプラインを有効化（F49/F50）。
            // コンストラクタ時点では ActualWidth=0 のため初回表示はフィルタなし → ここで適用し直す。
            _uiReady = true;
            UpdateViewportSize();
            RefreshFilterIfNeeded();
            StartPrefetchSmart();  // ウィンドウ表示が済んでから先読み開始（アニメデコード中は保留）
            StartupLog("Window_Loaded 完了（起動シーケンス終了）");

            // 強制終了などで残った古い一時展開フォルダを背景で掃除（v0.8.4）
            Task.Run(new Action(CleanupStaleTempDirs));
        }

        private void Window_LocationChanged(object sender, EventArgs e)
        {
            if (!_isFullscreen)
            {
                _windowedLeft = Left;
                _windowedTop  = Top;
            }
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_isFullscreen)
            {
                _windowedWidth  = Width;
                _windowedHeight = Height;
            }
            InfoStackPanel.MaxWidth = ActualWidth * 0.20;

            // 表示領域変化 → フィルタ済みキャッシュのサイズが合わなくなるため再フィルタ（デバウンス付き）
            UpdateViewportSize();
            ScheduleFilterRefresh();
        }

        // =========================================================
        // 終了時状態保存（F16）
        // =========================================================

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            _prefetchCts?.Cancel();
            SaveArchivePosition();  // アーカイブ閲覧中なら表示位置を記録（F52）
            CleanupTempDir();

            _settings.LastMode           = _displayMode;
            _settings.LastBinding        = _bindingDirection;
            _settings.LastInfoMode       = _infoMode;
            _settings.LastFirstSingle    = _firstSingle;
            _settings.LastGifPlayMode    = _gifPlayMode;
            _settings.LastIsFullscreen   = _isFullscreen;
            _settings.LastWindowedLeft   = _windowedLeft;
            _settings.LastWindowedTop    = _windowedTop;
            _settings.LastWindowedWidth  = _windowedWidth;
            _settings.LastWindowedHeight = _windowedHeight;

            IniFileManager.SaveState(_settings);
        }

        // =========================================================
        // マウスイベント（F19 ズーム / F20 ドラッグ）
        // =========================================================

        private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            double delta  = (e.Delta > 0) ? ZoomStep : -ZoomStep;
            Point  center = new Point(ActualWidth / 2.0, ActualHeight / 2.0);
            ApplyZoom(delta, center);
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                _isDragging  = true;
                _dragStart   = e.GetPosition(this);
                _dragStartX  = ZoomTranslate.X;
                _dragStartY  = ZoomTranslate.Y;
                Cursor       = Cursors.Arrow;
                CaptureMouse();
            }
            else
            {
                // Left以外のマウスボタン → MouseBindings でアクションを解決
                if (_settings.MouseBindings.TryGetValue(e.ChangedButton, out string actionName)
                    && _actionMap.TryGetValue(actionName, out Action action))
                {
                    action();
                    e.Handled = true;
                }
            }
        }

        private void Window_MouseMove(object sender, MouseEventArgs e)
        {
            // シークバーの出現/退場（下端48pxゾーン、F51）
            if (!_isDragging && !_seekDragging)
                SetSeekBarVisible(e.GetPosition(this).Y >= ActualHeight - 48);

            if (!_isDragging) return;

            Point current   = e.GetPosition(this);
            ZoomTranslate.X = _dragStartX + (current.X - _dragStart.X);
            ZoomTranslate.Y = _dragStartY + (current.Y - _dragStart.Y);
        }

        private void Window_MouseLeave(object sender, MouseEventArgs e)
        {
            if (!_seekDragging) SetSeekBarVisible(false);
        }

        private void Window_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                Cursor      = _isFullscreen ? Cursors.None : Cursors.Arrow;
                ReleaseMouseCapture();
            }
        }

        // =========================================================
        // シークバー（F51）
        // 画面下端ホバーでフェード表示。読了部分をオレンジで塗り、
        // 右綴じは右起点・左綴じは左起点。ドラッグ中はページ番号のみ更新し、
        // 離した時点でジャンプする。
        // =========================================================

        private void SetSeekBarVisible(bool show)
        {
            if (_seekBarShown == show) return;
            if (show && _clipFiles.Count <= 1) return;  // 1枚以下では出さない
            _seekBarShown = show;
            SeekBarPanel.IsHitTestVisible = show;
            SeekBarPanel.BeginAnimation(OpacityProperty,
                new DoubleAnimation(show ? 1.0 : 0.0, TimeSpan.FromMilliseconds(180)));
            if (show) UpdateSeekBarVisual(_currentIndex, showLabel: false);
            // フルスクリーン時（Cursor=None）もバー操作中はカーソルを見せる
            Cursor = show ? Cursors.Arrow : (_isFullscreen ? Cursors.None : Cursors.Arrow);
        }

        /// <summary>進行率(0..1)をトラック上のX座標へ。右綴じは右起点（右→左に進む）。</summary>
        private double SeekFracToX(double frac)
            => (_bindingDirection == BindingDirection.Right)
                ? SeekTrack.ActualWidth * (1.0 - frac)
                : SeekTrack.ActualWidth * frac;

        /// <summary>トラック上のX座標からファイルインデックスを求める。</summary>
        private int SeekIndexFromX(double x)
        {
            int count = _clipFiles.Count;
            if (count <= 1) return 0;
            double w = SeekTrack.ActualWidth;
            if (w <= 0) return _currentIndex;
            double frac = x / w;
            if (_bindingDirection == BindingDirection.Right) frac = 1.0 - frac;
            if (frac < 0) frac = 0; else if (frac > 1) frac = 1;
            return (int)Math.Round(frac * (count - 1));
        }

        /// <summary>塗り・サム・ページ番号ラベルを指定インデックスの位置へ更新する。</summary>
        private void UpdateSeekBarVisual(int idx, bool showLabel)
        {
            int    count = _clipFiles.Count;
            double w     = SeekTrack.ActualWidth;
            if (count <= 0 || w <= 0) return;

            double frac   = (count <= 1) ? 1.0 : idx / (double)(count - 1);
            double thumbX = SeekFracToX(frac);  // サム中心のX

            // 読了部分の塗り（右綴じ: 右端→サム / 左綴じ: 左端→サム）
            bool rtl = (_bindingDirection == BindingDirection.Right);
            SeekFill.HorizontalAlignment = rtl ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            SeekFill.Width = Math.Max(0, Math.Min(w, rtl ? w - thumbX : thumbX));

            // サム（直径14px、中心を thumbX に合わせてクランプ）
            SeekThumbPos.X = Math.Max(0, Math.Min(w - 14, thumbX - 7));

            if (showLabel)
            {
                SeekPageLabel.Text       = $"{idx + 1} / {count}";
                SeekPageLabel.Visibility = Visibility.Visible;
                double lw = SeekPageLabel.ActualWidth;
                SeekLabelPos.X = Math.Max(0, Math.Min(w - lw, thumbX - lw / 2.0));
            }
            else
            {
                SeekPageLabel.Visibility = Visibility.Collapsed;
            }
        }

        private void SeekBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_clipFiles.Count <= 1) return;
            _seekDragging = true;
            SeekBarPanel.CaptureMouse();
            UpdateSeekBarVisual(SeekIndexFromX(e.GetPosition(SeekTrack).X), showLabel: true);
            e.Handled = true;
        }

        private void SeekBar_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_seekDragging) return;
            UpdateSeekBarVisual(SeekIndexFromX(e.GetPosition(SeekTrack).X), showLabel: true);
            e.Handled = true;
        }

        private void SeekBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_seekDragging) return;
            _seekDragging = false;
            SeekBarPanel.ReleaseMouseCapture();

            int idx = SeekIndexFromX(e.GetPosition(SeekTrack).X);
            if (idx != _currentIndex && idx >= 0 && idx < _clipFiles.Count)
            {
                _currentIndex = idx;
                ClearNavHistory();
                NormalizeAnchor();
                ResetZoom();
                DisplayCurrent();  // シークバーの表示も内部で更新される
            }
            else
            {
                UpdateSeekBarVisual(_currentIndex, showLabel: false);
            }
            e.Handled = true;
        }

        private void SeekTrack_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_seekBarShown) UpdateSeekBarVisual(_currentIndex, showLabel: false);
        }

        // =========================================================
        // アクションマップ（マウスバインド用）
        // =========================================================

        /// <summary>
        /// アクション名 → メソッドの辞書を構築する。
        /// MouseBindings でアクション名を引いた後、ここで対応メソッドを実行する。
        /// </summary>
        private void BuildActionMap()
        {
            _actionMap = new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase)
            {
                ["NextPage"]          = NavigateNext,
                ["PrevPage"]          = NavigatePrev,
                ["ToggleSpread"]      = ToggleDisplayMode,
                ["SingleStep"]        = SingleStep,
                ["ToggleBinding"]     = ToggleBindingDirection,
                ["ToggleInfo"]        = ToggleInfoDisplay,
                ["CycleInfo"]         = CycleInfoDisplay,
                ["ToggleFirstSingle"] = ToggleFirstSingle,
                ["JumpFirst"]         = NavigateHome,
                ["JumpLast"]          = NavigateEnd,
                ["PageSkipForward"]   = () => NavigatePageSkip(forward: true),
                ["PageSkipBack"]      = () => NavigatePageSkip(forward: false),
                ["FileSave"]          = SaveCurrentFile,
                ["DeleteFile"]        = DeleteCurrentFile,
                ["NavigateDirUp"]     = () => NavigateSiblingDirectory(next: false),
                ["NavigateDirDown"]   = () => NavigateSiblingDirectory(next: true),
                ["OpenIniFile"]       = OpenIniFile,
                ["ToggleWindowMode"]  = ToggleWindowMode,
                ["ToggleMoireFilter"] = ToggleMoireFilter,
                ["ToggleSharpen"]     = ToggleSharpen,
                ["ToggleArchiveHistory"] = ToggleArchiveHistory,
                ["ToggleGifMode"]     = ToggleGifMode,
                ["GifPausePlay"]      = ToggleGifPausePlay,
                ["GifStepForward"]    = () => GifStepFrame(forward: true),
                ["GifStepBackward"]   = () => GifStepFrame(forward: false),
                ["CopyToClipboard"]   = CopyToClipboard,
                ["SaveFileAs"]        = SaveCurrentFileAs,
                ["RotateRight"]       = RotateRight,
                ["RotateLeft"]        = RotateLeft,
                ["FlipH"]             = ToggleFlipH,
                ["FlipV"]             = ToggleFlipV,
                ["Exit"]              = () => Application.Current.Shutdown(),
            };
        }

        // =========================================================
        // キー入力
        // =========================================================

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            // WPF では Alt+Key は e.Key == Key.System になるため SystemKey で実キーを取得する
            Key         k    = (e.Key == Key.System) ? e.SystemKey : e.Key;
            AppSettings s    = _settings;
            bool        ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool        alt  = (Keyboard.Modifiers & ModifierKeys.Alt)     != 0;

            if      (ctrl && k == Key.E)                   OpenExternalEditor();
            else if (ctrl && s.CopyToClipboard.Contains(k)) CopyToClipboard();
            else if (ctrl && s.SaveFileAs.Contains(k))      SaveCurrentFileAs();
            // Alt+矢印: 回転・反転
            else if (alt  && s.RotateRight.Contains(k))     RotateRight();
            else if (alt  && s.RotateLeft.Contains(k))      RotateLeft();
            else if (alt  && s.FlipH.Contains(k))           ToggleFlipH();
            else if (alt  && s.FlipV.Contains(k))           ToggleFlipV();
            else if (s.NextPage.Contains(k))
            {
                // 先頭単独表示（表紙）では IsRepeat を無視してキーリピートを止める。
                // ラップ直後のリピート発火で表紙が瞬時に通り過ぎるのを防ぐ。
                if (!e.IsRepeat || !(_firstSingle && _currentIndex == 0 && _displayMode == DisplayMode.Spread))
                    NavigateNext();
            }
            else if (s.PrevPage.Contains(k))
            {
                if (!e.IsRepeat || !(_firstSingle && _currentIndex == 0 && _displayMode == DisplayMode.Spread))
                    NavigatePrev();
            }
            else if (s.ToggleSpread.Contains(k))         ToggleDisplayMode();
            else if (s.SingleStep.Contains(k))           SingleStep();
            else if (s.ToggleBinding.Contains(k))        ToggleBindingDirection();
            else if (s.ToggleInfo.Contains(k))           ToggleInfoDisplay();
            else if (s.CycleInfo.Contains(k))            CycleInfoDisplay();
            else if (s.ToggleFirstSingle.Contains(k))    ToggleFirstSingle();
            else if (s.JumpFirst.Contains(k))            NavigateHome();
            else if (s.JumpLast.Contains(k))             NavigateEnd();
            else if (s.PageSkipForward.Contains(k))      NavigatePageSkip(forward: true);
            else if (s.PageSkipBack.Contains(k))         NavigatePageSkip(forward: false);
            else if (s.FileSave.Contains(k))             SaveCurrentFile();
            else if (s.DeleteFile.Contains(k))           DeleteCurrentFile();
            else if (s.NavigateDirUp.Contains(k))        NavigateSiblingDirectory(next: false);
            else if (s.NavigateDirDown.Contains(k))      NavigateSiblingDirectory(next: true);
            else if (s.OpenIniFile.Contains(k))          OpenIniFile();
            else if (s.ToggleWindowMode.Contains(k))     ToggleWindowMode();
            else if (s.ToggleGifMode.Contains(k))        ToggleGifMode();
            else if (s.GifPausePlay.Contains(k))         ToggleGifPausePlay();
            else if (s.GifStepForward.Contains(k))       GifStepFrame(forward: true);
            else if (s.GifStepBackward.Contains(k))      GifStepFrame(forward: false);
            else if (s.ToggleMoireFilter.Contains(k))    ToggleMoireFilter();
            else if (s.ToggleSharpen.Contains(k))        ToggleSharpen();
            else if (s.ToggleArchiveHistory.Contains(k)) ToggleArchiveHistory();
            else if (s.Exit.Contains(k))                 Application.Current.Shutdown();
        }
    }
}
