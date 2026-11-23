using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

sealed class ReaderState
{
    readonly string folder;
    public string Folder { get { return folder; } }

    public bool Dual = true;
    public bool RightToLeft = true;
    public bool SoloCover = true;
    public bool FitWidth;
    public bool ContinuousFitWidth = true;
    public PageMotion Motion = PageMotion.Slide;
    public string LastPath;

    public ReaderState(string storageFolder = null)
    {
        folder = storageFolder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "漫画阅读器");
        try
        {
            var saved = XElement.Load(System.IO.Path.Combine(folder, "settings.xml"));
            Dual = (bool?)saved.Element("dual") ?? Dual;
            RightToLeft = (bool?)saved.Element("rtl") ?? RightToLeft;
            SoloCover = (bool?)saved.Element("cover") ?? SoloCover;
            FitWidth = (bool?)saved.Element("fitWidth") ?? FitWidth;
            ContinuousFitWidth = (bool?)saved.Element("continuousFitWidth") ?? ContinuousFitWidth;
            PageMotion motion;
            if (Enum.TryParse((string)saved.Element("pageMotion"), out motion) && Enum.IsDefined(typeof(PageMotion), motion))
                Motion = motion;
            else if ((bool?)saved.Element("continuousScroll") == true) Motion = PageMotion.Scroll;
            LastPath = (string)saved.Element("lastPath");
        }
        catch (Exception error) { if (!IsStorageError(error)) throw; }
    }

    public int ReadPosition(string path)
    {
        try
        {
            int position;
            return int.TryParse(File.ReadAllText(PositionFile(path)), out position) ? position : 0;
        }
        catch (Exception error) { if (!IsStorageError(error)) throw; return 0; }
    }

    public void SavePosition(string path, int position)
    {
        Write(PositionFile(path), position.ToString(CultureInfo.InvariantCulture));
    }

    public void SaveSettings()
    {
        var xml = new XElement("reader", new XElement("dual", Dual), new XElement("rtl", RightToLeft),
            new XElement("cover", SoloCover), new XElement("fitWidth", FitWidth),
            new XElement("continuousFitWidth", ContinuousFitWidth),
            new XElement("pageMotion", Motion),
            new XElement("lastPath", LastPath ?? ""));
        Write(System.IO.Path.Combine(folder, "settings.xml"), xml.ToString());
    }

    string PositionFile(string path)
    {
        return Path.Combine(folder, FileKey(path) + ".page");
    }

    public static string FileKey(string path)
    {
        using (var hash = SHA256.Create())
        {
            var bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(path.ToUpperInvariant()));
            return BitConverter.ToString(bytes).Replace("-", "");
        }
    }

    void Write(string path, string data)
    {
        try { WriteAtomic(path, temp => File.WriteAllText(temp, data)); }
        catch (Exception error) { if (!IsStorageError(error)) throw; }
    }

    public static void WriteAtomic(string path, Action<string> write)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            write(temp);
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static bool IsStorageError(Exception error)
    {
        return error is IOException || error is UnauthorizedAccessException ||
            error is System.Xml.XmlException || error is FormatException ||
            error is System.Security.SecurityException;
    }
}
