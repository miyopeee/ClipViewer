using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Input;

namespace ClipViewer
{
    /// <summary>
    /// アプリケーション設定（iniファイルと1対1で対応）。
    /// v0.6.0: 全キーバインドを Key[] に変更（複数割り当て対応）
    ///         MouseBindings を新設（マウスボタン割り当て対応）
    ///         SaveDirectory デフォルト値を変更（v0.8.0 で「ピクチャ\ClipViewer」に中立化）
    /// v0.7.0: [Filters] セクション新設（モアレ軽減 / シャープ化）
    ///         ToggleMoireFilter(F9) / ToggleSharpen(F11) を新設
    ///         OpenIniFile を F9→F2、ToggleWindowMode を F11→Enter(Return) に変更
    /// </summary>
    public class AppSettings
    {
        // =========================================================
        // [KeyBindings]  ← カンマ区切りで複数キー指定可能
        // =========================================================
        public Key[] NextPage          { get; set; } = new[] { Key.Left };
        public Key[] PrevPage          { get; set; } = new[] { Key.Right };
        public Key[] ToggleSpread      { get; set; } = new[] { Key.Space };
        public Key[] SingleStep        { get; set; } = new[] { Key.NumPad0 };
        public Key[] ToggleBinding     { get; set; } = new[] { Key.F3 };
        public Key[] Exit              { get; set; } = new[] { Key.Escape };
        // 情報パネル（F1: Basic↔Off / Tab: 3状態サイクル）
        public Key[] ToggleInfo        { get; set; } = new[] { Key.F1 };
        public Key[] CycleInfo         { get; set; } = new[] { Key.Tab };
        public Key[] ToggleFirstSingle { get; set; } = new[] { Key.F4 };
        public Key[] JumpFirst         { get; set; } = new[] { Key.Home };
        public Key[] JumpLast          { get; set; } = new[] { Key.End };
        // Next = PageDown キー（WPF の Key 列挙体では PageDown は "Next" と表記される）
        public Key[] PageSkipForward   { get; set; } = new[] { Key.Next };
        public Key[] PageSkipBack      { get; set; } = new[] { Key.PageUp };
        public Key[] FileSave          { get; set; } = new[] { Key.Insert };
        public Key[] NavigateDirUp     { get; set; } = new[] { Key.Up };
        public Key[] NavigateDirDown   { get; set; } = new[] { Key.Down };
        public Key[] OpenIniFile       { get; set; } = new[] { Key.F2 };
        // ウィンドウモード切替（Return = Enter キー）
        public Key[] ToggleWindowMode  { get; set; } = new[] { Key.Return };
        // フィルタトグル（F10/F12 はフィルタ種切り替え等の将来用に予約）
        public Key[] ToggleMoireFilter { get; set; } = new[] { Key.F9 };
        public Key[] ToggleSharpen     { get; set; } = new[] { Key.F11 };
        // アーカイブ履歴機能 ON/OFF（デフォルト未割り当て = Key.None。実キーは ini で指定）
        public Key[] ToggleArchiveHistory { get; set; } = new[] { Key.None };
        // アニメーション制御
        public Key[] ToggleGifMode     { get; set; } = new[] { Key.F5 };
        public Key[] GifPausePlay      { get; set; } = new[] { Key.F6 };
        public Key[] GifStepForward    { get; set; } = new[] { Key.F7 };
        public Key[] GifStepBackward   { get; set; } = new[] { Key.F8 };
        // ファイル操作（Ctrl+キー）
        public Key[] CopyToClipboard   { get; set; } = new[] { Key.C };
        public Key[] SaveFileAs        { get; set; } = new[] { Key.S };
        // ファイル削除（ごみ箱）
        public Key[] DeleteFile        { get; set; } = new[] { Key.Delete };
        // 回転・反転（Alt+キー）
        public Key[] RotateRight       { get; set; } = new[] { Key.Right };
        public Key[] RotateLeft        { get; set; } = new[] { Key.Left };
        public Key[] FlipH             { get; set; } = new[] { Key.Up };
        public Key[] FlipV             { get; set; } = new[] { Key.Down };

        // =========================================================
        // [MouseBindings]
        // マウスボタン → アクション名 のマッピング。
        // アクション名は [KeyBindings] のキー名と同一
        // （NextPage / PrevPage / ToggleSpread / Exit 等）
        // =========================================================
        public Dictionary<MouseButton, string> MouseBindings { get; set; }
            = new Dictionary<MouseButton, string>
            {
                { MouseButton.XButton1, "PrevPage" },
                { MouseButton.XButton2, "NextPage" },
                { MouseButton.Middle,   "Exit"     },
            };

        // =========================================================
        // [Filters]  ← モアレ軽減 / シャープ化パイプライン（v0.7.0）
        // Enabled 系は F9/F11 トグルの結果が終了時に保存される
        // =========================================================
        /// <summary>Stage1（モアレ軽減）全体の ON/OFF。F9 でトグル。</summary>
        public bool MoireFilterEnabled { get; set; } = true;
        /// <summary>Stage1 のアルゴリズム。Phase1 では Lanczos のみ実装（Area/Gaussian は Lanczos で代替）。</summary>
        public MoireFilterAlgorithm MoireFilterMode { get; set; } = MoireFilterAlgorithm.Lanczos;
        /// <summary>Stage1 強度（0-100）。Lanczos カーネル幅倍率 1.0〜2.0 にマッピング。</summary>
        public int MoireFilterStrength { get; set; } = 40;
        /// <summary>表示解像度÷ソース解像度がこの値未満の縮小時のみフィルタ適用（0.0-1.0）。</summary>
        public double MoireDownscaleThreshold { get; set; } = 0.95;

        /// <summary>Stage2（アンシャープマスク）全体の ON/OFF。F11 でトグル。</summary>
        public bool SharpenEnabled { get; set; } = true;
        /// <summary>USM のぼかし半径 px（0.1-5.0）。</summary>
        public double SharpenRadius { get; set; } = 1.0;
        /// <summary>USM の強さ %（0-200）。</summary>
        public int SharpenAmount { get; set; } = 100;
        /// <summary>この値未満のコントラスト差はシャープ化対象外（0-255、リンギング防止）。</summary>
        public int SharpenThreshold { get; set; } = 20;

        // =========================================================
        // [Settings]  ← 起動時デフォルト値（固定設定）
        // =========================================================
        public DisplayMode      DefaultMode    { get; set; } = DisplayMode.Single;
        public BindingDirection DefaultBinding { get; set; } = BindingDirection.Right;
        /// <summary>
        /// 表示対象スクリーン番号。0=プライマリ、1以降=接続順。
        /// 存在しない番号を指定した場合はプライマリにフォールバック。
        /// </summary>
        public int    TargetScreen   { get; set; } = 0;
        public string ExternalEditor { get; set; } = @"C:\Program Files\CELSYS\CLIP STUDIO 1.5\CLIP STUDIO PAINT\CLIPStudioPaint.exe";
        public string SaveDirectory  { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ClipViewer");
        public int    PageSkipCount  { get; set; } = 10;
        /// <summary>アーカイブ閲覧位置履歴（F52）の有効/無効。ToggleArchiveHistory キーでも切替可能。</summary>
        public bool   ArchiveHistoryEnabled { get; set; } = true;
        /// <summary>アーカイブ閲覧位置履歴の最大登録件数（古いものから自動削除）。</summary>
        public int    ArchiveHistoryCount   { get; set; } = 30;

        // =========================================================
        // [State]  ← 終了時に保存・起動時に復元する動作状態
        // =========================================================
        public DisplayMode      LastMode           { get; set; } = DisplayMode.Spread;
        public BindingDirection LastBinding        { get; set; } = BindingDirection.Right;
        public InfoDisplayMode  LastInfoMode       { get; set; } = InfoDisplayMode.Basic;
        public bool             LastFirstSingle    { get; set; } = false;
        public GifPlayMode      LastGifPlayMode    { get; set; } = GifPlayMode.Loop;
        public bool             LastIsFullscreen   { get; set; } = true;
        public double           LastWindowedLeft   { get; set; } = 100;
        public double           LastWindowedTop    { get; set; } = 100;
        public double           LastWindowedWidth  { get; set; } = 1280;
        public double           LastWindowedHeight { get; set; } = 800;
    }

    public enum DisplayMode
    {
        Single,
        Spread
    }

    public enum BindingDirection
    {
        Right,  // 右綴じ（漫画式）: 右=奇数、左=偶数
        Left    // 左綴じ（洋書式）: 左=奇数、右=偶数
    }

    public enum GifPlayMode
    {
        Loop,        // 無限ループ
        AutoAdvance  // 1ループ再生後に次の画像へ自動遷移
    }

    public enum InfoDisplayMode
    {
        Off,      // 非表示
        Basic,    // ファイル名・ページ・ズーム率
        Detailed  // Basic + ピクセルサイズ・フルパス・EXIF
    }

    /// <summary>Stage1（モアレ軽減）のアルゴリズム種別。</summary>
    public enum MoireFilterAlgorithm
    {
        Off,      // Stage1 を常時バイパス（MoireFilterEnabled=False と同義）
        Area,     // エリア平均（Phase 3 で実装予定、現状は Lanczos で代替）
        Lanczos,  // Lanczos-3（Phase 1 実装済み）
        Gaussian  // ガウシアン強デスクリーン（Phase 3 で実装予定、現状は Lanczos で代替）
    }
}
