using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

sealed class PdfBook : ReaderBook
{
    const long FastBitmapBudget = 32L * 1024 * 1024;
    readonly PdfDocument document;

    PdfBook(string path, PdfDocument document, Size[] sizes) : base(path, sizes)
    {
        this.document = document;
    }

    public static Task<PdfBook> LoadAsync(string path, CancellationToken token)
    {
        // 系统 PDF 对象在后台线程创建。
        return Task.Run(async () =>
        {
            var file = await StorageFile.GetFileFromPathAsync(path).AsTask(token).ConfigureAwait(false);
            var pdf = await PdfDocument.LoadFromFileAsync(file).AsTask(token).ConfigureAwait(false);
            if (pdf.PageCount == 0) throw new InvalidDataException("PDF 没有可读页面。");
            var dimensions = new Size[checked((int)pdf.PageCount)];
            for (int i = 0; i < dimensions.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                using (var page = pdf.GetPage((uint)i))
                    dimensions[i] = new Size(Math.Max(1, page.Size.Width), Math.Max(1, page.Size.Height));
            }
            return new PdfBook(path, pdf, dimensions);
        }, token);
    }

    protected override Task<BitmapSource> RenderPageAsync(int index, int width, CancellationToken token)
    {
        return Task.Run<BitmapSource>(async () =>
        {
            token.ThrowIfCancellationRequested();
            using (var page = document.GetPage((uint)index))
            using (var stream = new InMemoryRandomAccessStream())
            {
                var options = new PdfPageRenderOptions { DestinationWidth = (uint)width };
                // 中等尺寸直接使用位图，省掉 PNG 压缩。大页保留压缩路径，限制暂存内存。
                if (EstimatedRenderBytes(index, width) <= FastBitmapBudget / 2)
                    options.BitmapEncoderId = Windows.Graphics.Imaging.BitmapEncoder.BmpEncoderId;
                await page.RenderToStreamAsync(stream, options).AsTask(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                stream.Seek(0);
                using (var input = stream.AsStreamForRead())
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.DecodePixelWidth = width;
                    image.StreamSource = input;
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
            }
        }, token);
    }
}

