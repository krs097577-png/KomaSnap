package com.komasnap.reader;

import android.graphics.Bitmap;
import android.graphics.Color;
import android.graphics.pdf.PdfRenderer;
import android.os.ParcelFileDescriptor;

import java.io.File;
import java.io.IOException;

/** {@link android.graphics.pdf.PdfRenderer} backed book (the platform PDFium). */
public final class PdfBook extends Book {

    private PdfRenderer renderer;
    private ParcelFileDescriptor descriptor;
    private final Object lock = new Object();
    private boolean broken;

    private PdfBook(File file, int[] widths, int[] heights) throws IOException {
        super(widths, heights, null);
        descriptor = ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_ONLY);
        try {
            renderer = new PdfRenderer(descriptor);
        } catch (IOException | RuntimeException e) {
            Streams.closeQuietly(descriptor);
            descriptor = null;
            throw e instanceof IOException ? (IOException) e : new IOException("此 PDF 无法打开：" + e.getMessage());
        }
    }

    public static PdfBook open(File file) throws IOException {
        ParcelFileDescriptor probe = ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_ONLY);
        PdfRenderer renderer = null;
        try {
            renderer = new PdfRenderer(probe);
            int count = renderer.getPageCount();
            if (count <= 0) throw new IOException("PDF 没有可读页面。");
            int[] widths = new int[count];
            int[] heights = new int[count];
            for (int i = 0; i < count; i++) {
                PdfRenderer.Page page = renderer.openPage(i);
                try {
                    widths[i] = Math.max(1, Math.round(page.getWidth()));
                    heights[i] = Math.max(1, Math.round(page.getHeight()));
                } finally {
                    page.close();
                }
            }
            return new PdfBook(file, widths, heights);
        } catch (SecurityException e) {
            throw new IOException("此 PDF 需要密码，请先在支持密码的应用中另存一份可直接打开的副本。");
        } finally {
            if (renderer != null) {
                try {
                    renderer.close();
                } catch (RuntimeException ignored) {
                }
            }
            Streams.closeQuietly(probe);
        }
    }

    @Override
    protected Bitmap renderPage(int page, int width) throws IOException {
        synchronized (lock) {
            if (broken || renderer == null) throw new IOException("此 PDF 已关闭，请重新打开。");
            try {
                return renderLocked(page, width);
            } catch (RuntimeException first) {
                // A single failed render can poison the renderer; rebuild it once.
                try {
                    rebuild();
                    return renderLocked(page, width);
                } catch (RuntimeException second) {
                    broken = true;
                    throw new IOException("无法渲染 PDF 第 " + (page + 1) + " 页：" + second.getMessage());
                }
            }
        }
    }

    private Bitmap renderLocked(int page, int width) throws IOException {
        int logicalWidth = Math.max(1, widths[page]);
        int logicalHeight = Math.max(1, heights[page]);
        int height = Math.max(1, Math.round(width * (logicalHeight / (float) logicalWidth)));
        PdfRenderer.Page pdfPage = renderer.openPage(page);
        try {
            Bitmap bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888);
            bitmap.eraseColor(Color.WHITE);
            pdfPage.render(bitmap, null, null, PdfRenderer.Page.RENDER_MODE_FOR_DISPLAY);
            return bitmap;
        } finally {
            pdfPage.close();
        }
    }

    private void rebuild() {
        if (descriptor != null) {
            try {
                renderer = new PdfRenderer(descriptor);
            } catch (IOException e) {
                broken = true;
            }
        }
    }

    @Override
    public void close() {
        synchronized (lock) {
            if (renderer != null) {
                try {
                    renderer.close();
                } catch (RuntimeException ignored) {
                }
                renderer = null;
            }
            Streams.closeQuietly(descriptor);
            descriptor = null;
            broken = true;
        }
        clearCache();
    }
}
