using System;

namespace ClipViewer
{
    /// <summary>
    /// MAX_PATH(260文字)超のパスを Win32 拡張パス形式（\\?\ プレフィックス）へ正規化する（BF04, v0.8.6）。
    ///
    /// 背景: v0.8.5 の App.config（UseLegacyPathHandling=false / BlockLongPaths=false）は
    /// 「.NET側の門」を開けるだけで、素の長パスは Win32 CreateFile が MAX_PATH で拒否する
    /// （実測: 292文字の実在ファイルに対し File.Exists=false / FileStream=DirectoryNotFoundException。
    /// 　\\?\ を付ければ両方成功する）。OS側の門はレジストリ LongPathsEnabled + マニフェストでも
    /// 開けられるが、配布先の環境設定に依存しないよう、開く直前にコード側でプレフィックスを付ける。
    ///
    /// 注意: \\?\ パスは Win32 の正規化（. や .. の解決）を通らないため、
    /// 呼び出し元は正規化済みの絶対パス（引数・Directory.GetFiles の結果等）を渡すこと。
    /// </summary>
    internal static class LongPath
    {
        /// <summary>閾値以上のパスに \\?\（UNCは \\?\UNC\）を付与する。短いパスはそのまま返す。</summary>
        public static string Fix(string path)
        {
            // ディレクトリは 248、ファイルは 260 が Win32 の限界。余裕を見て 240 から付与する
            if (string.IsNullOrEmpty(path) || path.Length < 240) return path;
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
            if (path.StartsWith(@"\\", StringComparison.Ordinal))
                return @"\\?\UNC\" + path.Substring(2);
            return @"\\?\" + path;
        }
    }
}
