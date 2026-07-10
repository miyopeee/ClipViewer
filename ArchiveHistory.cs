using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ClipViewer
{
    /// <summary>
    /// アーカイブ閲覧位置の履歴（F52、v0.8.0）。
    /// exe と同じフォルダの ClipViewer_history.txt に
    /// 「最終閲覧ticks TAB アーカイブフルパス TAB アーカイブ内相対パス」を1行1件で保持する。
    /// 最終閲覧が新しい順に最大件数（ini の ArchiveHistoryCount、既定30）を維持（LRU）。
    /// 読み書きの失敗はすべて握りつぶし、本体動作を妨げない。
    /// </summary>
    public static class ArchiveHistory
    {
        private const string FileName = "ClipViewer_history.txt";

        private static string HistoryPath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);

        /// <summary>アーカイブの前回表示エントリ（アーカイブ内相対パス）を返す。履歴になければ null。</summary>
        public static string Lookup(string archivePath)
        {
            try
            {
                foreach (var entry in Read())
                    if (string.Equals(entry.Item2, archivePath, StringComparison.OrdinalIgnoreCase))
                        return entry.Item3;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// アーカイブの表示位置を記録する（既存エントリは上書きして最新扱いに）。
        /// maxEntries を超えた分は最終閲覧が古い順に削除される。
        /// </summary>
        public static void Record(string archivePath, string relativeEntry, int maxEntries)
        {
            if (string.IsNullOrEmpty(archivePath) || string.IsNullOrEmpty(relativeEntry)) return;
            if (maxEntries < 1) maxEntries = 1;
            try
            {
                var list = Read()
                    .Where(t => !string.Equals(t.Item2, archivePath, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                list.Insert(0, Tuple.Create(DateTime.Now.Ticks, archivePath, relativeEntry));
                File.WriteAllLines(HistoryPath, list
                    .OrderByDescending(t => t.Item1)
                    .Take(maxEntries)
                    .Select(t => $"{t.Item1}\t{t.Item2}\t{t.Item3}"));
            }
            catch { }
        }

        private static List<Tuple<long, string, string>> Read()
        {
            var result = new List<Tuple<long, string, string>>();
            if (!File.Exists(HistoryPath)) return result;
            foreach (string raw in File.ReadAllLines(HistoryPath))
            {
                string[] parts = raw.Split('\t');
                if (parts.Length != 3) continue;
                if (!long.TryParse(parts[0], out long ticks)) continue;
                result.Add(Tuple.Create(ticks, parts[1], parts[2]));
            }
            return result;
        }
    }
}
