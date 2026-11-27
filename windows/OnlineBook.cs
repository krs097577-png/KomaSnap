using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

sealed class OnlineBook : ReaderBook
{
    readonly OnlineManga.Source source;
    readonly OnlineManga.Page[] images;
    readonly BitmapSource firstPage;
    readonly Uri chapterPage;
    readonly bool trim;
    public readonly string Title;

    OnlineBook(OnlineManga.Entry manga, OnlineManga.Entry chapter, OnlineManga.Page[] images, BitmapSource first,
        bool trim)
        : base(chapter.PageUrl.AbsoluteUri, images.Select((page, index) => page.Width > 0 && (!trim || index != 0) ?
            new Size(page.Width, page.Height) : new Size(first.PixelWidth, first.PixelHeight)).ToArray())
    {
        source = chapter.Source;
        this.images = images;
        firstPage = first;
        chapterPage = chapter.PageUrl;
        this.trim = trim;
        Title = manga.Title + " · " + chapter.Title;
    }

    public static async Task<OnlineBook> OpenChapter(OnlineManga.Entry manga, OnlineManga.Entry chapter,
        CancellationToken token, bool trim, OnlineManga.Page[] prepared = null)
    {
        var pages = prepared ?? await OnlineManga.PageImages(chapter, token).ConfigureAwait(false);
        if (pages.Length == 0)
            throw new IOException("来源没有提供可读取的页面。");
        if (pages.Length > 2000) throw new IOException("来源返回的页数过多。");
        var first = await FetchPage(pages[0].Url, chapter.Source, chapter.PageUrl, 3200, trim, token).ConfigureAwait(false);
        return new OnlineBook(manga, chapter, pages, first, trim);
    }

    protected override async Task<BitmapSource> RenderPageAsync(int index, int width, CancellationToken token)
    {
        BitmapSource bitmap = index == 0 ? firstPage : null;
        if (bitmap == null)
            bitmap = await FetchPage(images[index].Url, source, chapterPage, width, trim, token);
        token.ThrowIfCancellationRequested();
        var size = SizeOf(index);
        int height = Math.Max(1, (int)Math.Round(width * size.Height / size.Width));
        if (Math.Abs(bitmap.PixelWidth * (double)height / bitmap.PixelHeight - width) <= 1 &&
            bitmap.PixelWidth <= width) return bitmap;

        // 站点不总是公开每张图片的尺寸。比例不同时留出纸张空白，保持漫画原图比例。
        double scale = Math.Min((double)width / bitmap.PixelWidth, (double)height / bitmap.PixelHeight);
        double w = bitmap.PixelWidth * scale, h = bitmap.PixelHeight * scale;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            drawing.DrawImage(bitmap, new Rect((width - w) / 2, (height - h) / 2, w, h));
        }
        var fitted = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        fitted.Render(visual);
        fitted.Freeze();
        return fitted;
    }

    static Task<BitmapSource> FetchPage(Uri url, OnlineManga.Source source, Uri chapter, int width, bool trim,
        CancellationToken token)
    {
        var options = source.Rule == null ? null : source.Rule.RequestHeaders(url, chapter, image: true);
        return FetchBitmap(url, source.Referer, width, token, trim: trim, options: options);
    }

    internal static async Task<BitmapSource> FetchBitmap(Uri url, Uri site, int width, CancellationToken token,
        int maxBytes = 20 * 1024 * 1024, int maxHeight = 3200, bool trim = false,
        OnlineManga.RequestOptions options = null)
    {
        using (var bytes = await OnlineManga.Get(url, site, maxBytes, true, token, options).ConfigureAwait(false))
        {
            return await Task.Run<BitmapSource>(() =>
            {
                token.ThrowIfCancellationRequested();
                var frame = BitmapDecoder.Create(bytes, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                int originalWidth = frame.PixelWidth, originalHeight = frame.PixelHeight;
                if (originalWidth <= 0 || originalHeight <= 0 || originalWidth * (long)originalHeight > 64000000)
                    throw new IOException("漫画图片尺寸超出支持范围。");
                int target = Math.Max(1, (int)Math.Min(Math.Min(width, originalWidth),
                    (double)maxHeight * originalWidth / originalHeight));
                bytes.Position = 0;
                var image = new BitmapImage();
                image.BeginInit();
                image.StreamSource = bytes;
                image.CacheOption = BitmapCacheOption.OnLoad;
                if (target < originalWidth) image.DecodePixelWidth = target;
                image.EndInit();
                image.Freeze();
                return trim ? TrimMargins(image) : image;
            }, token).ConfigureAwait(false);
        }
    }

    static BitmapSource TrimMargins(BitmapSource image)
    {
        const int sampleSize = 480;
        int width = image.PixelWidth, height = image.PixelHeight;
        if (width < 64 || height < 64) return image;
        double scale = Math.Min(1, sampleSize / (double)Math.Max(width, height));
        BitmapSource sample = image;
        if (scale < 1) sample = new TransformedBitmap(sample, new ScaleTransform(scale, scale));
        if (sample.Format != PixelFormats.Bgra32)
            sample = new FormatConvertedBitmap(sample, PixelFormats.Bgra32, null, 0);
        int w = sample.PixelWidth, h = sample.PixelHeight, stride = w * 4;
        var pixels = new byte[stride * h];
        sample.CopyPixels(pixels, stride, 0);
        int[] corners = { 0, (w - 1) * 4, (h - 1) * stride, (h - 1) * stride + (w - 1) * 4 };
        bool white = corners.Count(index => IsPaper(pixels, index, true)) >= 3;
        if (!white && corners.Count(index => IsPaper(pixels, index, false)) < 3) return image;
        int left = w, right = -1, top = h, bottom = -1;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (IsPaper(pixels, y * stride + x * 4, white)) continue;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = y;
            }
        }
        if ((right - left + 1) * 2 < w || (bottom - top + 1) * 2 < h) return image;
        int x0 = Math.Max(0, (int)Math.Floor((left / (double)w - .015) * width));
        int y0 = Math.Max(0, (int)Math.Floor((top / (double)h - .015) * height));
        int x1 = Math.Min(width, (int)Math.Ceiling(((right + 1.0) / w + .015) * width));
        int y1 = Math.Min(height, (int)Math.Ceiling(((bottom + 1.0) / h + .015) * height));
        if (x0 == 0 && y0 == 0 && x1 == width && y1 == height) return image;
        var cropped = new CroppedBitmap(image, new Int32Rect(x0, y0, x1 - x0, y1 - y0));
        cropped.Freeze();
        return cropped;
    }

    static bool IsPaper(byte[] pixels, int offset, bool white)
    {
        if (pixels[offset + 3] < 16) return true;
        return white ? pixels[offset] > 235 && pixels[offset + 1] > 235 && pixels[offset + 2] > 235 :
            pixels[offset] < 20 && pixels[offset + 1] < 20 && pixels[offset + 2] < 20;
    }
}

