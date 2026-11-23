package com.komasnap.reader;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;

/** Small stream helpers with hard size limits, mirroring the desktop reader. */
public final class Streams {

    private Streams() {
    }

    public static byte[] readAll(InputStream in, int limit) throws IOException {
        ByteArrayOutputStream out = new ByteArrayOutputStream(Math.min(limit, 1 << 16));
        byte[] buffer = new byte[65536];
        int read;
        int total = 0;
        while ((read = in.read(buffer)) != -1) {
            total += read;
            if (total > limit) throw new IOException("文件过大，超出 " + (limit / (1024 * 1024)) + " MB 限制");
            out.write(buffer, 0, read);
        }
        return out.toByteArray();
    }

    public static void copy(InputStream in, OutputStream out) throws IOException {
        byte[] buffer = new byte[65536];
        int read;
        while ((read = in.read(buffer)) != -1) out.write(buffer, 0, read);
    }

    public static void closeQuietly(java.io.Closeable c) {
        if (c == null) return;
        try {
            c.close();
        } catch (IOException ignored) {
        }
    }
}
