using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RevitSketchPoC.Chat.Views
{
    /// <summary>
    /// Converts clipboard <see cref="BitmapSource"/> (often native InteropBitmap) into PNG bytes via a
    /// fully realized <see cref="RenderTargetBitmap"/> to avoid native crashes in encoders / Revit host.
    /// </summary>
    internal static class ClipboardImageEncoder
    {
        private const int MaxEdgePixels = 4096;
        private const int MaxTotalPixels = 12_000_000;

        internal static byte[]? TryEncodeToPng(BitmapSource? src)
        {
            if (src == null)
            {
                return null;
            }

            try
            {
                var w0 = Math.Max(1, src.PixelWidth);
                var h0 = Math.Max(1, src.PixelHeight);
                var scale = 1.0;
                if (w0 > MaxEdgePixels || h0 > MaxEdgePixels)
                {
                    scale = Math.Min((double)MaxEdgePixels / w0, (double)MaxEdgePixels / h0);
                }

                var w = Math.Max(1, (int)Math.Round(w0 * scale));
                var h = Math.Max(1, (int)Math.Round(h0 * scale));
                if ((long)w * h > MaxTotalPixels)
                {
                    var s2 = Math.Sqrt((double)MaxTotalPixels / (w * h));
                    w = Math.Max(1, (int)Math.Round(w * s2));
                    h = Math.Max(1, (int)Math.Round(h * s2));
                }

                BitmapSource drawSource;
                try
                {
                    var converted = new FormatConvertedBitmap(src, PixelFormats.Pbgra32, null, 0.0);
                    converted.Freeze();
                    drawSource = converted;
                }
                catch
                {
                    drawSource = src;
                }

                var dpiX = drawSource.DpiX > 1 ? drawSource.DpiX : 96.0;
                var dpiY = drawSource.DpiY > 1 ? drawSource.DpiY : 96.0;
                var rtb = new RenderTargetBitmap(w, h, dpiX, dpiY, PixelFormats.Pbgra32);
                var dv = new DrawingVisual();
                using (var ctx = dv.RenderOpen())
                {
                    ctx.DrawImage(drawSource, new Rect(0, 0, w, h));
                }

                rtb.Render(dv);
                rtb.Freeze();

                using var ms = new MemoryStream();
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                enc.Save(ms);
                return ms.ToArray();
            }
            catch
            {
                return null;
            }
        }
    }
}
