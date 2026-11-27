using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// 隔离测试使用本地自制图片和模拟服务，不连接第三方漫画网站。
static class BatchVerify
{
    [STAThread] static int Main(string[] args)
    {
        int result = 1; var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (s, e) => { try { await Run(args[0]); result = 0; } catch (Exception error) { Console.Error.WriteLine(error); } finally { app.Shutdown(); } };
        app.Run(); return result;
    }
    static async Task Run(string parent)
    {
        string root = Path.Combine(parent, "batch-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var visual = new DrawingVisual(); using (var ctx = visual.RenderOpen()) ctx.DrawRectangle(Brushes.CornflowerBlue, null, new Rect(0, 0, 80, 120));
        var bitmap = new RenderTargetBitmap(80, 120, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); byte[] png;
        using (var buf = new MemoryStream()) { encoder.Save(buf); png = buf.ToArray(); }
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        bool stopped = false;
        var server = Task.Run(async () => {
            while (!stopped) {
                TcpClient client; try { client = await listener.AcceptTcpClientAsync(); } catch { break; }
                try {
                    var input = client.GetStream(); var header = new MemoryStream(); int b;
                    while ((b = input.ReadByte()) >= 0) { header.WriteByte((byte)b); var h = header.ToArray(); int n = h.Length; if (n >= 4 && h[n-4] == 13 && h[n-3] == 10 && h[n-2] == 13 && h[n-1] == 10) break; if (n > 16384) throw new Exception("header too long"); }
                    string head = Encoding.ASCII.GetString(header.ToArray()); int length = 0;
                    foreach (string line in head.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries)) if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line.Substring(15).Trim());
                    var body = new byte[length]; int offset = 0; while (offset < length) { int count = await input.ReadAsync(body, offset, length - offset); if (count == 0) break; offset += count; }
                    byte[] data;
                    bool isGraphql = head.StartsWith("POST /api/graphql ", StringComparison.Ordinal);
                    if (isGraphql) {
                        string query = Encoding.UTF8.GetString(body);
                        string json = query.Contains("fetchMangaAndChapters") ?
                            "{\"data\":{\"fetchMangaAndChapters\":{\"manga\":{\"id\":7,\"title\":\"自制漫画\",\"description\":\"测试\",\"thumbnailUrl\":null,\"inLibrary\":false},\"chapters\":[{\"id\":11,\"name\":\"第一章\"},{\"id\":12,\"name\":\"第二章\"}]}}}" :
                            "{\"data\":{\"fetchChapterPages\":{\"pages\":[\"/api/v1/manga/7/chapter/1/page/0\",\"/api/v1/manga/7/chapter/1/page/1\"]}}}";
                        data = Encoding.UTF8.GetBytes(json);
                    } else { data = png; }
                    byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: " + (isGraphql ? "application/json" : "image/png") + "\r\nContent-Length: " + data.Length + "\r\nConnection: close\r\n\r\n");
                    await input.WriteAsync(response, 0, response.Length); await input.WriteAsync(data, 0, data.Length); client.Close();
                } catch { client.Close(); }
            }
        });
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        typeof(SuwayomiService).GetField("process", flags).SetValue(null, Process.GetCurrentProcess());
        typeof(SuwayomiService).GetField("baseUrl", flags).SetValue(null, "http://127.0.0.1:" + port);
        typeof(SuwayomiService).GetField("http", flags).SetValue(null, new HttpClient(new HttpClientHandler { UseProxy = false }));
        var owner = new Window { Width = 900, Height = 700, ShowInTaskbar = false }; owner.Show();
        var state = new ReaderState(root); var shelf = new Bookshelf(state);
        var workspace = new OnlineWorkspace(owner, state, paths => shelf.Add(paths), book => { }); owner.Content = workspace.View;
        var item = new Dictionary<string, object> { { "id", 7 }, { "title", "自制漫画" } };
        var privateFlags = BindingFlags.Instance | BindingFlags.NonPublic;
        await (Task)typeof(OnlineWorkspace).GetMethod("Detail", privateFlags).Invoke(workspace, new object[] { item });
        var content = (StackPanel)typeof(OnlineWorkspace).GetField("content", privateFlags).GetValue(workspace);
        var bar = content.Children.OfType<WrapPanel>().First();
        var controls = bar.Children.OfType<Button>().ToArray();
        var rows = content.Children.OfType<DockPanel>().Where(x => x.Children.OfType<CheckBox>().Any()).ToArray();
        if (rows.Length != 2) throw new Exception("章节选择控件不完整");
        controls.First(x => (string)x.Content == "全选").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (rows.Any(x => x.Children.OfType<CheckBox>().First().IsChecked != true)) throw new Exception("全选失败");
        controls.First(x => (string)x.Content == "清空选择").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (rows.Any(x => x.Children.OfType<CheckBox>().First().IsChecked != false)) throw new Exception("清空选择失败");
        var chapters = new[] { new Dictionary<string, object> { { "id", 11 }, { "name", "第一章" } }, new Dictionary<string, object> { { "id", 12 }, { "name", "第二章" } } };
        workspace.EnqueueChapters("自制漫画", chapters); workspace.EnqueueChapters("自制漫画", chapters);
        var jobs = (IList)typeof(OnlineWorkspace).GetField("jobs", privateFlags).GetValue(workspace);
        if (jobs.Count != 2) throw new Exception("批量入队或去重失败");
        for (int i = 0; i < 200 && jobs.Cast<object>().Any(j => !(bool)j.GetType().GetField("Done").GetValue(j) && j.GetType().GetField("Error").GetValue(j) == null); i++) await Task.Delay(100);
        foreach (var job in jobs.Cast<object>()) {
            var t = job.GetType(); string path = (string)t.GetField("Path").GetValue(job);
            if (!(bool)t.GetField("Done").GetValue(job) || !shelf.Read().Any(x => x.Path == path)) throw new Exception("下载或导入失败：" + t.GetField("Error").GetValue(job));
            if ((await ReaderBook.OpenAsync(path, CancellationToken.None)).Count != 2) throw new Exception("CBZ 内容错误");
        }
        Console.WriteLine("PASS: chapter select all / clear, two parallel queue jobs, duplicate skipping, two valid CBZs and bookshelf import");
        owner.Close(); stopped = true; listener.Stop(); await server;
    }
}

