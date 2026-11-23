package com.komasnap.reader;

import android.animation.Animator;
import android.animation.AnimatorListenerAdapter;
import android.animation.ValueAnimator;
import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.Path;
import android.graphics.RectF;
import android.os.Handler;
import android.os.Looper;
import android.view.GestureDetector;
import android.view.MotionEvent;
import android.view.ScaleGestureDetector;
import android.view.View;
import android.view.animation.DecelerateInterpolator;
import android.widget.Scroller;

import java.io.IOException;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Future;
import java.util.List;

/**
 * Paged (single/double) reading surface.
 *
 * <p>Layout follows the desktop reader: pages are scaled to fit the page (or the
 * width), centred, and mirrored for right-to-left books. Bitmaps are rendered
 * off the UI thread at the displayed size through {@link Book#render}; the
 * previous spread stays on screen until the new one is complete, then a short
 * slide animation runs.</p>
 */
public class PagedReader extends View {

    private static final int BACKGROUND = Color.rgb(23, 25, 28);
    private static final long SLIDE_MS = 250;

    private final ReaderController controller;
    private final ExecutorService renderer;
    private final Handler main = new Handler(Looper.getMainLooper());
    private final Paint paint = new Paint(Paint.FILTER_BITMAP_FLAG | Paint.ANTI_ALIAS_FLAG);
    private final RectF destination = new RectF();
    private final PageCurl pageCurl = new PageCurl();
    private int errorColor = ReaderTheme.DARK.accent;
    private final GestureDetector gestures;
    private final ScaleGestureDetector scaleDetector;
    private final Scroller scroller;

    private int[] pages = new int[0];
    private Bitmap[] bitmaps = new Bitmap[0];
    private int currentSpread = -1;

    private float baseScale = 1f;
    private float zoom = 1f;
    private float offsetX;
    private float offsetY;
    private float contentWidth;
    private float contentHeight;

    // Drag-to-turn.
    private float downX;
    private float downY;
    private boolean swipe;
    private boolean multiTouch;
    private float dragOffset;
    private int dragDirection;
    private int[] dragPages;
    private Bitmap[] dragBitmaps;
    private float dragScale;

    // Slide animation of a completed turn.
    private int[] oldPages;
    private Bitmap[] oldBitmaps;
    private float oldWidth;
    private int slideDirection;
    private float oldScale, oldOffsetX, oldOffsetY;
    private float slideProgress, slideDistance, slideFrom;
    private final ValueAnimator animation = ValueAnimator.ofFloat(0f, 1f);
    private boolean sliding;
    private boolean curling;

    private volatile int renderGeneration;
    private int requestedSpread = -1;
    private Future<?> pendingRender;
    private Future<?> pendingPreload;
    private float heldDrag;
    private String error;
    private final Runnable sharpen = () -> {
        if (currentSpread >= 0 && requestedSpread == currentSpread)
            renderSpread(pages, currentSpread, 0, 0f);
    };

    public PagedReader(Context context, ReaderController controller, ExecutorService renderer) {
        super(context);
        this.controller = controller;
        this.renderer = renderer;
        setBackgroundColor(BACKGROUND);
        setFocusable(true);
        animation.setDuration(SLIDE_MS);
        animation.setInterpolator(new DecelerateInterpolator());
        animation.addUpdateListener(frame -> {
            slideProgress = (float) frame.getAnimatedValue();
            invalidate();
        });
        animation.addListener(new AnimatorListenerAdapter() {
            @Override
            public void onAnimationEnd(Animator animator) {
                sliding = false;
                oldPages = null;
                oldBitmaps = null;
                invalidate();
            }
        });
        gestures = new GestureDetector(context, new GestureDetector.SimpleOnGestureListener() {
            @Override
            public boolean onSingleTapConfirmed(MotionEvent e) {
                return handleTap(e.getX());
            }

            @Override
            public boolean onDoubleTap(MotionEvent e) {
                if (zoom > 1.05f) resetZoom();
                else zoomTo(2.5f, e.getX(), e.getY());
                return true;
            }

            @Override
            public boolean onFling(MotionEvent e1, MotionEvent e2, float vx, float vy) {
                if (!swipe && scroller.isFinished() && (zoom > 1.02f || canScroll())) {
                    scroller.fling((int) offsetX, (int) offsetY, (int) -vx, (int) -vy,
                            0, (int) Math.max(0, contentWidth - getWidth()),
                            0, (int) Math.max(0, contentHeight - getHeight()));
                    postInvalidateOnAnimation();
                    return true;
                }
                return false;
            }
        });
        scaleDetector = new ScaleGestureDetector(context, new ScaleGestureDetector.SimpleOnScaleGestureListener() {
            @Override
            public boolean onScale(ScaleGestureDetector detector) {
                zoomTo(zoom * detector.getScaleFactor(), detector.getFocusX(), detector.getFocusY());
                return true;
            }
        });
        scroller = new Scroller(context);
    }

    // region public API -----------------------------------------------------

    /** Binds a book (resetting cached failure tiles and zoom). */
    public void setBook(Book book) {
        suspend();
        pages = new int[0];
        bitmaps = new Bitmap[0];
        currentSpread = -1;
        zoom = 1f;
        offsetX = offsetY = 0;
        computeLayout();
        invalidate();
    }

    public void suspend() {
        ++renderGeneration;
        cancelQueuedRender();
        main.removeCallbacks(sharpen);
        animation.cancel();
        heldDrag = 0f;
        requestedSpread = -1;
        swipe = false;
        dragPages = null;
        dragBitmaps = null;
    }

    public int spreadIndex() {
        return currentSpread;
    }

    public float zoom() {
        return zoom;
    }

    public void resetZoom() {
        zoom = 1f;
        offsetX = 0;
        offsetY = 0;
        computeLayout();
        invalidate();
        controller.onZoomChanged();
    }

    public void zoomBy(float factor) {
        zoomTo(zoom * factor, getWidth() / 2f, getHeight() / 2f);
    }

    public void showError(String message) {
        error = message;
        invalidate();
    }

    /** Requests a turn; the spread is rendered, then animated in. */
    public boolean turn(int direction) {
        return turn(direction, 0f);
    }

    private boolean turn(int direction, float fromDrag) {
        if (controller.book() == null || currentSpread < 0) return false;
        int from = requestedSpread >= 0 ? requestedSpread : currentSpread;
        List<int[]> spreads = controller.spreads();
        int target = from + direction;
        if (target < 0 || target >= spreads.size()) return false;
        renderSpread(spreads.get(target), target, direction, fromDrag);
        return true;
    }

    /** Jumps to the spread containing {@code page}; no animation. */
    public void jumpTo(int page) {
        Book book = controller.book();
        if (book == null) return;
        List<int[]> spreads = controller.spreads();
        if (spreads.isEmpty()) return;
        int index = 0;
        for (int i = 0; i < spreads.size(); i++) {
            for (int p : spreads.get(i)) if (p == page) index = i;
        }
        renderSpread(spreads.get(index), index, 0, 0f);
    }

    // endregion

    // region rendering ------------------------------------------------------

    /**
     * Render width: the page is displayed {@code widthOf * scale} device pixels
     * wide in the Android canvas. The cache rounds up to a nearby resolution.
     */
    private float renderWidthFor(Book book, int page, float scale) {
        return Math.max(1f, book.widthOf(page) * scale);
    }

    private void cancelQueuedRender() {
        if (pendingRender != null) pendingRender.cancel(false);
        if (pendingPreload != null) pendingPreload.cancel(false);
        pendingRender = pendingPreload = null;
    }

    private void renderSpread(final int[] spread, final int index, final int direction, float fromDrag) {
        final Book book = controller.book();
        if (book == null || spread.length == 0) return;
        final int generation = ++renderGeneration;
        cancelQueuedRender();
        requestedSpread = index;
        heldDrag = fromDrag;
        if (getWidth() == 0 || !isAttachedToWindow()) return;
        final float scale = scaleFor(book, spread) * zoom;
        final Bitmap[] result = new Bitmap[spread.length];
        boolean complete = true;
        for (int i = 0; i < spread.length; i++) {
            result[i] = book.cached(spread[i], renderWidthFor(book, spread[i], scale));
            complete &= result[i] != null;
        }
        if (complete) {
            applySpread(spread, result, index, direction);
            preload(book, index, direction, generation);
            return;
        }
        pendingRender = renderer.submit(() -> {
            try {
                for (int i = 0; i < spread.length; i++) {
                    if (generation != renderGeneration) return;
                    if (result[i] == null) {
                        result[i] = book.render(spread[i], renderWidthFor(book, spread[i], scale));
                        result[i].prepareToDraw();
                    }
                }
            } catch (IOException | RuntimeException error) {
                main.post(() -> {
                    if (generation != renderGeneration) return;
                    requestedSpread = currentSpread;
                    heldDrag = 0f;
                    showError("无法显示当前页。\n" + error.getMessage());
                });
                return;
            }
            main.post(() -> {
                if (generation != renderGeneration) return;
                applySpread(spread, result, index, direction);
                preload(book, index, direction, generation);
            });
        });
    }

    private void preload(Book book, int index, int direction, int generation) {
        List<int[]> spreads = controller.spreads();
        int step = direction < 0 ? -1 : 1;
        int[] indices = {index + step, index - step, index + step * 2};
        float[] scales = new float[indices.length];
        long budget = 96L * 1024 * 1024;
        for (int page : pages) budget -= book.estimatedRenderBytes(page, renderWidthFor(book, page, scale()));
        for (int i = 0; i < indices.length; i++) {
            int target = indices[i];
            if (target < 0 || target >= spreads.size()) continue;
            int[] candidate = spreads.get(target);
            float targetScale = scaleFor(book, candidate) * zoom;
            long bytes = 0L;
            for (int page : candidate) bytes += book.estimatedRenderBytes(page, renderWidthFor(book, page, targetScale));
            if (bytes > budget) continue;
            budget -= bytes;
            scales[i] = targetScale;
        }
        pendingPreload = renderer.submit(() -> {
            for (int i = 0; i < indices.length; i++) {
                if (scales[i] <= 0f) continue;
                for (int page : spreads.get(indices[i])) {
                    if (generation != renderGeneration) return;
                    try {
                        book.render(page, renderWidthFor(book, page, scales[i])).prepareToDraw();
                    } catch (IOException | RuntimeException ignored) {
                        // 前台实际打开此页时才显示错误。
                    }
                }
            }
        });
    }

    private void applySpread(int[] spread, Bitmap[] images, int index, int direction) {
        boolean sameSpread = index == currentSpread && direction == 0;
        boolean animate = direction != 0 && pages.length > 0 && bitmaps.length > 0 && !sliding;
        animation.cancel();
        if (animate) {
            oldPages = pages;
            oldBitmaps = bitmaps;
            oldWidth = contentWidth;
            oldScale = scale();
            oldOffsetX = offsetX;
            oldOffsetY = offsetY;
            slideDirection = controller.rightToLeft() ? direction : -direction;
            curling = controller.curlMotion();
            slideProgress = 0f;
            sliding = true;
        }
        pages = spread;
        bitmaps = images;
        currentSpread = index;
        requestedSpread = index;
        error = null;
        if (!sameSpread) offsetX = offsetY = 0;
        computeLayout();
        if (sliding) {
            slideDistance = curling ? curlDistance(oldWidth)
                    : Math.max(1f, Math.min(getWidth(), (oldWidth + contentWidth) / 2f + 24f));
            slideFrom = animationStart(heldDrag, slideDirection, slideDistance);
            animation.setDuration(curling ? Math.round(310 * (1f - slideFrom * 0.65f)) : SLIDE_MS);
            animation.start();
        }
        heldDrag = 0f;
        invalidate();
        // Record the new position for persistence.
        int position = spread[0];
        if (direction == 0) {
            for (int page : spread) if (page == controller.page()) position = page;
        }
        controller.onPositionChanged(position);
    }

    private float scaleFor(Book book, int[] spread) {
        float rawWidth = 0;
        float rawHeight = 0;
        for (int page : spread) {
            rawWidth += book.widthOf(page);
            rawHeight = Math.max(rawHeight, book.heightOf(page));
        }
        if (rawWidth <= 0 || rawHeight <= 0) return 1f;
        float availableWidth = Math.max(1, getWidth() - 42);
        float availableHeight = Math.max(1, getHeight() - 30);
        float fitWidthScale = availableWidth / rawWidth;
        float fitPageScale = Math.min(fitWidthScale, availableHeight / rawHeight);
        return controller.fitWidth() ? fitWidthScale : fitPageScale;
    }

    private void computeLayout() {
        if (pages.length == 0) {
            contentWidth = contentHeight = 0;
            return;
        }
        Book book = controller.book();
        if (book == null) return;
        float rawWidth = 0;
        float rawHeight = 0;
        for (int page : pages) {
            rawWidth += book.widthOf(page);
            rawHeight = Math.max(rawHeight, book.heightOf(page));
        }
        if (rawWidth <= 0 || rawHeight <= 0) return;
        baseScale = scaleFor(book, pages);
        contentWidth = rawWidth * baseScale * zoom;
        contentHeight = rawHeight * baseScale * zoom;
        clampOffsets();
    }

    private float scale() {
        return baseScale * zoom;
    }

    private boolean canScroll() {
        return contentWidth > getWidth() + 1 || contentHeight > getHeight() + 1;
    }

    private void clampOffsets() {
        offsetX = Math.max(0, Math.min(Math.max(0, contentWidth - getWidth()), offsetX));
        offsetY = Math.max(0, Math.min(Math.max(0, contentHeight - getHeight()), offsetY));
    }

    @Override
    protected void onSizeChanged(int w, int h, int oldw, int oldh) {
        super.onSizeChanged(w, h, oldw, oldh);
        computeLayout();
        List<int[]> spreads = controller.spreads();
        if (getVisibility() == VISIBLE && requestedSpread >= 0 && requestedSpread < spreads.size())
            renderSpread(spreads.get(requestedSpread), requestedSpread, 0, 0f);
    }

    @Override
    public void computeScroll() {
        if (scroller.computeScrollOffset()) {
            offsetX = scroller.getCurrX();
            offsetY = scroller.getCurrY();
            clampOffsets();
            postInvalidateOnAnimation();
        }
    }

    // endregion

    // region input ----------------------------------------------------------

    private boolean handleTap(float x) {
        float width = getWidth();
        int physical = x < width * 0.22f ? -1 : x > width * 0.78f ? 1 : 0;
        if (physical == 0) {
            controller.onTapChrome();
            return true;
        }
        int direction = controller.rightToLeft() ? -physical : physical;
        turn(direction);
        return true;
    }

    @Override
    public boolean onTouchEvent(MotionEvent event) {
        scaleDetector.onTouchEvent(event);
        boolean handled = gestures.onTouchEvent(event);
        switch (event.getActionMasked()) {
            case MotionEvent.ACTION_DOWN:
                downX = event.getX();
                downY = event.getY();
                swipe = false;
                multiTouch = false;
                dragOffset = 0;
                dragPages = null;
                dragBitmaps = null;
                if (!scroller.isFinished()) scroller.forceFinished(true);
                handled = true;
                break;
            case MotionEvent.ACTION_POINTER_DOWN:
                multiTouch = true;
                swipe = false;
                dragOffset = 0;
                dragPages = null;
                dragBitmaps = null;
                invalidate();
                break;
            case MotionEvent.ACTION_POINTER_UP:
                int remaining = event.getActionIndex() == 0 ? 1 : 0;
                downX = event.getX(remaining);
                downY = event.getY(remaining);
                break;
            case MotionEvent.ACTION_MOVE:
                if (event.getPointerCount() > 1) break;
                float dx = event.getX() - downX;
                float dy = event.getY() - downY;
                boolean horizontal = Math.abs(dx) > Math.abs(dy) * 1.25f;
                boolean canTurn = !multiTouch && contentWidth <= getWidth() + 1
                        && requestedSpread == currentSpread && currentSpread >= 0
                        && (!sliding || controller.curlMotion());
                if (!swipe && canScroll() && !(canTurn && horizontal)) {
                    if (Math.abs(dx) + Math.abs(dy) > 3) {
                        offsetX -= dx;
                        offsetY -= dy;
                        downX = event.getX();
                        downY = event.getY();
                        clampOffsets();
                        invalidate();
                    }
                    break;
                }
                if (canTurn && (swipe || (Math.abs(dx) > 9 && horizontal))) {
                    if (sliding) animation.end();
                    int direction = ((dx > 0) == controller.rightToLeft()) ? 1 : -1;
                    Book book = controller.book();
                    List<int[]> spreads = controller.spreads();
                    int target = currentSpread + direction;
                    if ((!swipe || dragDirection != direction) && book != null) {
                        dragDirection = direction;
                        dragPages = null;
                        if (target >= 0 && target < spreads.size()) {
                            int[] neighbor = spreads.get(target);
                            dragScale = scaleFor(book, neighbor) * zoom;
                            Bitmap[] images = new Bitmap[neighbor.length];
                            boolean ready = true;
                            for (int i = 0; i < neighbor.length; i++) {
                                images[i] = book.cached(neighbor[i], renderWidthFor(book, neighbor[i], dragScale));
                                ready &= images[i] != null;
                            }
                            if (ready) { dragPages = neighbor; dragBitmaps = images; }
                        }
                    }
                    swipe = true;
                }
                if (swipe) {
                    dragOffset = (dragPages != null ? dx : dx * 0.45f);
                    postInvalidateOnAnimation();
                    handled = true;
                }
                break;
            case MotionEvent.ACTION_UP:
                if (swipe) {
                    float dx2 = event.getX() - downX;
                    long elapsed = Math.max(1, event.getEventTime() - event.getDownTime());
                    boolean commit = Math.abs(dx2) >= Math.min(120, getWidth() * 0.16f)
                            || (Math.abs(dx2) >= 36 && Math.abs(dx2) / (float) elapsed >= 0.55f);
                    int direction = ((dx2 > 0) == controller.rightToLeft()) ? 1 : -1;
                    swipe = false;
                    float fromDrag = controller.curlMotion() && dragPages == null ? 0f : dragOffset;
                    dragOffset = 0;
                    if (!commit || !turn(direction, fromDrag)) invalidate();
                    dragPages = null;
                    dragBitmaps = null;
                    handled = true;
                }
                break;
            case MotionEvent.ACTION_CANCEL:
                swipe = false;
                dragOffset = 0;
                dragPages = null;
                dragBitmaps = null;
                invalidate();
                break;
            default:
                break;
        }
        if (handled && getParent() != null) getParent().requestDisallowInterceptTouchEvent(true);
        return true;
    }

    private void zoomTo(float target, float focusX, float focusY) {
        float clamped = Math.max(0.35f, Math.min(3f, target));
        if (Math.abs(clamped - zoom) < 0.002f) return;
        float centreX = getWidth() / 2f;
        float centreY = getHeight() / 2f;
        float contentX = (centreX - offsetX) / scale();
        float contentY = (centreY - offsetY) / scale();
        zoom = clamped;
        computeLayout();
        offsetX = centreX - contentX * scale();
        offsetY = centreY - contentY * scale();
        clampOffsets();
        invalidate();
        controller.onZoomChanged();
        main.removeCallbacks(sharpen);
        main.postDelayed(sharpen, 160);
    }

    // endregion

    // region drawing --------------------------------------------------------

    void applyTheme(ReaderTheme theme) {
        setBackgroundColor(theme.canvas);
        errorColor = theme.accent;
        pageCurl.setPaperColor(theme.paper);
        invalidate();
    }

    private float curlDistance(float width) {
        return Math.max(1f, Math.min(getWidth(), width > 0 ? width : getWidth()) * 0.85f);
    }

    private static float animationStart(float dx, int direction, float distance) {
        return Math.max(0f, Math.min(0.95f, dx * direction / distance));
    }

    private void drawCurl(Canvas canvas, Book book, int[] frontPages, Bitmap[] frontImages,
                          float frontX, float frontY, float frontScale, float frontWidth,
                          int[] backPages, Bitmap[] backImages, float backX, float backY, float backScale,
                          int direction, float progress) {
        if (backPages != null) drawSpread(canvas, book, backPages, backImages, backX, backY, backScale, 0f);
        float height = 0f;
        for (int page : frontPages) height = Math.max(height, book.heightOf(page) * frontScale);
        float left = Math.max(0f, (getWidth() - frontWidth) / 2f) - frontX;
        float top = Math.max(0f, (getHeight() - height) / 2f) - frontY;
        int saved = canvas.save();
        // 裁剪到旧页范围，避免卷角和阴影画到阅读区空白处。
        canvas.clipRect(left, top, left + frontWidth, top + height);
        Path paper = pageCurl.update(left, top, frontWidth, height, direction, progress);
        if (backPages != null) canvas.clipPath(paper);
        drawSpread(canvas, book, frontPages, frontImages, frontX, frontY, frontScale, 0f);
        canvas.restoreToCount(saved);
        pageCurl.drawFold(canvas, left, top, frontWidth, height);
    }

    @Override
    protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);
        Book book = controller.book();
        if (book == null) return;
        float s = scale();
        if (sliding && oldPages != null) {
            float progress = slideFrom + (1f - slideFrom) * slideProgress;
            if (curling) {
                drawCurl(canvas, book, oldPages, oldBitmaps, oldOffsetX, oldOffsetY, oldScale, oldWidth,
                        pages, bitmaps, offsetX, offsetY, s, slideDirection, progress);
            } else {
                float movement = slideDirection * slideDistance;
                drawSpread(canvas, book, oldPages, oldBitmaps, oldOffsetX, oldOffsetY, oldScale, movement * progress);
                drawSpread(canvas, book, pages, bitmaps, offsetX, offsetY, s, movement * (progress - 1f));
            }
        } else if (controller.curlMotion() && (swipe || heldDrag != 0f)) {
            float dx = swipe ? dragOffset : heldDrag;
            int physical = dx >= 0 ? 1 : -1;
            drawCurl(canvas, book, pages, bitmaps, offsetX, offsetY, s, contentWidth,
                    dragPages, dragBitmaps, 0f, 0f, dragScale, physical,
                    animationStart(dx, physical, curlDistance(contentWidth)));
        } else if (swipe) {
            drawSpread(canvas, book, pages, bitmaps, offsetX, offsetY, s, dragOffset);
            if (dragPages != null) {
                float width = 0f;
                for (int page : dragPages) width += book.widthOf(page) * dragScale;
                float gap = Math.min(getWidth(), (contentWidth + width) / 2f + 24f);
                drawSpread(canvas, book, dragPages, dragBitmaps, 0f, 0f, dragScale,
                        dragOffset - Math.copySign(gap, dragOffset));
            }
        } else {
            drawSpread(canvas, book, pages, bitmaps, offsetX, offsetY, s, heldDrag);
        }
        if (error != null) drawError(canvas);
    }

    private void drawSpread(Canvas canvas, Book book, int[] spread, Bitmap[] images,
                            float offsetX, float offsetY, float scale, float translateX) {
        if (spread == null || spread.length == 0) return;
        float rawWidth = 0;
        float rawHeight = 0;
        for (int page : spread) {
            rawWidth += book.widthOf(page);
            rawHeight = Math.max(rawHeight, book.heightOf(page));
        }
        float width = rawWidth * scale;
        float height = rawHeight * scale;
        float x = Math.max(0, (getWidth() - width) / 2f) - offsetX + translateX;
        float y = Math.max(0, (getHeight() - height) / 2f) - offsetY;
        boolean rtl = controller.rightToLeft();
        for (int i = 0; i < spread.length; i++) {
            int slot = rtl ? spread.length - 1 - i : i;
            int page = spread[slot];
            float w = book.widthOf(page) * scale;
            float h = book.heightOf(page) * scale;
            Bitmap bitmap = slot < images.length ? images[slot] : null;
            if (bitmap != null && x < getWidth() && x + w > 0) {
                destination.set(x, y, x + w, y + h);
                canvas.drawBitmap(bitmap, null, destination, paint);
            }
            x += w;
        }
    }

    private void drawError(Canvas canvas) {
        Paint p = new Paint(Paint.ANTI_ALIAS_FLAG);
        p.setColor(errorColor);
        p.setTextSize(getResources().getDisplayMetrics().scaledDensity * 14f);
        p.setTextAlign(Paint.Align.CENTER);
        String[] lines = error.split("\n");
        float y = getHeight() / 2f - (lines.length - 1) * p.getTextSize();
        for (String line : lines) {
            canvas.drawText(line, getWidth() / 2f, y, p);
            y += p.getTextSize() * 1.5f;
        }
    }
}
