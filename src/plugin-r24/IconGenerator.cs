using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace RevitMcp.Plugin
{
    public static class IconGenerator
    {
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        private static BitmapSource BitmapToBitmapSource(Bitmap bitmap)
        {
            var hBitmap = bitmap.GetHbitmap();
            try
            {
                return Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }

        public static BitmapSource CreateCircleIcon(int size, Color color)
        {
            using (var bmp = new Bitmap(size, size))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var brush = new SolidBrush(color))
                {
                    var margin = size / 8;
                    g.FillEllipse(brush, margin, margin, size - 2 * margin, size - 2 * margin);
                }
                return BitmapToBitmapSource(bmp);
            }
        }

        public static BitmapSource CreateHistoryIcon(int size)
        {
            using (var bmp = new Bitmap(size, size))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var pen = new Pen(Color.FromArgb(60, 60, 60), Math.Max(size / 12f, 1.5f)))
                {
                    var m = size / 6;
                    var lineH = (size - 2 * m) / 4f;
                    for (int i = 0; i < 4; i++)
                    {
                        var y = m + (int)(i * lineH + lineH / 2);
                        g.DrawLine(pen, m, y, size - m, y);
                    }
                }
                return BitmapToBitmapSource(bmp);
            }
        }

        public static BitmapSource CreateInfoIcon(int size)
        {
            using (var bmp = new Bitmap(size, size))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                var m = size / 8;
                using (var pen = new Pen(Color.FromArgb(70, 130, 180), Math.Max(size / 10f, 1.5f)))
                    g.DrawEllipse(pen, m, m, size - 2 * m, size - 2 * m);
                using (var font = new Font("Arial", size / 3f, System.Drawing.FontStyle.Bold))
                using (var brush = new SolidBrush(Color.FromArgb(70, 130, 180)))
                {
                    var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString("i", font, brush, size / 2f, size / 2f, sf);
                }
                return BitmapToBitmapSource(bmp);
            }
        }

        public static BitmapSource CreateChatIcon(int size)
        {
            using (var bmp = new Bitmap(size, size))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                var m = size / 8;
                var bubbleW = size - 2 * m;
                var bubbleH = (int)(bubbleW * 0.7f);
                var bubbleX = m;
                var bubbleY = m;

                // Draw rounded chat bubble body
                using (var brush = new SolidBrush(Color.FromArgb(33, 150, 243)))
                {
                    var radius = Math.Max(size / 6, 2);
                    var rect = new Rectangle(bubbleX, bubbleY, bubbleW, bubbleH);
                    var path = new System.Drawing.Drawing2D.GraphicsPath();
                    path.AddArc(rect.Left, rect.Top, radius * 2, radius * 2, 180, 90);
                    path.AddArc(rect.Right - radius * 2, rect.Top, radius * 2, radius * 2, 270, 90);
                    path.AddArc(rect.Right - radius * 2, rect.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
                    // Tail at bottom-left
                    var tailX = bubbleX + radius;
                    var tailTip = bubbleY + bubbleH + (int)(size * 0.15f);
                    path.AddLine(rect.Right - radius * 2, rect.Bottom, tailX + radius, rect.Bottom);
                    path.AddLine(tailX + radius, rect.Bottom, tailX - radius / 2, tailTip);
                    path.AddLine(tailX - radius / 2, tailTip, tailX, rect.Bottom);
                    path.AddArc(rect.Left, rect.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
                    path.CloseFigure();
                    g.FillPath(brush, path);
                }

                // Draw three dots inside bubble
                using (var brush = new SolidBrush(Color.White))
                {
                    var dotR = Math.Max(size / 14, 1);
                    var dotY = bubbleY + bubbleH / 2 - dotR;
                    var spacing = bubbleW / 4;
                    g.FillEllipse(brush, bubbleX + spacing - dotR, dotY, dotR * 2, dotR * 2);
                    g.FillEllipse(brush, bubbleX + spacing * 2 - dotR, dotY, dotR * 2, dotR * 2);
                    g.FillEllipse(brush, bubbleX + spacing * 3 - dotR, dotY, dotR * 2, dotR * 2);
                }

                return BitmapToBitmapSource(bmp);
            }
        }

        // Pre-generated icons cached for Ribbon use
        public static BitmapSource McpOn32 { get; } = CreateCircleIcon(32, Color.FromArgb(76, 175, 80));
        public static BitmapSource McpOn16 { get; } = CreateCircleIcon(16, Color.FromArgb(76, 175, 80));
        public static BitmapSource McpOff32 { get; } = CreateCircleIcon(32, Color.FromArgb(244, 67, 54));
        public static BitmapSource McpOff16 { get; } = CreateCircleIcon(16, Color.FromArgb(244, 67, 54));
        public static BitmapSource History32 { get; } = CreateHistoryIcon(32);
        public static BitmapSource History16 { get; } = CreateHistoryIcon(16);
        public static BitmapSource Info32 { get; } = CreateInfoIcon(32);
        public static BitmapSource Info16 { get; } = CreateInfoIcon(16);
        public static BitmapSource Chat32 { get; } = CreateChatIcon(32);
        public static BitmapSource Chat16 { get; } = CreateChatIcon(16);
    }
}
