package com.komasnap.reader;

import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.Rect;

import java.io.ByteArrayInputStream;
import java.io.IOException;

/** Bitmap helpers shared by every book format. */
public final class Bitmaps {

    private Bitmaps() {
    }

    public static final class Size {
        public final int width;
        public final int height;

        Size(int width, int height) {
            this.width = width;
            this.height = height;
        }
    }

    public static Size bounds(byte[] bytes) {
        BitmapFactory.Options o = new BitmapFactory.Options();
        o.inJustDecodeBounds = true;
        BitmapFactory.decodeByteArray(bytes, 0, bytes.length, o);
        int w = o.outWidth;
        int h = o.outHeight;
        if (w <= 0 || h <= 0) return null;
        return new Size(w, h);
    }

    public static Bitmap decode(byte[] bytes) {
        return decode(bytes, 0);
    }

    /**
     * Decodes {@code bytes} no larger than {@code maxWidth} wide, preserving aspect
     * ratio. Uses power-of-two subsampling first, then an exact rescale.
     */
    public static Bitmap decode(byte[] bytes, int maxWidth) {
        if (bytes == null || bytes.length == 0) return null;
        BitmapFactory.Options o = new BitmapFactory.Options();
        o.inJustDecodeBounds = true;
        BitmapFactory.decodeByteArray(bytes, 0, bytes.length, o);
        if (o.outWidth <= 0 || o.outHeight <= 0) return null;
        int sample = 1;
        if (maxWidth > 0 && o.outWidth > maxWidth * 2) {
            while (o.outWidth / (sample * 2) >= maxWidth * 2) sample *= 2;
        }
        BitmapFactory.Options d = new BitmapFactory.Options();
        d.inSampleSize = sample;
        d.inPreferredConfig = Bitmap.Config.ARGB_8888;
        Bitmap bitmap = BitmapFactory.decodeByteArray(bytes, 0, bytes.length, d);
        if (bitmap == null) return null;
        if (maxWidth > 0 && bitmap.getWidth() > maxWidth) {
            int w = maxWidth;
            int h = Math.max(1, Math.round(bitmap.getHeight() * (w / (float) bitmap.getWidth())));
            Bitmap scaled = Bitmap.createScaledBitmap(bitmap, w, h, true);
            if (scaled != bitmap) bitmap.recycle();
            bitmap = scaled;
        }
        return bitmap;
    }

    public static Bitmap decode(java.io.InputStream in) throws IOException {
        byte[] data = Streams.readAll(in, 96 * 1024 * 1024);
        return decode(data);
    }

    /** Replacement cover when a page cannot be decoded: dark slate with an X. */
    public static Bitmap failureTile(int width, int height) {
        int w = Math.max(1, width);
        int h = Math.max(1, height);
        Bitmap b = Bitmap.createBitmap(w, h, Bitmap.Config.ARGB_8888);
        Canvas c = new Canvas(b);
        c.drawColor(Color.rgb(47, 79, 79));
        Paint p = new Paint(Paint.ANTI_ALIAS_FLAG);
        p.setColor(Color.argb(120, 255, 255, 255));
        p.setStyle(Paint.Style.STROKE);
        p.setStrokeWidth(Math.max(2f, w / 40f));
        c.drawRect(new Rect(2, 2, w - 2, h - 2), p);
        return b;
    }
}
