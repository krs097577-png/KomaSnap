package android.graphics;
public class Paint {
    public static final int ANTI_ALIAS_FLAG = 1;
    public static final int SUBPIXEL_TEXT_FLAG = 128;
    public enum Style { FILL, STROKE }
    public enum Align { LEFT, CENTER, RIGHT }
    private float textSize = 12f;
    public Paint() { }
    public Paint(int flags) { }
    public void setColor(int c) { }
    public void setStyle(Style s) { }
    public void setStrokeWidth(float w) { }
    public void setTextSize(float size) { textSize = size; }
    public void setTextAlign(Align a) { }
    public Typeface setTypeface(Typeface t) { return t; }
    public Typeface getTypeface() { return null; }
    public float getTextSize() { return textSize; }
    public FontMetrics getFontMetrics() {
        FontMetrics fm = new FontMetrics();
        fm.ascent = -textSize * 0.93f;
        fm.descent = textSize * 0.24f;
        fm.leading = 0f;
        return fm;
    }
    public static class FontMetrics { public float ascent, descent, leading; }
}