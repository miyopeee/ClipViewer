using System;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipViewer
{
    /// <summary>
    /// フィルタパイプライン設定のスナップショット。
    /// バックグラウンドスレッド（プリフェッチ）から参照するため、
    /// 適用時点の AppSettings 値をコピーして渡す。
    /// </summary>
    public class FilterParams
    {
        public bool                 MoireEnabled;     // Stage1 有効（Enabled && Mode != Off）
        public MoireFilterAlgorithm MoireMode;
        public int                  MoireStrength;    // 0-100
        public bool                 SharpenEnabled;   // Stage2 有効
        public double               SharpenRadius;    // 0.1-5.0 px
        public int                  SharpenAmount;    // 0-200 %
        public int                  SharpenThreshold; // 0-255

        public bool AnyEnabled => MoireEnabled || SharpenEnabled;

        /// <summary>キャッシュ一致判定用の署名文字列。</summary>
        public string Signature =>
            $"{MoireEnabled}|{MoireMode}|{MoireStrength}|{SharpenEnabled}|{SharpenRadius}|{SharpenAmount}|{SharpenThreshold}";
    }

    /// <summary>
    /// モアレ軽減フィルタパイプライン（v0.7.0 / F49/F50）。
    ///
    /// [Stage 1] Lanczos-3 ダウンスケール（間引き前ローパス＝モアレ軽減の本体）
    ///   MoireStrength(0-100) をカーネル幅倍率 1.0〜2.0 にマッピングする。
    ///   Stage1 無効時も Stage2 が有効なら倍率 1.0（標準の高品質縮小）でリサイズし、
    ///   表示サイズの画素面を Stage2 に供給する。
    /// [Stage 2] アンシャープマスク（Stage1 のぼけ対策）
    ///   SharpenThreshold 未満のコントラスト差はシャープ化対象外（リンギング防止）。
    ///
    /// Area / Gaussian アルゴリズムは Phase 3 で追加予定（現状は Lanczos で代替）。
    /// 全メソッドはピクセル配列（Bgra32）に対する純関数で、Freeze 済み BitmapSource を
    /// 入出力とするためバックグラウンドスレッドから安全に呼び出せる。
    /// </summary>
    public static class ImageFilters
    {
        /// <summary>
        /// 2段パイプラインを適用し、targetW × targetH の Freeze 済み BitmapSource を返す。
        /// src は Freeze 済みであること。
        /// </summary>
        public static BitmapSource ApplyPipeline(BitmapSource src, int targetW, int targetH, FilterParams p)
        {
            BitmapSource bgra = (src.Format == PixelFormats.Bgra32)
                ? src
                : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);

            int sw = bgra.PixelWidth;
            int sh = bgra.PixelHeight;
            var pixels = new byte[sw * 4 * sh];
            bgra.CopyPixels(pixels, sw * 4, 0);

            // Stage 1: Lanczos-3 リサイズ
            // 強度 0 → 倍率1.0（標準 Lanczos）、100 → 倍率2.0（強ローパス）。既定 40 → 1.4。
            double support = p.MoireEnabled ? 1.0 + p.MoireStrength / 100.0 : 1.0;
            byte[] resized = (targetW != sw || targetH != sh)
                ? LanczosResize(pixels, sw, sh, targetW, targetH, support)
                : pixels;

            // Stage 2: アンシャープマスク
            if (p.SharpenEnabled && p.SharpenAmount > 0)
                resized = UnsharpMask(resized, targetW, targetH, p.SharpenRadius, p.SharpenAmount, p.SharpenThreshold);

            var result = BitmapSource.Create(
                targetW, targetH, 96, 96, PixelFormats.Bgra32, null, resized, targetW * 4);
            result.Freeze();
            return result;
        }

        // =========================================================
        // Stage 1: Lanczos-3 リサイズ（分離可能・並列）
        // =========================================================

        private static byte[] LanczosResize(byte[] src, int sw, int sh, int tw, int th, double support)
        {
            byte[] horz = ResampleAxis(src, sw, sh, tw, horizontal: true,  support);
            return        ResampleAxis(horz, tw, sh, th, horizontal: false, support);
        }

        /// <summary>
        /// 分離可能 Lanczos-3 の1軸リサンプリング。
        /// horizontal=true: (w,h)→(target,h) / false: (w,h)→(w,target)
        /// </summary>
        private static byte[] ResampleAxis(byte[] src, int w, int h, int target, bool horizontal, double support)
        {
            int    srcLen = horizontal ? w : h;
            double scale  = target / (double)srcLen;
            // ダウンスケール時はカーネルをソース空間へ拡大（間引き前ローパス）。support が強度倍率。
            double filterScale = Math.Max(1.0 / scale, 1.0) * support;
            double radius      = 3.0 * filterScale;

            // 出力位置ごとの重みテーブルを事前計算（全行・全列で共通）
            var starts  = new int[target];
            var weights = new float[target][];
            for (int o = 0; o < target; o++)
            {
                double center = (o + 0.5) / scale - 0.5;
                int s0 = (int)Math.Ceiling(center - radius);
                int s1 = (int)Math.Floor(center + radius);
                if (s0 < 0)          s0 = 0;
                if (s1 > srcLen - 1) s1 = srcLen - 1;
                int n  = Math.Max(1, s1 - s0 + 1);
                var wt = new float[n];
                double sum = 0;
                for (int i = 0; i < n; i++)
                {
                    double v = Lanczos3((s0 + i - center) / filterScale);
                    wt[i] = (float)v;
                    sum  += v;
                }
                if (sum > 1e-8)
                    for (int i = 0; i < n; i++) wt[i] = (float)(wt[i] / sum);
                starts[o]  = s0;
                weights[o] = wt;
            }

            int outW = horizontal ? target : w;
            int outH = horizontal ? h : target;
            var dst  = new byte[outW * 4 * outH];

            if (horizontal)
            {
                Parallel.For(0, h, y =>
                {
                    int srcRow = y * w * 4;
                    int dstRow = y * outW * 4;
                    for (int o = 0; o < target; o++)
                    {
                        float[] wt = weights[o];
                        int     s0 = starts[o];
                        float b = 0, g = 0, r = 0, a = 0;
                        for (int i = 0; i < wt.Length; i++)
                        {
                            int   si = srcRow + (s0 + i) * 4;
                            float f  = wt[i];
                            b += src[si]     * f;
                            g += src[si + 1] * f;
                            r += src[si + 2] * f;
                            a += src[si + 3] * f;
                        }
                        int di = dstRow + o * 4;
                        dst[di]     = ClampByte(b);
                        dst[di + 1] = ClampByte(g);
                        dst[di + 2] = ClampByte(r);
                        dst[di + 3] = ClampByte(a);
                    }
                });
            }
            else
            {
                int rowBytes = w * 4;
                Parallel.For(0, target, o =>
                {
                    float[] wt  = weights[o];
                    int     s0  = starts[o];
                    var     acc = new float[rowBytes];
                    for (int i = 0; i < wt.Length; i++)
                    {
                        float f  = wt[i];
                        int   si = (s0 + i) * rowBytes;
                        for (int x = 0; x < rowBytes; x++)
                            acc[x] += src[si + x] * f;
                    }
                    int di = o * rowBytes;
                    for (int x = 0; x < rowBytes; x++)
                        dst[di + x] = ClampByte(acc[x]);
                });
            }
            return dst;
        }

        /// <summary>Lanczos-3 カーネル関数。</summary>
        private static double Lanczos3(double x)
        {
            x = Math.Abs(x);
            if (x < 1e-8) return 1.0;
            if (x >= 3.0) return 0.0;
            double px = Math.PI * x;
            return 3.0 * Math.Sin(px) * Math.Sin(px / 3.0) / (px * px);
        }

        // =========================================================
        // Stage 2: アンシャープマスク（閾値付き・並列）
        // =========================================================

        private static byte[] UnsharpMask(byte[] src, int w, int h, double radius, int amountPct, int threshold)
        {
            float[] kernel = BuildGaussianKernel(radius, out int kr);
            byte[]  blur   = GaussianBlur(src, w, h, kernel, kr);

            float amount = amountPct / 100f;
            var   dst    = new byte[src.Length];

            Parallel.For(0, h, y =>
            {
                int row = y * w * 4;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 4;
                    for (int c = 0; c < 3; c++)  // B, G, R（A はそのまま）
                    {
                        int diff = src[i + c] - blur[i + c];
                        dst[i + c] = (Math.Abs(diff) < threshold)
                            ? src[i + c]
                            : ClampByte(src[i + c] + diff * amount);
                    }
                    dst[i + 3] = src[i + 3];
                }
            });
            return dst;
        }

        private static float[] BuildGaussianKernel(double radius, out int kr)
        {
            double sigma = Math.Max(0.1, radius);
            kr = Math.Max(1, (int)Math.Ceiling(sigma * 2.5));
            var k = new float[kr * 2 + 1];
            double sum = 0;
            for (int i = -kr; i <= kr; i++)
            {
                double v = Math.Exp(-(i * (double)i) / (2 * sigma * sigma));
                k[i + kr] = (float)v;
                sum      += v;
            }
            for (int i = 0; i < k.Length; i++) k[i] = (float)(k[i] / sum);
            return k;
        }

        private static byte[] GaussianBlur(byte[] src, int w, int h, float[] kernel, int kr)
        {
            var tmp = new byte[src.Length];
            var dst = new byte[src.Length];

            // 横パス（端はエッジクランプ）
            Parallel.For(0, h, y =>
            {
                int row = y * w * 4;
                for (int x = 0; x < w; x++)
                {
                    float b = 0, g = 0, r = 0, a = 0;
                    for (int i = -kr; i <= kr; i++)
                    {
                        int sx = x + i;
                        if (sx < 0) sx = 0; else if (sx >= w) sx = w - 1;
                        int   si = row + sx * 4;
                        float f  = kernel[i + kr];
                        b += src[si]     * f;
                        g += src[si + 1] * f;
                        r += src[si + 2] * f;
                        a += src[si + 3] * f;
                    }
                    int di = row + x * 4;
                    tmp[di]     = ClampByte(b);
                    tmp[di + 1] = ClampByte(g);
                    tmp[di + 2] = ClampByte(r);
                    tmp[di + 3] = ClampByte(a);
                }
            });

            // 縦パス
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    float b = 0, g = 0, r = 0, a = 0;
                    for (int i = -kr; i <= kr; i++)
                    {
                        int sy = y + i;
                        if (sy < 0) sy = 0; else if (sy >= h) sy = h - 1;
                        int   si = (sy * w + x) * 4;
                        float f  = kernel[i + kr];
                        b += tmp[si]     * f;
                        g += tmp[si + 1] * f;
                        r += tmp[si + 2] * f;
                        a += tmp[si + 3] * f;
                    }
                    int di = (y * w + x) * 4;
                    dst[di]     = ClampByte(b);
                    dst[di + 1] = ClampByte(g);
                    dst[di + 2] = ClampByte(r);
                    dst[di + 3] = ClampByte(a);
                }
            });
            return dst;
        }

        private static byte ClampByte(float v)
            => (v <= 0f) ? (byte)0 : (v >= 255f) ? (byte)255 : (byte)(v + 0.5f);
    }
}
