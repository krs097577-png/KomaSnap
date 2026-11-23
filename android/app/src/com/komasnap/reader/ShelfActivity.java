package com.komasnap.reader;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.ContentResolver;
import android.content.Intent;
import android.database.Cursor;
import android.graphics.Bitmap;
import android.graphics.Typeface;
import android.net.Uri;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.provider.OpenableColumns;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.widget.FrameLayout;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import java.io.File;
import java.io.IOException;
import java.io.InputStream;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/** Bookshelf: import files, show covers and progress, open the reading screen. */
public class ShelfActivity extends Activity {

    public static final String ACTION_PICK_AND_OPEN = "com.komasnap.reader.PICK_AND_OPEN";
    private static final int REQUEST_PICK = 1001;
    private static final String[] MIME_TYPES = {
            "application/pdf",
            "application/epub+zip",
            "application/vnd.comicbook+zip",
            "application/x-cbz",
            "application/zip",
            "application/octet-stream",
    };

    private final Handler main = new Handler(Looper.getMainLooper());
    private final ExecutorService worker = Executors.newFixedThreadPool(2);

    private Store store;
    private State state;
    private ReaderTheme.Appearance appearance;
    private Shelf shelf;
    private LinearLayout grid;
    private View empty;
    private TextView stats;
    private TextView notice;
    private ScrollView scroll;
    private int gridWidth;
    private List<Shelf.Entry> entries = new ArrayList<>();

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        store = new Store(getFilesDir());
        state = new State(store.state());
        shelf = new Shelf(store);
        appearance = new ReaderTheme.Appearance(this);
        buildUi();
        handleIntent(getIntent());
    }

    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        handleIntent(intent);
    }

    @Override
    protected void onResume() {
        super.onResume();
        appearance.sync();
        reload(false);
    }

    // region UI -------------------------------------------------------------

    private void buildUi() {
        FrameLayout root = new FrameLayout(this);
        appearance.bind(theme -> root.setBackgroundColor(theme.app));

        LinearLayout column = new LinearLayout(this);
        column.setOrientation(LinearLayout.VERTICAL);
        root.addView(column, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));

        LinearLayout header = new LinearLayout(this);
        header.setOrientation(LinearLayout.HORIZONTAL);
        header.setGravity(Gravity.CENTER_VERTICAL);
        appearance.bind(theme -> header.setBackgroundColor(theme.surface));
        header.setPadding(dp(20), dp(14), dp(14), dp(14));
        column.addView(header);

        TextView title = new TextView(this);
        title.setText("书架");
        title.setTextSize(24);
        appearance.bind(theme -> title.setTextColor(theme.text));
        title.setTypeface(Typeface.DEFAULT_BOLD);
        header.addView(title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));

        stats = new TextView(this);
        appearance.bind(theme -> stats.setTextColor(theme.secondary));
        stats.setTextSize(12);
        stats.setPadding(0, 0, dp(12), 0);
        header.addView(stats);

        TextView add = new TextView(this);
        add.setText("+ 添加书籍");
        TextView themeButton = new TextView(this);
        themeButton.setTextSize(14);
        themeButton.setGravity(Gravity.CENTER);
        themeButton.setFocusable(true);
        appearance.bindMenu(themeButton);
        themeButton.setPadding(dp(12), dp(9), dp(12), dp(9));
        LinearLayout.LayoutParams themeParams = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        themeParams.setMarginEnd(dp(8));
        header.addView(themeButton, themeParams);
        add.setTextSize(14);
        add.setTypeface(Typeface.DEFAULT_BOLD);
        add.setGravity(Gravity.CENTER);
        appearance.bind(theme -> theme.styleButton(add, false));
        add.setPadding(dp(16), dp(9), dp(16), dp(9));
        add.setOnClickListener(v -> pickBooks());
        header.addView(add);

        scroll = new ScrollView(this);
        appearance.bind(theme -> scroll.setBackgroundColor(theme.app));
        column.addView(scroll, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f));

        grid = new LinearLayout(this);
        grid.setOrientation(LinearLayout.VERTICAL);
        grid.setPadding(dp(20), dp(18), dp(20), dp(24));
        scroll.addView(grid);
        grid.addOnLayoutChangeListener((view, left, top, right, bottom, oldLeft, oldTop, oldRight, oldBottom) -> {
            int width = right - left;
            if (width > 0 && width != gridWidth) {
                gridWidth = width;
                grid.post(this::layoutCards);
            }
        });

        // Empty state overlaid on the scroll view.
        LinearLayout emptyState = new LinearLayout(this);
        emptyState.setOrientation(LinearLayout.VERTICAL);
        emptyState.setGravity(Gravity.CENTER);
        emptyState.setVisibility(View.GONE);
        FrameLayout.LayoutParams emptyParams = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT);
        emptyParams.topMargin = dp(80);
        root.addView(emptyState, emptyParams);
        empty = emptyState;

        ImageView icon = new ImageView(this);
        icon.setImageResource(R.mipmap.ic_launcher);
        icon.setScaleType(ImageView.ScaleType.FIT_CENTER);
        LinearLayout.LayoutParams iconParams = new LinearLayout.LayoutParams(dp(96), dp(96));
        iconParams.bottomMargin = dp(16);
        emptyState.addView(icon, iconParams);

        TextView emptyTitle = new TextView(this);
        emptyTitle.setText("把书放到书架上");
        emptyTitle.setTextSize(24);
        emptyTitle.setTypeface(Typeface.DEFAULT_BOLD);
        appearance.bind(theme -> emptyTitle.setTextColor(theme.text));
        emptyTitle.setGravity(Gravity.CENTER);
        emptyState.addView(emptyTitle);

        TextView emptyHint = new TextView(this);
        emptyHint.setText("支持 PDF、EPUB、CBZ 或图片 ZIP\n点右上角「添加书籍」选择文件");
        emptyHint.setTextSize(14);
        appearance.bind(theme -> emptyHint.setTextColor(theme.secondary));
        emptyHint.setGravity(Gravity.CENTER);
        emptyHint.setPadding(0, dp(10), 0, 0);
        emptyState.addView(emptyHint);

        TextView emptyButton = new TextView(this);
        emptyButton.setText("添加书籍");
        emptyButton.setTextSize(15);
        emptyButton.setTypeface(Typeface.DEFAULT_BOLD);
        emptyButton.setGravity(Gravity.CENTER);
        appearance.bind(theme -> theme.styleButton(emptyButton, true));
        emptyButton.setPadding(dp(22), dp(11), dp(22), dp(11));
        LinearLayout.LayoutParams buttonParams = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        buttonParams.topMargin = dp(20);
        emptyButton.setOnClickListener(v -> pickBooks());
        emptyState.addView(emptyButton, buttonParams);

        notice = new TextView(this);
        appearance.bind(theme -> notice.setTextColor(theme.accent));
        notice.setTextSize(13);
        appearance.bind(theme -> notice.setBackgroundColor(theme.elevated));
        notice.setPadding(dp(20), dp(9), dp(20), dp(9));
        notice.setVisibility(View.GONE);
        column.addView(notice);
        appearance.bind(theme -> layoutCards());

        setContentView(root);
    }

    private int dp(float value) {
        return Math.round(TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, value,
                getResources().getDisplayMetrics()));
    }

    // endregion

    // region import and listing --------------------------------------------

    private void pickBooks() {
        Intent intent = new Intent(Intent.ACTION_OPEN_DOCUMENT);
        intent.addCategory(Intent.CATEGORY_OPENABLE);
        intent.setType("*/*");
        intent.putExtra(Intent.EXTRA_MIME_TYPES, MIME_TYPES);
        intent.putExtra(Intent.EXTRA_ALLOW_MULTIPLE, true);
        try {
            startActivityForResult(intent, REQUEST_PICK);
        } catch (android.content.ActivityNotFoundException e) {
            setNotice("此设备没有可用的文件选择器。", false);
        }
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        if (requestCode != REQUEST_PICK || resultCode != RESULT_OK || data == null) return;
        List<Uri> uris = new ArrayList<>();
        if (data.getClipData() != null) {
            for (int i = 0; i < data.getClipData().getItemCount(); i++) {
                uris.add(data.getClipData().getItemAt(i).getUri());
            }
        } else if (data.getData() != null) {
            uris.add(data.getData());
        }
        if (uris.isEmpty()) return;
        importUris(uris);
    }

    private void handleIntent(Intent intent) {
        if (intent == null) return;
        if (Intent.ACTION_VIEW.equals(intent.getAction()) && intent.getData() != null) {
            List<Uri> uris = new ArrayList<>();
            uris.add(intent.getData());
            importUris(uris);
        } else if (ACTION_PICK_AND_OPEN.equals(intent.getAction())) {
            pickBooks();
        }
    }

    private void importUris(List<Uri> uris) {
        setNotice("正在导入…", false);
        final ContentResolver resolver = getContentResolver();
        worker.execute(() -> {
            int ok = 0;
            int skipped = 0;
            String error = null;
            for (Uri uri : uris) {
                String name = displayName(resolver, uri);
                if (!Store.supports(name)) {
                    skipped++;
                    continue;
                }
                try (InputStream in = resolver.openInputStream(uri)) {
                    if (in == null) {
                        skipped++;
                        continue;
                    }
                    File stored = store.importBook(name, in);
                    ok++;
                } catch (IOException | SecurityException e) {
                    error = e.getMessage();
                }
            }
            final int imported = ok;
            final int ignored = skipped;
            final String failure = error;
            main.post(() -> {
                reload(true);
                if (failure != null) setNotice("导入失败：" + failure, false);
                else if (imported == 0) setNotice("没有可导入的文件（支持 PDF、EPUB、CBZ、ZIP）。", false);
                else setNotice("已导入 " + imported + " 本书" + (ignored > 0 ? "，跳过 " + ignored + " 个文件" : "") + "。", true);
            });
        });
    }

    private static String displayName(ContentResolver resolver, Uri uri) {
        String name = null;
        try (Cursor cursor = resolver.query(uri, null, null, null, null)) {
            if (cursor != null && cursor.moveToFirst()) {
                int index = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME);
                if (index >= 0) name = cursor.getString(index);
            }
        } catch (RuntimeException ignored) {
        }
        if (name != null && !name.isEmpty()) return name;
        String path = uri.getLastPathSegment();
        if (path == null) return "book";
        int slash = path.lastIndexOf('/');
        return slash >= 0 ? path.substring(slash + 1) : path;
    }

    private void reload(boolean generateCovers) {
        entries = shelf.read();
        boolean isEmpty = entries.isEmpty();
        empty.setVisibility(isEmpty ? View.VISIBLE : View.GONE);
        scroll.setVisibility(isEmpty ? View.GONE : View.VISIBLE);
        stats.setText(isEmpty ? "" : entries.size() + " 本");
        layoutCards();
        for (Shelf.Entry entry : entries) {
            if (generateCovers) ensureCover(entry);
            else ensureCoverIfMissing(entry);
        }
    }

    private void layoutCards() {
        grid.removeAllViews();
        int available = (gridWidth > 0 ? gridWidth : getResources().getDisplayMetrics().widthPixels)
                - grid.getPaddingLeft() - grid.getPaddingRight();
        int gap = dp(12);
        int columns = Math.max(1, (available + gap) / (dp(132) + gap));
        int cardWidth = Math.max(1, Math.min(dp(172), (available - (columns - 1) * gap) / columns));
        LinearLayout row = null;
        for (int i = 0; i < entries.size(); i++) {
            if (i % columns == 0) {
                row = new LinearLayout(this);
                row.setOrientation(LinearLayout.HORIZONTAL);
                LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(
                        ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
                params.bottomMargin = dp(14);
                row.setLayoutParams(params);
                grid.addView(row);
            }
            Shelf.Entry entry = entries.get(i);
            View card = shelfCard(entry, cardWidth);
            LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(
                    cardWidth, ViewGroup.LayoutParams.WRAP_CONTENT);
            if (i % columns != 0) params.setMarginStart(gap);
            row.addView(card, params);
        }
    }

    private View shelfCard(Shelf.Entry entry, int cardWidth) {
        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setBackground(appearance.current().interactive(this, appearance.current().surface, appearance.current().border));
        card.setPadding(dp(8), dp(8), dp(8), dp(10));
        card.setClickable(true);
        card.setFocusable(true);

        FrameLayout coverBox = new FrameLayout(this);
        coverBox.setBackgroundColor(appearance.current().cover);
        LinearLayout.LayoutParams coverParams = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, Math.round(Math.max(1, cardWidth - dp(16)) * 1.45f));
        card.addView(coverBox, coverParams);

        File cover = shelf.cover(entry.displayTitle());
        ImageView image = new ImageView(this);
        image.setScaleType(ImageView.ScaleType.FIT_CENTER);
        coverBox.addView(image, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        if (cover != null) {
            Bitmap bitmap = decodeFile(cover, dp(240), dp(360));
            if (bitmap != null) image.setImageBitmap(bitmap);
            else placeholder(image, entry);
        } else {
            placeholder(image, entry);
        }

        TextView title = new TextView(this);
        title.setText(entry.displayTitle());
        title.setTextSize(14);
        title.setTextColor(appearance.current().text);
        title.setTypeface(Typeface.DEFAULT_BOLD);
        title.setSingleLine(true);
        title.setEllipsize(android.text.TextUtils.TruncateAt.END);
        title.setPadding(0, dp(9), 0, dp(3));
        card.addView(title);

        int saved = state.readPosition(entry.displayTitle());
        int page = Math.max(0, saved);
        String progressText = entry.pages > 0
                ? "第 " + (Math.min(page, entry.pages - 1) + 1) + " 页 / " + entry.pages
                : page > 0 ? "第 " + (page + 1) + " 页" : entry.lastOpened > 0 ? "已打开" : "未读";
        TextView progressLabel = new TextView(this);
        progressLabel.setText(progressText);
        progressLabel.setTextSize(12);
        progressLabel.setTextColor(appearance.current().secondary);
        card.addView(progressLabel);

        card.setContentDescription(entry.displayTitle() + "，" + progressText);
        card.setOnClickListener(v -> openEntry(entry));
        card.setOnLongClickListener(v -> {
            showEntryMenu(entry);
            return true;
        });
        return card;
    }

    private void placeholder(ImageView image, Shelf.Entry entry) {
        image.setImageDrawable(null);
        image.setBackgroundColor(appearance.current().cover);
        TextView label = new TextView(this);
        label.setText(NaturalSort.extension(entry.displayTitle()).toUpperCase(java.util.Locale.ROOT));
        label.setTextSize(22);
        label.setTypeface(Typeface.DEFAULT_BOLD);
        label.setTextColor(appearance.current().accent);
        label.setGravity(Gravity.CENTER);
        if (image.getParent() instanceof ViewGroup) {
            ((ViewGroup) image.getParent()).addView(label, new FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        }
    }

    private void showEntryMenu(Shelf.Entry entry) {
        new AlertDialog.Builder(this)
                .setTitle(entry.displayTitle())
                .setItems(new String[]{"开始阅读", "移出书架并删除文件", "取消"}, (dialog, which) -> {
                    if (which == 0) openEntry(entry);
                    else if (which == 1) confirmDelete(entry);
                })
                .show();
    }

    private void confirmDelete(Shelf.Entry entry) {
        new AlertDialog.Builder(this)
                .setTitle("移出书架")
                .setMessage("将从设备中删除《" + entry.displayTitle() + "》的副本，是否继续？")
                .setPositiveButton("删除", (dialog, which) -> {
                    shelf.delete(entry.displayTitle());
                    state.savePosition(entry.displayTitle(), 0);
                    reload(false);
                    setNotice("已移出书架。", true);
                })
                .setNegativeButton("取消", null)
                .show();
    }

    private void openEntry(Shelf.Entry entry) {
        Intent intent = new Intent(this, ReaderActivity.class);
        intent.putExtra(ReaderActivity.EXTRA_TITLE, entry.displayTitle());
        startActivity(intent);
    }

    // endregion

    // region covers ---------------------------------------------------------

    private void ensureCoverIfMissing(Shelf.Entry entry) {
        if (shelf.cover(entry.displayTitle()) != null) return;
        ensureCover(entry);
    }

    private void ensureCover(Shelf.Entry entry) {
        if (shelf.cover(entry.displayTitle()) != null) return;
        final String title = entry.displayTitle();
        worker.execute(() -> {
            Bitmap bitmap = null;
            Book book = null;
            try {
                File file = shelf.fileFor(title);
                if (file == null) return;
                book = Books.open(file, title);
                if (book.pageCount() == 0) return;
                Bitmap rendered = book.render(0, 480);
                if (rendered == null) return;
                bitmap = Bitmap.createScaledBitmap(rendered, 240,
                        Math.max(1, Math.round(rendered.getHeight() * (240f / rendered.getWidth()))), true);
                shelf.saveCover(title, bitmap);
            } catch (IOException | RuntimeException ignored) {
            } finally {
                if (book != null) book.close();
            }
            final Bitmap cover = bitmap;
            if (cover == null) return;
            main.post(this::reloadCovers);
        });
    }

    private void reloadCovers() {
        // Cheap refresh: only swap cover images in place.
        reload(false);
    }

    private Bitmap decodeFile(File file, int width, int height) {
        android.graphics.BitmapFactory.Options bounds = new android.graphics.BitmapFactory.Options();
        bounds.inJustDecodeBounds = true;
        android.graphics.BitmapFactory.decodeFile(file.getAbsolutePath(), bounds);
        if (bounds.outWidth <= 0) return null;
        int sample = 1;
        while (bounds.outWidth / (sample * 2) > width) sample *= 2;
        android.graphics.BitmapFactory.Options options = new android.graphics.BitmapFactory.Options();
        options.inSampleSize = sample;
        return android.graphics.BitmapFactory.decodeFile(file.getAbsolutePath(), options);
    }

    // endregion

    // region notices --------------------------------------------------------

    private void setNotice(String message, boolean temporary) {
        if (message == null || message.isEmpty()) {
            notice.setVisibility(View.GONE);
            return;
        }
        notice.setText(message);
        notice.setVisibility(View.VISIBLE);
        if (temporary) {
            main.postDelayed(() -> notice.setVisibility(View.GONE), 4000);
        }
    }

    @Override
    protected void onDestroy() {
        worker.shutdownNow();
        super.onDestroy();
    }

    // endregion
}
