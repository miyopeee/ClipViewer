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
            // ファイル名のみで比較（ディレクトリパスは無視）
            string nameX = Path.GetFileName(x);
            string nameY = Path.GetFileName(y);
            return StrCmpLogicalW(nameX, nameY);
        };
    }
}
