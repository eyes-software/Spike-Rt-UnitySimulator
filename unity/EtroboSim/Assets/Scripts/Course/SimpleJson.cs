using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Etrobo.Sim
{
    public enum JsonKind { Null, Bool, Number, String, Array, Object }

    /// <summary>
    /// コース定義 JSON を読むための最小限のパーサ。
    /// UnityEngine.JsonUtility は [[x,y],[x,y]] のような入れ子配列も、
    /// 型が混在する segments 配列も扱えないため自前で持つ。
    /// 外部パッケージに依存しないので、ネットワークなしでもプロジェクトが開ける。
    /// </summary>
    public sealed class JsonValue
    {
        public JsonKind Kind;
        public bool BoolValue;
        public double NumberValue;
        public string StringValue;
        public List<JsonValue> Array;
        public Dictionary<string, JsonValue> Object;

        public static readonly JsonValue Null = new JsonValue { Kind = JsonKind.Null };

        public int Count => Kind == JsonKind.Array ? Array.Count : 0;

        public JsonValue this[int i] =>
            Kind == JsonKind.Array && i >= 0 && i < Array.Count ? Array[i] : Null;

        public JsonValue this[string key] =>
            Kind == JsonKind.Object && Object.TryGetValue(key, out var v) ? v : Null;

        public bool Has(string key) => Kind == JsonKind.Object && Object.ContainsKey(key);

        public float AsFloat(float fallback = 0f) =>
            Kind == JsonKind.Number ? (float)NumberValue : fallback;

        public int AsInt(int fallback = 0) =>
            Kind == JsonKind.Number ? (int)Math.Round(NumberValue) : fallback;

        public bool AsBool(bool fallback = false)
        {
            if (Kind == JsonKind.Bool) return BoolValue;
            if (Kind == JsonKind.Number) return NumberValue != 0.0;
            return fallback;
        }

        public string AsString(string fallback = null) =>
            Kind == JsonKind.String ? StringValue : fallback;

        public IEnumerable<JsonValue> Items()
        {
            if (Kind != JsonKind.Array) yield break;
            foreach (var v in Array) yield return v;
        }

        public static JsonValue Parse(string text)
        {
            int pos = 0;
            var v = ParseValue(text, ref pos);
            SkipWhitespace(text, ref pos);
            if (pos != text.Length)
                throw new FormatException("JSON: 末尾に余分な文字があります (offset " + pos + ")");
            return v;
        }

        // ---- パーサ本体 -----------------------------------------------------

        static void SkipWhitespace(string s, ref int p)
        {
            while (p < s.Length)
            {
                char c = s[p];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { p++; continue; }

                // コース定義は手書きするので // と /* */ のコメントを許す
                if (c == '/' && p + 1 < s.Length)
                {
                    if (s[p + 1] == '/')
                    {
                        p += 2;
                        while (p < s.Length && s[p] != '\n') p++;
                        continue;
                    }
                    if (s[p + 1] == '*')
                    {
                        p += 2;
                        while (p + 1 < s.Length && !(s[p] == '*' && s[p + 1] == '/')) p++;
                        p = Math.Min(p + 2, s.Length);
                        continue;
                    }
                }
                break;
            }
        }

        static JsonValue ParseValue(string s, ref int p)
        {
            SkipWhitespace(s, ref p);
            if (p >= s.Length) throw new FormatException("JSON: 入力が途中で終わっています");

            switch (s[p])
            {
                case '{': return ParseObject(s, ref p);
                case '[': return ParseArray(s, ref p);
                case '"': return new JsonValue { Kind = JsonKind.String, StringValue = ParseString(s, ref p) };
                case 't': Expect(s, ref p, "true"); return new JsonValue { Kind = JsonKind.Bool, BoolValue = true };
                case 'f': Expect(s, ref p, "false"); return new JsonValue { Kind = JsonKind.Bool, BoolValue = false };
                case 'n': Expect(s, ref p, "null"); return Null;
                default: return ParseNumber(s, ref p);
            }
        }

        static void Expect(string s, ref int p, string word)
        {
            if (p + word.Length > s.Length || string.CompareOrdinal(s, p, word, 0, word.Length) != 0)
                throw new FormatException("JSON: '" + word + "' を期待しました (offset " + p + ")");
            p += word.Length;
        }

        static JsonValue ParseObject(string s, ref int p)
        {
            var obj = new Dictionary<string, JsonValue>();
            p++; // '{'
            SkipWhitespace(s, ref p);
            if (p < s.Length && s[p] == '}')
            {
                p++;
                return new JsonValue { Kind = JsonKind.Object, Object = obj };
            }

            while (true)
            {
                SkipWhitespace(s, ref p);
                if (p >= s.Length || s[p] != '"')
                    throw new FormatException("JSON: オブジェクトのキーを期待しました (offset " + p + ")");
                string key = ParseString(s, ref p);

                SkipWhitespace(s, ref p);
                if (p >= s.Length || s[p] != ':')
                    throw new FormatException("JSON: ':' を期待しました (offset " + p + ")");
                p++;

                obj[key] = ParseValue(s, ref p);

                SkipWhitespace(s, ref p);
                if (p >= s.Length) throw new FormatException("JSON: オブジェクトが閉じていません");
                if (s[p] == ',') { p++; continue; }
                if (s[p] == '}') { p++; break; }
                throw new FormatException("JSON: ',' または '}' を期待しました (offset " + p + ")");
            }
            return new JsonValue { Kind = JsonKind.Object, Object = obj };
        }

        static JsonValue ParseArray(string s, ref int p)
        {
            var arr = new List<JsonValue>();
            p++; // '['
            SkipWhitespace(s, ref p);
            if (p < s.Length && s[p] == ']')
            {
                p++;
                return new JsonValue { Kind = JsonKind.Array, Array = arr };
            }

            while (true)
            {
                arr.Add(ParseValue(s, ref p));
                SkipWhitespace(s, ref p);
                if (p >= s.Length) throw new FormatException("JSON: 配列が閉じていません");
                if (s[p] == ',') { p++; continue; }
                if (s[p] == ']') { p++; break; }
                throw new FormatException("JSON: ',' または ']' を期待しました (offset " + p + ")");
            }
            return new JsonValue { Kind = JsonKind.Array, Array = arr };
        }

        static string ParseString(string s, ref int p)
        {
            var sb = new StringBuilder();
            p++; // 開きの引用符
            while (p < s.Length)
            {
                char c = s[p++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (p >= s.Length) break;

                char e = s[p++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (p + 4 > s.Length) throw new FormatException("JSON: \\u エスケープが不完全です");
                        sb.Append((char)Convert.ToInt32(s.Substring(p, 4), 16));
                        p += 4;
                        break;
                    default:
                        throw new FormatException("JSON: 未知のエスケープ \\" + e);
                }
            }
            throw new FormatException("JSON: 文字列が閉じていません");
        }

        static JsonValue ParseNumber(string s, ref int p)
        {
            int start = p;
            if (p < s.Length && (s[p] == '-' || s[p] == '+')) p++;
            while (p < s.Length)
            {
                char c = s[p];
                bool expSign = (c == '-' || c == '+') && (s[p - 1] == 'e' || s[p - 1] == 'E');
                if (char.IsDigit(c) || c == '.' || c == 'e' || c == 'E' || expSign) { p++; continue; }
                break;
            }
            string token = s.Substring(start, p - start);
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                throw new FormatException("JSON: 数値として読めません '" + token + "' (offset " + start + ")");
            return new JsonValue { Kind = JsonKind.Number, NumberValue = d };
        }
    }
}
