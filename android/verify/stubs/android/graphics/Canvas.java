package android.graphics;
public class Canvas {
    public Canvas(Bitmap b) { }
    public void drawColor(int c) { }
    public void drawRect(Rect r, Paint p) { }
    public void drawBitmap(Bitmap b, Rect s, Rect d, Paint p) { }
    public int save() { return 0; }
    public void restore() { }
    public void restoreToCount(int c) { }
    public void translate(float x, float y) { }
    public void scale(float x, float y) { }
    public void clipRect(int l, int t, int r, int b) { }
    public void clipRect(float l, float t, float r, float b) { }
}
