package com.komasnap.reader;

import java.io.File;
import java.io.IOException;

/** Opens a book by shelf title (the file name without extension). */
public final class Books {

    private Books() {
    }

    public static Book open(Store store, String title) throws IOException {
        File file = null;
        for (File candidate : store.libraryFiles()) {
            if (NaturalSort.titleOf(candidate.getName()).equalsIgnoreCase(title)) {
                file = candidate;
                break;
            }
        }
        if (file == null) throw new IOException("找不到书籍文件：" + title);
        return open(file, title);
    }

    public static Book open(File file, String title) throws IOException {
        String ext = NaturalSort.extension(file.getName());
        switch (ext) {
            case "pdf":
                return PdfBook.open(file);
            case "epub":
                return ArchiveBook.open(file, true);
            case "cbz":
            case "zip":
                return ArchiveBook.open(file, false);
            default:
                throw new IOException("支持打开 PDF、EPUB、CBZ 和图片 ZIP。");
        }
    }

    /** The natural-sorted library listing used by the shelf. */
    public static java.util.List<File> library(Store store) {
        return store.libraryFiles();
    }
}
