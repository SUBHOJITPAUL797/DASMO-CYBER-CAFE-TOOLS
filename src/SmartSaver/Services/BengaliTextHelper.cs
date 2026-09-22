using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace SmartSaver.Services;

/// <summary>
/// Handles detection, legacy glyph decoding, and visual-to-logical Unicode normalization
/// for Bengali text in Indian government PDF documents (e.g. Banglarbhumi Land Records / ROR).
/// </summary>
public static class BengaliTextHelper
{
    public const string PreferredBengaliFont = "Nirmala UI";

    /// <summary>
    /// Mapping of legacy TrueType subset font glyph codes used by Indian government portals
    /// (which lacked standard ToUnicode CMaps and mapped complex Indic ligatures to Greek/Cyrillic codepoints)
    /// to standard Bengali Unicode strings.
    /// </summary>
    private static readonly Dictionary<char, string> LegacyGlyphMap = new()
    {
        { '\u042B', "ত্ত" },     // Ы -> ত্ত (t+t) e.g. আকাল দত্ত
        { '\u047E', "স্ব" },     // Ѿ -> স্ব (s+w) e.g. পিতা/স্বামী
        { '\u03AD', "\u09C7" }, // έ -> ে (E-kar)
        { '\u039D', "\u09BF" }, // Ν -> ি (I-kar)
        { '\u039C', "\u09BF" }, // Μ -> ি (I-kar)
        { '\u03DC', "শ্র" },     // Ϝ -> শ্র (sh+r) e.g. শ্রেণী
        { '\u049D', "্য" },      // ҝ -> ্য (ya-phala) e.g. সংখ্যা, মধ্যে
        { '\u043E', "ন্ত" },     // о -> ন্ত (n+t) e.g. মন্তব্য
        { '\u0414', "ঙ" },      // Д -> ঙ e.g. ডাঙা
        { '\u113F', "স্তু" },    // ᄿ -> স্তু (s+t+u) e.g. বাস্তু
        { '\u03CF', "ত্র" },     // Ϗ -> ত্র (t+r) e.g. অত্র
        { '\u042F', "ত্ব" },     // Я -> ত্ব (t+w) e.g. অত্রস্বত্বের
        { '\u03EC', "ত্ব" },     // Ϭ -> ত্ব (t+w) e.g. অত্রসত্বের
        { '\u03BA', "র্" },      // κ -> reph (r+) e.g. নির্ধারিত, যথার্থ
        { '\u0434', "দ্ধ" },     // д -> দ্ধ (d+dh) e.g. অর্দ্ধগ্রাম
        { '\u03C2', "গ্র" },     // ς -> গ্র (g+r) e.g. গ্রাম
        { '\u0405', "ক্ত" },     // Ѕ -> ক্ত (k+t) e.g. ব্যক্তি
        { '\u00A0', " " },      // Non-breaking space -> standard space
    };

    /// <summary>
    /// Checks if text contains Bengali Unicode characters (\u0980-\u09FF) or legacy Bengali glyph codes.
    /// </summary>
    public static bool ContainsBengali(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        foreach (char c in text)
        {
            if ((c >= '\u0980' && c <= '\u09FF') || LegacyGlyphMap.ContainsKey(c))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Normalizes extracted Bengali text by translating legacy government glyphs,
    /// reordering post-base reph and pre-base vowels (ি, ে, ৈ) into proper Unicode logical order,
    /// and fixing known government portal ligature artefacts.
    /// </summary>
    public static string NormalizeBengaliText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        if (!ContainsBengali(text)) return text;

        // 1. Direct replacement of multi-character font/OCR corruption patterns
        string s = text
            .Replace("বҝাΝЅ", "ব্যক্তি")
            .Replace("ব্\u049dা\u039d\u0405", "ব্যক্তি")
            .Replace("ব্koINs", "ব্যক্তি")
            .Replace("ব্ҝাΝЅ", "ব্যক্তি")
            .Replace("অদçk াম", "অর্দ্ধগ্রাম")
            .Replace("অচ্ঞেসরদাগের", "অত্রস্বত্বের দাগের");

        // 2. Map single legacy font glyph codes to standard Unicode
        var sb = new StringBuilder(s.Length + 16);
        foreach (char ch in s)
        {
            if (LegacyGlyphMap.TryGetValue(ch, out var replacement))
            {
                sb.Append(replacement);
            }
            else
            {
                sb.Append(ch);
            }
        }
        s = sb.ToString();

        // 3. Reorder post-base reph:
        // In visual encoding, reph (র্) was placed AFTER the base consonant/ligature (e.g. দ্ধ + র্, থ + র্).
        // In Unicode logical order, reph (র্) MUST precede the consonant (র্ + দ্ধ, র্ + থ).
        s = Regex.Replace(s, @"([ক-হড়-য়]|দ্ধ|ত্ত|ন্ত|স্ব|ত্ব|শ্র)(র্)", "$2$1");

        // 4. Reorder visual pre-base vowel signs:
        // In visual encoding, pre-base vowels (ি \u09BF, ে \u09C7, ৈ \u09C8) are positioned BEFORE the consonant cluster.
        // In Unicode logical order, the consonant cluster MUST precede its vowel sign.
        // Swap: (vowel sign) + (consonant cluster) -> (consonant cluster) + (vowel sign)
        s = Regex.Replace(s, @"([\u09BF\u09C7\u09C8])([ক-হড়-য়](?:্[ক-হড়-য়])*)", "$2$1");

        // 5. Clean up common government land record terms and ligatures
        s = s.Replace("ব্যাক্তি", "ব্যক্তি")
             .Replace("অদ্ধগ্র্রাম", "অর্দ্ধগ্রাম")
             .Replace("অদ্ধর্গ্রাম", "অর্দ্ধগ্রাম")
             .Replace("অদ্ধগ্রা", "অর্দ্ধগ্রা")
             .Replace("যথাথর্", "যথার্থ")
             .Replace("যথাথ", "যথার্থ")
             .Replace("র্পরিমাণ", "পরিমাণ")
             .Replace("নিধারি্", "নির্ধারি")
             .Replace("নিধারি", "নির্ধারি")
             .Replace("দ্ত", "দত্ত")
             .Replace("দЫ", "দত্ত")
             .Replace("শে্রণী", "শ্রেণী")
             .Replace("খিতয়ান", "খতিয়ান")
             .Replace("জিমর", "জমির")
             .Replace("পিরমাণ", "পরিমাণ")
             .Replace("মেধ্য", "মধ্যে")
             .Replace("দােগর", "দাগের")
             .Replace("অনুসাের", "অনুসারে")
             .Replace("হইেব", "হইবে")
             .Replace("অত্রসতে্বর", "অত্রসত্বের")
             .Replace("নিধর্ারিত", "নির্ধারিত");

        return s;
    }
}
