using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipViewer
{
    /// <summary>
    /// .psd ファイルから結合済みフラット画像（Image Data セクション）を抽出するクラス。
    ///
    /// PSD ファイル構造:
    ///   [Header 26bytes]
    ///   [Color Mode Data]
    ///   [Image Resources]
    ///   [Layer and Mask Info]
    ///   [Image Data] ← 全レイヤーを結合したフラット画像（常に存在）
    ///
    /// 対応: PSD (version=1) / RGB / 8bit / 圧縮 Raw(0) または PackBits RLE(1)
    /// 非対応: PSB(version=2) / CMYK / Lab / 16bit以上 / ZIP圧縮
    /// </summary>
    public static class PsdFileReader
    {
        /// <summary>
        /// .psd ファイルから画像データ（PNG）を返す。
        /// 失敗した場合または非対応フォーマットの場合は null を返す。
        /// </summary>
        public static byte[] ExtractPreviewImage(string psdFilePath)
        {
            byte[] data = File.ReadAllBytes(psdFilePath);

            // ヘッダー検証
            if (data.Length < 26) return null;
            if (data[0] != 0x38 || data[1] != 0x42 ||
                data[2] != 0x50 || data[3] != 0x53) return null; // "8BPS"

            int version = ReadUInt16BE(data, 4);
            if (version != 1) return null; // PSB 非対応

            int channels = ReadUInt16BE(data, 12);
            int height   = (int)ReadUInt32BE(data, 14);
            int width    = (int)ReadUInt32BE(data, 18);
            int depth    = ReadUInt16BE(data, 22);
            int colorMode= ReadUInt16BE(data, 24);

            // RGB 8bit のみ対応
            if (depth != 8 || colorMode != 3 || channels < 3) return null;
            if (width <= 0 || height <= 0) return null;

            int offset = 26;

            // Color Mode Data セクションをスキップ
            int cmLen = (int)ReadUInt32BE(data, offset);
            offset += 4 + cmLen;

            // Image Resources セクションをスキップ
            int irLen = (int)ReadUInt32BE(data, offset);
            offset += 4 + irLen;

            // Layer and Mask Info セクションをスキップ
            int lmLen = (int)ReadUInt32BE(data, offset);
            offset += 4 + lmLen;

            // Image Data セクション
            if (offset + 2 > data.Length) return null;
            int compress = ReadUInt16BE(data, offset);
            offset += 2;

            int pixelCount = width * height;
            byte[][] chData = new byte[channels][];
            for (int ch = 0; ch < channels; ch++)
                chData[ch] = new byte[pixelCount];

            if (compress == 0) // Raw（無圧縮）
            {
                for (int ch = 0; ch < channels; ch++)
                {
                    if (offset + pixelCount > data.Length) return null;
                    Buffer.BlockCopy(data, offset, chData[ch], 0, pixelCount);
                    offset += pixelCount;
                }
            }
            else if (compress == 1) // PackBits RLE
            {
                // 行バイト数テーブル: channels * height 個の uint16
                int tableSize = channels * height;
                if (offset + tableSize * 2 > data.Length) return null;

                int[] rowBytes = new int[tableSize];
                for (int i = 0; i < tableSize; i++)
                {
                    rowBytes[i] = ReadUInt16BE(data, offset);
                    offset += 2;
                }

                for (int ch = 0; ch < channels; ch++)
                {
                    int pixelOffset = 0;
                    for (int row = 0; row < height; row++)
                    {
                        int cnt = rowBytes[ch * height + row];
                        if (offset + cnt > data.Length) return null;
                        PackBitsDecode(data, offset, cnt, chData[ch], pixelOffset, width);
                        offset += cnt;
                        pixelOffset += width;
                    }
                }
            }
            else
            {
                return null; // ZIP 非対応
            }

            // チャンネルプレーナー → BGRA インターリーブ変換
            byte[] pixels = new byte[pixelCount * 4];
            for (int i = 0; i < pixelCount; i++)
            {
                pixels[i * 4 + 0] = chData[2][i]; // B
                pixels[i * 4 + 1] = chData[1][i]; // G
                pixels[i * 4 + 2] = chData[0][i]; // R
                pixels[i * 4 + 3] = 255;           // A（不透明）
            }

            // BitmapSource を生成して PNG エンコード → byte[] で返す
            var bitmapSource = BitmapSource.Create(
                width, height,
                96, 96,
                PixelFormats.Bgra32,
                null,
                pixels,
                width * 4);

            using (var ms = new MemoryStream())
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
                encoder.Save(ms);
                return ms.ToArray();
            }
        }

        /// <summary>PackBits RLE デコード（1行分）</summary>
        private static void PackBitsDecode(
            byte[] src, int srcOffset, int srcLen,
            byte[] dst, int dstOffset, int expectedWidth)
        {
            int si     = srcOffset;
            int di     = dstOffset;
            int srcEnd = srcOffset + srcLen;
            int dstEnd = dstOffset + expectedWidth;

            while (si < srcEnd && di < dstEnd)
            {
                int n = (sbyte)src[si++]; // 符号付きで解釈
                if (n >= 0)
                {
                    // n=0〜127: 次の n+1 バイトをリテラルコピー
                    int count  = n + 1;
                    int actual = Math.Min(count, dstEnd - di);
                    Buffer.BlockCopy(src, si, dst, di, actual);
                    si += count;
                    di += actual;
                }
                else if (n != -128)
                {
                    // n=-1〜-127: 次の1バイトを (1-n) 回繰り返し
                    int  count  = 1 - n;
                    byte val    = src[si++];
                    int  actual = Math.Min(count, dstEnd - di);
                    for (int k = 0; k < actual; k++)
                        dst[di + k] = val;
                    di += actual;
                }
                // n == -128: NOP（スキップ）
            }
        }

        private static ushort ReadUInt16BE(byte[] data, int offset) =>
            (ushort)((data[offset] << 8) | data[offset + 1]);

        private static uint ReadUInt32BE(byte[] data, int offset) =>
            (uint)((data[offset] << 24) | (data[offset + 1] << 16)
                 | (data[offset + 2] << 8)  |  data[offset + 3]);
    }
}
