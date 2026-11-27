using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

sealed class Bookshelf
{
    public sealed class Entry
    {
        public string Path;
        public int Pages;
        public long LastOpened;
        public long Added;
        public string Title { get { return System.IO.Path.GetFileNameWithoutExtension(Path); } }
    }

    static readonly StringComparer FileComparer = StringComparer.OrdinalIgnoreCase;
    readonly ReaderState state;
    readonly string catalog;
    static readonly Regex VolumeName = new Regex(
        @"^(?<series>.+?)(?:(?:第\s*)?(?<number>[0-9]{1,4})\s*[卷册]|(?:\bvol(?:ume)?\.?\s*|[\s_\-]v\s*|卷\s*)(?<number>[0-9]{1,4})|[\s_\-]+(?<number>[0-9]{1,3}))$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    sealed class Volume
    {
        public Entry Book;
        public string Series;
        public int Number;
    }

    public Bookshelf(ReaderState state)
    {
        this.state = state;
        catalog = Path.Combine(state.Folder, "bookshelf.xml");
    }

    public List<Entry> Read()
    {
        var items = new Dictionary<string, Entry>(FileComparer);
        if (!File.Exists(catalog))
        {
            if (!string.IsNullOrWhiteSpace(state.LastPath) && ReaderBook.Supports(state.LastPath))
                items.Add(state.LastPath, NewEntry(state.LastPath));
        }
        else
        {
            foreach (var node in XElement.Load(catalog).Elements("book"))
            {
                string path = (string)node.Attribute("path");
                if (string.IsNullOrWhiteSpace(path) || !ReaderBook.Supports(path)) continue;
                int pages;
                long time, added;
                int.TryParse((string)node.Attribute("pages"), out pages);
                long.TryParse((string)node.Attribute("lastOpened"), out time);
                long.TryParse((string)node.Attribute("added"), out added);
                items[path] = new Entry { Path = path, Pages = Math.Max(0, pages), LastOpened = time, Added = added };
            }
        }
        return items.Values.OrderByDescending(item => item.LastOpened).ThenByDescending(item => item.Added)
            .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public int Add(IEnumerable<string> paths)
    {
        var items = Read();
        var existing = new HashSet<string>(items.Select(item => item.Path), FileComparer);
        int added = 0;
        foreach (string source in paths)
        {
            string path = Path.GetFullPath(source);
            if (!ReaderBook.Supports(path) || !existing.Add(path)) continue;
            items.Add(NewEntry(path));
            added++;
        }
        if (added > 0) Save(items);
        return added;
    }

    static Volume ParseVolume(Entry book)
    {
        var match = VolumeName.Match(book.Title.Normalize(NormalizationForm.FormKC).Trim());
        int number;
        if (!match.Success || !int.TryParse(match.Groups["number"].Value, out number)) return null;
        string series = Regex.Replace(match.Groups["series"].Value.Trim(' ', '_', '-'), @"[\s_\-]+", " ");
        if (series.Length == 0 || !series.Any(c => !char.IsWhiteSpace(c) && !char.IsDigit(c))) return null;
        return new Volume { Book = book, Series = series, Number = number };
    }

    public static IEnumerable<Entry> InVolumeOrder(IList<Entry> books)
    {
        var volumes = books.Select(ParseVolume).ToArray();
        var series = volumes.Where(volume => volume != null).ToLookup(volume => volume.Series, FileComparer);
        var shown = new HashSet<string>(FileComparer);
        for (int i = 0; i < books.Count; i++)
        {
            var volume = volumes[i];
            if (volume == null) yield return books[i];
            else if (shown.Add(volume.Series))
                foreach (var item in series[volume.Series].OrderBy(item => item.Number)) yield return item.Book;
        }
    }

    public static Entry NextVolume(string path, IEnumerable<Entry> books)
    {
        var current = ParseVolume(new Entry { Path = path });
        if (current == null) return null;
        var candidates = books.Select(ParseVolume)
            .Where(item => item != null && FileComparer.Equals(item.Series, current.Series)
                && item.Number == current.Number + 1 && File.Exists(item.Book.Path))
            .Select(item => item.Book).ToList();
        if (candidates.Count > 1)
        {
            var sameFolder = candidates.Where(item => FileComparer.Equals(
                System.IO.Path.GetDirectoryName(path), System.IO.Path.GetDirectoryName(item.Path))).ToList();
            if (sameFolder.Count > 0) candidates = sameFolder;
        }
        if (candidates.Count > 1)
        {
            var sameFormat = candidates.Where(item => FileComparer.Equals(
                System.IO.Path.GetExtension(path), System.IO.Path.GetExtension(item.Path))).ToList();
            if (sameFormat.Count > 0) candidates = sameFormat;
        }
        return candidates.Count == 1 ? candidates[0] : null;
    }

    public void RecordOpened(ReaderBook book)
    {
        var items = Read();
        var item = items.FirstOrDefault(entry => FileComparer.Equals(entry.Path, book.Path));
        if (item == null) { item = NewEntry(book.Path); items.Add(item); }
        item.Pages = book.Count;
        item.LastOpened = DateTime.UtcNow.Ticks;
        Save(items);
    }

    public void Remove(string path)
    {
        var items = Read();
        if (items.RemoveAll(entry => FileComparer.Equals(path, entry.Path)) > 0) Save(items);
    }

    public BitmapSource Cover(string path)
    {
        try
        {
            string file = CoverPath(path);
            if (!File.Exists(file)) return null;
            using (var input = File.OpenRead(file))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = input;
                image.EndInit();
                image.Freeze();
                return image;
            }
        }
        catch (Exception error)
        {
            if (ReaderState.IsStorageError(error) || error is NotSupportedException) return null;
            throw;
        }
    }

    public bool HasCover(string path) { return File.Exists(CoverPath(path)); }

    public void DeleteCover(string path)
    {
        try { File.Delete(CoverPath(path)); }
        catch (DirectoryNotFoundException) { }
    }

    public void SaveCover(string path, BitmapSource bitmap)
    {
        ReaderState.WriteAtomic(CoverPath(path), temp =>
        {
            using (var output = File.Create(temp))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(output);
            }
        });
    }

    string CoverPath(string path) { return Path.Combine(state.Folder, "covers", ReaderState.FileKey(path) + ".png"); }

    static Entry NewEntry(string path) { return new Entry { Path = path, Added = DateTime.UtcNow.Ticks }; }

    void Save(IEnumerable<Entry> entries)
    {
        var document = new XElement("bookshelf", entries.Select(entry => new XElement("book",
            new XAttribute("path", entry.Path), new XAttribute("pages", entry.Pages),
            new XAttribute("lastOpened", entry.LastOpened), new XAttribute("added", entry.Added))));
        ReaderState.WriteAtomic(catalog, document.Save);
    }
}

