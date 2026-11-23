package com.komasnap.reader;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * Shelf catalogue: remembers which imported books were added, their page count,
 * and when they were last opened. Each row is
 * {@code title \t pages \t lastOpened \t added}. Library files that are not in
 * the catalogue are still listed (they were imported by the user, so they are
 * part of the library), newest catalogued books first.
 */
public final class Shelf {

    public static final class Entry {
        public String title;
        public int pages;
        public long lastOpened;
        public long added;

        public String displayTitle() {
            return title == null || title.isEmpty() ? "未命名" : title;
        }
    }

    private final Store store;
    private final File catalog;

    public Shelf(Store store) {
        this.store = store;
        this.catalog = new File(store.state(), "bookshelf.tsv");
    }

    public List<Entry> read() {
        Map<String, Entry> byTitle = new LinkedHashMap<>();
        for (File file : store.libraryFiles()) {
            Entry e = new Entry();
            e.title = Native.titleOf(file);
            e.pages = 0;
            byTitle.put(e.title.toLowerCase(java.util.Locale.ROOT), e);
        }
        for (Entry saved : loadCatalog()) {
            Entry e = byTitle.get(saved.title.toLowerCase(java.util.Locale.ROOT));
            if (e == null) continue;
            e.pages = saved.pages;
            e.lastOpened = saved.lastOpened;
            e.added = saved.added;
        }
        List<Entry> out = new ArrayList<>(byTitle.values());
        out.sort((a, b) -> {
            int c = Long.compare(b.lastOpened, a.lastOpened);
            if (c != 0) return c;
            c = Long.compare(b.added, a.added);
            if (c != 0) return c;
            return NaturalSort.INSTANCE.compare(a.title, b.title);
        });
        return out;
    }

    private List<Entry> loadCatalog() {
        List<Entry> out = new ArrayList<>();
        if (!catalog.isFile()) return out;
        List<String> lines = new ArrayList<>();
        try (InputStream in = new FileInputStream(catalog)) {
            byte[] data = Streams.readAll(in, 4 * 1024 * 1024);
            String text = new String(data, StandardCharsets.UTF_8);
            for (String line : text.split("\n")) {
                if (!line.trim().isEmpty()) lines.add(line);
            }
        } catch (IOException ignored) {
            return out;
        }
        for (String line : lines) {
            String[] parts = line.split("\t", -1);
            if (parts.length < 4) continue;
            Entry e = new Entry();
            e.title = parts[0];
            try {
                e.pages = Integer.parseInt(parts[1]);
                e.lastOpened = Long.parseLong(parts[2]);
                e.added = Long.parseLong(parts[3]);
            } catch (NumberFormatException ignored) {
                continue;
            }
            out.add(e);
        }
        return out;
    }

    public void recordOpened(String title, int pages) {
        List<Entry> entries = loadCatalog();
        Entry target = null;
        for (Entry e : entries) {
            if (e.title.equalsIgnoreCase(title)) target = e;
        }
        if (target == null) {
            target = new Entry();
            target.title = title;
            target.added = System.currentTimeMillis();
            entries.add(target);
        }
        target.pages = pages;
        target.lastOpened = System.currentTimeMillis();
        save(entries);
    }

    public void remove(String title) {
        List<Entry> entries = loadCatalog();
        entries.removeIf(e -> e.title.equalsIgnoreCase(title));
        save(entries);
    }

    private void save(List<Entry> entries) {
        StringBuilder out = new StringBuilder();
        for (Entry e : entries) {
            out.append(e.title.replace('\t', ' ')).append('\t')
                    .append(e.pages).append('\t')
                    .append(e.lastOpened).append('\t')
                    .append(e.added).append('\n');
        }
        File tmp = new File(catalog.getParentFile(), catalog.getName() + ".tmp");
        try (OutputStream stream = new FileOutputStream(tmp)) {
            stream.write(out.toString().getBytes(StandardCharsets.UTF_8));
        } catch (IOException ignored) {
            return;
        }
        if (!tmp.renameTo(catalog)) {
            catalog.delete();
            tmp.renameTo(catalog);
        }
    }

    public File fileFor(String title) {
        for (File f : store.libraryFiles()) {
            if (Native.titleOf(f).equalsIgnoreCase(title)) return f;
        }
        return null;
    }

    public File coverFor(String title) {
        return new File(store.covers(), State.sha256(title) + ".png");
    }

    /** Falls back to a placeholder-free null when no cached cover exists. */
    public File cover(String title) {
        File file = coverFor(title);
        return file.isFile() ? file : null;
    }

    public void saveCover(String title, android.graphics.Bitmap bitmap) {
        File file = coverFor(title);
        File tmp = new File(file.getParentFile(), file.getName() + ".tmp");
        try (OutputStream out = new FileOutputStream(tmp)) {
            bitmap.compress(android.graphics.Bitmap.CompressFormat.PNG, 100, out);
        } catch (IOException ignored) {
            return;
        }
        if (!tmp.renameTo(file)) {
            file.delete();
            tmp.renameTo(file);
        }
    }

    /** Removes both the catalog row and the imported file (cover stays cached). */
    public void delete(String title) {
        remove(title);
        File file = fileFor(title);
        if (file != null) file.delete();
    }

    /** Small helpers that needed a Context-free implementation. */
    static final class Native {
        static String titleOf(File file) {
            return NaturalSort.titleOf(file.getName());
        }
    }

    public static byte[] readAll(InputStream in) throws IOException {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = in.read(buffer)) != -1) out.write(buffer, 0, read);
        return out.toByteArray();
    }
}
