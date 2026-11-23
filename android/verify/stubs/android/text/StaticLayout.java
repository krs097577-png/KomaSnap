package android.text;
public class StaticLayout extends Layout {
    public interface BuilderLike {
        StaticLayout build();
    }
    public static final class Builder {
        private CharSequence text; private int width, start, end;
        public static Builder obtain(CharSequence t, int s, int e, android.text.TextPaint p, int w) {
            Builder b = new Builder(); b.text = t; b.start = s; b.end = e; b.width = w; return b;
        }
        public Builder setLineSpacing(float add, float mult) { return this; }
        public Builder setIncludePad(boolean pad) { return this; }
        public Builder setAlignment(Alignment a) { return this; }
        public StaticLayout build() { return new StaticLayout(text, width); }
    }
    private final CharSequence text; private final int width;
    private StaticLayout(CharSequence text, int width) { this.text = text; this.width = width; }
    public int getLineCount() {
        String s = text == null ? "" : text.toString();
        if (s.isEmpty()) return 1;
        return Math.max(1, (int) Math.ceil(s.length() * 12.0 / width));
    }
    public int getLineTop(int line) { return line * 38; }
    public int getLineBottom(int line) { return (line + 1) * 38; }
    public void draw(android.graphics.Canvas c) { }
}