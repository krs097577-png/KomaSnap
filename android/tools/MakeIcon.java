import java.awt.Color;
import java.awt.Graphics2D;
import java.awt.RenderingHints;
import java.awt.geom.AffineTransform;
import java.awt.image.BufferedImage;
import java.io.File;
import javax.imageio.ImageIO;

/**
 * Generates every launcher asset from {@code app/icon-source.png}.
 *
 * <p>The supplied artwork is an icon lock-up: a comic page with a folded corner
 * above the "KomaSnap" wordmark. Launcher icons must stay legible at 48 dp, so
 * only the page glyph is used: the glyph is located by brightness (the artwork
 * is a white glyph on a near-black background) and the wordmark band below it is
 * excluded. Its background colour is sampled from the artwork itself.</p>
 */
public final class MakeIcon {

    private static final String SOURCE = "icon-source.png";
    private static final int[] LEGACY = {48, 72, 96, 144, 192};
    private static final String[] DENSITY = {"mdpi", "hdpi", "xhdpi", "xxhdpi", "xxxhdpi"};
    private static final int[] ADAPTIVE_108DP = {108, 162, 216, 324, 432};

    public static void main(String[] args) throws Exception {
        File res = new File(args[0]);
        File source = new File(res.getParentFile(), SOURCE);
        if (!source.isFile()) throw new IllegalStateException("missing icon source: " + source);
        BufferedImage art = ImageIO.read(source);
        if (art == null) throw new IllegalStateException("cannot decode " + source);

        Color background = new Color(art.getRGB(2, 2));
        int[] glyph = glyphBounds(art);
        int glyphX = glyph[0], glyphY = glyph[1], glyphW = glyph[2], glyphH = glyph[3];
        int margin = Math.max(8, Math.round(Math.max(glyphW, glyphH) * 0.04f));
        int side = Math.max(glyphW, glyphH) + margin * 2;
        int cx = glyphX + glyphW / 2;
        int cy = glyphY + glyphH / 2;
        int sx = clamp(cx - side / 2, 0, art.getWidth() - side);
        int sy = clamp(cy - side / 2, 0, art.getHeight() - side);
        BufferedImage crop = art.getSubimage(sx, sy, side, side);
        System.out.println("glyph=" + glyphX + "," + glyphY + " " + glyphW + "x" + glyphH
                + " bg=#" + hex(background) + " crop=" + side + "px");

        for (int i = 0; i < LEGACY.length; i++) {
            BufferedImage icon = compose(crop, background, LEGACY[i], 0.72f, true);
            File out = new File(res, "mipmap-" + DENSITY[i] + "/ic_launcher.png");
            out.getParentFile().mkdirs();
            ImageIO.write(icon, "png", out);
            System.out.println("wrote " + out);
        }

        for (int i = 0; i < ADAPTIVE_108DP.length; i++) {
            BufferedImage foreground = compose(crop, background, ADAPTIVE_108DP[i], 0.60f, false);
            File out = new File(res, "mipmap-" + DENSITY[i] + "/ic_launcher_foreground.png");
            out.getParentFile().mkdirs();
            ImageIO.write(foreground, "png", out);
            System.out.println("wrote " + out);
        }

        BufferedImage play = compose(crop, background, 512, 0.68f, true);
        File playStore = new File(res, "../../outputs/icon-playstore-512.png");
        playStore.getParentFile().mkdirs();
        ImageIO.write(play, "png", playStore);
        System.out.println("wrote " + playStore.getCanonicalPath());
    }

    private static BufferedImage compose(BufferedImage glyph, Color background, int size,
                                         float fill, boolean opaqueBackground) {
        BufferedImage canvas = new BufferedImage(size, size, BufferedImage.TYPE_INT_ARGB);
        Graphics2D g = canvas.createGraphics();
        g.setRenderingHint(RenderingHints.KEY_ANTIALIASING, RenderingHints.VALUE_ANTIALIAS_ON);
        g.setRenderingHint(RenderingHints.KEY_INTERPOLATION, RenderingHints.VALUE_INTERPOLATION_BICUBIC);
        g.setRenderingHint(RenderingHints.KEY_RENDERING, RenderingHints.VALUE_RENDER_QUALITY);
        if (opaqueBackground) {
            g.setColor(background);
            g.fillRect(0, 0, size, size);
        }
        int target = Math.round(size * fill);
        AffineTransform transform = new AffineTransform();
        transform.translate((size - target) / 2.0, (size - target) / 2.0);
        transform.scale(target / (double) glyph.getWidth(), target / (double) glyph.getHeight());
        g.drawImage(glyph, transform, null);
        g.dispose();
        return canvas;
    }

    /** Finds the top bright band (the glyph) and returns x, y, w, h. */
    private static int[] glyphBounds(BufferedImage image) {
        int width = image.getWidth();
        int height = image.getHeight();
        int[] perRow = new int[height];
        for (int y = 0; y < height; y++) {
            int count = 0;
            for (int x = 0; x < width; x++) {
                if (bright(image.getRGB(x, y))) count++;
            }
            perRow[y] = count;
        }
        int top = -1;
        for (int y = 0; y < height; y++) {
            if (perRow[y] > 4) {
                top = y;
                break;
            }
        }
        if (top < 0) throw new IllegalStateException("no glyph found in source artwork");
        int bottom = top;
        int gap = 0;
        for (int y = top; y < height; y++) {
            if (perRow[y] > 4) {
                bottom = y;
                gap = 0;
            } else if (++gap > 24) {
                break;
            }
        }
        int left = width;
        int right = 0;
        for (int y = top; y <= bottom; y++) {
            for (int x = 0; x < width; x++) {
                if (bright(image.getRGB(x, y))) {
                    if (x < left) left = x;
                    if (x > right) right = x;
                }
            }
        }
        if (right <= left) throw new IllegalStateException("empty glyph bounds");
        return new int[]{left, top, right - left + 1, bottom - top + 1};
    }

    private static boolean bright(int argb) {
        int r = (argb >> 16) & 0xff;
        int g = (argb >> 8) & 0xff;
        int b = argb & 0xff;
        return r + g + b > 300;
    }

    private static int clamp(int value, int min, int max) {
        return Math.max(min, Math.min(max, value));
    }

    private static String hex(Color color) {
        return String.format("%02X%02X%02X", color.getRed(), color.getGreen(), color.getBlue());
    }
}
