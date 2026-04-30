using System;
using System.Collections.Generic;
using TinyPinyin;

namespace StrmAssistant.Common
{
    /// <summary>
    /// 多音字读音表（仅收录拼音字母不同的多音字；声调差异对 FTS5 索引无意义）。
    /// 数据用于搜索召回：对带多音字的中文字段，把全部读音都写入索引，
    /// 用户用任何一种拼法（重庆 chong/zhong、银行 hang/xing 等）都能命中。
    ///
    /// 字典里每个 char 的所有读音都列出（不假设 TinyPinyin 选哪个为主读音），
    /// GetReadings 会把 TinyPinyin 返回的主读音和字典读音合并去重。
    /// 读音必须是大写、无声调的拼音字母（与 TinyPinyin.GetPinyin 输出一致）。
    /// </summary>
    internal static class PinyinPolyphony
    {
        private static readonly Dictionary<char, string[]> Readings = new Dictionary<char, string[]>
        {
            { '长', new[] { "CHANG", "ZHANG" } },
            { '重', new[] { "ZHONG", "CHONG" } },
            { '行', new[] { "XING", "HANG" } },
            { '还', new[] { "HAI", "HUAN" } },
            { '乐', new[] { "LE", "YUE" } },
            { '调', new[] { "DIAO", "TIAO" } },
            { '没', new[] { "MEI", "MO" } },
            { '和', new[] { "HE", "HUO", "HU", "HUAN" } },
            { '大', new[] { "DA", "DAI" } },
            { '都', new[] { "DOU", "DU" } },
            { '数', new[] { "SHU", "SHUO" } },
            { '模', new[] { "MO", "MU" } },
            { '朝', new[] { "CHAO", "ZHAO" } },
            { '校', new[] { "XIAO", "JIAO" } },
            { '角', new[] { "JIAO", "JUE" } },
            { '觉', new[] { "JUE", "JIAO" } },
            { '解', new[] { "JIE", "XIE" } },
            { '系', new[] { "XI", "JI" } },
            { '单', new[] { "DAN", "SHAN", "CHAN" } },
            { '强', new[] { "QIANG", "JIANG" } },
            { '落', new[] { "LUO", "LA", "LAO" } },
            { '圈', new[] { "QUAN", "JUAN" } },
            { '给', new[] { "GEI", "JI" } },
            { '便', new[] { "BIAN", "PIAN" } },
            { '参', new[] { "CAN", "SHEN", "CEN" } },
            { '传', new[] { "CHUAN", "ZHUAN" } },
            { '藏', new[] { "CANG", "ZANG" } },
            { '卡', new[] { "KA", "QIA" } },
            { '弹', new[] { "DAN", "TAN" } },
            { '着', new[] { "ZHE", "ZHAO", "ZHUO" } },
            { '血', new[] { "XUE", "XIE" } },
            { '阿', new[] { "A", "E" } },
            { '露', new[] { "LU", "LOU" } },
            { '削', new[] { "XIAO", "XUE" } },
            { '差', new[] { "CHA", "CHAI", "CI" } },
            { '屏', new[] { "PING", "BING" } },
            { '薄', new[] { "BO", "BAO" } },
            { '蔓', new[] { "MAN", "WAN" } },
            { '钥', new[] { "YAO", "YUE" } },
            { '折', new[] { "ZHE", "SHE" } },
            { '扎', new[] { "ZHA", "ZA" } },
            { '塞', new[] { "SAI", "SE" } },
            { '泊', new[] { "BO", "PO" } },
            { '熨', new[] { "YUN", "YU" } },
            { '什', new[] { "SHEN", "SHI" } },
            { '说', new[] { "SHUO", "SHUI" } },
            { '佛', new[] { "FO", "FU" } },
            { '畜', new[] { "CHU", "XU" } },
            { '车', new[] { "CHE", "JU" } },
            { '匙', new[] { "CHI", "SHI" } },
            { '辟', new[] { "BI", "PI" } },
            { '颤', new[] { "CHAN", "ZHAN" } },
            { '埋', new[] { "MAI", "MAN" } },
            { '臭', new[] { "CHOU", "XIU" } },
            { '宿', new[] { "SU", "XIU" } },
            { '约', new[] { "YUE", "YAO" } },
            { '识', new[] { "SHI", "ZHI" } },
            { '熟', new[] { "SHU", "SHOU" } },
            { '曝', new[] { "PU", "BAO" } },
            { '壳', new[] { "KE", "QIAO" } },
            { '朴', new[] { "PU", "PIAO" } },
            { '种', new[] { "ZHONG", "CHONG" } },
            { '乘', new[] { "CHENG", "SHENG" } },
            { '亲', new[] { "QIN", "QING" } },
        };

        /// <summary>
        /// 返回 char c 的全部读音（大写无声调）。非汉字返回空数组。
        /// 始终把 TinyPinyin 的主读音也合进去，避免字典遗漏。
        /// </summary>
        public static string[] GetReadings(char c)
        {
            string primary = null;
            if (PinyinHelper.IsChinese(c))
            {
                primary = PinyinHelper.GetPinyin(c);
            }

            Readings.TryGetValue(c, out var alts);

            if (string.IsNullOrEmpty(primary) && (alts == null || alts.Length == 0))
            {
                return Array.Empty<string>();
            }

            if (alts == null || alts.Length == 0)
            {
                return new[] { primary };
            }

            // 合并去重
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(primary)) set.Add(primary);
            foreach (var a in alts)
            {
                if (!string.IsNullOrEmpty(a)) set.Add(a);
            }

            var result = new string[set.Count];
            set.CopyTo(result);
            return result;
        }
    }
}
