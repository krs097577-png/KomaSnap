using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

abstract class ReaderBook
{
    public const long CacheLimit = 160L * 1024 * 1024;
    readonly Size[] sizes;
    readonly Dictionary<Tuple<int, int>, BitmapSource> cache = new Dictionary<Tuple<int, int>, BitmapSource>();
    readonly LinkedList<Tuple<int, int>> recency = new LinkedList<Tuple<int, int>>();
    readonly Dictionary<Tuple<int, int>, PendingPage> pending = new Dictionary<Tuple<int, int>, PendingPage>();
    readonly SemaphoreSlim renderSlots = new SemaphoreSlim(2);
    long cachedBytes;

    sealed class PendingPage
    {
        public readonly CancellationTokenSource Lifetime = new CancellationTokenSource();
        public Task<BitmapSource> Result;
        public int Readers;
    }

    protected ReaderBook(string path, Size[] sizes, bool? preferredRightToLeft = null)
    {
        Path = path;
        this.sizes = sizes;
        PreferredRightToLeft = preferredRightToLeft;
    }

    public string Path { get; private set; }
    public bool? PreferredRightToLeft { get; private set; }
    public int Count { get { return sizes.Length; } }
    public Size SizeOf(int page) { return sizes[page]; }

    public static bool Supports(string path)
    {
        switch (System.IO.Path.GetExtension(path).ToLowerInvariant())
        {
            case ".pdf": case ".epub": case ".cbz": case ".zip": return true;
            default: return false;
        }
    }

    public static async Task<ReaderBook> OpenAsync(string path, CancellationToken token)
    {
        switch (System.IO.Path.GetExtension(path).ToLowerInvariant())
        {
            case ".pdf": return await PdfBook.LoadAsync(path, token);
            case ".epub": return await ArchiveBook.LoadAsync(path, true, token);
            case ".cbz": case ".zip": return await ArchiveBook.LoadAsync(path, false, token);
            default: throw new InvalidDataException("支持打开 PDF、EPUB、CBZ 和图片 ZIP。");
        }
    }

    bool IsWide(int index)
    {
        return sizes[index].Width >= sizes[index].Height * 1.1;
    }

    public List<int[]> MakeSpreads(bool dual, bool soloCover)
    {
        var spreads = new List<int[]>();
        for (int i = 0; i < Count;)
        {
            bool canPair = dual && !(soloCover && i == 0) && i + 1 < Count &&
                !IsWide(i) && !IsWide(i + 1);
            if (canPair)
            {
                spreads.Add(new[] { i, i + 1 });
                i += 2;
            }
            else spreads.Add(new[] { i++ });
        }
        return spreads;
    }

    int RenderWidth(int index, double screenWidth)
    {
        Size size = sizes[index];
        int maxWidth = Math.Max(1, (int)(3200 * Math.Min(1, size.Width / size.Height)));
        int target = (int)(Math.Ceiling(Math.Min(maxWidth, Math.Max(1, screenWidth)) / 128) * 128);
        return Math.Min(maxWidth, target);
    }

    public long EstimatedRenderBytes(int index, double screenWidth)
    {
        int width = RenderWidth(index, screenWidth);
        return width * (long)Math.Ceiling(width * sizes[index].Height / sizes[index].Width) * 4;
    }

    public bool TryGetCached(int page, double screenWidth, out BitmapSource bitmap)
    {
        return cache.TryGetValue(Tuple.Create(page, RenderWidth(page, screenWidth)), out bitmap);
    }

    public void ClearCache()
    {
        cache.Clear();
        recency.Clear();
        cachedBytes = 0;
        // 取消后续写入。取消回调可能同步结束解码并修改 pending。
        var jobs = new List<PendingPage>(pending.Values);
        pending.Clear();
        foreach (var job in jobs) job.Lifetime.Cancel();
    }

    public async Task<BitmapSource> RenderAsync(int index, double screenWidth, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var key = Tuple.Create(index, RenderWidth(index, screenWidth));
        BitmapSource bitmap;
        if (cache.TryGetValue(key, out bitmap))
        {
            recency.Remove(key);
            recency.AddFirst(key);
            return bitmap;
        }

        PendingPage job;
        if (!pending.TryGetValue(key, out job))
        {
            job = new PendingPage();
            pending.Add(key, job);
            job.Result = RenderAndCache(key, job);
        }
        job.Readers++;
        try
        {
            if (!job.Result.IsCompleted && token.CanBeCanceled)
            {
                var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (token.Register(() => cancelled.TrySetResult(true)))
                    await Task.WhenAny(job.Result, cancelled.Task);
                token.ThrowIfCancellationRequested();
            }
            return await job.Result;
        }
        finally
        {
            if (--job.Readers == 0 && !job.Result.IsCompleted)
            {
                Forget(key, job);
                job.Lifetime.Cancel();
                ObserveAbandonedRender(job.Result);
            }
        }
    }

    static void ObserveAbandonedRender(Task<BitmapSource> render)
    {
        // 原生渲染可能在取消时返回异常，仍需观察已无人等待的任务。
        render.ContinueWith(task => GC.KeepAlive(task.Exception),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    async Task<BitmapSource> RenderAndCache(Tuple<int, int> key, PendingPage job)
    {
        var token = job.Lifetime.Token;
        try
        {
            await renderSlots.WaitAsync(token);
            BitmapSource bitmap;
            try { bitmap = await RenderPageAsync(key.Item1, key.Item2, token); }
            finally { renderSlots.Release(); }
            token.ThrowIfCancellationRequested();
            AddToCache(key, bitmap);
            return bitmap;
        }
        finally
        {
            Forget(key, job);
            job.Lifetime.Dispose();
        }
    }

    void Forget(Tuple<int, int> key, PendingPage job)
    {
        PendingPage current;
        if (pending.TryGetValue(key, out current) && current == job) pending.Remove(key);
    }

    void AddToCache(Tuple<int, int> key, BitmapSource bitmap)
    {
        cache.Add(key, bitmap);
        recency.AddFirst(key);
        cachedBytes += Cost(bitmap);
        while (recency.Count > 16 || cachedBytes > CacheLimit)
        {
            var old = recency.Last.Value;
            cachedBytes -= Cost(cache[old]);
            cache.Remove(old);
            recency.RemoveLast();
        }
    }

    protected abstract Task<BitmapSource> RenderPageAsync(int index, int width, CancellationToken token);

    static long Cost(BitmapSource bitmap)
    {
        return (long)bitmap.PixelWidth * bitmap.PixelHeight * 4;
    }
}
