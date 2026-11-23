package com.komasnap.reader;

import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.Rect;
import android.os.Handler;
import android.os.Looper;
import android.view.GestureDetector;
import android.view.MotionEvent;
import android.view.VelocityTracker;
import android.view.View;
import android.view.ViewConfiguration;
import android.widget.Scroller;

import java.io.IOException;
import java.util.HashMap;
import java.util.Iterator;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Future;

/**
 * Continuous vertical reading (the desktop "上下滚动" motion).
 *
 * <p>Pages are laid out in a centred vertical column scaled to the viewport
 * width. Only the visible window plus a small buffer is decoded; the previous
 * frame stays visible while a page decodes, mirroring {@code ContinuousReader}
 * in the desktop app.</p>
 */
public class ScrolledReader extends View {

    private static final int GAP = 14;
    private static final int INSET = 20;
    private static final int KEEP_BEFORE = 1;
    private static final int KEEP_AFTER = 2;
    private static final int BACKGROUND = Color.rgb(23, 25, 28);
    private static final int MAX_DECODED = 8;

    private final ReaderController controller;
    private final ExecutorService renderer;
    private final Handler main = new Handler(Looper.getMainLooper());
    private final Paint paint = new Paint(Paint.FILTER_BITMAP_FLAG);
    private final Paint paper = new Paint();
    private final Rect destination = new Rect();
    private final Scroller scroller;
    private final int touchSlop;
    private final int maxFling;
    private final GestureDetector gestures;
    private final Map<Integer, Bitmap> decoded = new HashMap<>();
    private final Map<Integer, Future<?>> pending = new HashMap<>();
    private final java.util.ArrayDeque<Integer> order = new java.util.ArrayDeque<>();

    private Book book;
    private int pageCount;
    private int[] tops = new int[0];
    private int[] heights = new int[0];
    private float[] widths = new float[0];
    private int[] requested = new int[0];
    private int generation;
    private int firstRequested = -1, lastRequested = -1;
    private int reportedPage;
    private int scrollY;
    private int contentHeight;

    private int lastTouchY;
    private boolean dragging;
    private VelocityTracker velocity;

    public ScrolledReader(Context context, ReaderController controller, ExecutorService renderer) {
        super(context);
        this.controller = controller;
        this.renderer = renderer;
        setBackgroundColor(BACKGROUND);
        setFocusable(true);
        scroller = new Scroller(context);
        touchSlop = ViewConfiguration.get(context).getScaledTouchSlop();
        maxFling = ViewConfiguration.get(context).getScaledMaximumFlingVelocity();
        paper.setColor(Color.WHITE);
        gestures = new GestureDetector(context, new GestureDetector.SimpleOnGestureListener() {
            @Override
            public boolean onSingleTapConfirmed(MotionEvent e) {
                controller.onTapChrome();
                return true;
            }
        });
    }

    public void setBook(Book book, int page) {
        cancelDecodes();
        if (this.book != null && this.book != book) releaseDecoded();
        this.book = book;
        this.pageCount = book == null ? 0 : book.pageCount();
        tops = new int[pageCount];
        heights = new int[pageCount];
        widths = new float[pageCount];
        requested = new int[pageCount];
        reportedPage = page;
        computeLayout();
        jumpTo(page);
    }

    public int page() {
        return pageAt(scrollY + GAP + 0.5f);
    }

    public void jumpTo(int page) {
        if (pageCount == 0) return;
        int target = Math.max(0, Math.min(pageCount - 1, page));
        scrollY = Math.max(0, tops[target] - GAP);
        reportedPage = target;
        scroller.forceFinished(true);
        invalidate();
    }

    public void scrollBy(float distance) {
        int origin = scroller.isFinished() ? scrollY : scroller.getFinalY();
        int target = Math.max(0, Math.min(maxScroll(), origin + Math.round(distance)));
        scroller.startScroll(0, scrollY, 0, target - scrollY, 220);
        postInvalidateOnAnimation();
    }

    private int maxScroll() {
        return Math.max(0, contentHeight - getHeight());
    }

    private void computeLayout() {
        if (book == null) {
            contentHeight = 0;
            return;
        }
        float usable = Math.max(1, getWidth() - INSET * 2);
        int y = GAP;
        for (int i = 0; i < pageCount; i++) {
            float pw = Math.max(1, book.widthOf(i));
            float ph = Math.max(1, book.heightOf(i));
            float scale = usable / pw;
            if (!controller.fitWidth()) scale = Math.min(scale, Math.max(1, getHeight() - GAP * 2) / ph);
            widths[i] = pw * scale;
            heights[i] = Math.max(1, Math.round(ph * scale));
            tops[i] = y;
            y += heights[i] + GAP;
        }
        contentHeight = Math.max(y, pageCount == 0 ? 0 : tops[pageCount - 1] - GAP + Math.max(1, getHeight()));
    }

    @Override
    protected void onSizeChanged(int w, int h, int oldw, int oldh) {
        super.onSizeChanged(w, h, oldw, oldh);
        int page = reportedPage;
        cancelDecodes();
        java.util.Arrays.fill(requested, 0);
        computeLayout();
        jumpTo(page);
        invalidate();
    }

    @Override
    public void computeScroll() {
        if (scroller.computeScrollOffset()) {
            moveScroll(scroller.getCurrY());
            postInvalidateOnAnimation();
        }
    }

    private int pageAt(float offset) {
        if (pageCount == 0) return 0;
        int low = 0;
        int high = pageCount - 1;
        while (low < high) {
            int mid = low + (high - low + 1) / 2;
            if (tops[mid] <= offset) low = mid;
            else high = mid - 1;
        }
        return low;
    }

    // region rendering ------------------------------------------------------

    private void cancelDecodes() {
        generation++;
        for (Future<?> job : pending.values()) job.cancel(false);
        pending.clear();
        firstRequested = lastRequested = -1;
    }

    private void requestDecode(int page) {
        final Book owner = book;
        int width = owner.renderWidth(page, widths[page]);
        Bitmap hit = decoded.get(page);
        if (hit != null && hit.getWidth() == width) return;
        if (requested[page] == width) return;
        requested[page] = width;
        hit = owner.cached(page, width);
        if (hit != null) {
            remember(page, hit);
            return;
        }
        final int token = generation;
        Future<?> job = renderer.submit(() -> {
            Bitmap bitmap = null;
            try {
                bitmap = owner.render(page, width);
                bitmap.prepareToDraw();
            } catch (IOException | RuntimeException e) {
                bitmap = null;
            }
            final Bitmap result = bitmap;
            main.post(() -> {
                if (book != owner || generation != token || !pending.containsKey(page) || requested[page] != width) return;
                pending.remove(page);
                if (result != null) remember(page, result);
                invalidate();
            });
        });
        pending.put(page, job);
    }

    private void remember(int page, Bitmap bitmap) {
        decoded.put(page, bitmap);
        order.remove(page);
        order.addFirst(page);
        trimDecoded();
    }

    private void trimDecoded() {
        while (order.size() > MAX_DECODED) {
            int oldest = order.removeLast();
            if (oldest == pageAt(scrollY) || oldest == pageAt(scrollY + getHeight())) {
                order.addFirst(oldest);
                break;
            }
            // The bitmap belongs to Book's cache; only drop our reference here.
            decoded.remove(oldest);
            if (!pending.containsKey(oldest)) requested[oldest] = 0;
        }
    }

    private void releaseDecoded() {
        decoded.clear();
        order.clear();
    }

    @Override
    protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);
        Book owner = book;
        if (owner == null || pageCount == 0) return;
        int first = pageAt(scrollY);
        int last = pageAt(scrollY + getHeight());
        int from = Math.max(0, first - KEEP_BEFORE);
        int to = Math.min(pageCount - 1, last + KEEP_AFTER);

        if (first != firstRequested || last != lastRequested) {
            firstRequested = first;
            lastRequested = last;
            Iterator<Map.Entry<Integer, Future<?>>> jobs = pending.entrySet().iterator();
            while (jobs.hasNext()) {
                Map.Entry<Integer, Future<?>> job = jobs.next();
                int page = job.getKey();
                if (page >= from && page <= to) continue;
                job.getValue().cancel(false);
                requested[page] = 0;
                jobs.remove();
            }
            for (int page = first; page <= last; page++) requestDecode(page);
            for (int page = last + 1; page <= to; page++) requestDecode(page);
            for (int page = from; page < first; page++) requestDecode(page);
        }

        for (int page = first; page <= last; page++) {
            int top = tops[page] - scrollY;
            int bottom = top + heights[page];
            if (bottom < 0 || top > getHeight()) continue;
            int width = Math.round(widths[page]);
            int left = (getWidth() - width) / 2;
            destination.set(left, top, left + width, bottom);
            Bitmap bitmap = decoded.get(page);
            if (bitmap != null) canvas.drawBitmap(bitmap, null, destination, paint);
            else canvas.drawRect(destination, paper);
        }
    }

    private void moveScroll(int offset) {
        scrollY = Math.max(0, Math.min(maxScroll(), offset));
        int page = page();
        if (page != reportedPage) {
            reportedPage = page;
            controller.onPositionChanged(page);
        }
        invalidate();
    }

    // endregion

    // region input ----------------------------------------------------------

    @Override
    public boolean onTouchEvent(MotionEvent event) {
        gestures.onTouchEvent(event);
        switch (event.getActionMasked()) {
            case MotionEvent.ACTION_DOWN:
                scroller.forceFinished(true);
                lastTouchY = (int) event.getY();
                dragging = false;
                if (velocity == null) velocity = VelocityTracker.obtain();
                velocity.clear();
                velocity.addMovement(event);
                break;
            case MotionEvent.ACTION_MOVE:
                if (velocity != null) velocity.addMovement(event);
                int dy = (int) event.getY() - lastTouchY;
                if (!dragging && Math.abs(dy) > touchSlop) dragging = true;
                if (dragging) {
                    moveScroll(scrollY - dy);
                    lastTouchY = (int) event.getY();
                    invalidate();
                }
                break;
            case MotionEvent.ACTION_UP:
                if (dragging) {
                    if (velocity != null) {
                        velocity.computeCurrentVelocity(1000, maxFling);
                        int vy = (int) -velocity.getYVelocity();
                        scroller.fling(0, scrollY, 0, vy, 0, 0, 0, maxScroll());
                        postInvalidateOnAnimation();
                    }
                }
                dragging = false;
                if (velocity != null) {
                    velocity.recycle();
                    velocity = null;
                }
                break;
            case MotionEvent.ACTION_CANCEL:
                dragging = false;
                if (velocity != null) {
                    velocity.recycle();
                    velocity = null;
                }
                break;
            default:
                break;
        }
        return true;
    }

    /** 0..1 progress based on the page currently at the top. */
    public float progress() {
        if (pageCount <= 1) return 0f;
        return pageAt(scrollY) / (float) (pageCount - 1);
    }

    // endregion
}
