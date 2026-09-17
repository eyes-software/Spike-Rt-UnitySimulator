using System.Globalization;
using System.Text;
using UnityEngine;

namespace Etrobo.Sim
{
    /// <summary>
    /// コース JSON の一部だけを書き換える。
    ///
    /// 読み込んだモデルから全部を書き直すと、いま使っていない項目
    /// （z 順・注記・難所の細かい寸法など）を落としてしまう。
    /// そこで本文はテキストのまま残し、該当する部分だけ差し替える。
    /// </summary>
    public static class CourseFileWriter
    {
        /// <summary>
        /// start_pose を置き換えた JSON を返す。無ければ先頭に足す。
        /// 書式は問わない（対応する括弧を数えて範囲を取る）。
        /// </summary>
        public static string ReplaceStartPose(string json, Vector2 posMm, float headingDeg)
        {
            string value = "{\"position\": ["
                           + Num(posMm.x) + ", " + Num(posMm.y)
                           + "], \"heading\": " + Num(headingDeg) + "}";

            int key = json.IndexOf("\"start_pose\"", System.StringComparison.Ordinal);
            if (key < 0) return InsertAtRoot(json, "\"start_pose\": " + value);

            int open = json.IndexOf('{', key);
            if (open < 0) return InsertAtRoot(json, "\"start_pose\": " + value);

            int close = MatchingBrace(json, open);
            if (close < 0) return InsertAtRoot(json, "\"start_pose\": " + value);

            return json.Substring(0, open) + value + json.Substring(close + 1);
        }

        /// <summary>open の '{' に対応する '}' の位置。見つからなければ -1。</summary>
        static int MatchingBrace(string s, int open)
        {
            int depth = 0;
            bool inString = false;

            for (int i = open; i < s.Length; i++)
            {
                char c = s[i];

                if (inString)
                {
                    if (c == '\\') i++;             // エスケープは 1 文字読み飛ばす
                    else if (c == '"') inString = false;
                    continue;
                }

                if (c == '"') inString = true;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0) return i;
            }
            return -1;
        }

        /// <summary>ルートオブジェクトの先頭へ項目を足す。</summary>
        static string InsertAtRoot(string json, string entry)
        {
            int open = json.IndexOf('{');
            if (open < 0) return json;

            var sb = new StringBuilder(json.Length + entry.Length + 4);
            sb.Append(json, 0, open + 1);
            sb.Append('\n').Append(' ').Append(entry).Append(',');
            sb.Append(json, open + 1, json.Length - open - 1);
            return sb.ToString();
        }

        /// <summary>小数 1 桁。ロケールに左右されないよう InvariantCulture で書く。</summary>
        static string Num(float v)
        {
            float r = Mathf.Round(v * 10f) / 10f;
            return r.ToString("0.0###", CultureInfo.InvariantCulture);
        }
    }
}
