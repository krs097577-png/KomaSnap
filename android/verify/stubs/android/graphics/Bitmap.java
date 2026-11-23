package android.graphics;
public class Bitmap {
    public enum Config { ARGB_8888, RGB_565 }
    public static Bitmap createBitmap(int w, int h, Config c) { throw new stub.StubCall("Bitmap.createBitmap"); }
    public static Bitmap createScaledBitmap(Bitmap b, int w, int h, boolean f) { throw new stub.StubCall("Bitmap.createScaledBitmap"); }
    public int getWidth() { throw new stub.StubCall("Bitmap.getWidth"); }
    public int getHeight() { throw new stub.StubCall("Bitmap.getHeight"); }
    public void recycle() { }
}