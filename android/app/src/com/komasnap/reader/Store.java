package com.komasnap.reader;

import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.util.ArrayList;
import java.util.List;

/**
 * App-private book storage. Imported books are copied into {@code files/books}
 * so opening, re-opening and cover caching never depend on transient content
 * URIs or external storage permissions. This is the Android equivalent of the
 * desktop reader's "open a path" model.
 */
public final class Store {

    public static final String[] EXTENSIONS = {"pdf", "epub", "cbz", "zip"};

    private final File root;
    private final File books;
    private final File covers;
    private final File state;

    public Store(File filesDir) {
        root = filesDir;
        books = new File(root, "books");
        covers = new File(root, "covers");
        state = new File(root, "state");
        books.mkdirs();
        covers.mkdirs();
        state.mkdirs();
    }

    public File books() {
        return books;
    }

    public File covers() {
        return covers;
    }

    public File state() {
        return state;
    }

    public static boolean supports(String name) {
        String ext = NaturalSort.extension(name);
        for (String e : EXTENSIONS) if (e.equals(ext)) return true;
        return false;
    }

    /**
     * Copies an input stream to app storage under a readable, unique file name.
     * Returns the stored file; the caller keeps the original title separately.
     */
    public File importBook(String suggestedName, InputStream in) throws IOException {
        String safe = sanitize(NaturalSort.titleOf(suggestedName == null ? "book" : suggestedName));
        String ext = NaturalSort.extension(suggestedName == null ? "" : suggestedName);
        if (ext.isEmpty() || !supports("x." + ext)) ext = "bin";
        String base = safe.isEmpty() ? "book" : safe;
        File target = new File(books, base + "." + ext);
        int n = 2;
        while (target.exists()) {
            target = new File(books, base + " (" + n + ")." + ext);
            n++;
        }
        File tmp = new File(books, target.getName() + ".part");
        try (OutputStream out = new FileOutputStream(tmp)) {
            Streams.copy(in, out);
        }
        if (!tmp.renameTo(target)) {
            tmp.delete();
            throw new IOException("无法写入书籍文件");
        }
        return target;
    }

    public List<File> libraryFiles() {
        List<File> out = new ArrayList<>();
        File[] files = books.listFiles();
        if (files == null) return out;
        for (File f : files) {
            if (f.isFile() && supports(f.getName()) && !f.getName().endsWith(".part")) out.add(f);
        }
        out.sort((a, b) -> NaturalSort.INSTANCE.compare(a.getName(), b.getName()));
        return out;
    }

    public static String sanitize(String name) {
        StringBuilder out = new StringBuilder();
        for (int i = 0; i < name.length(); i++) {
            char c = name.charAt(i);
            if (c == '/' || c == '\\' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<'
                    || c == '>' || c == '|' || c < 32) {
                out.append('_');
            } else {
                out.append(c);
            }
        }
        String s = out.toString().trim();
        if (s.length() > 80) s = s.substring(0, 80);
        return s.isEmpty() ? "book" : s;
    }

    public byte[] read(File file, int limit) throws IOException {
        try (InputStream in = new FileInputStream(file)) {
            return Streams.readAll(in, limit);
        }
    }
}
