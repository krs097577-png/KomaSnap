using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
