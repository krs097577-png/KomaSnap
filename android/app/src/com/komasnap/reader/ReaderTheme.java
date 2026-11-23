package com.komasnap.reader;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Context;
import android.content.res.ColorStateList;
import android.graphics.drawable.Drawable;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.RippleDrawable;
import android.graphics.drawable.StateListDrawable;
import android.os.Build;
import android.view.Gravity;
import android.view.View;
import android.view.Window;
import android.widget.LinearLayout;
import android.widget.RadioButton;
import android.widget.TextView;

import java.util.ArrayList;
import java.util.List;
import java.util.function.Consumer;

enum ReaderTheme {
    LIGHT("白色", 0xFFF7F8FA, 0xFFFFFFFF, 0xFFF0F2F5, 0xFF292724, 0xFF5B534B,
            0xFF855200, 0xFFFFF7E8, 0xFFCDC3B5, 0xFF958777, 0xF5FFFFFF,
            0xFFE6E9EF, 0xFFE8E1D6, 0xFFD8CDBE, 0xFFFFFFFF, 0, 0xFFF7F8FA, 0xFF99A1B3),
    EYE_CARE("护眼", 0xFFE9DEC4, 0xFFF2E9D4, 0xFFDFD2B5, 0xFF382E24, 0xFF605345,
            0xFF765124, 0xFFFAF0DA, 0xFFC9B996, 0xFFA38A61, 0xF5F2E9D4,
            0xFFD5C6A5, 0xFFE6D8B9, 0xFFD9C8A5, 0xFFE6D5AC, 0x22E89C25, 0xFFE6D5AC, 0xFF8C7648),
    DARK("夜间", 0xFF171B21, 0xFF202630, 0xFF2A323E, 0xFFEEF1F5, 0xFFB8C1CD,
            0xFFF6CD81, 0xFF262014, 0xFF414C5A, 0xFF657588, 0xF2232933,
            0xFF151A21, 0xFF24303B, 0xFF495665, 0xFFFFFFFF, 0, 0xFF1E2025, 0xFF5A606E);

    final String label;
    final int app, surface, elevated, text, secondary, accent, onAccent, border, focus, overlay;
    final int canvas, cover, track, paper, tint, swatch, swatchBorder;

    ReaderTheme(String label, int app, int surface, int elevated, int text, int secondary,
                int accent, int onAccent, int border, int focus, int overlay, int canvas,
                int cover, int track, int paper, int tint, int swatch, int swatchBorder) {
        this.label = label;
        this.app = app;
        this.surface = surface;
        this.elevated = elevated;
        this.text = text;
        this.secondary = secondary;
        this.accent = accent;
        this.onAccent = onAccent;
        this.border = border;
        this.focus = focus;
        this.overlay = overlay;
        this.canvas = canvas;
        this.cover = cover;
        this.track = track;
        this.paper = paper;
        this.tint = tint;
        this.swatch = swatch;
        this.swatchBorder = swatchBorder;
    }

    private static int dp(Context context, float value) {
        return Math.round(context.getResources().getDisplayMetrics().density * value);
    }

    Drawable frame(Context context, int fill, int stroke, int radius) {
        GradientDrawable shape = new GradientDrawable();
        shape.setColor(fill);
        shape.setCornerRadius(dp(context, radius));
        shape.setStroke(dp(context, 1), stroke);
        return shape;
    }

    Drawable interactive(Context context, int fill, int stroke) {
        StateListDrawable states = new StateListDrawable();
        states.addState(new int[]{android.R.attr.state_focused}, frame(context, fill, focus, 8));
        states.addState(new int[]{android.R.attr.state_hovered}, frame(context, fill, focus, 8));
        states.addState(new int[]{}, frame(context, fill, stroke, 8));
        return new RippleDrawable(ColorStateList.valueOf((text & 0xFFFFFF) | 0x24000000), states, null);
    }

    void styleButton(TextView view, boolean primary) {
        view.setTextColor(new ColorStateList(new int[][]{{-android.R.attr.state_enabled}, {}},
                new int[]{secondary, primary ? onAccent : text}));
        view.setBackground(interactive(view.getContext(), primary ? accent : elevated, primary ? accent : border));
    }

    private Drawable preview(Context context) {
        GradientDrawable dot = new GradientDrawable();
        dot.setShape(GradientDrawable.OVAL);
        dot.setColor(swatch);
        dot.setStroke(dp(context, 1), swatchBorder);
        int size = dp(context, 13);
        dot.setBounds(0, 0, size, size);
        return dot;
    }

    /** 绑定视图并原地更新，切换主题时保留阅读位置和位图缓存。 */
    static final class Appearance {
        private final Activity activity;
        private final List<Consumer<ReaderTheme>> bindings = new ArrayList<>();
        private ReaderTheme current;

        Appearance(Activity activity) {
            this.activity = activity;
            current = read();
            styleWindow();
        }

        ReaderTheme current() {
            return current;
        }

        void bind(Consumer<ReaderTheme> binding) {
            bindings.add(binding);
            binding.accept(current);
        }

        void bindMenu(TextView button) {
            bind(theme -> {
                theme.styleButton(button, false);
                button.setCompoundDrawablesRelative(theme.preview(activity), null, null, null);
                button.setCompoundDrawablePadding(dp(activity, 6));
                button.setContentDescription("主题，当前" + theme.label);
            });
            button.setText("主题");
            button.setOnClickListener(view -> showMenu());
        }

        private ReaderTheme read() {
            String saved = activity.getSharedPreferences("appearance", Context.MODE_PRIVATE)
                    .getString("theme", DARK.name());
            try {
                return ReaderTheme.valueOf(saved);
            } catch (IllegalArgumentException ignored) {
                return DARK;
            }
        }

        void sync() {
            apply(read());
        }

        void cycle() {
            ReaderTheme[] themes = values();
            select(themes[(current.ordinal() + 1) % themes.length]);
        }

        private void select(ReaderTheme theme) {
            activity.getSharedPreferences("appearance", Context.MODE_PRIVATE)
                    .edit().putString("theme", theme.name()).apply();
            apply(theme);
        }

        private void apply(ReaderTheme theme) {
            if (theme == current) return;
            current = theme;
            for (Consumer<ReaderTheme> binding : bindings) binding.accept(theme);
            styleWindow();
        }

        private void styleWindow() {
            Window window = activity.getWindow();
            window.setStatusBarColor(current.surface);
            window.setNavigationBarColor(Build.VERSION.SDK_INT < 26 ? DARK.surface : current.surface);
            int lightIcons = View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR;
            if (Build.VERSION.SDK_INT >= 26) lightIcons |= View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR;
            View decor = window.getDecorView();
            int flags = decor.getSystemUiVisibility() & ~lightIcons;
            decor.setSystemUiVisibility(current == DARK ? flags : flags | lightIcons);
        }

        void showMenu() {
            LinearLayout panel = new LinearLayout(activity);
            panel.setOrientation(LinearLayout.VERTICAL);
            panel.setPadding(dp(activity, 16), dp(activity, 12), dp(activity, 16), dp(activity, 12));
            panel.setBackground(current.frame(activity, current.elevated, current.border, 12));
            TextView title = new TextView(activity);
            title.setText("阅读主题");
            title.setTextSize(18);
            title.setTextColor(current.text);
            title.setPadding(dp(activity, 8), dp(activity, 6), 0, dp(activity, 8));
            panel.addView(title);
            AlertDialog dialog = new AlertDialog.Builder(activity).setView(panel).create();
            ColorStateList checkColors = new ColorStateList(new int[][]{{android.R.attr.state_checked}, {}},
                    new int[]{current.accent, current.secondary});
            for (ReaderTheme option : values()) {
                RadioButton row = new RadioButton(activity);
                row.setText(option.label);
                row.setChecked(option == current);
                row.setTextColor(current.text);
                row.setGravity(Gravity.CENTER_VERTICAL);
                row.setMinHeight(dp(activity, 48));
                row.setButtonTintList(checkColors);
                row.setCompoundDrawablesRelative(null, null, option.preview(activity), null);
                row.setBackground(current.interactive(activity, current.elevated, current.elevated));
                row.setPadding(dp(activity, 8), 0, dp(activity, 12), 0);
                row.setOnClickListener(view -> {
                    select(option);
                    dialog.dismiss();
                });
                panel.addView(row);
            }
            dialog.show();
            if (dialog.getWindow() != null) dialog.getWindow().setBackgroundDrawableResource(android.R.color.transparent);
        }
    }
}
