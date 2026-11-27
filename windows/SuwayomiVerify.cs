using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

static class SuwayomiVerify
{
    [STAThread]
    static int Main(string[] args)
    {
        int result = 1; var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (s, e) => { try { await Run(args[0]); result = 0; } catch (Exception error) { Console.Error.WriteLine(error); } finally { SuwayomiService.Stop(); app.Shutdown(); } };
        app.Run(); return result;
    }
    static async Task Run(string root)
    {
        var dir = Path.Combine(root, "Suwayomi", "local", "KomaSnap QA", "Chapter 01"); Directory.CreateDirectory(dir);
        for (int i = 1; i <= 2; i++) {
            var visual = new DrawingVisual(); using (var ctx = visual.RenderOpen()) ctx.DrawRectangle(i == 1 ? Brushes.CornflowerBlue : Brushes.Goldenrod, null, new Rect(0, 0, 400, 600));
            var image = new RenderTargetBitmap(400, 600, 96, 96, PixelFormats.Pbgra32); image.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using (var f = File.Create(Path.Combine(dir, i + ".png"))) encoder.Save(f);
        }
        var secondDir = Path.Combine(root, "Suwayomi", "local", "KomaSnap QA", "Chapter 02"); Directory.CreateDirectory(secondDir);
        File.Copy(Path.Combine(dir, "1.png"), Path.Combine(secondDir, "1.png"), true);
        File.Copy(Path.Combine(dir, "2.png"), Path.Combine(secondDir, "2.png"), true);
        await SuwayomiService.Start(root);
        var sources = SuwayomiService.Obj((await SuwayomiService.Query("{ sources { nodes { id name lang } } }"))["sources"]);
        var remote = SuwayomiService.Arr(sources["nodes"]).Select(SuwayomiService.Obj).FirstOrDefault(x => SuwayomiService.S(x, "name") == "MangaDex" && SuwayomiService.S(x, "lang") == "zh-Hans");
        if (remote != null) {
            var searchRemote = await SuwayomiService.Query("mutation { fetchSourceManga(input:{source:" + SuwayomiService.J(SuwayomiService.S(remote, "id")) + ",type:SEARCH,page:1,query:\"Kage no Jitsuryokusha\"}) { mangas { id title } } }");
            var remoteManga = SuwayomiService.Arr(SuwayomiService.Obj(searchRemote["fetchSourceManga"])["mangas"]).Select(SuwayomiService.Obj).First(x => SuwayomiService.S(x, "title") == "Kage no Jitsuryokusha ni Naritakute!");
            var remoteDetail = await SuwayomiService.Query("mutation { fetchMangaAndChapters(input:{id:" + SuwayomiService.S(remoteManga, "id") + ",fetchManga:true,fetchChapters:true}) { manga { title } chapters { id name } } }");
            var remoteChapter = SuwayomiService.Obj(SuwayomiService.Arr(SuwayomiService.Obj(remoteDetail["fetchMangaAndChapters"])["chapters"]).First());
            int remoteId = int.Parse(SuwayomiService.S(remoteChapter, "id"));
            var remotePages = await SuwayomiService.Query("mutation { fetchChapterPages(input:{chapterId:" + remoteId + "}) { pages } }");
            var remoteUrls = SuwayomiService.Arr(SuwayomiService.Obj(remotePages["fetchChapterPages"])["pages"]).Select(Convert.ToString).ToArray();
            var remoteBook = await SuwayomiBook.Open(remoteId, "MangaDex QA", remoteUrls);
            var remoteBitmap = await remoteBook.RenderAsync(0, 500, CancellationToken.None);
            if (remoteBitmap.PixelWidth < 1 || remoteBitmap.PixelHeight < 1) throw new Exception("MangaDex image failed");
            Console.WriteLine("PASS: real MangaDex search, chapters and reader image");
            var host = new Window { ShowInTaskbar = false, Width = 900, Height = 700 }; host.Show();
            ReaderBook opened = null; var panel = new OnlineWorkspace(host, new ReaderState(root), p => { }, b => opened = b);
            host.Content = panel.View;
            var flags2 = BindingFlags.NonPublic | BindingFlags.Instance; var panelType = typeof(OnlineWorkspace);
            await (Task)panelType.GetMethod("SearchPage", flags2).Invoke(panel, null);
            panelType.GetField("sources", flags2).SetValue(panel, new System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>> { remote });
            ((TextBox)panelType.GetField("searchBox", flags2).GetValue(panel)).Text = "Kage no Jitsuryokusha";
            // 搜索内部会重新读取来源，测试环境仅保留已授权的 MangaDex 扩展。
            await (Task)panelType.GetMethod("Search", flags2).Invoke(panel, null);
            await Task.Delay(1200); host.UpdateLayout();
            var screenshot = new RenderTargetBitmap((int)host.ActualWidth, (int)host.ActualHeight, 96, 96, PixelFormats.Pbgra32); screenshot.Render(host);
            var shot = new PngBitmapEncoder(); shot.Frames.Add(BitmapFrame.Create(screenshot)); using (var sf = File.Create(Path.Combine(root, "product-search.png"))) shot.Save(sf);
            var displayed = (System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>>)panelType.GetField("results", flags2).GetValue(panel);
            var found = displayed.First(x => SuwayomiService.S(x, "title") == "Kage no Jitsuryokusha ni Naritakute!");
            await (Task)panelType.GetMethod("Detail", flags2).Invoke(panel, new object[] { found });
            await (Task)panelType.GetMethod("ReadChapter", flags2).Invoke(panel, new object[] { remoteChapter });
            if (opened == null || opened.Count < 1) throw new Exception("Integrated reading failed");
            await (Task)panelType.GetMethod("OnlineShelf", flags2).Invoke(panel, null);
            await (Task)panelType.GetMethod("SourcesPage", flags2).Invoke(panel, null);
            await (Task)panelType.GetMethod("SettingsPage", flags2).Invoke(panel, null);
            var extensionData = await SuwayomiService.Query("{ extensions { nodes { name pkgName isInstalled } } }");
            var extension = SuwayomiService.Arr(SuwayomiService.Obj(extensionData["extensions"])["nodes"]).Select(SuwayomiService.Obj).First(x => SuwayomiService.S(x, "name") == "MangaDex" && SuwayomiService.S(x, "isInstalled") == "True");
            await (Task)panelType.GetMethod("CheckSource", flags2).Invoke(panel, new object[] { extension });
            Console.WriteLine("PASS: integrated cards, detail, reading, online shelf, source management, settings and availability"); host.Close();
        }
        var source = SuwayomiService.Arr(sources["nodes"]).Select(SuwayomiService.Obj).First(x => SuwayomiService.S(x, "name").ToLowerInvariant().Contains("local"));
        var owner = new Window { ShowInTaskbar = false, Width = 100, Height = 100 }; owner.Show();
        var dialog = (SuwayomiWindow)Activator.CreateInstance(typeof(SuwayomiWindow), BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] { owner, new ReaderState(root), new Action<System.Collections.Generic.IEnumerable<string>>(p => { }) }, null);
        var type = typeof(SuwayomiWindow); var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        await (Task)type.GetMethod("Sources", flags).Invoke(dialog, null);
        var combo = (ComboBox)type.GetField("sources", flags).GetValue(dialog);
        for (int i = 0; i < combo.Items.Count; i++) if (combo.Items[i].ToString().ToLowerInvariant().Contains("local")) combo.SelectedIndex = i;
        ((TextBox)type.GetField("search", flags).GetValue(dialog)).Text = "KomaSnap QA";
        await (Task)type.GetMethod("Search", flags).Invoke(dialog, new object[] { false });
        var list = (ListBox)type.GetField("list", flags).GetValue(dialog);
        if (list.Items.Count < 1) throw new Exception("Search UI is empty");
        await (Task)type.GetMethod("Open", flags).Invoke(dialog, null);
        if (list.Items.Count < 2) throw new Exception("Chapter UI is empty");
        Console.WriteLine("PASS: native source, search and chapter UI methods"); owner.Close();
        var id = SuwayomiService.S(source, "id");
        var search = await SuwayomiService.Query("mutation { fetchSourceManga(input:{source:" + SuwayomiService.J(id) + ",type:SEARCH,page:1,query:\"KomaSnap QA\"}) { mangas { id title } } }");
        var manga = SuwayomiService.Obj(SuwayomiService.Arr(SuwayomiService.Obj(search["fetchSourceManga"])["mangas"]).First());
        var details = await SuwayomiService.Query("mutation { fetchMangaAndChapters(input:{id:" + SuwayomiService.S(manga, "id") + ",fetchManga:true,fetchChapters:true}) { manga { title } chapters { id name } } }");
        var testChapters = SuwayomiService.Arr(SuwayomiService.Obj(details["fetchMangaAndChapters"])["chapters"]).Select(SuwayomiService.Obj).ToArray();
        if (testChapters.Length < 2) throw new Exception("Batch test needs two chapters");
        var chapter = testChapters.First();
        int chapterId = int.Parse(SuwayomiService.S(chapter, "id"));
        var pageResult = await SuwayomiService.Query("mutation { fetchChapterPages(input:{chapterId:" + chapterId + "}) { pages } }");
        var pages = SuwayomiService.Arr(SuwayomiService.Obj(pageResult["fetchChapterPages"])["pages"]).Select(Convert.ToString).ToArray();
        if (pages.Length != 2) throw new Exception("Incorrect page count: " + pages.Length);
        var book = await SuwayomiBook.Open(chapterId, "QA", pages);
        var bitmap = await book.RenderAsync(1, 500, CancellationToken.None);
        if (bitmap.PixelWidth != 400 || bitmap.PixelHeight != 600) throw new Exception("Incorrect page bitmap");
        var cbz = Path.Combine(root, "qa-output.cbz"); if (File.Exists(cbz)) File.Delete(cbz);
        await SuwayomiWindow.SaveCbz(cbz, pages, (i, n) => Console.WriteLine("page " + i + "/" + n));
        var loaded = await ReaderBook.OpenAsync(cbz, CancellationToken.None);
        if (loaded.Count != 2) throw new Exception("CBZ page count mismatch");
        var state = new ReaderState(root); var shelf = new Bookshelf(state); shelf.Add(new[] { cbz });
        if (!shelf.Read().Any(x => x.Path == cbz)) throw new Exception("Shelf import failed");
        var taskHost = new Window { ShowInTaskbar = false, Width = 100, Height = 100 }; taskHost.Show();
        var product = new OnlineWorkspace(taskHost, state, paths => shelf.Add(paths), b => { });
        string batchName = "KomaSnapBatch-" + Guid.NewGuid().ToString("N");
        product.EnqueueChapters(batchName, testChapters.Take(2));
        var productJobs = (System.Collections.IList)typeof(OnlineWorkspace).GetField("jobs", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(product);
        var batchJobs = productJobs.Cast<object>().Where(x => ((string)x.GetType().GetField("Title").GetValue(x)).StartsWith(batchName)).ToArray();
        if (batchJobs.Length != 2) throw new Exception("Batch queue did not create two jobs");
        product.EnqueueChapters(batchName, testChapters.Take(2));
        if (productJobs.Cast<object>().Count(x => ((string)x.GetType().GetField("Title").GetValue(x)).StartsWith(batchName)) != 2) throw new Exception("Duplicate batch jobs were created");
        for (int i = 0; i < 300 && batchJobs.Any(x => !(bool)x.GetType().GetField("Done").GetValue(x) && x.GetType().GetField("Error").GetValue(x) == null); i++) await Task.Delay(100);
        foreach (var task in batchJobs) {
            var taskType = task.GetType(); string path = (string)taskType.GetField("Path").GetValue(task);
            if (!(bool)taskType.GetField("Done").GetValue(task) || !shelf.Read().Any(x => x.Path == path)) throw new Exception("Batch download/import failed: " + taskType.GetField("Error").GetValue(task));
            if ((await ReaderBook.OpenAsync(path, CancellationToken.None)).Count != 2) throw new Exception("Invalid batch CBZ");
        }
        Console.WriteLine("PASS: multi-chapter queue, duplicate skipping, CBZ and bookshelf import");
        var jobType = typeof(OnlineWorkspace).GetNestedType("Job", BindingFlags.NonPublic);
        var job = Activator.CreateInstance(jobType); var jf = BindingFlags.Public | BindingFlags.Instance;
        string jobFolder = Path.Combine(root, "Downloads"); Directory.CreateDirectory(jobFolder);
        string jobPath = Path.Combine(jobFolder, "qa-task.cbz"); if (File.Exists(jobPath)) File.Delete(jobPath);
        jobType.GetField("Chapter", jf).SetValue(job, -1); jobType.GetField("Title", jf).SetValue(job, "QA 章节"); jobType.GetField("Path", jf).SetValue(job, jobPath);
        ((System.Collections.IList)typeof(OnlineWorkspace).GetField("jobs", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(product)).Add(job);
        typeof(OnlineWorkspace).GetMethod("RunJob", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(product, new[] { job });
        for (int i = 0; i < 300 && (bool)jobType.GetField("Running", jf).GetValue(job); i++) await Task.Delay(100);
        if (jobType.GetField("Error", jf).GetValue(job) == null) throw new Exception("Expected failed job");
        jobType.GetField("Chapter", jf).SetValue(job, chapterId);
        typeof(OnlineWorkspace).GetMethod("RunJob", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(product, new[] { job });
        for (int i = 0; i < 300 && !(bool)jobType.GetField("Done", jf).GetValue(job) && jobType.GetField("Error", jf).GetValue(job) == null; i++) await Task.Delay(100);
        if (!(bool)jobType.GetField("Done", jf).GetValue(job) || !shelf.Read().Any(x => x.Path == jobPath)) throw new Exception("Product download failed: " + jobType.GetField("Error", jf).GetValue(job));
        var jobBook = await ReaderBook.OpenAsync(jobPath, CancellationToken.None); if (jobBook.Count != 2) throw new Exception("Product CBZ is invalid");
        for (int i = 0; i < 30 && (bool)jobType.GetField("Running", jf).GetValue(job); i++) await Task.Delay(100);
        var reopenedPanel = new OnlineWorkspace(taskHost, state, paths => { }, b => { });
        var restored = (System.Collections.IList)typeof(OnlineWorkspace).GetField("jobs", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(reopenedPanel);
        if (!restored.Cast<object>().Any(x => (bool)jobType.GetField("Done", jf).GetValue(x))) throw new Exception("Download records were not restored");
        Console.WriteLine("PASS: failed download retry, CBZ and shelf import"); taskHost.Close();
        state.SavePosition(book.Path, 1); if (state.ReadPosition(book.Path) != 1) throw new Exception("Progress failed");
        var server = (System.Diagnostics.Process)typeof(SuwayomiService).GetField("process", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        server.Refresh(); if (server.MainWindowHandle != IntPtr.Zero) throw new Exception("Server console is visible");
        server.Kill(); server.WaitForExit(); await SuwayomiService.Query("{ __typename }");
        if (!SuwayomiService.IsRunning) throw new Exception("Service did not reconnect");
        Console.WriteLine("PASS: hidden server and reconnect after termination");
        Console.WriteLine("PASS: local source search, chapters, images, online ReaderBook, CBZ, bookshelf, progress");
    }
}

