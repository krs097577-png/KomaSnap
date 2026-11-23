package android.graphics;
public class BitmapFactory {
    public static class Options { public boolean inJustDecodeBounds; public int inSampleSize; public int outWidth; public int outHeight; public Bitmap.Config inPreferredConfig; }
    public static Bitmap decodeByteArray(byte[] data, int offset, int length, Options o) {
        int[] size = pngSize(data);
        if (size == null) return null;
        if (o != null && o.inJustDecodeBounds) { o.outWidth = size[0]; o.outHeight = size[1]; return null; }
        throw new stub.StubCall("BitmapFactory.decodeByteArray (pixels)");
    }
    private static int[] pngSize(byte[] d) {
        if (d == null || d.length < 24) return null;
        if ((d[0] & 0xff) != 0x89 || d[1] != 'P' || d[2] != 'N' || d[3] != 'G') return null;
        int w = ((d[16] & 0xff) << 24) | ((d[17] & 0xff) << 16) | ((d[18] & 0xff) << 8) | (d[19] & 0xff);
        int h = ((d[20] & 0xff) << 24) | ((d[21] & 0xff) << 16) | ((d[22] & 0xff) << 8) | (d[23] & 0xff);
        if (w <= 0 || h <= 0) return null;
        return new int[]{w, h};
    }
}