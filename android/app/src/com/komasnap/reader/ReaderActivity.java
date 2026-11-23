package com.komasnap.reader;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.content.res.ColorStateList;
import android.graphics.Typeface;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.InputType;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.KeyEvent;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowManager;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.HorizontalScrollView;
import android.widget.LinearLayout;
import android.widget.SeekBar;
import android.widget.TextView;
import android.widget.Toast;

import java.io.IOException;
import java.util.List;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/** Reading screen: header, paged/continuous surface and footer, ported from {@code Reader.cs}. */
public class ReaderActivity extends Activity implements ReaderController {

    public static final String EXTRA_TITLE = "title";
    public static final String EXTRA_PAGE = "page";

    private final Handler main = new Handler(Looper.getMainLooper());
    private final ExecutorService loader = Executors.newSingleThreadExecutor();
    private final ExecutorService renderer = Executors.newFixedThreadPool(2);

    private Store store;
    private State state;
    private ReaderTheme.Appearance appearance;
    private Shelf shelf;
    private Book book;
    private String title;
    private int position;
    private int spreadIndex;
    private List<int[]> spreads;

    private ReaderLayout layout;
    private LinearLayout header;
    private LinearLayout footer;
    private LinearLayout options;
    private TextView bookTitle;
    private EditText pageNumber;
    private TextView pageCountText;
    private TextView readingStatus;
    private TextView statusLabel;
    private SeekBar progress;
    private TextView notice;
    private boolean chromeVisible = true;
    private boolean changingProgress;
    private boolean fullscreen;
    private boolean opening;
    private final Runnable hideNotice = () -> setNotice(null, false);
    private final Runnable savePosition = this::savePositionNow;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        store = new Store(getFilesDir());
        state = new State(store.state());
        shelf = new Shelf(store);
        appearance = new ReaderTheme.Appearance(this);
        title = getIntent().getStringExtra(EXTRA_TITLE);
        buildUi();
        if (title == null || title.isEmpty()) {
            finish();
            return;
        }
        open(title, getIntent().getIntExtra(EXTRA_PAGE, -1));
    }

    // region UI -------------------------------------------------------------

    private void buildUi() {
        FrameLayout root = new FrameLayout(this);
        appearance.bind(theme -> root.setBackgroundColor(theme.app));

        LinearLayout column = new LinearLayout(this);
        column.setOrientation(LinearLayout.VERTICAL);
        root.addView(column, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));

        // Header ------------------------------------------------------------
        header = new LinearLayout(this);
        header.setOrientation(LinearLayout.VERTICAL);
        appearance.bind(theme -> header.setBackgroundColor(theme.surface));
        header.setPadding(dp(12), dp(9), dp(12), dp(9));
        column.addView(header, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        LinearLayout titleRow = new LinearLayout(this);
        titleRow.setOrientation(LinearLayout.HORIZONTAL);
        titleRow.setGravity(Gravity.CENTER_VERTICAL);
        header.addView(titleRow);

        TextView openButton = textButton("打开文件", true);
        openButton.setOnClickListener(v -> chooseFile());
        titleRow.addView(openButton, buttonParams());

        TextView shelfButton = textButton("书架", false);
        shelfButton.setOnClickListener(v -> finish());
        titleRow.addView(shelfButton, buttonParams());

        bookTitle = new TextView(this);
        appearance.bind(theme -> bookTitle.setTextColor(theme.text));
        bookTitle.setTextSize(15);
        bookTitle.setTypeface(Typeface.DEFAULT_BOLD);
        bookTitle.setSingleLine(true);
        bookTitle.setEllipsize(android.text.TextUtils.TruncateAt.END);
        bookTitle.setPadding(dp(8), 0, dp(10), 0);
        LinearLayout.LayoutParams titleParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f);
        titleRow.addView(bookTitle, titleParams);

        TextView themeButton = new TextView(this);
        themeButton.setTextSize(13);
        themeButton.setGravity(Gravity.CENTER);
        themeButton.setFocusable(true);
        appearance.bindMenu(themeButton);
        themeButton.setPadding(dp(11), 0, dp(11), 0);
        LinearLayout.LayoutParams themeParams = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, dp(38));
        titleRow.addView(themeButton, themeParams);

        // Options row -------------------------------------------------------
        HorizontalScrollView scroll = new HorizontalScrollView(this);
        scroll.setHorizontalScrollBarEnabled(false);
        options = new LinearLayout(this);
        options.setOrientation(LinearLayout.HORIZONTAL);
        options.setLayoutParams(new HorizontalScrollView.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        scroll.addView(options);
        header.addView(scroll, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        options.addView(optionButton("双页", "切换单页与双页", v -> toggleDual()));
        options.addView(optionButton("右到左", "切换阅读方向", v -> toggleDirection()));
        options.addView(optionButton("封面单页", "切换封面单页", v -> toggleCover()));
        options.addView(optionButton("适整页", "切换适整页与适宽", v -> toggleFit()));
        options.addView(optionButton("－", "缩小", v -> zoom(1 / 1.2f)));
        options.addView(optionButton("＋", "放大", v -> zoom(1.2f)));
        options.addView(optionButton("翻页方式", "选择翻页方式", v -> chooseMotion()));
        options.addView(optionButton("全屏", "全屏阅读", v -> toggleFullscreen()));

        // Reading surface ---------------------------------------------------
        layout = new ReaderLayout(this, this, renderer);
        appearance.bind(layout::applyTheme);
        column.addView(layout, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f));

        // Footer ------------------------------------------------------------
        footer = new LinearLayout(this);
        footer.setOrientation(LinearLayout.VERTICAL);
        appearance.bind(theme -> footer.setBackgroundColor(theme.surface));
        footer.setPadding(dp(12), dp(7), dp(12), dp(7));
        column.addView(footer, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        LinearLayout footerRow = new LinearLayout(this);
        footerRow.setOrientation(LinearLayout.HORIZONTAL);
        footerRow.setGravity(Gravity.CENTER_VERTICAL);
        footer.addView(footerRow);

        TextView previous = textButton("上一页", false);
        previous.setOnClickListener(v -> turn(-1));
        footerRow.addView(previous, buttonParams());

        TextView next = textButton("下一页", false);
        next.setOnClickListener(v -> turn(1));
        footerRow.addView(next, buttonParams());

        pageNumber = new EditText(this);
        pageNumber.setInputType(InputType.TYPE_CLASS_NUMBER);
        pageNumber.setGravity(Gravity.CENTER);
        appearance.bind(theme -> pageNumber.setTextColor(theme.text));
        pageNumber.setTextSize(13);
        appearance.bind(theme -> pageNumber.setBackground(theme.interactive(this, theme.app, theme.border)));
        pageNumber.setPadding(dp(4), 0, dp(4), 0);
        pageNumber.setSingleLine(true);
        LinearLayout.LayoutParams pageParams = new LinearLayout.LayoutParams(dp(56), dp(34));
        pageParams.setMargins(dp(2), 0, dp(2), 0);
        footerRow.addView(pageNumber, pageParams);
        pageNumber.setOnEditorActionListener((v, actionId, event) -> {
            jumpToTypedPage();
            return true;
        });

        pageCountText = new TextView(this);
        appearance.bind(theme -> pageCountText.setTextColor(theme.secondary));
        pageCountText.setTextSize(13);
        pageCountText.setText("/ —");
        pageCountText.setPadding(dp(6), 0, dp(4), 0);
        footerRow.addView(pageCountText);

        LinearLayout progressRow = new LinearLayout(this);
        progressRow.setOrientation(LinearLayout.HORIZONTAL);
        progressRow.setGravity(Gravity.CENTER_VERTICAL);
        footer.addView(progressRow);

        progress = new SeekBar(this);
        progress.setMax(1000);
        progress.setProgressDrawable(getResources().getDrawable(R.drawable.seekbar_progress, getTheme()));
        progress.setThumb(getResources().getDrawable(R.drawable.seekbar_thumb, getTheme()));
        progress.setSplitTrack(false);
        appearance.bind(theme -> {
            progress.setProgressTintList(ColorStateList.valueOf(theme.accent));
            progress.setProgressBackgroundTintList(ColorStateList.valueOf(theme.track));
            progress.setThumbTintList(ColorStateList.valueOf(theme.accent));
        });
        progress.setOnSeekBarChangeListener(new SeekBar.OnSeekBarChangeListener() {
            @Override
            public void onProgressChanged(SeekBar seekBar, int value, boolean fromUser) {
                if (!fromUser || changingProgress || book == null) return;
                int page = Math.round(value / 1000f * (book.pageCount() - 1));
                updateStatusLabels(page);
            }

            @Override
            public void onStartTrackingTouch(SeekBar seekBar) {
            }

            @Override
            public void onStopTrackingTouch(SeekBar seekBar) {
                if (book == null) return;
                int page = Math.round(seekBar.getProgress() / 1000f * (book.pageCount() - 1));
                goTo(page);
            }
        });
        LinearLayout.LayoutParams barParams = new LinearLayout.LayoutParams(0, dp(34), 1f);
        barParams.setMargins(dp(2), 0, dp(10), 0);
        progressRow.addView(progress, barParams);

        readingStatus = new TextView(this);
        appearance.bind(theme -> readingStatus.setTextColor(theme.secondary));
        readingStatus.setTextSize(12);
        readingStatus.setText("F11 全屏");
        progressRow.addView(readingStatus);

        // Notice overlay ----------------------------------------------------
        notice = new TextView(this);
        appearance.bind(theme -> notice.setTextColor(theme.accent));
        notice.setTextSize(13);
        appearance.bind(theme -> notice.setBackgroundColor(theme.overlay));
        notice.setPadding(dp(13), dp(8), dp(13), dp(8));
        notice.setVisibility(View.GONE);
        FrameLayout.LayoutParams noticeParams = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        noticeParams.gravity = Gravity.CENTER_HORIZONTAL | Gravity.TOP;
        noticeParams.topMargin = dp(70);
        root.addView(notice, noticeParams);

        setContentView(root);
    }

    private TextView textButton(String label, boolean accent) {
        TextView view = new TextView(this);
        view.setText(label);
        view.setTextSize(13);
        appearance.bind(theme -> theme.styleButton(view, accent));
        view.setTypeface(accent ? Typeface.DEFAULT_BOLD : Typeface.DEFAULT);
        view.setGravity(Gravity.CENTER);
        view.setPadding(dp(11), 0, dp(11), 0);
        view.setClickable(true);
        view.setFocusable(true);
        return view;
    }

    private TextView optionButton(String label, String tooltip, View.OnClickListener click) {
        TextView view = textButton(label, false);
        view.setContentDescription(tooltip);
        view.setOnClickListener(click);
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, dp(34));
        params.setMargins(0, dp(6), dp(6), 0);
        view.setLayoutParams(params);
        return view;
    }

    private LinearLayout.LayoutParams buttonParams() {
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(dp(76), dp(38));
        params.setMargins(0, 0, dp(6), 0);
        return params;
    }

    private int dp(float value) {
        return Math.round(TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, value,
                getResources().getDisplayMetrics()));
    }

    // endregion

    // region opening --------------------------------------------------------

    private void open(String requestedTitle, int requestedPage) {
        if (opening) return;
        opening = true;
        setNotice("正在打开文件…", false);
        final String target = requestedTitle;
        main.post(() -> {
            if (isDestroyed()) return;
            Book previous = book;
            book = null;
            layout.bind(null, 0, false);
            loader.execute(() -> {
                if (previous != null) previous.close();
                Book loaded = null;
                String error = null;
                try {
                    loaded = openBook(target);
                } catch (IOException | RuntimeException e) {
                    error = e.getMessage() == null ? e.toString() : e.getMessage();
                }
                final Book result = loaded;
                final String failure = error;
                main.post(() -> {
                    opening = false;
                    if (isDestroyed()) {
                        if (result != null) result.close();
                        return;
                    }
                    if (result == null) {
                        setNotice("无法打开文件。\n" + (failure == null ? "未知错误" : failure), false);
                        return;
                    }
                    book = result;
                    int saved = state.readPosition(target);
                    int start = requestedPage >= 0 ? requestedPage : saved;
                    position = Math.max(0, Math.min(book.pageCount() - 1, start));
                    List<int[]> spreads = book.makeSpreads(state.dual, state.soloCover);
                    spreadIndex = spreadIndexOf(position, spreads);
                    this.spreads = spreads;
                    ReaderActivity.this.bookTitle.setText(target);
                    this.title = target;
                    shelf.recordOpened(target, book.pageCount());
                    state.save();
                    setNotice(null, false);
                    layout.bind(book, position, state.motion == State.MOTION_SCROLL);
                    refreshLabels();
                    syncOptions();
                    savePositionNow();
                });
            });
        });
    }

    private Book openBook(String title) throws IOException {
        Book opened = Books.open(store, title);
        if (opened == null) throw new IOException("找不到书籍文件：" + title);
        return opened;
    }

    private int spreadIndexOf(int page, List<int[]> spreads) {
        for (int i = 0; i < spreads.size(); i++) {
            for (int p : spreads.get(i)) if (p == page) return i;
        }
        return 0;
    }

    // endregion

    // region ReaderController ----------------------------------------------

    @Override
    public Book book() {
        return book;
    }

    @Override
    public int page() {
        return position;
    }

    @Override
    public boolean rightToLeft() {
        Boolean preferred = book == null ? null : book.preferredRightToLeft();
        return preferred != null ? preferred : state.rightToLeft;
    }

    @Override
    public boolean fitWidth() {
        return state.motion == State.MOTION_SCROLL ? state.continuousFitWidth : state.fitWidth;
    }

    @Override
    public boolean curlMotion() {
        return state.motion == State.MOTION_CURL;
    }

    @Override
    public boolean dual() {
        return state.dual;
    }

    @Override
    public void goTo(int page) {
        if (book == null) return;
        position = Math.max(0, Math.min(book.pageCount() - 1, page));
        spreadIndex = spreadIndexOf(position, currentSpreads());
        if (state.motion == State.MOTION_SCROLL) {
            layout.scrolled().jumpTo(position);
        } else {
            layout.paged().jumpTo(position);
        }
        refreshLabels();
        scheduleSave();
    }

    @Override
    public boolean turn(int direction) {
        if (book == null) return false;
        if (state.motion == State.MOTION_SCROLL) {
            float distance = layout.scrolled().getHeight() * 0.88f;
            layout.scrolled().scrollBy(direction * distance);
            return true;
        }
        return layout.paged().turn(direction);
    }

    @Override
    public void onTapChrome() {
        boolean nowVisible = !chromeVisible;
        chromeVisible = nowVisible;
        header.setVisibility(nowVisible ? View.VISIBLE : View.GONE);
        footer.setVisibility(nowVisible ? View.VISIBLE : View.GONE);
        if (fullscreen != !nowVisible) setFullscreen(nowVisible);
    }

    @Override
    public void onZoomChanged() {
        syncOptions();
    }

    @Override
    public void onPositionChanged(int page) {
        position = page;
        spreadIndex = spreadIndexOf(page, currentSpreads());
        refreshLabels();
        scheduleSave();
    }

    @Override
    public List<int[]> spreads() {
        if (book == null) return java.util.Collections.emptyList();
        if (spreads == null) spreads = book.makeSpreads(state.dual, state.soloCover);
        return spreads;
    }

    private List<int[]> currentSpreads() {
        return spreads();
    }

    // endregion

    // region actions --------------------------------------------------------

    private void toggleDual() {
        if (state.motion == State.MOTION_SCROLL) return;
        state.dual = !state.dual;
        state.save();
        rebuildSpreads();
    }

    private void toggleDirection() {
        Boolean preferred = book == null ? null : book.preferredRightToLeft();
        if (preferred == null) {
            state.rightToLeft = !state.rightToLeft;
            state.save();
        }
        syncOptions();
        if (state.motion == State.MOTION_SCROLL) layout.bind(book, position, true);
        else layout.paged().jumpTo(position);
    }

    private void toggleCover() {
        if (state.motion == State.MOTION_SCROLL) return;
        state.soloCover = !state.soloCover;
        state.save();
        rebuildSpreads();
    }

    private void toggleFit() {
        if (state.motion == State.MOTION_SCROLL) state.continuousFitWidth = !state.continuousFitWidth;
        else state.fitWidth = !state.fitWidth;
        state.save();
        syncOptions();
        if (state.motion == State.MOTION_SCROLL) layout.bind(book, position, true);
        else layout.paged().jumpTo(position);
    }

    private void rebuildSpreads() {
        if (book == null) return;
        spreads = book.makeSpreads(state.dual, state.soloCover);
        spreadIndex = spreadIndexOf(position, spreads);
        position = spreads.get(spreadIndex)[0];
        layout.paged().jumpTo(position);
        refreshLabels();
        syncOptions();
    }

    private void zoom(float factor) {
        if (state.motion == State.MOTION_SCROLL) return;
        if (factor > 1) layout.paged().zoomBy(factor);
        else layout.paged().zoomBy(factor);
        syncOptions();
    }

    private void chooseMotion() {
        final String[] labels = {"左右滑动（滑动翻页）", "仿真卷页", "上下滚动"};
        int checked = state.motion;
        new AlertDialog.Builder(this)
                .setTitle("翻页方式")
                .setSingleChoiceItems(labels, checked, (dialog, which) -> {
                    dialog.dismiss();
                    setMotion(which);
                })
                .setNegativeButton("取消", null)
                .show();
    }

    private void setMotion(int motion) {
        if (motion == state.motion) return;
        boolean reflow = (state.motion == State.MOTION_SCROLL) != (motion == State.MOTION_SCROLL);
        state.motion = motion;
        state.save();
        if (book == null) return;
        if (reflow) {
            spreads = book.makeSpreads(state.dual, state.soloCover);
            spreadIndex = spreadIndexOf(position, spreads);
            layout.bind(book, position, motion == State.MOTION_SCROLL);
            if (motion != State.MOTION_SCROLL) layout.paged().jumpTo(position);
        } else if (motion == State.MOTION_SCROLL) {
            layout.scrolled().jumpTo(position);
        }
        syncOptions();
        refreshLabels();
    }

    private void toggleFullscreen() {
        if (book == null) return;
        chromeVisible = !fullscreen;
        fullscreen = !fullscreen;
        setFullscreen(chromeVisible);
        if (fullscreen) setNotice("点按中间或返回键退出全屏", true, 2500);
        else setNotice(null, false);
    }

    private void setFullscreen(boolean showChrome) {
        if (showChrome) {
            getWindow().clearFlags(WindowManager.LayoutParams.FLAG_FULLSCREEN);
            header.setVisibility(View.VISIBLE);
            footer.setVisibility(View.VISIBLE);
        } else {
            getWindow().addFlags(WindowManager.LayoutParams.FLAG_FULLSCREEN);
            header.setVisibility(View.GONE);
            footer.setVisibility(View.GONE);
        }
        chromeVisible = showChrome;
    }

    private void chooseFile() {
        Intent intent = new Intent(this, ShelfActivity.class);
        intent.setAction(ShelfActivity.ACTION_PICK_AND_OPEN);
        startActivity(intent);
    }

    private void jumpToTypedPage() {
        if (book == null) return;
        String text = pageNumber.getText().toString().trim();
        try {
            int number = Integer.parseInt(text);
            if (number >= 1 && number <= book.pageCount()) goTo(number - 1);
            else setNotice("请输入 1 至 " + book.pageCount() + " 之间的页码。", true, 2500);
        } catch (NumberFormatException e) {
            setNotice("请输入 1 至 " + book.pageCount() + " 之间的页码。", true, 2500);
        }
        hideKeyboard();
        refreshLabels();
    }

    private void hideKeyboard() {
        android.view.inputmethod.InputMethodManager imm =
                (android.view.inputmethod.InputMethodManager) getSystemService(INPUT_METHOD_SERVICE);
        if (imm != null) imm.hideSoftInputFromWindow(pageNumber.getWindowToken(), 0);
    }

    // endregion

    // region state and labels ----------------------------------------------

    private void refreshLabels() {
        if (book == null) {
            bookTitle.setText(title == null ? "KomaSnap" : title);
            return;
        }
        bookTitle.setText(title);
        changingProgress = true;
        progress.setProgress(book.pageCount() <= 1 ? 0
                : Math.round(position / (float) (book.pageCount() - 1) * 1000));
        changingProgress = false;
        pageCountText.setText("/ " + book.pageCount());
        updateStatusLabels(position);
        syncOptions();
    }

    private void updateStatusLabels(int page) {
        if (book == null) return;
        List<int[]> list = currentSpreads();
        int[] spread = new int[]{page};
        for (int[] candidate : list) {
            for (int p : candidate) if (p == page) spread = candidate;
        }
        String status = spread.length == 1
                ? "第 " + (spread[0] + 1) + " 页"
                : "第 " + (spread[0] + 1) + "–" + (spread[spread.length - 1] + 1) + " 页";
        readingStatus.setText(status);
        if (!pageNumber.hasFocus()) pageNumber.setText(String.valueOf(page + 1));
    }

    private void syncOptions() {
        if (options == null) return;
        for (int i = 0; i < options.getChildCount(); i++) {
            View child = options.getChildAt(i);
            if (!(child instanceof TextView)) continue;
            TextView text = (TextView) child;
            String label = text.getText().toString();
            if (label.startsWith("双页") || label.startsWith("单页")) {
                text.setText(state.dual ? "双页" : "单页");
                text.setEnabled(state.motion != State.MOTION_SCROLL);
            } else if (label.startsWith("右到左") || label.startsWith("左到右")) {
                text.setText(rightToLeft() ? "右到左" : "左到右");
            } else if (label.startsWith("封面")) {
                text.setText(state.soloCover ? "封面单页" : "封面配对");
                text.setEnabled(state.motion != State.MOTION_SCROLL && state.dual);
            } else if (label.startsWith("适整页") || label.startsWith("适宽")) {
                String base = fitWidth() ? "适宽" : "适整页";
                float zoom = state.motion == State.MOTION_SCROLL ? 1f : layout.paged().zoom();
                text.setText(Math.abs(zoom - 1f) < 0.01f ? base : base + " · " + Math.round(zoom * 100) + "%");
            } else if (label.startsWith("翻页方式")) {
                String[] names = {"左右滑动", "仿真卷页", "上下滚动"};
                text.setText("翻页方式 · " + names[Math.max(0, Math.min(2, state.motion))]);
            }
        }
    }

    private void scheduleSave() {
        main.removeCallbacks(savePosition);
        main.postDelayed(savePosition, 400);
    }

    private void savePositionNow() {
        main.removeCallbacks(savePosition);
        if (book == null) return;
        state.savePosition(title, position);
    }

    private void setNotice(String message, boolean temporary) {
        setNotice(message, temporary, 4000);
    }

    private void setNotice(String message, boolean temporary, long duration) {
        main.removeCallbacks(hideNotice);
        if (message == null || message.isEmpty()) {
            notice.setVisibility(View.GONE);
            return;
        }
        notice.setText(message);
        notice.setVisibility(View.VISIBLE);
        if (temporary) main.postDelayed(hideNotice, duration);
    }

    @Override
    public void onBackPressed() {
        if (fullscreen) {
            toggleFullscreen();
            return;
        }
        savePositionNow();
        super.onBackPressed();
    }

    @Override
    public boolean onKeyDown(int keyCode, KeyEvent event) {
        if (keyCode == KeyEvent.KEYCODE_T && event.hasNoModifiers() && !pageNumber.hasFocus()) {
            if (event.getRepeatCount() == 0) appearance.cycle();
            return true;
        }
        return super.onKeyDown(keyCode, event);
    }

    @Override
    protected void onResume() {
        super.onResume();
        appearance.sync();
    }

    @Override
    protected void onPause() {
        savePositionNow();
        super.onPause();
    }

    @Override
    protected void onDestroy() {
        if (layout != null) layout.suspend();
        Book closing = book;
        book = null;
        renderer.shutdownNow();
        if (closing != null) loader.execute(closing::close);
        loader.shutdown();
        super.onDestroy();
    }

    @Override
    public void onConfigurationChanged(android.content.res.Configuration newConfig) {
        super.onConfigurationChanged(newConfig);
        // Keep the reading position; the surfaces re-measure themselves.
        if (book != null && state.motion != State.MOTION_SCROLL) {
            layout.paged().jumpTo(position);
        }
        syncOptions();
    }

    // endregion
}
