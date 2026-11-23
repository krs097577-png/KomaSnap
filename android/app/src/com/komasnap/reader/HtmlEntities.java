package com.komasnap.reader;

import java.util.HashMap;
import java.util.Map;

/** Minimal HTML entity table (the subset that appears in real EPUB files). */
public final class HtmlEntities {

    private static final Map<String, String> NAMED = new HashMap<>();

    static {
        NAMED.put("quot", "\"");
        NAMED.put("amp", "&");
        NAMED.put("apos", "'");
        NAMED.put("lt", "<");
        NAMED.put("gt", ">");
        NAMED.put("nbsp", "\u00a0");
        NAMED.put("ensp", "\u2002");
        NAMED.put("emsp", "\u2003");
        NAMED.put("thinsp", "\u2009");
        NAMED.put("zwnj", "\u200c");
        NAMED.put("zwj", "\u200d");
        NAMED.put("shy", "\u00ad");
        NAMED.put("ndash", "\u2013");
        NAMED.put("mdash", "\u2014");
        NAMED.put("horbar", "\u2015");
        NAMED.put("lsquo", "\u2018");
        NAMED.put("rsquo", "\u2019");
        NAMED.put("sbquo", "\u201a");
        NAMED.put("ldquo", "\u201c");
        NAMED.put("rdquo", "\u201d");
        NAMED.put("bdquo", "\u201e");
        NAMED.put("dagger", "\u2020");
        NAMED.put("Dagger", "\u2021");
        NAMED.put("bull", "\u2022");
        NAMED.put("hellip", "\u2026");
        NAMED.put("permil", "\u2030");
        NAMED.put("prime", "\u2032");
        NAMED.put("Prime", "\u2033");
        NAMED.put("lsaquo", "\u2039");
        NAMED.put("rsaquo", "\u203a");
        NAMED.put("oline", "\u203e");
        NAMED.put("frasl", "\u2044");
        NAMED.put("euro", "\u20ac");
        NAMED.put("trade", "\u2122");
        NAMED.put("larr", "\u2190");
        NAMED.put("uarr", "\u2191");
        NAMED.put("rarr", "\u2192");
        NAMED.put("darr", "\u2193");
        NAMED.put("harr", "\u2194");
        NAMED.put("crarr", "\u21b5");
        NAMED.put("forall", "\u2200");
        NAMED.put("part", "\u2202");
        NAMED.put("exist", "\u2203");
        NAMED.put("empty", "\u2205");
        NAMED.put("nabla", "\u2207");
        NAMED.put("isin", "\u2208");
        NAMED.put("notin", "\u2209");
        NAMED.put("ni", "\u220b");
        NAMED.put("prod", "\u220f");
        NAMED.put("sum", "\u2211");
        NAMED.put("minus", "\u2212");
        NAMED.put("lowast", "\u2217");
        NAMED.put("radic", "\u221a");
        NAMED.put("prop", "\u221d");
        NAMED.put("infin", "\u221e");
        NAMED.put("ang", "\u2220");
        NAMED.put("and", "\u2227");
        NAMED.put("or", "\u2228");
        NAMED.put("cap", "\u2229");
        NAMED.put("cup", "\u222a");
        NAMED.put("int", "\u222b");
        NAMED.put("there4", "\u2234");
        NAMED.put("sim", "\u223c");
        NAMED.put("cong", "\u2245");
        NAMED.put("asymp", "\u2248");
        NAMED.put("ne", "\u2260");
        NAMED.put("equiv", "\u2261");
        NAMED.put("le", "\u2264");
        NAMED.put("ge", "\u2265");
        NAMED.put("sub", "\u2282");
        NAMED.put("sup", "\u2283");
        NAMED.put("nsub", "\u2284");
        NAMED.put("sube", "\u2286");
        NAMED.put("supe", "\u2287");
        NAMED.put("oplus", "\u2295");
        NAMED.put("otimes", "\u2297");
        NAMED.put("perp", "\u22a5");
        NAMED.put("sdot", "\u22c5");
        NAMED.put("lceil", "\u2308");
        NAMED.put("rceil", "\u2309");
        NAMED.put("lfloor", "\u230a");
        NAMED.put("rfloor", "\u230b");
        NAMED.put("lang", "\u2329");
        NAMED.put("rang", "\u232a");
        NAMED.put("loz", "\u25ca");
        NAMED.put("spades", "\u2660");
        NAMED.put("clubs", "\u2663");
        NAMED.put("hearts", "\u2665");
        NAMED.put("diams", "\u2666");
        NAMED.put("OElig", "\u0152");
        NAMED.put("oelig", "\u0153");
        NAMED.put("Scaron", "\u0160");
        NAMED.put("scaron", "\u0161");
        NAMED.put("Yuml", "\u0178");
        NAMED.put("fnof", "\u0192");
        NAMED.put("circ", "\u02c6");
        NAMED.put("tilde", "\u02dc");
        NAMED.put("Alpha", "\u0391");
        NAMED.put("Beta", "\u0392");
        NAMED.put("Gamma", "\u0393");
        NAMED.put("Delta", "\u0394");
        NAMED.put("Omega", "\u03a9");
        NAMED.put("alpha", "\u03b1");
        NAMED.put("beta", "\u03b2");
        NAMED.put("gamma", "\u03b3");
        NAMED.put("delta", "\u03b4");
        NAMED.put("epsilon", "\u03b5");
        NAMED.put("lambda", "\u03bb");
        NAMED.put("mu", "\u03bc");
        NAMED.put("pi", "\u03c0");
        NAMED.put("sigma", "\u03c3");
        NAMED.put("omega", "\u03c9");
        NAMED.put("copy", "\u00a9");
        NAMED.put("reg", "\u00ae");
        NAMED.put("deg", "\u00b0");
        NAMED.put("plusmn", "\u00b1");
        NAMED.put("micro", "\u00b5");
        NAMED.put("para", "\u00b6");
        NAMED.put("middot", "\u00b7");
        NAMED.put("cedil", "\u00b8");
        NAMED.put("ordf", "\u00aa");
        NAMED.put("ordm", "\u00ba");
        NAMED.put("laquo", "\u00ab");
        NAMED.put("raquo", "\u00bb");
        NAMED.put("not", "\u00ac");
        NAMED.put("frac14", "\u00bc");
        NAMED.put("frac12", "\u00bd");
        NAMED.put("frac34", "\u00be");
        NAMED.put("iexcl", "\u00a1");
        NAMED.put("iquest", "\u00bf");
        NAMED.put("Agrave", "\u00c0");
        NAMED.put("Aacute", "\u00c1");
        NAMED.put("Auml", "\u00c4");
        NAMED.put("times", "\u00d7");
        NAMED.put("divide", "\u00f7");
        NAMED.put("yuml", "\u00ff");
        NAMED.put("aacute", "\u00e1");
        NAMED.put("eacute", "\u00e9");
        NAMED.put("iacute", "\u00ed");
        NAMED.put("oacute", "\u00f3");
        NAMED.put("uacute", "\u00fa");
        NAMED.put("agrave", "\u00e0");
        NAMED.put("egrave", "\u00e8");
        NAMED.put("ccedil", "\u00e7");
        NAMED.put("uuml", "\u00fc");
        NAMED.put("ouml", "\u00f6");
        NAMED.put("auml", "\u00e4");
        NAMED.put("szlig", "\u00df");
        NAMED.put("ntilde", "\u00f1");
        NAMED.put("hearts", "\u2665");
    }

    private HtmlEntities() {
    }

    /** Decodes one entity name; text that is not an entity is returned unchanged. */
    public static String decode(String entity) {
        if (entity == null) return "";
        if (!entity.isEmpty() && entity.charAt(0) == '#') {
            try {
                int code;
                if (entity.length() > 1 && (entity.charAt(1) == 'x' || entity.charAt(1) == 'X')) {
                    code = Integer.parseInt(entity.substring(2), 16);
                } else {
                    code = Integer.parseInt(entity.substring(1));
                }
                if (code <= 0 || code > 0x10FFFF) return "\uFFFD";
                return new String(Character.toChars(code));
            } catch (NumberFormatException e) {
                return entity;
            }
        }
        String named = NAMED.get(entity);
        return named != null ? named : entity;
    }
}
