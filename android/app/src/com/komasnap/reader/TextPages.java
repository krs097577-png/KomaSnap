package com.komasnap.reader;

import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.text.Layout;
import android.text.StaticLayout;
import android.text.TextPaint;

import java.util.ArrayList;
import java.util.List;

/**
 * Fixed-size pagination of EPUB text, ported from the WPF {@code FlowDocument}
 * paginator in the desktop reader: 760x1080 logical pages, 58x48 padding,
 * 23pt body text on a 38pt line grid and 34pt headings on 49pt.
 *
 * <p>Text wraps with {@link StaticLayout} at the logical width, so a page always
 * holds the same paragraphs no matter which screen width it is later rendered
 * at. Rendering scales the logical page up to the requested pixel width.</p>
 */
public final class TextPages {

    public static final int PAGE_WIDTH = 760;
    public static final int PAGE_HEIGHT = 1080;
    public static final int PADDING_X = 58;
    public static final int PADDING_Y = 48;
    public static final float BODY_SIZE = 23f;
    public static final float BODY_LINE = 38f;
    public static final float HEADING_SIZE = 34f;
    public static final float HEADING_LINE = 49f;
    public static final float PARAGRAPH_GAP = 14f;

    public static final class Block {
        public final String text;
        public final boolean heading;

        public Block(String text, boolean heading) {
            this.text = text;
            this.heading = heading;
        }
    }

    private static final class Line {
        final int paragraph;
        final boolean heading;

        Line(int paragraph, boolean heading) {
            this.paragraph = paragraph;
            this.heading = heading;
        }
    }

    private static final class PageRange {
        int start;
        int end;
    }

    private final List<Block> blocks;
    private final List<Line> lines;
    private final List<PageRange> pages;

    private TextPages(List<Block> blocks, List<Line> lines, List<PageRange> pages) {
        this.blocks = blocks;
        this.lines = lines;
        this.pages = pages;
    }

    public int pageCount() {
        return pages.size();
    }

    public static TextPages paginate(List<Block> source) {
        List<Block> blocks = new ArrayList<>(source);
        TextPaint body = bodyPaint();
        TextPaint heading = headingPaint();
        List<Line> lines = new ArrayList<>();
        for (int p = 0; p < blocks.size(); p++) {
            Block block = blocks.get(p);
            StaticLayout layout = layout(block.text, block.heading ? heading : body,
                    block.heading ? HEADING_LINE : BODY_LINE);
            int count = Math.max(1, layout.getLineCount());
            for (int i = 0; i < count; i++) lines.add(new Line(p, block.heading));
        }
        List<PageRange> pages = new ArrayList<>();
        PageRange current = new PageRange();
        current.start = 0;
        float y = 0;
        float limit = PAGE_HEIGHT - PADDING_Y * 2;
        for (int i = 0; i < lines.size(); i++) {
            Line line = lines.get(i);
            float lineHeight = line.heading ? HEADING_LINE : BODY_LINE;
            boolean last = i + 1 >= lines.size() || lines.get(i + 1).paragraph != line.paragraph;
            float gap = last ? PARAGRAPH_GAP : 0;
            if (y + lineHeight > limit && i > current.start) {
                current.end = i;
                pages.add(current);
                current = new PageRange();
                current.start = i;
                y = 0;
            }
            y += lineHeight + gap;
        }
        current.end = lines.size();
        pages.add(current);
        return new TextPages(blocks, lines, pages);
    }

    private static TextPaint bodyPaint() {
        TextPaint p = new TextPaint(Paint.ANTI_ALIAS_FLAG | Paint.SUBPIXEL_TEXT_FLAG);
        p.setColor(Color.BLACK);
        p.setTextSize(BODY_SIZE);
        p.setTypeface(android.graphics.Typeface.create("serif", android.graphics.Typeface.NORMAL));
        return p;
    }

    private static TextPaint headingPaint() {
        TextPaint p = bodyPaint();
        p.setTextSize(HEADING_SIZE);
        p.setTypeface(android.graphics.Typeface.create("serif", android.graphics.Typeface.BOLD));
        return p;
    }

    private static StaticLayout layout(String text, TextPaint paint, float lineHeight) {
        Paint.FontMetrics fm = paint.getFontMetrics();
        float natural = fm.descent - fm.ascent + fm.leading;
        float extra = Math.max(0f, lineHeight - natural);
        return StaticLayout.Builder.obtain(text, 0, text.length(), paint, PAGE_WIDTH - PADDING_X * 2)
                .setLineSpacing(extra, 1f)
                .setIncludePad(false)
                .setAlignment(Layout.Alignment.ALIGN_NORMAL)
                .build();
    }

    public Bitmap render(int page, int width, int logicalWidth, int logicalHeight) {
        int w = Math.max(1, width);
        int h = Math.max(1, Math.round(w * (logicalHeight / (float) logicalWidth)));
        Bitmap bitmap = Bitmap.createBitmap(w, h, Bitmap.Config.ARGB_8888);
        Canvas canvas = new Canvas(bitmap);
        canvas.drawColor(Color.rgb(255, 250, 240)); // FloralWhite paper
        float scale = w / (float) logicalWidth;
        canvas.save();
        canvas.scale(scale, scale);
        canvas.clipRect(0, 0, logicalWidth, logicalHeight);
        TextPaint body = bodyPaint();
        TextPaint heading = headingPaint();
        PageRange range = pages.get(page);

        float y = PADDING_Y;
        int i = range.start;
        while (i < range.end) {
            int paragraph = lines.get(i).paragraph;
            boolean isHeading = lines.get(i).heading;
            float lineHeight = isHeading ? HEADING_LINE : BODY_LINE;
            Block block = blocks.get(paragraph);
            StaticLayout layout = layout(block.text, isHeading ? heading : body, lineHeight);

            // How many lines of this paragraph came before this page.
            int before = 0;
            for (int k = i - 1; k >= 0 && lines.get(k).paragraph == paragraph; k--) before++;
            int here = 0;
            while (i + here < range.end && lines.get(i + here).paragraph == paragraph) here++;

            if (before == 0) {
                // Nothing of this paragraph is above us: draw from its first line.
                canvas.save();
                canvas.translate(PADDING_X, y);
                layout.draw(canvas);
                canvas.restore();
                y += layout.getLineCount() * lineHeight;
            } else if (before < layout.getLineCount()) {
                // Continuation: draw only the lines that belong to this page.
                int top = layout.getLineTop(before);
                int last = Math.min(layout.getLineCount() - 1, before + here - 1);
                int bottom = layout.getLineBottom(last);
                canvas.save();
                canvas.clipRect(PADDING_X - 2, y, PAGE_WIDTH - PADDING_X + 2, y + (bottom - top));
                canvas.translate(PADDING_X, y - top);
                layout.draw(canvas);
                canvas.restore();
                y += (bottom - top);
            }
            boolean paragraphEnded = i + here >= lines.size() || lines.get(i + here).paragraph != paragraph;
            if (paragraphEnded) y += PARAGRAPH_GAP;
            i += Math.max(1, here);
        }
        canvas.restore();
        return bitmap;
    }
}
