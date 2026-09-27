using System;
using System.IO;
using UnityEngine;

namespace Etrobo.Sim
{
    /// <summary>
    /// シミュレータ自身の版。
    ///
    /// 走りや判定が変わる修正をしたら <see cref="Number"/> を上げ、同じ名前で
    /// git tag を打つ。走行ログや録画がどの版で採られたものかを後から
    /// 突き合わせられるようにするためで、これが無いと「前に測った数字」と
    /// 比べられない。実際、静止摩擦・アームの爪・タイヤ径・計測ゲートの
    /// 判定はどれも数日のうちに変わっている。
    ///
    /// 版数のほかに、ソースがあるときは最後のコミットも出す。コミットより
    /// 新しいファイルがあれば末尾に + を付ける（手元で直して走らせた状態）。
    /// </summary>
    public static class SimVersion
    {
        /// <summary>手で上げる版数。git tag と対応させる。</summary>
        public const string Number = "0.9.0";

        static string _full;

        /// <summary>"0.9.0 (c35ba3d+)" のような表示用の文字列。</summary>
        public static string Full => _full ?? (_full = Build());

        /// <summary>起動ログの 1 行目。</summary>
        public static string Banner =>
            $"[Etsim] EtroboUnitySim {Full}  Unity {Application.unityVersion}";

        static string Build()
        {
            try
            {
                // Assets の 3 つ上がリポジトリの根。配布ビルドには .git が無い。
                string root = Path.GetFullPath(
                    Path.Combine(Application.dataPath, "..", "..", ".."));
                string git = Path.Combine(root, ".git");
                if (!Directory.Exists(git)) return Number;

                string refPath = HeadRefPath(git);
                string hash = ReadHash(git, refPath);
                if (string.IsNullOrEmpty(hash)) return Number;

                string mark = Modified(root, refPath ?? Path.Combine(git, "HEAD")) ? "+" : "";
                return $"{Number} ({hash.Substring(0, 7)}{mark})";
            }
            catch (Exception)
            {
                // 版の表示ごときで起動を止めない
                return Number;
            }
        }

        /// <summary>HEAD が指しているブランチのファイル。分離 HEAD なら null。</summary>
        static string HeadRefPath(string git)
        {
            string head = File.ReadAllText(Path.Combine(git, "HEAD")).Trim();
            if (!head.StartsWith("ref:")) return null;

            string rel = head.Substring(4).Trim().Replace('/', Path.DirectorySeparatorChar);
            string path = Path.Combine(git, rel);
            return File.Exists(path) ? path : null;
        }

        static string ReadHash(string git, string refPath)
        {
            if (refPath != null) return File.ReadAllText(refPath).Trim();

            string head = File.ReadAllText(Path.Combine(git, "HEAD")).Trim();
            if (!head.StartsWith("ref:")) return head;          // 分離 HEAD

            // ブランチのファイルが無いときは packed-refs にまとめられている
            string packed = Path.Combine(git, "packed-refs");
            if (!File.Exists(packed)) return null;

            string want = head.Substring(4).Trim();
            foreach (string line in File.ReadAllLines(packed))
            {
                if (line.Length == 0 || line[0] == '#' || line[0] == '^') continue;
                int sp = line.IndexOf(' ');
                if (sp > 0 && line.Substring(sp + 1).Trim() == want)
                    return line.Substring(0, sp);
            }
            return null;
        }

        /// <summary>
        /// コミットより新しいファイルがあるか。
        ///
        /// git を起動せずに更新時刻だけで見る。中身を比べているわけではないので
        /// 触っただけでも + が付くが、「このログは手元の変更入りかもしれない」と
        /// 分かればよいので、厳しめに倒しておく。
        /// </summary>
        static bool Modified(string root, string sinceFile)
        {
            DateTime since = File.GetLastWriteTimeUtc(sinceFile);

            foreach (string dir in new[] { Path.Combine(root, "unity", "EtroboSim", "Assets"),
                                           Path.Combine(root, "course") })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (string pattern in new[] { "*.cs", "*.json" })
                    foreach (string f in Directory.GetFiles(dir, pattern, SearchOption.AllDirectories))
                        if (File.GetLastWriteTimeUtc(f) > since) return true;
            }
            return false;
        }
    }
}
