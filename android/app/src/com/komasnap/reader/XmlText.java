package com.komasnap.reader;

import java.util.LinkedHashMap;
import java.util.Map;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/** Tolerant XML/HTML text extraction used by the EPUB reader. */
public final class XmlText {

    private XmlText() {
    }

    /** Decodes XML bytes, honouring UTF-8/UTF-16 byte-order marks. */
    public static String decodeBytes(byte[] bytes) {
        if (bytes == null || bytes.length == 0) return "";
        try {
            if (bytes.length >= 2 && (bytes[0] & 0xff) == 0xFE && (bytes[1] & 0xff) == 0xFF) {
                return new String(bytes, 2, bytes.length - 2, "UTF-16BE");
            }
            if (bytes.length >= 2 && (bytes[0] & 0xff) == 0xFF && (bytes[1] & 0xff) == 0xFE) {
                return new String(bytes, 2, bytes.length - 2, "UTF-16LE");
            }
            if (bytes.length >= 3 && (bytes[0] & 0xff) == 0xEF && (bytes[1] & 0xff) == 0xBB && (bytes[2] & 0xff) == 0xBF) {
                return new String(bytes, 3, bytes.length - 3, "UTF-8");
            }
            return new String(bytes, "UTF-8");
        } catch (java.io.UnsupportedEncodingException e) {
            return new String(bytes);
        }
    }

    public static String decode(String raw) {
        if (raw == null || raw.isEmpty()) return "";
        StringBuilder out = new StringBuilder(raw.length());
        int i = 0;
        int len = raw.length();
        while (i < len) {
            char c = raw.charAt(i);
            if (c == '<') {
                int comment = raw.indexOf("<!--", i);
                if (comment == i) {
                    int end = raw.indexOf("-->", i + 4);
                    i = end < 0 ? len : end + 3;
                    continue;
                }
                int cdata = raw.indexOf("<![CDATA[", i);
                if (cdata == i) {
                    int end = raw.indexOf("]]>", i + 9);
                    if (end < 0) {
                        out.append(raw, i + 9, len);
                        break;
                    }
                    out.append(raw, i + 9, end);
                    i = end + 3;
                    continue;
                }
                int end = raw.indexOf('>', i);
                if (end < 0) break;
                i = end + 1;
                continue;
            }
            if (c == '&') {
                int semi = raw.indexOf(';', i);
                if (semi > i && semi - i <= 10) {
                    String entity = raw.substring(i + 1, semi);
                    String decoded = HtmlEntities.decode(entity);
                    if (decoded != null) {
                        out.append(decoded);
                        i = semi + 1;
                        continue;
                    }
                }
                out.append('&');
                i++;
                continue;
            }
            out.append(c);
            i++;
        }
        return out.toString();
    }

    /**
     * Parses {@code name="value"} / {@code name='value'} / {@code name=value}
     * pairs without relying on regex capture groups.
     */
    public static Map<String, String> attrs(String raw) {
        Map<String, String> out = new LinkedHashMap<>();
        if (raw == null) return out;
        int i = 0;
        int n = raw.length();
        while (i < n) {
            while (i < n && !isNameStart(raw.charAt(i))) i++;
            int start = i;
            while (i < n && isNamePart(raw.charAt(i))) i++;
            if (start == i) break;
            String name = raw.substring(start, i).toLowerCase(java.util.Locale.ROOT);
            while (i < n && Character.isWhitespace(raw.charAt(i))) i++;
            if (i >= n || raw.charAt(i) != '=') continue;
            i++;
            while (i < n && Character.isWhitespace(raw.charAt(i))) i++;
            if (i >= n) break;
            String value;
            char quote = raw.charAt(i);
            if (quote == '"' || quote == '\'') {
                i++;
                int end = raw.indexOf(quote, i);
                if (end < 0) end = n;
                value = raw.substring(i, end);
                i = Math.min(n, end + 1);
            } else {
                int end = i;
                while (end < n && !Character.isWhitespace(raw.charAt(end)) && raw.charAt(end) != '>'
                        && raw.charAt(end) != '/') end++;
                value = raw.substring(i, end);
                i = end;
            }
            value = HtmlEntities.decode(value);
            out.put(name, value);
            if (!name.contains(":")) {
                // plain name
            } else {
                int colon = name.indexOf(':');
                if (colon >= 0) out.putIfAbsent(name.substring(colon + 1), value);
            }
        }
        return out;
    }

    private static boolean isNameStart(char c) {
        return Character.isLetter(c) || c == '_' || c == ':';
    }

    private static boolean isNamePart(char c) {
        return Character.isLetterOrDigit(c) || c == '-' || c == '_' || c == ':' || c == '.';
    }

    /** First attribute value of the first {@code tag} occurrence (open or self-closing). */
    public static String attr(String xml, String tag, String attribute) {
        Matcher m = Pattern.compile("<(?:\\w+:)?" + Pattern.quote(tag) + "\\b([^>]*?)/?>",
                Pattern.CASE_INSENSITIVE | Pattern.DOTALL).matcher(xml);
        while (m.find()) {
            String value = attrs(m.group(1)).get(attribute.toLowerCase(java.util.Locale.ROOT));
            if (value != null) return value;
        }
        return null;
    }
}
