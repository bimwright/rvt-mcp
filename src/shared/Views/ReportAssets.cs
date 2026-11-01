using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RvtMcp.Plugin.Views
{
    /// <summary>Original BIMwright vector artwork shared by Reports chrome and ribbon commands.</summary>
    internal static class ReportAssets
    {
        public static readonly SolidColorBrush Navy = Brush("#0C3F76");
        public static readonly SolidColorBrush Green = Brush("#589039");
        public static readonly DrawingImage Report = CreateReport();
        public static readonly DrawingImage Settings = CreateSettings();

        private static DrawingImage CreateReport()
        {
            var drawing = new DrawingGroup();
            using (var dc = drawing.Open())
            {
                dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 32, 32));
                dc.DrawGeometry(Brushes.White, Pen(Navy), Geometry.Parse("M7,3 L20,3 L26,9 L26,28 Q26,29 25,29 L7,29 Q6,29 6,28 L6,4 Q6,3 7,3 Z"));
                dc.DrawGeometry(null, Pen(Green), Geometry.Parse("M20,3 V9 H26 M11,14 H21 M11,19 H21 M11,24 H18"));
            }
            return Freeze(drawing);
        }

        private static DrawingImage CreateSettings()
        {
            var shape = new StreamGeometry();
            using (var path = shape.Open())
            {
                for (var i = 0; i < 64; i++)
                {
                    var angle = i * Math.PI / 32 - Math.PI / 2;
                    var radius = i % 8 < 4 ? 13 : 10.5;
                    var point = new Point(16 + Math.Cos(angle) * radius, 16 + Math.Sin(angle) * radius);
                    if (i == 0) path.BeginFigure(point, true, true);
                    else path.LineTo(point, true, false);
                }
            }
            var drawing = new DrawingGroup();
            using (var dc = drawing.Open())
            {
                dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 32, 32));
                dc.DrawGeometry(Brushes.White, Pen(Navy), shape);
                dc.DrawEllipse(Green, null, new Point(16, 16), 4, 4);
            }
            return Freeze(drawing);
        }

        public static BitmapSource Bitmap(DrawingImage image, int size)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) dc.DrawImage(image, new Rect(0, 0, size, size));
            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }

        private static DrawingImage Freeze(DrawingGroup drawing)
        {
            drawing.Freeze();
            var image = new DrawingImage(drawing);
            image.Freeze();
            return image;
        }
        private static Pen Pen(Brush brush) => new Pen(brush, 1.8) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        private static SolidColorBrush Brush(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
    }
}
