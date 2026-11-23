package android.graphics;
public class Color {
    public static final int BLACK = -16777216;
    public static final int WHITE = -1;
    public static int rgb(int r, int g, int b) { return (0xff << 24) | (r << 16) | (g << 8) | b; }
    public static int argb(int a, int r, int g, int b) { return (a << 24) | (r << 16) | (g << 8) | b; }
}