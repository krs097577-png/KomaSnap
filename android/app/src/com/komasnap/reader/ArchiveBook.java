package com.komasnap.reader;

import android.graphics.Bitmap;

import java.io.IOException;
import java.util.ArrayList;
import java.util.Enumeration;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import java.util.zip.ZipEntry;
import java.util.zip.ZipFile;

/**
 * ZIP / CBZ / EPUB backed book.
 *
 * <p>Comic archives contribute one image page per entry (natural-sorted).
 * EPUBs follow the OPF spine and contribute either image pages or paginated
 * text pages, with paragraphs measured by {@link TextPages}.</p>
 */
public final class ArchiveBook extends Book {

    private static final int ENTRY_LIMIT = 64 * 1024 * 1024;

    public static final class Part {
        final String imagePath;
        final byte[] imageBytes;       // EPUB inline data: URIs
        final List<TextPages.Block> blocks;
        final int width;
        final int height;

        Part(String imagePath, byte[] imageBytes, List<TextPages.Block> blocks, int width, int height) {
            this.imagePath = imagePath;
            this.imageBytes = imageBytes;
            this.blocks = blocks;
            this.width = width;
            this.height = height;
        }
    }

    private static final class Page {
        final Part part;
        final TextPages text;   // non-null for text pages
        final int pageNumber;

        Page(Part part, TextPages text, int pageNumber) {
            this.part = part;
            this.text = text;
            this.pageNumber = pageNumber;
        }
    }

    private final ZipFile zip;
    private final Page[] pages;
    private final Object lock = new Object();

    private ArchiveBook(ZipFile zip, List<Page> pages, Boolean rightToLeft) {
        super(dimensions(pages, true), dimensions(pages, false), rightToLeft);
        this.zip = zip;
        this.pages = pages.toArray(new Page[0]);
    }

    private static int[] dimensions(List<Page> pages, boolean width) {
        int[] out = new int[pages.size()];
        for (int i = 0; i < out.length; i++) {
            Page p = pages.get(i);
            out[i] = Math.max(1, width ? p.part.width : p.part.height);
        }
        return out;
    }

    public static ArchiveBook open(java.io.File file, boolean epub) throws IOException {
        ZipFile zip = new ZipFile(file);
        try {
            List<Part> parts = new ArrayList<>();
            Boolean direction = epub ? readEpub(zip, parts) : null;
            if (!epub) readComic(zip, parts);
            if (epub && direction == null) {
                for (Part p : parts) {
                    if (p.blocks != null && !p.blocks.isEmpty()) {
                        direction = Boolean.FALSE;
                        break;
                    }
                }
            }
            List<Page> pages = new ArrayList<>();
            for (Part part : parts) {
                if (part.blocks != null) {
                    TextPages text = TextPages.paginate(part.blocks);
                    for (int i = 0; i < text.pageCount(); i++) pages.add(new Page(part, text, i));
                } else {
                    pages.add(new Page(part, null, 0));
                }
            }
            if (pages.isEmpty()) {
                throw new IOException(epub ? "这本 EPUB 没有可读取的文字或图片页面。" : "压缩包中没有支持的图片。");
            }
            return new ArchiveBook(zip, pages, direction);
        } catch (IOException | RuntimeException e) {
            Streams.closeQuietly(zip);
            throw e;
        }
    }

    // region Comic archives -------------------------------------------------

    private static void readComic(ZipFile zip, List<Part> parts) throws IOException {
        List<ZipEntry> images = new ArrayList<>();
        Enumeration<? extends ZipEntry> entries = zip.entries();
        while (entries.hasMoreElements()) {
            ZipEntry entry = entries.nextElement();
            if (entry.isDirectory()) continue;
            String name = entry.getName();
            if (!NaturalSort.isImage(name)) continue;
            if (isHiddenSegment(name)) continue;
            images.add(entry);
        }
        images.sort((a, b) -> {
            int c = NaturalSort.INSTANCE.compare(a.getName(), b.getName());
            return c != 0 ? c : a.getName().compareTo(b.getName());
        });
        for (ZipEntry entry : images) {
            byte[] bytes = read(zip, entry, ENTRY_LIMIT);
            Bitmaps.Size size = Bitmaps.bounds(bytes);
            if (size == null) throw new IOException("无法识别的图片：" + entry.getName());
            if ((long) size.width * size.height > 120_000_000L) {
                throw new IOException("图片尺寸超出可读取范围：" + entry.getName());
            }
            parts.add(new Part(entry.getName(), null, null, size.width, size.height));
        }
    }

    private static boolean isHiddenSegment(String path) {
        for (String segment : path.split("[/\\\\]")) {
            if (segment.startsWith(".") || segment.equalsIgnoreCase("__MACOSX")) return true;
        }
        return false;
    }

    // endregion

    // region EPUB -----------------------------------------------------------

    private static Boolean readEpub(ZipFile zip, List<Part> parts) throws IOException {
        ZipEntry encryption = zip.getEntry("META-INF/encryption.xml");
        if (encryption != null) {
            String xml = XmlText.decodeBytes(read(zip, encryption, 8 * 1024 * 1024));
            Matcher m = Pattern.compile("<(?:\\w+:)?EncryptionMethod[^>]*Algorithm\\s*=\\s*\"([^\"]*)\"", Pattern.CASE_INSENSITIVE).matcher(xml);
            while (m.find()) {
                String algorithm = m.group(1);
                if (!algorithm.equals("http://www.idpf.org/2008/embedding")
                        && !algorithm.equals("http://ns.adobe.com/pdf/enc#RC")) {
                    throw new IOException("此 EPUB 含有加密内容，请使用可直接阅读的 EPUB 文件。");
                }
            }
        }
        ZipEntry container = zip.getEntry("META-INF/container.xml");
        if (container == null) throw new IOException("找不到 EPUB 书籍清单。");
        // Attributes must be read from the raw markup; XmlText.decode strips tags.
        String containerXml = XmlText.decodeBytes(read(zip, container, 8 * 1024 * 1024));
        String packagePath = XmlText.attr(containerXml, "rootfile", "full-path");
        if (packagePath == null || packagePath.isEmpty()) throw new IOException("找不到 EPUB 书籍清单。");
        packagePath = resolve("", packagePath);
        ZipEntry packageEntry = packagePath == null ? null : zip.getEntry(packagePath);
        if (packageEntry == null) throw new IOException("书中缺少文件：" + packagePath);
        String opf = XmlText.decodeBytes(read(zip, packageEntry, 8 * 1024 * 1024));

        Map<String, String[]> manifest = new HashMap<>();
        Matcher items = Pattern.compile("<(?:\\w+:)?item\\b([^>]*)>", Pattern.CASE_INSENSITIVE).matcher(opf);
        while (items.find()) {
            Map<String, String> attrs = XmlText.attrs(items.group(1));
            String id = attrs.get("id");
            if (id != null) {
                manifest.put(id, new String[]{attrs.get("href"), attrs.get("media-type"), attrs.get("fallback")});
            }
        }
        Matcher spineMatcher = Pattern.compile("<(?:\\w+:)?spine\\b([^>]*)>(.*?)</(?:\\w+:)?spine\\s*>",
                Pattern.CASE_INSENSITIVE | Pattern.DOTALL).matcher(opf);
        String spineAttrs = null;
        String spine = null;
        if (spineMatcher.find()) {
            spineAttrs = spineMatcher.group(1);
            spine = spineMatcher.group(2);
        }
        if (spine == null) throw new IOException("EPUB 缺少阅读顺序。");
        String direction = XmlText.attrs(spineAttrs).get("page-progression-direction");
        Boolean rtl = "rtl".equals(direction) ? Boolean.TRUE : "ltr".equals(direction) ? Boolean.FALSE : null;

        Matcher refs = Pattern.compile("<(?:\\w+:)?itemref\\b([^>]*)>", Pattern.CASE_INSENSITIVE).matcher(spine);
        while (refs.find()) {
            Map<String, String> ref = XmlText.attrs(refs.group(1));
            if ("no".equals(ref.get("linear"))) continue;
            String id = ref.get("idref");
            Set<String> seen = new HashSet<>();
            while (id != null && seen.add(id)) {
                String[] item = manifest.get(id);
                if (item == null || item[0] == null) break;
                String path = resolve(packagePath, item[0]);
                if (path == null) break;
                if (NaturalSort.isImage(path)) {
                    addImage(zip, path, parts);
                    break;
                }
                String media = item[1] == null ? "" : item[1];
                if (media.equals("application/xhtml+xml") || media.equals("text/html") || media.equals("image/svg+xml")
                        || path.toLowerCase(java.util.Locale.ROOT).endsWith(".xhtml")
                        || path.toLowerCase(java.util.Locale.ROOT).endsWith(".html")
                        || path.toLowerCase(java.util.Locale.ROOT).endsWith(".htm")) {
                    ZipEntry chapter = zip.getEntry(path);
                    if (chapter == null) throw new IOException("书中缺少文件：" + path);
                    new ChapterReader(zip, path, parts).visit(XmlText.decodeBytes(read(zip, chapter, 8 * 1024 * 1024)));
                    break;
                }
                id = item[2];
            }
        }
        return rtl;
    }

    private static void addImage(ZipFile zip, String path, List<Part> parts) throws IOException {
        ZipEntry entry = zip.getEntry(path);
        if (entry == null) throw new IOException("书中缺少图片：" + path);
        byte[] bytes = read(zip, entry, ENTRY_LIMIT);
        Bitmaps.Size size = Bitmaps.bounds(bytes);
        if (size == null) throw new IOException("无法识别的图片：" + path);
        parts.add(new Part(path, null, null, size.width, size.height));
    }

    private static final class ChapterReader {
        private final ZipFile zip;
        private final String path;
        private final List<Part> parts;
        private final StringBuilder line = new StringBuilder();
        private final List<TextPages.Block> blocks = new ArrayList<>();
        private final Set<String> seenInline = new HashSet<>();

        ChapterReader(ZipFile zip, String path, List<Part> parts) {
            this.zip = zip;
            this.path = path;
            this.parts = parts;
        }

        void visit(String html) throws IOException {
            if (html == null) return;
            html = html.replaceAll("(?is)<(script|style|head|nav|iframe)[^>]*>.*?</\\1\\s*>", " ");
            html = html.replaceAll("(?is)<!--.*?-->", " ");
            int i = 0;
            int len = html.length();
            boolean heading = false;
            while (i < len) {
                char c = html.charAt(i);
                if (c != '<') {
                    int next = html.indexOf('<', i);
                    if (next < 0) next = len;
                    line.append(XmlText.decode(html.substring(i, next)));
                    i = next;
                    continue;
                }
                int end = html.indexOf('>', i);
                if (end < 0) break;
                String tag = html.substring(i + 1, end).trim();
                i = end + 1;
                boolean closing = tag.startsWith("/");
                String name = tagName(tag);
                if (name == null) continue;
                if (name.equals("br") || name.equals("hr")) {
                    flushLine(heading);
                    continue;
                }
                if (name.equals("img") || name.equals("image")) {
                    Map<String, String> attrs = XmlText.attrs(tag);
                    String source = attrs.get("src");
                    if (source == null) source = attrs.get("href");
                    if (source == null) source = attrs.get("xlink:href");
                    flushLine(heading);
                    flushText();
                    if (source != null && source.startsWith("data:")) {
                        addInline(source, attrs.get("alt"));
                    } else {
                        String resolved = resolve(path, source);
                        if (resolved != null && NaturalSort.isImage(resolved) && zip.getEntry(resolved) != null) {
                            addImage(zip, resolved, parts);
                        } else {
                            String alt = attrs.get("alt");
                            if (alt != null && !alt.trim().isEmpty()) line.append(alt);
                        }
                    }
                    continue;
                }
                if (blockTags.contains(name)) flushLine(heading);
                if (name.equals("li") && !closing) line.append("• ");
                int digit = name.length() == 2 && name.charAt(0) == 'h' ? name.charAt(1) - '0' : 0;
                if (digit >= 1 && digit <= 6) heading = !closing;
            }
            flushLine(heading);
            flushText();
        }

        private void addInline(String dataUri, String alt) {
            int comma = dataUri.indexOf(',');
            if (comma < 0) return;
            if (!dataUri.substring(5, comma).contains("base64")) return;
            try {
                byte[] bytes = android.util.Base64.decode(dataUri.substring(comma + 1), android.util.Base64.DEFAULT);
                Bitmaps.Size size = Bitmaps.bounds(bytes);
                if (size == null) return;
                if (seenInline.add(String.valueOf(bytes.length))) {
                    parts.add(new Part(null, bytes, null, size.width, size.height));
                }
            } catch (IllegalArgumentException ignored) {
                if (alt != null) line.append(alt);
            }
        }

        private void flushLine(boolean heading) {
            String paragraph = line.toString().replaceAll("[\\t\\r\\n ]+", " ").trim();
            line.setLength(0);
            if (!paragraph.isEmpty()) blocks.add(new TextPages.Block(paragraph, heading));
        }

        void flushText() {
            flushLine(false);
            if (!blocks.isEmpty()) {
                parts.add(new Part(null, null, new ArrayList<>(blocks), TextPages.PAGE_WIDTH, TextPages.PAGE_HEIGHT));
                blocks.clear();
            }
        }
    }

    private static final Set<String> blockTags = new HashSet<>(java.util.Arrays.asList(
            "address", "article", "blockquote", "div", "dl", "dt", "dd", "figure", "figcaption",
            "footer", "header", "h1", "h2", "h3", "h4", "h5", "h6", "li", "main", "p", "pre",
            "section", "tr", "body", "html"));

    private static String tagName(String tag) {
        String t = tag.startsWith("/") ? tag.substring(1) : tag;
        int i = 0;
        while (i < t.length() && !Character.isWhitespace(t.charAt(i)) && t.charAt(i) != '/' && t.charAt(i) != '>') i++;
        String name = t.substring(0, i).toLowerCase(java.util.Locale.ROOT);
        int colon = name.indexOf(':');
        if (colon >= 0) name = name.substring(colon + 1);
        return name.isEmpty() ? null : name;
    }

    static String resolve(String basePath, String reference) {
        if (reference == null) return null;
        String path = reference.trim();
        if (path.isEmpty()) return null;
        int hash = path.indexOf('#');
        if (hash >= 0) path = path.substring(0, hash);
        int question = path.indexOf('?');
        if (question >= 0) path = path.substring(0, question);
        try {
            path = java.net.URLDecoder.decode(path.replace("+", "%2B"), "UTF-8");
        } catch (Exception ignored) {
        }
        path = path.replace('\\', '/');
        if (path.isEmpty() || path.startsWith("/") || path.contains(":")) return null;
        int slash = basePath == null ? -1 : basePath.lastIndexOf('/');
        String combined = (slash < 0 ? "" : basePath.substring(0, slash + 1)) + path;
        List<String> resolved = new ArrayList<>();
        for (String segment : combined.split("/")) {
            if (segment.isEmpty() || segment.equals(".")) continue;
            if (segment.equals("..")) {
                if (resolved.isEmpty()) return null;
                resolved.remove(resolved.size() - 1);
            } else {
                resolved.add(segment);
            }
        }
        return String.join("/", resolved);
    }

    // endregion

    @Override
    protected Bitmap renderPage(int page, int width) throws IOException {
        Page p = pages[page];
        if (p.part.imageBytes != null) {
            Bitmap b = Bitmaps.decode(p.part.imageBytes, width);
            if (b == null) throw new IOException("无法解码图片页");
            return b;
        }
        if (p.part.imagePath != null) {
            byte[] bytes;
            synchronized (lock) {
                ZipEntry entry = zip.getEntry(p.part.imagePath);
                if (entry == null) throw new IOException("书中缺少图片：" + p.part.imagePath);
                bytes = read(zip, entry, ENTRY_LIMIT);
            }
            Bitmap b = Bitmaps.decode(bytes, width);
            if (b == null) throw new IOException("无法解码图片：" + p.part.imagePath);
            return b;
        }
        return p.text.render(p.pageNumber, width, p.part.width, p.part.height);
    }

    private static byte[] read(ZipFile zip, ZipEntry entry, int limit) throws IOException {
        if (entry.getSize() > limit) throw new IOException("压缩包中的单个文件过大：" + entry.getName());
        java.io.InputStream in = zip.getInputStream(entry);
        try {
            return Streams.readAll(in, limit);
        } finally {
            Streams.closeQuietly(in);
        }
    }

    @Override
    public void close() {
        synchronized (lock) {
            try {
                zip.close();
            } catch (IOException ignored) {
            }
        }
        clearCache();
    }
}
