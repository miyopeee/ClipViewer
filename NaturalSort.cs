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
            //
            // 🚨 Path.GetDirectoryName / GetFileName は使わないこと（v0.8.5修正）:
            // MAX_PATH(260文字)超のパスで PathTooLongException を投げ、Sort ごと
            // アプリがクラッシュする（AI生成画像の長大ファイル名で実際に発生）。
            // 例外を投げ得ない純粋な文字列操作で分割する。
            int ix = x.LastIndexOf('\\');
            int iy = y.LastIndexOf('\\');
            string dirX = ix >= 0 ? x.Substring(0, ix) : "";
            string dirY = iy >= 0 ? y.Substring(0, iy) : "";
            int d = StrCmpLogicalW(dirX, dirY);
            if (d != 0) return d;
            return StrCmpLogicalW(x.Substring(ix + 1), y.Substring(iy + 1));
        };
    }
}
