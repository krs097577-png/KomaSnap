using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace KomaSnap
{
    public sealed class OnlineCoverImage : Image
    {
        static readonly SemaphoreSlim Slots = new SemaphoreSlim(4);
        static readonly Dictionary<Tuple<Uri, Uri, OnlineRule>, BitmapSource> Cache =
            new Dictionary<Tuple<Uri, Uri, OnlineRule>, BitmapSource>();
        static readonly Queue<Tuple<Uri, Uri, OnlineRule>> Order = new Queue<Tuple<Uri, Uri, OnlineRule>>();
        CancellationTokenSource loading;

        public OnlineCoverImage()
        {
            Loaded += (s, e) => LoadCover();
            DataContextChanged += (s, e) => { if (IsLoaded) LoadCover(); };
            Unloaded += (s, e) => { if (loading != null) loading.Cancel(); };
        }

        async void LoadCover()
        {
            if (loading != null) loading.Cancel();
            Source = null;
            var entry = DataContext as OnlineManga.Entry;
            if (entry == null) return;
            var key = entry.Cover == null ? null : Tuple.Create(entry.Cover, entry.Source.Referer, entry.Source.Rule);
            BitmapSource ready = null;
            if (key != null && Cache.TryGetValue(key, out ready)) Source = ready;
            if ((ready != null || key == null) && !OnlineManga.CanLoadDetails(entry)) return;

            using (var operation = new CancellationTokenSource())
            {
                loading = operation;
                var token = operation.Token;
                try
                {
                    await Slots.WaitAsync(token);
                    try
                    {
                        if (key != null)
                        {
                            try
                            {
                                if (!Cache.TryGetValue(key, out ready))
                                {
                                    var options = key.Item3 == null ? null : key.Item3.RequestHeaders(key.Item1, null, image: true);
                                    ready = await OnlineBook.FetchBitmap(key.Item1, key.Item2, 144, token,
                                        4 * 1024 * 1024, 200, options: options);
                                    token.ThrowIfCancellationRequested();
                                    if (!Cache.ContainsKey(key))
                                    {
                                        Cache.Add(key, ready);
                                        Order.Enqueue(key);
                                        if (Order.Count > 96) Cache.Remove(Order.Dequeue());
                                    }
                                }
                                Source = ready;
                            }
                            catch (Exception error) { if (!Recoverable(error)) throw; }
                        }
                        token.ThrowIfCancellationRequested();
                        if (OnlineManga.CanLoadDetails(entry)) await OnlineManga.LoadDetails(entry, token);
                    }
                    finally { Slots.Release(); }
                }
                catch (Exception error)
                {
                    if (!Recoverable(error)) throw;
                }
                finally { if (loading == operation) loading = null; }
            }
        }

        static bool Recoverable(Exception error)
        {
            return error is OperationCanceledException || error is System.Net.Http.HttpRequestException ||
                error is NotSupportedException || error is ArgumentException ||
                error is System.Text.RegularExpressions.RegexMatchTimeoutException || ReaderState.IsStorageError(error);
        }
    }
}

