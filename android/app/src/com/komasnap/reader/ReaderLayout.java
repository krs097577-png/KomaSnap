package com.komasnap.reader;

import android.content.Context;
import android.graphics.Canvas;
import android.view.ViewGroup;
import android.widget.FrameLayout;


/**
 * Hosts the current reading surface and switches between paged and continuous
 * modes while keeping the same book, page and zoom behaviour.
 */
public class ReaderLayout extends FrameLayout {

    private final PagedReader paged;
    private final ScrolledReader scrolled;
    private boolean scrollMode;
    private int tint;

    public ReaderLayout(Context context, ReaderController controller, java.util.concurrent.ExecutorService renderer) {
        super(context);
        paged = new PagedReader(context, controller, renderer);
        scrolled = new ScrolledReader(context, controller, renderer);
        addView(paged, new LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.MATCH_PARENT));
        addView(scrolled, new LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.MATCH_PARENT));
        scrolled.setVisibility(GONE);
    }

    public void bind(Book book, int page, boolean scroll) {
        this.scrollMode = scroll;
        if (scroll) {
            paged.suspend();
            paged.setVisibility(GONE);
            scrolled.setVisibility(VISIBLE);
            scrolled.setBook(book, page);
        } else {
            scrolled.setBook(null, 0);
            scrolled.setVisibility(GONE);
            paged.setVisibility(VISIBLE);
            paged.setBook(book);
            paged.jumpTo(page);
        }
    }

    void applyTheme(ReaderTheme theme) {
        setBackgroundColor(theme.canvas);
        paged.applyTheme(theme);
        scrolled.setBackgroundColor(theme.canvas);
        tint = theme.tint;
        invalidate();
    }

    @Override
    protected void dispatchDraw(Canvas canvas) {
        super.dispatchDraw(canvas);
        if (tint != 0) canvas.drawColor(tint);
    }

    public PagedReader paged() {
        return paged;
    }

    public ScrolledReader scrolled() {
        return scrolled;
    }

    public boolean scrollMode() {
        return scrollMode;
    }

    public void suspend() {
        paged.suspend();
        scrolled.setBook(null, 0);
    }
}
