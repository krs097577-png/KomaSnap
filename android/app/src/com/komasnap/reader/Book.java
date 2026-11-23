package com.komasnap.reader;

import android.graphics.Bitmap;

import java.io.IOException;

/**
 * Base type for every readable book page source.
 *
 * <p>Pages are laid out as fixed logical sizes (pixels at 96 dpi in the original
 * WPF app) and rendered lazily at a requested pixel width, exactly like
 * {@code ReaderBook} in KomaSnap.exe. Rendered bitmaps are cached in a small
 * LRU keyed by (page, rounded render width).</p>
 */
public abstract class Book {

    public interface Callback<T> {
        void onDone(T value, Throwable error);
    }

    public interface Listener {
        void onPageRendered(int page);
    }

    public static final int CACHE_ENTRIES = 16;
    public static final long CACHE_BYTES = 160L * 1024 * 1024;

    private static final class Key {
        final int page;
        final int width;

        Key(int page, int width) {
            this.page = page;
            this.width = width;
        }

        @Override
        public boolean equals(Object o) {
            if (!(o instanceof Key)) return false;
            Key k = (Key) o;
            return k.page == page && k.width == width;
        }

        @Override
        public int hashCode() {
            return page * 31 + width;
        }
    }

    private final java.util.LinkedHashMap<Key, Bitmap> cache =
            new java.util.LinkedHashMap<>(16, 0.75f, true);
    private final Object decodeLock = new Object();
    private long bytes;

    protected final int[] widths;
    protected final int[] heights;
    protected final Boolean preferredRightToLeft;
    protected volatile Listener listener;

    protected Book(int[] widths, int[] heights, Boolean preferredRightToLeft) {
        this.widths = widths;
        this.heights = heights;
        this.preferredRightToLeft = preferredRightToLeft;
    }

    public int pageCount() {
        return widths.length;
    }

    public int widthOf(int page) {
        return widths[page];
    }

    public int heightOf(int page) {
        return heights[page];
    }

    /** EPUB spine direction; null means "unknown, use the reader preference". */
    public Boolean preferredRightToLeft() {
        return preferredRightToLeft;
    }

    public void setListener(Listener listener) {
        this.listener = listener;
    }

    public void close() {
    }

    /** A page is "wide" when its width is at least 1.1x its height: never paired. */
    public boolean isWide(int page) {
        return widths[page] >= heights[page] * 1.1;
    }

    /**
     * Builds spreads exactly like the desktop reader: pair consecutive pages when
     * dual page is on, no solo cover exception applies, and neither page is wide.
     */
    public java.util.List<int[]> makeSpreads(boolean dual, boolean soloCover) {
        java.util.List<int[]> spreads = new java.util.ArrayList<>();
        int i = 0;
        while (i < pageCount()) {
            boolean canPair = dual && !(soloCover && i == 0) && i + 1 < pageCount()
                    && !isWide(i) && !isWide(i + 1);
            if (canPair) {
                spreads.add(new int[]{i, i + 1});
                i += 2;
            } else {
                spreads.add(new int[]{i});
                i++;
            }
        }
        return spreads;
    }

    /** Render width in pixels, rounded to 128 like the desktop reader, capped at 3200. */
    public int renderWidth(int page, float requestedWidth) {
        int w = Math.max(1, widths[page]);
        int h = Math.max(1, heights[page]);
        int maxWidth = Math.max(1, (int) (3200 * Math.min(1f, (float) w / (float) h)));
        int target = (int) (Math.ceil(Math.min(maxWidth, Math.max(1f, requestedWidth)) / 128f) * 128);
        return Math.min(maxWidth, Math.max(128, target));
    }

    public long estimatedRenderBytes(int page, float requestedWidth) {
        int width = renderWidth(page, requestedWidth);
        long height = (long) Math.ceil(width * (double) heights[page] / Math.max(1, widths[page]));
        return width * height * 4L;
    }

    public synchronized Bitmap cached(int page, float requestedWidth) {
        Key key = new Key(page, renderWidth(page, requestedWidth));
        return cache.get(key);
    }

    /**
     * Returns a cached bitmap, or renders one synchronously on a worker thread.
     * Callers must never invoke this on the UI thread.
     */
    public Bitmap render(int page, float requestedWidth) throws IOException {
        int width = renderWidth(page, requestedWidth);
        Bitmap hit = cached(page, width);
        if (hit != null) return hit;
        synchronized (decodeLock) {
            hit = cached(page, width);
            if (hit != null) return hit;
            Bitmap bitmap = renderPage(page, width);
            if (bitmap == null) throw new IOException("无法解码第 " + (page + 1) + " 页");
            if (bitmap.getWidth() != width) {
                bitmap = Bitmap.createScaledBitmap(bitmap, width,
                        Math.max(1, Math.round(bitmap.getHeight() * (width / (float) bitmap.getWidth()))), true);
            }
            synchronized (this) {
                cache.put(new Key(page, width), bitmap);
                bytes += (long) bitmap.getWidth() * bitmap.getHeight() * 4;
                trim();
            }
            Listener l = listener;
            if (l != null) l.onPageRendered(page);
            return bitmap;
        }
    }

    private void trim() {
        while (cache.size() > CACHE_ENTRIES) {
            java.util.Iterator<java.util.Map.Entry<Key, Bitmap>> it = cache.entrySet().iterator();
            if (!it.hasNext()) break;
            Bitmap b = it.next().getValue();
            bytes -= (long) b.getWidth() * b.getHeight() * 4;
            it.remove();
        }
        while (bytes > CACHE_BYTES && cache.size() > 1) {
            java.util.Iterator<java.util.Map.Entry<Key, Bitmap>> it = cache.entrySet().iterator();
            if (!it.hasNext()) break;
            Bitmap b = it.next().getValue();
            bytes -= (long) b.getWidth() * b.getHeight() * 4;
            it.remove();
        }
    }

    public synchronized void clearCache() {
        cache.clear();
        bytes = 0;
    }

    /** Renders one page at exactly {@code width} pixels on a worker thread. */
    protected abstract Bitmap renderPage(int page, int width) throws IOException;
}
