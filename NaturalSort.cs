using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.IO;

namespace ClipViewer
{
    /// <summary>
    /// ファイル名の自然順ソート（1, 2, 10, 11...）。
    /// Windows Shell の StrCmpLogicalW を利用する。
    /// </summary>
    public static class NaturalSort
    {
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int StrCmpLogicalW(string x, string y);

        public static readonly Comparison<string> Comparer = (x, y) =>
        {
            // ディレクトリ階層 → ファイル名 の順で自然順比較する（v0.8.1修正）。
            // ファイル名のみの比較だと、複数フォルダ構成のアーカイブ
            // （A\01.jpg, B\01.jpg ...）で A と B のファイルが交錯して表示される。
            // フォルダAを全て表示し終えてからフォルダBに進むのが正しい読書順。
            string dirX = Path.GetDirectoryName(x) ?? "";
            string dirY = Path.GetDirectoryName(y) ?? "";
            int d = StrCmpLogicalW(dirX, dirY);
            if (d != 0) return d;
            return StrCmpLogicalW(Path.GetFileName(x), Path.GetFileName(y));
        };
    }
}
