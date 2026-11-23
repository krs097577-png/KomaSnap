package com.komasnap.reader;

import java.util.List;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;

/** JVM-side checks for the pure logic of the Android port (no Android runtime). */
public final class LogicTest {

    private static int passed;
    private static int failed;

    public static void main(String[] args) throws Exception {
        testNaturalSort();
        testSpreads();
        testCacheDuringRender();
        testResolve();
        testXmlText();
        testSha();
        testEpub();
        System.out.println("=== " + passed + " passed, " + failed + " failed ===");
        if (failed > 0) System.exit(1);
    }

    private static void check(String name, boolean ok) {
        if (ok) { passed++; System.out.println("PASS " + name); }
        else { failed++; System.out.println("FAIL " + name); }
    }

    private static void testNaturalSort() {
        check("natural 2 < 10", NaturalSort.INSTANCE.compare("p2.jpg", "p10.jpg") < 0);
        check("natural 1 < 2", NaturalSort.INSTANCE.compare("1.jpg", "2.jpg") < 0);
        check("natural leading zero", NaturalSort.INSTANCE.compare("p1.jpg", "p01.jpg") <= 0);
        check("natural letters", NaturalSort.INSTANCE.compare("a.jpg", "b.jpg") < 0);
        check("natural mixed", NaturalSort.INSTANCE.compare("ch2/pg9.jpg", "ch2/pg10.jpg") < 0);
        check("natural equal", NaturalSort.INSTANCE.compare("x.jpg", "x.jpg") == 0);
        check("extension", NaturalSort.extension("a/b/Book.CBZ").equals("cbz"));
        check("title", NaturalSort.titleOf("dir/My Book.epub").equals("My Book"));
        check("image ext", NaturalSort.isImage("a.PNG") && !NaturalSort.isImage("a.txt"));
    }

    private static final class FakeBook extends Book {
        FakeBook(int[] widths, int[] heights) { super(widths, heights, null); }
        @Override protected android.graphics.Bitmap renderPage(int page, int width) { return null; }
    }

    private static void testCacheDuringRender() throws Exception {
        int[] sides = new int[32];
        java.util.Arrays.fill(sides, 1024);
        CountDownLatch decoding = new CountDownLatch(1);
        CountDownLatch release = new CountDownLatch(1);
        java.util.concurrent.atomic.AtomicInteger count = new java.util.concurrent.atomic.AtomicInteger();
        Book book = new Book(sides, sides, null) {
            @Override protected android.graphics.Bitmap renderPage(int page, int width) throws java.io.IOException {
                if (page == 1) {
                    count.incrementAndGet();
                    decoding.countDown();
                    try { if (!release.await(5, TimeUnit.SECONDS)) throw new java.io.IOException("render wait"); }
                    catch (InterruptedException e) { throw new java.io.IOException(e); }
                }
                return new android.graphics.Bitmap() {
                    @Override public int getWidth() { return width; }
                    @Override public int getHeight() { return width; }
                };
            }
        };
        verifyConcurrentLookup(book, decoding, release, count);
        book.clearCache();
        for (int page = 0; page < 32; page++) book.render(page, 128);
        check("cache keeps only the latest 16 pages", book.cached(15, 128) == null
                && book.cached(16, 128) != null && book.cached(31, 128) != null);
    }

    private static void verifyConcurrentLookup(Book book, CountDownLatch decoding, CountDownLatch release,
                                java.util.concurrent.atomic.AtomicInteger count) throws Exception {
        java.util.concurrent.ExecutorService pool = Executors.newFixedThreadPool(3);
        try {
            android.graphics.Bitmap hit = book.render(0, 128);
            java.util.concurrent.Future<android.graphics.Bitmap> worker = pool.submit(() -> book.render(1, 128));
            check("render started", decoding.await(2, TimeUnit.SECONDS));
            java.util.concurrent.Future<android.graphics.Bitmap> lookup = pool.submit(() -> book.cached(0, 128));
            check("cache lookup does not wait for ongoing decode", lookup.get(800, TimeUnit.MILLISECONDS) == hit);
            java.util.concurrent.Future<android.graphics.Bitmap> duplicate = pool.submit(() -> book.render(1, 128));
            release.countDown();
            check("concurrent requests reuse a single decode", worker.get() == duplicate.get() && count.get() == 1);
        } finally {
            release.countDown();
            pool.shutdownNow();
        }
    }

    private static void testSpreads() {
        FakeBook book = new FakeBook(
                new int[]{800, 800, 800, 800, 800},
                new int[]{1200, 1200, 1200, 1200, 1200});
        List<int[]> dualCover = book.makeSpreads(true, true);
        check("solo cover first page", dualCover.get(0).length == 1 && dualCover.get(0)[0] == 0);
        check("solo cover pairs 1+2", dualCover.get(1).length == 2 && dualCover.get(1)[0] == 1 && dualCover.get(1)[1] == 2);
        check("no cover pairs 0+1", book.makeSpreads(true, false).get(0).length == 2);
        List<int[]> single = book.makeSpreads(false, false);
        check("single page mode", single.size() == 5 && single.get(0).length == 1);
        FakeBook wide = new FakeBook(new int[]{1600, 800, 800}, new int[]{1000, 1200, 1200});
        check("wide page is never paired", wide.makeSpreads(true, false).get(0).length == 1);
        check("page count", book.pageCount() == 5);
        check("render width capped", book.renderWidth(0, 100000) <= 3200);
        check("render width multiple of 128", book.renderWidth(0, 500) % 128 == 0);
        check("aspect ratio", Math.abs(book.renderWidth(0, 2000) / 128f
                - Math.round(book.renderWidth(0, 2000) / 128f)) < 0.001f);
    }

    private static void testResolve() {
        check("resolve sibling", "OEBPS/text/ch1.xhtml".equals(
                ArchiveBook.resolve("OEBPS/content.opf", "text/ch1.xhtml")));
        check("resolve parent", "OEBPS/img/a.png".equals(
                ArchiveBook.resolve("OEBPS/text/ch1.xhtml", "../img/a.png")));
        check("resolve absolute rejected", ArchiveBook.resolve("OEBPS/c.opf", "/etc/passwd") == null);
        check("resolve url scheme rejected", ArchiveBook.resolve("OEBPS/c.opf", "http://x/y.png") == null);
        check("resolve fragment stripped", "OEBPS/ch1.xhtml".equals(
                ArchiveBook.resolve("OEBPS/c.opf", "ch1.xhtml#frag")));
        check("resolve escape rejected", ArchiveBook.resolve("OEBPS/c.opf", "../../x.png") == null);
        check("resolve url-encoded", "OEBPS/a b.png".equals(
                ArchiveBook.resolve("OEBPS/c.opf", "a%20b.png")));
    }

    private static void testXmlText() {
        check("strip tags", XmlText.decode("<p>Hello <b>world</b></p>").trim().equals("Hello world"));
        check("entities", XmlText.decode("a &amp; b &lt; c").equals("a & b < c"));
        check("numeric entity", XmlText.decode("&#20320;&#22909;").equals("\u4f60\u597d"));
        check("hex entity", XmlText.decode("&#x4F60;").equals("\u4f60"));
        check("nbsp", XmlText.decode("a&nbsp;b").indexOf('\u00a0') >= 0);
        check("cdata", XmlText.decode("<![CDATA[x<y]]>").equals("x<y"));
        check("comment removed", XmlText.decode("a<!-- hidden -->b").equals("ab"));
        check("attr parse", "x.xhtml".equals(XmlText.attr("<item href=\"x.xhtml\" id=\"c1\"/>", "item", "href")));
        check("self-closing attr", "OEBPS/c.opf".equals(XmlText.attr(
                "<rootfile full-path=\"OEBPS/c.opf\" media-type=\"x\"/>", "rootfile", "full-path")));
        check("last attr without slash", "v".equals(XmlText.attr("<item id=\"c1\" x=\"v\"/>", "item", "x")));
    }

    private static void testSha() {
        String a = State.sha256("book.epub");
        String b = State.sha256("BOOK.EPUB");
        check("sha stable", a != null && a.length() == 64);
        check("sha case-insensitive", a.equals(b));
        check("sha differs", !a.equals(State.sha256("other.epub")));
    }

    private static void testEpub() throws Exception {
        java.io.File temp = java.io.File.createTempFile("koma-test", ".epub");
        temp.deleteOnExit();
        try (java.util.zip.ZipOutputStream zip = new java.util.zip.ZipOutputStream(new java.io.FileOutputStream(temp))) {
            put(zip, "mimetype", "application/epub+zip");
            put(zip, "META-INF/container.xml",
                    "<?xml version=\"1.0\"?><container><rootfiles>"
                            + "<rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/>"
                            + "</rootfiles></container>");
            put(zip, "OEBPS/content.opf",
                    "<?xml version=\"1.0\"?><package><metadata><dc:title>Test</dc:title></metadata>"
                            + "<manifest>"
                            + "<item id=\"c1\" href=\"text/ch1.xhtml\" media-type=\"application/xhtml+xml\"/>"
                            + "<item id=\"i1\" href=\"img/p1.png\" media-type=\"image/png\"/>"
                            + "</manifest>"
                            + "<spine page-progression-direction=\"rtl\"><itemref idref=\"c1\"/></spine></package>");
            put(zip, "OEBPS/text/ch1.xhtml",
                    "<html><head><style>p{}</style></head><body><h1>Title</h1>"
                            + "<p>First &amp; second paragraph.</p><p>Next<br/>line</p>"
                            + "<img src=\"../img/p1.png\" alt=\"cover\"/></body></html>");
            put(zip, "OEBPS/img/p1.png", pngBytes(640, 960));
        }
        ArchiveBook book = ArchiveBook.open(temp, true);
        check("epub page count", book.pageCount() >= 2);
        check("epub rtl from spine", Boolean.TRUE.equals(book.preferredRightToLeft()));
        boolean textFirst = book.widthOf(0) == TextPages.PAGE_WIDTH && book.heightOf(0) == TextPages.PAGE_HEIGHT;
        check("epub text page uses flow size", textFirst || (book.widthOf(0) == 640 && book.heightOf(0) == 960));
        boolean imagePresent = false;
        boolean textPresent = false;
        for (int i = 0; i < book.pageCount(); i++) {
            if (book.widthOf(i) == 640 && book.heightOf(i) == 960) imagePresent = true;
            if (book.widthOf(i) == TextPages.PAGE_WIDTH && book.heightOf(i) == TextPages.PAGE_HEIGHT) textPresent = true;
        }
        check("epub image page present", imagePresent);
        check("epub text page present", textPresent);
        check("epub spread pairing", book.makeSpreads(true, false).size() >= 1);
    }

    private static byte[] pngBytes(int w, int h) {
        byte[] d = new byte[33];
        d[0] = (byte) 0x89; d[1] = 'P'; d[2] = 'N'; d[3] = 'G';
        d[4] = 13; d[5] = 10; d[6] = 26; d[7] = 10;
        int[] sizes = {w, h};
        int p = 16;
        for (int s : sizes) {
            d[p++] = (byte) (s >>> 24); d[p++] = (byte) (s >>> 16);
            d[p++] = (byte) (s >>> 8); d[p++] = (byte) s;
        }
        d[24] = 8; d[25] = 6;
        return d;
    }

    private static void put(java.util.zip.ZipOutputStream zip, String name, String content) throws Exception {
        put(zip, name, content.getBytes("UTF-8"));
    }

    private static void put(java.util.zip.ZipOutputStream zip, String name, byte[] content) throws Exception {
        zip.putNextEntry(new java.util.zip.ZipEntry(name));
        zip.write(content);
        zip.closeEntry();
    }
}
