package com.komasnap.reader;

/** Everything a reading surface needs to know about the current book and mode. */
public interface ReaderController {

    Book book();

    /** Current 0-based page. */
    int page();

    boolean rightToLeft();

    boolean fitWidth();

    boolean curlMotion();

    boolean dual();
    java.util.List<int[]> spreads();

    /** Moves to the spread that contains {@code page} and re-renders. */
    void goTo(int page);

    /** Moves one spread forward/backward and returns true when the position changed. */
    boolean turn(int direction);

    void onTapChrome();

    void onZoomChanged();

    void onPositionChanged(int page);
}
