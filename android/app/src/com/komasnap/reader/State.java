package com.komasnap.reader;

import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.security.MessageDigest;
import java.util.Properties;

/**
 * Reader preferences and per-book reading position, stored as plain
 * {@code settings.properties} + {@code <sha256>.page} files.
 *
 * <p>Defaults match the desktop app: dual page, right-to-left, solo cover,
 * fit page, slide motion.</p>
 */
public final class State {

    public static final int MOTION_SLIDE = 0;
    public static final int MOTION_CURL = 1;
    public static final int MOTION_SCROLL = 2;

    public boolean dual = true;
    public boolean rightToLeft = true;
    public boolean soloCover = true;
    public boolean fitWidth = false;
    public boolean continuousFitWidth = true;
    public int motion = MOTION_SLIDE;

    private final File folder;

    public State(File folder) {
        this.folder = folder;
        folder.mkdirs();
        load();
    }

    private File settingsFile() {
        return new File(folder, "settings.properties");
    }

    private void load() {
        File file = settingsFile();
        if (!file.isFile()) return;
        Properties p = new Properties();
        try (InputStream in = new FileInputStream(file)) {
            p.load(in);
        } catch (IOException | IllegalArgumentException ignored) {
            return;
        }
        dual = bool(p, "dual", dual);
        rightToLeft = bool(p, "rtl", rightToLeft);
        soloCover = bool(p, "cover", soloCover);
        fitWidth = bool(p, "fitWidth", fitWidth);
        continuousFitWidth = bool(p, "continuousFitWidth", continuousFitWidth);
        try {
            int m = Integer.parseInt(p.getProperty("motion", String.valueOf(motion)));
            if (m >= MOTION_SLIDE && m <= MOTION_SCROLL) motion = m;
        } catch (NumberFormatException ignored) {
        }
    }

    private static boolean bool(Properties p, String key, boolean fallback) {
        String value = p.getProperty(key);
        if (value == null) return fallback;
        return value.equalsIgnoreCase("true") || value.equals("1");
    }

    public void save() {
        Properties p = new Properties();
        p.setProperty("dual", String.valueOf(dual));
        p.setProperty("rtl", String.valueOf(rightToLeft));
        p.setProperty("cover", String.valueOf(soloCover));
        p.setProperty("fitWidth", String.valueOf(fitWidth));
        p.setProperty("continuousFitWidth", String.valueOf(continuousFitWidth));
        p.setProperty("motion", String.valueOf(motion));
        File tmp = new File(folder, "settings.properties.tmp");
        try (OutputStream out = new FileOutputStream(tmp)) {
            p.store(out, "KomaSnap reader");
        } catch (IOException ignored) {
            return;
        }
        File target = settingsFile();
        if (!tmp.renameTo(target)) {
            target.delete();
            tmp.renameTo(target);
        }
    }

    public int readPosition(String bookId) {
        File file = positionFile(bookId);
        if (!file.isFile()) return 0;
        try {
            byte[] data = new byte[(int) Math.min(file.length(), 32)];
            try (InputStream in = new FileInputStream(file)) {
                int read = in.read(data);
                String text = new String(data, 0, Math.max(0, read), "UTF-8").trim();
                return Math.max(0, Integer.parseInt(text));
            }
        } catch (IOException | NumberFormatException e) {
            return 0;
        }
    }

    public void savePosition(String bookId, int position) {
        File target = positionFile(bookId);
        File tmp = new File(folder, target.getName() + ".tmp");
        try (OutputStream out = new FileOutputStream(tmp)) {
            out.write(String.valueOf(Math.max(0, position)).getBytes("UTF-8"));
        } catch (IOException ignored) {
            return;
        }
        if (!tmp.renameTo(target)) {
            target.delete();
            tmp.renameTo(target);
        }
    }

    private File positionFile(String bookId) {
        return new File(folder, sha256(bookId) + ".page");
    }

    static String sha256(String value) {
        try {
            MessageDigest digest = MessageDigest.getInstance("SHA-256");
            byte[] bytes = digest.digest(value.toUpperCase(java.util.Locale.ROOT).getBytes("UTF-8"));
            StringBuilder out = new StringBuilder();
            for (byte b : bytes) out.append(String.format("%02X", b));
            return out.toString();
        } catch (Exception e) {
            return Integer.toHexString(value.hashCode());
        }
    }
}
