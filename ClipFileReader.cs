using System;
using System.Data;
using System.IO;
using System.Text;
using System.Data.SQLite;

namespace ClipViewer
{
    /// <summary>
    /// .clip ファイルからプレビュー画像（PNG）を抽出するクラス。
    ///
    /// .clip ファイル構造:
    ///   [独自ヘッダ] + [外部データ] + [SQLite DB] + [フッタ]
    ///
    /// SQLite DB は "SQLite format 3\0" というマジックバイトで始まる。
    /// その位置を検索し、以降をメモリ上の一時ファイルとして扱い
    /// System.Data.SQLite で開いて CanvasPreview テーブルから
    /// ImageData (PNG BLOB) を取得する。
    /// </summary>
    public static class ClipFileReader
    {
        private static readonly byte[] SqliteMagic = Encoding.ASCII.GetBytes("SQLite format 3\0");

        /// <summary>
        /// .clip ファイルからプレビュー画像データ（PNG）を返す。
        /// 失敗した場合は null を返す。
        /// </summary>
        public static byte[] ExtractPreviewImage(string clipFilePath)
        {
            byte[] fileBytes = File.ReadAllBytes(clipFilePath);

            long sqliteOffset = FindSqliteOffset(fileBytes);
            if (sqliteOffset < 0)
                return null;

            // SQLite 部分をメモリストリームとして扱うため一時ファイルへ書き出す
            // （System.Data.SQLite はファイルパスを必要とするため）
            string tempPath = Path.GetTempFileName();
            try
            {
                using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                {
                    fs.Write(fileBytes, (int)sqliteOffset, fileBytes.Length - (int)sqliteOffset);
                }

                return QueryImageData(tempPath);
            }
            finally
            {
                try { File.Delete(tempPath); } catch { }
            }
        }

        private static long FindSqliteOffset(byte[] data)
        {
            int magicLen = SqliteMagic.Length;
            int limit = data.Length - magicLen;

            for (int i = 0; i <= limit; i++)
            {
                bool match = true;
                for (int j = 0; j < magicLen; j++)
                {
                    if (data[i + j] != SqliteMagic[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                    return i;
            }
            return -1;
        }

        private static byte[] QueryImageData(string dbPath)
        {
            string connectionString = $"Data Source={dbPath};Version=3;Read Only=True;";

            using (var connection = new SQLiteConnection(connectionString))
            {
                connection.Open();

                // CanvasPreview テーブルの全カラムを走査し、
                // PNG または JPEG のシグネチャを持つ最初の BLOB を返す。
                // カラム名（"Image" / "ImageData" 等）をハードコードしないことで
                // CSP バージョン間の差異に対応する。
                using (var command = new SQLiteCommand(
                    "SELECT * FROM CanvasPreview LIMIT 1", connection))
                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            if (reader.IsDBNull(i)) continue;
                            try
                            {
                                long dataLength = reader.GetBytes(i, 0, null, 0, 0);
                                if (dataLength < 8) continue;

                                byte[] buffer = new byte[(int)dataLength];
                                reader.GetBytes(i, 0, buffer, 0, (int)dataLength);

                                // PNG シグネチャ: 89 50 4E 47 0D 0A 1A 0A
                                if (buffer[0] == 0x89 && buffer[1] == 0x50 &&
                                    buffer[2] == 0x4E && buffer[3] == 0x47)
                                    return buffer;

                                // JPEG シグネチャ: FF D8 FF
                                if (buffer[0] == 0xFF && buffer[1] == 0xD8 &&
                                    buffer[2] == 0xFF)
                                    return buffer;
                            }
                            catch { /* BLOB 以外のカラムは GetBytes で例外が出る場合がある */ }
                        }
                    }
                }
            }

            return null;
        }
    }
}
