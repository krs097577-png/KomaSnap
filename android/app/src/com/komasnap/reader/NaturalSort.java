package com.komasnap.reader;

import java.util.Comparator;

/**
 * Port of Windows' {@code StrCmpLogicalW} ("natural sort"), which the desktop
 * reader used to order archive entries. Digit runs compare numerically, with a
 * shorter run winning when values are equal, and ties fall back to a
 * case-insensitive ordinal comparison. That ordering is what makes
 * {@code 2.jpg} come before {@code 10.jpg} in comic archives.
 */
public final class NaturalSort implements Comparator<String> {

    public static final NaturalSort INSTANCE = new NaturalSort();

    private NaturalSort() {
    }

    @Override
    public int compare(String a, String b) {
        if (a == null) return b == null ? 0 : -1;
        if (b == null) return 1;
        int i = 0;
        int j = 0;
        int n = a.length();
        int m = b.length();
        while (i < n && j < m) {
            char ca = a.charAt(i);
            char cb = b.charAt(j);
            boolean da = isDigit(ca);
            boolean db = isDigit(cb);
            if (da && db) {
                int si = i;
                int sj = j;
                while (i < n && a.charAt(i) == '0') i++;
                while (j < m && b.charAt(j) == '0') j++;
                int ei = i;
                int ej = j;
                while (ei < n && isDigit(a.charAt(ei))) ei++;
                while (ej < m && isDigit(b.charAt(ej))) ej++;
                int la = ei - i;
                int lb = ej - j;
                if (la != lb) return la - lb;
                for (int k = 0; k < la; k++) {
                    char x = a.charAt(i + k);
                    char y = b.charAt(j + k);
                    if (x != y) return x - y;
                }
                int zeros = (i - si) - (j - sj);
                if (zeros != 0) return zeros;
                i = ei;
                j = ej;
                continue;
            }
            if (ca != cb) {
                char la = Character.toLowerCase(ca);
                char lb = Character.toLowerCase(cb);
                if (la != lb) return la - lb;
                // Same letter, different case: prefer lowercase first like Windows.
                return ca - cb;
            }
            i++;
            j++;
        }
        return (n - i) - (m - j);
    }

    private static boolean isDigit(char c) {
        return c >= '0' && c <= '9';
    }

    public static boolean isImage(String name) {
        if (name == null) return false;
        int dot = name.lastIndexOf('.');
        if (dot < 0) return false;
        String ext = name.substring(dot + 1).toLowerCase(java.util.Locale.ROOT);
        switch (ext) {
            case "jpg":
            case "jpeg":
            case "png":
            case "gif":
            case "bmp":
            case "webp":
            case "tif":
            case "tiff":
            case "avif":
            case "heic":
            case "heif":
                return true;
            default:
                return false;
        }
    }

    public static String extension(String path) {
        if (path == null) return "";
        int slash = Math.max(path.lastIndexOf('/'), path.lastIndexOf('\\'));
        int dot = path.lastIndexOf('.');
        if (dot <= slash) return "";
        return path.substring(dot + 1).toLowerCase(java.util.Locale.ROOT);
    }

    public static String titleOf(String path) {
        if (path == null) return "";
        int slash = Math.max(path.lastIndexOf('/'), path.lastIndexOf('\\'));
        String name = slash >= 0 ? path.substring(slash + 1) : path;
        int dot = name.lastIndexOf('.');
        return dot > 0 ? name.substring(0, dot) : name;
    }
}
