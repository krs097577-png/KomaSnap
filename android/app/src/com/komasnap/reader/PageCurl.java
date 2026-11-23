package com.komasnap.reader;

import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.LinearGradient;
import android.graphics.Matrix;
import android.graphics.Paint;
import android.graphics.Path;
import android.graphics.Shader;

/** 在绘制线程复用几何和画笔，卷曲时无需生成位图。 */
final class PageCurl {
    private final Path front = new Path();
    private final Path fold = new Path();
    private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Matrix gradientMatrix = new Matrix();
    private final LinearGradient shade = new LinearGradient(0f, 0f, 1f, 0f,
            new int[]{0x1F000000, 0x09000000, 0x32FFFFFF, 0x53000000},
            new float[]{0f, .34f, .69f, 1f}, Shader.TileMode.CLAMP);
    private int paperColor = Color.WHITE;
    private float seam;
    private float lift;
    private int direction;

    void setPaperColor(int color) {
        paperColor = color;
    }

    Path update(float left, float top, float width, float height, int direction, float progress) {
        this.direction = direction;
        seam = width * progress;
        lift = Math.min(width * .20f, height * .17f)
                * (float) Math.sin(Math.PI * progress);
        float bottom = top + height;
        front.rewind();
        front.moveTo(x(left, width, seam - lift * .26f), top);
        front.cubicTo(x(left, width, seam - lift * .21f), top + height * .38f,
                x(left, width, seam + lift * .11f), top + height * .77f,
                x(left, width, seam + lift * .46f), bottom);
        front.lineTo(x(left, width, width), bottom);
        front.lineTo(x(left, width, width), top);
        front.close();

        fold.rewind();
        fold.moveTo(x(left, width, seam - lift * .26f), top);
        fold.cubicTo(x(left, width, seam - lift * .21f), top + height * .38f,
                x(left, width, seam + lift * .11f), top + height * .77f,
                x(left, width, seam + lift * .46f), bottom);
        fold.lineTo(x(left, width, seam - lift), bottom);
        fold.cubicTo(x(left, width, seam - lift * .65f), top + height * .75f,
                x(left, width, seam - lift * .29f), top + height * .35f,
                x(left, width, seam - lift * .26f), top);
        fold.close();
        return front;
    }

    void drawFold(Canvas canvas, float left, float top, float width, float height) {
        if (lift < .5f) return;
        int save = canvas.save();
        canvas.clipRect(left, top, left + width, top + height);
        paint.setColor(paperColor);
        paint.setShader(null);
        canvas.drawPath(fold, paint);
        float start = x(left, width, seam - lift);
        gradientMatrix.setScale(direction * lift * 1.46f, 1f);
        gradientMatrix.postTranslate(start, 0f);
        shade.setLocalMatrix(gradientMatrix);
        paint.setShader(shade);
        canvas.drawPath(fold, paint);
        canvas.restoreToCount(save);
    }

    private float x(float left, float width, float distance) {
        return direction > 0 ? left + distance : left + width - distance;
    }
}
