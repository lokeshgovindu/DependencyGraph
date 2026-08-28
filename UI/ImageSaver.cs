using System.IO;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Size = System.Windows.Size;

namespace DependencyGraph.UI
{
    public class ImageSaver
    {
        /// <summary>Saves given element as png image into given stream using standard file dialog box.</summary>
        /// <param name="v">Element to be saved.</param>
        /// <param name="defaultName">Name for saved file. Do not provide extension (.png).</param>
        /// <param name="defaultLocation">Initial location of dialog box.</param>
        /// <param name="sourceRect">
        /// Region of the element to capture, in the element's post-render-transform coordinates.
        /// Pass <see cref="Rect.Empty"/> to fall back to the element's measured bounds.
        /// </param>
        public static bool AsPNG(FrameworkElement v, Rect sourceRect,
            string defaultName = null, string defaultLocation = null)
        {
            var sd = new SaveFileDialog()
            {
                FileName = (defaultName ?? "New Image") + ".png",
                InitialDirectory = defaultLocation,
                Filter = "PNG image file (*.png)|*.png|All files (*.*)|*.*"
            };
            if (sd.ShowDialog() != DialogResult.OK) return false;
            return AsPNG(v, sd.FileName, sourceRect);
        }

        public static bool AsPNG(FrameworkElement element, string fileName)
        {
            return AsPNG(element, fileName, Rect.Empty);
        }

        public static bool AsPNG(FrameworkElement element, string fileName, Rect sourceRect)
        {
            var image = GetImage(element, sourceRect);
            if (image == null) return false;

            using (var fs = File.Open(fileName, FileMode.Create))
            {
                AsPNG(image, fs);
            }
            return true;
        }

        /// <summary>Saves given element as png image into given stream.</summary>
        /// <param name="v">Element to be saved.</param>
        /// <param name="output">Stream where image should be saved.</param>
        public static void AsPNG(FrameworkElement v, Stream output)
        {
            var image = GetImage(v, Rect.Empty);
            if (image != null) AsPNG(image, output);
        }

        /// <summary>Saves givnen bitma as png image into given stream.</summary>
        /// <param name="src">Bitmap to be saved.</param>
        /// <param name="outputStream">Stream where image should be saved.</param>
        public static void AsPNG(BitmapSource src, Stream outputStream)
        {
            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(src));
            encoder.Save(outputStream);
        }

        /// <summary>Padding around the graph in the saved image, in device-independent pixels.</summary>
        private const double Margin = 12;

        /// <summary>Largest image we will produce: a side limit and a total-area limit.</summary>
        private const double MaxDimension = 10000;
        private const double MaxPixels = 40e6;

        /// <summary>Factor to shrink the given size by so it stays within the image limits.</summary>
        private static double FitWithinLimits(Size size)
        {
            if (size.Width <= 0 || size.Height <= 0) return 1.0;

            double scale = 1.0;

            double longest = System.Math.Max(size.Width, size.Height);
            if (longest > MaxDimension) scale = MaxDimension / longest;

            double area = (size.Width * scale) * (size.Height * scale);
            if (area > MaxPixels) scale *= System.Math.Sqrt(MaxPixels / area);

            return scale;
        }

        /// <summary>
        /// Renders the element at its full extent, not just the part scrolled into view.
        /// The graph canvas is larger than its viewport and carries the pan/zoom transform, so
        /// measuring ActualWidth/ActualHeight would silently crop everything off screen.
        /// </summary>
        public static RenderTargetBitmap GetImage(FrameworkElement v, Rect sourceRect)
        {
            if (v == null) return null;

            Rect bounds = sourceRect;
            if (bounds.IsEmpty) bounds = VisualTreeHelper.GetDescendantBounds(v);
            if (bounds.IsEmpty) bounds = new Rect(new Size(v.ActualWidth, v.ActualHeight));

            if (bounds.Width <= 0 || bounds.Height <= 0) return null;

            bounds.Inflate(Margin, Margin);

            // The capture is at the zoom currently on screen, so a zoomed-in view of a large
            // graph could ask for a bitmap of hundreds of megapixels and throw. Scale the output
            // down instead of failing - Stretch.Fill resamples for free.
            double scale = FitWithinLimits(bounds.Size);

            var dv = new DrawingVisual();
            using (DrawingContext context = dv.RenderOpen())
            {
                var target = new Rect(0, 0, bounds.Width * scale, bounds.Height * scale);

                // The element's own background does not cover the inflated margin, and a
                // transparent PNG of a graph is not much use, so paint the ground first.
                context.DrawRectangle(BackgroundOf(v), null, target);

                // Stretch.Fill against a destination the same size as the viewbox is an exact
                // 1:1 crop - and unlike Stretch.None it honours a viewbox that does not start
                // at the origin, which matters because MSAGL lays content out at negative
                // coordinates.
                context.DrawRectangle(
                    new VisualBrush(v)
                    {
                        Stretch      = Stretch.Fill,
                        ViewboxUnits = BrushMappingMode.Absolute,
                        Viewbox      = bounds,
                    },
                    pen: null,
                    rectangle: target);
            }

            var (x, y) = GetDPI(v);
            var b = new RenderTargetBitmap(
                (int)System.Math.Ceiling(bounds.Width * scale * x / 96.0),
                (int)System.Math.Ceiling(bounds.Height * scale * y / 96.0),
                x, y, PixelFormats.Pbgra32);
            b.Render(dv);
            return b;
        }

        /// <summary>
        /// First explicit background going up from the element, so the saved image is filled with
        /// the same colour the graph sits on rather than an arbitrary white.
        /// </summary>
        private static Brush BackgroundOf(FrameworkElement v)
        {
            for (DependencyObject d = v; d != null; d = VisualTreeHelper.GetParent(d))
            {
                if (d is System.Windows.Controls.Panel panel && panel.Background != null)
                    return panel.Background;
                if (d is System.Windows.Controls.Control control && control.Background != null)
                    return control.Background;
            }
            return Brushes.White;
        }

        /// <summary>Returns current DPI based on given visual.</summary>
        /// <param name="v"></param>
        /// <returns></returns>
        public static (double x, double y) GetDPI(Visual v)
        {
            var src = PresentationSource.FromVisual(v);
            if (src == null) return (96, 96);
            return (
                96.0 * src.CompositionTarget.TransformToDevice.M11,
                96.0 * src.CompositionTarget.TransformToDevice.M22
            );
        }

    }
}
