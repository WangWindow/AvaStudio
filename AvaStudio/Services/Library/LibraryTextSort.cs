using System;
using System.Text;
using TinyPinyin;

namespace AvaStudio.Services.Library;

/// <summary>
/// 资料库文本排序工具。中文标题按无声调拼音参与 A-Z 分组与排序，
/// 拉丁字符、数字与符号沿用原有规则，使中文、日文假名与全角字符都能落到稳定的分组中。
/// </summary>
public static class LibraryTextSort
{
    /// <summary>
    /// 取文本的字母分组标头：ASCII 字母返回大写 A-Z，中文返回拼音首字母，
    /// ASCII 数字与空文本归入 #，其余符号与不支持的文字归入 &amp;。
    /// </summary>
    /// <param name="text">用于分组的标题、专辑名或艺术家名。</param>
    /// <returns>单个字符的分组标头。</returns>
    public static string GetLetterHeader(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "#";
        }

        var normalized = text.Normalize(NormalizationForm.FormKC).TrimStart();
        if (normalized.Length == 0)
        {
            return "#";
        }

        var character = normalized[0];
        if (char.IsAsciiLetter(character))
        {
            return char.ToUpperInvariant(character).ToString();
        }

        if (char.IsAsciiDigit(character))
        {
            return "#";
        }

        if (PinyinHelper.IsChinese(character))
        {
            var pinyin = PinyinHelper.GetPinyin(character);
            return string.IsNullOrEmpty(pinyin)
                ? "&"
                : char.ToUpperInvariant(pinyin[0]).ToString();
        }

        return "&";
    }

    /// <summary>
    /// 取用于排序的拼音键：中文转换为大写无声调拼音，拉丁文本原样保留，
    /// 全角字母与数字先规范化为 ASCII，因此同组条目按读音而非码位排列。
    /// </summary>
    /// <param name="text">用于排序的标题、专辑名或艺术家名。</param>
    /// <returns>规范化后的大写排序键；空文本返回空字符串。</returns>
    public static string GetSortKey(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.Normalize(NormalizationForm.FormKC);
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        return PinyinHelper.GetPinyin(normalized, string.Empty).ToUpperInvariant();
    }
}
