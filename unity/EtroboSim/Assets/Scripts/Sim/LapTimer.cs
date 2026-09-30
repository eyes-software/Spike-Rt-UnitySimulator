using System.Collections.Generic;
using UnityEngine;

namespace Etrobo.Sim
{
    /// <summary>
    /// 計測ゲートの通過判定とタイム計測。
    ///
    /// 規約ではスタート (5.9)・チェックポイント到達 (5.12)・LAP ゲート到達
    /// (5.13) のいずれも「走行体の一部が矢印の示す方向でゲートラインを
    /// 通過する」ことで成立する。そこで走行体の四隅それぞれについて
    /// 毎ステップ「前回位置 -> 今回位置」の線分を作り、ゲート線分と
    /// 交差したかを見る。点が範囲に入ったかを見る方式だと高速通過時に
    /// 取りこぼすので、線分どうしの交差で判定する。
    ///
    /// 中心 1 点で見ていたころは、成立が走行距離にして車体の半分ぶん
    /// 遅れていた。スタートラインのように走行体が跨いだ状態から始まる
    /// ゲートでは、中心が一度も跨がず永久に成立しないこともあった。
    /// </summary>
    public sealed class LapTimer
    {
        readonly List<CourseGate> _gates;
        readonly HashSet<string> _passedCheckpoints = new HashSet<string>();

        readonly Vector2[] _prevCorners = new Vector2[4];
        bool _hasPrev;

        /// <summary>スタートが成立した（規約 5.9）。</summary>
        public bool Running { get; private set; }

        /// <summary>LAP ゲートに到達した（規約 5.13）。走行タイムはここで止まる。</summary>
        public bool Finished { get; private set; }

        /// <summary>走行タイムを計っている。</summary>
        public bool Timing { get; private set; }

        public float ElapsedSeconds { get; private set; }

        /// <summary>競技時間 [s]（規約の最大計測時間）。</summary>
        public const float CompetitionLimitSeconds = 120f;

        /// <summary>
        /// スタートの合図からの経過時間 [s]。走行タイム（ElapsedSeconds）と違い、
        /// ゴールしても止まらず、リセットまで数え続ける。
        /// </summary>
        public float CompetitionElapsedSeconds { get; private set; }

        /// <summary>
        /// 競技時間を計っている。スタートの合図で立ち、リセットでだけ下りる。
        /// Timing は LAP ゲート（ゴール）で走行タイムを止めるために下りるので、
        /// 経過時間と残り時間はこちらを見る。
        /// </summary>
        public bool CompetitionTiming { get; private set; }

        /// <summary>
        /// 競技時間を計っている間に、走行体の一部がガレージに入った。
        /// 入った時点で経過時間と残り時間の数えを止める（出ても再開しない。リセットで戻る）。
        /// </summary>
        public bool GarageEntered { get; private set; }

        /// <summary>競技の残り時間 [s]。120 秒から減り、0 で止まる。</summary>
        public float RemainingSeconds => Mathf.Max(0f, CompetitionLimitSeconds - CompetitionElapsedSeconds);

        /// <summary>残り時間が 0 になったか。</summary>
        public bool TimeUp => CompetitionTiming && CompetitionElapsedSeconds >= CompetitionLimitSeconds;
        public int CheckpointsPassed => _passedCheckpoints.Count;
        public int CheckpointTotal { get; }

        /// <summary>直近に通過したゲートの ID。HUD 表示用。</summary>
        public string LastGateId { get; private set; } = "-";

        // -- フィニッシュ（規約 5.14） ---------------------------------------

        /// <summary>ガレージ。コース図から読む（規約 5.2.4）。無ければ判定しない。</summary>
        public GarageArea Garage;

        /// <summary>ガレージの中で完全停止していなければならない時間 [s]。</summary>
        public const float FinishHoldSeconds = 3f;

        /// <summary>フィニッシュが成立した。成立と同時に競技終了。</summary>
        public bool FinishEstablished { get; private set; }

        /// <summary>走行体の一部がガレージに入っている。</summary>
        public bool InGarage { get; private set; }

        /// <summary>ガレージの中で完全停止している時間 [s]。</summary>
        public float GarageStopSeconds { get; private set; }

        /// <summary>
        /// フィニッシュの判定を 1 ステップ進める。
        ///
        /// 「走行体の一部がガレージエリア内に既定時間（3 秒）間完全停止」。
        /// 侵入後 1 度のみなので、成立したら以後は見ない。
        /// 最大計測時間は模擬していないので、その制限だけは入っていない。
        /// </summary>
        public void StepFinish(Vector2[] footprintMm, bool fullyStopped, float dt)
        {
            if (Garage == null || !Garage.Found || FinishEstablished) return;

            InGarage = Garage.TouchedBy(footprintMm);

            if (InGarage && CompetitionTiming && !GarageEntered)
            {
                GarageEntered = true;
                Debug.Log($"[Etsim] ガレージに入りました。経過 {CompetitionElapsedSeconds:F2} 秒・残り {RemainingSeconds:F2} 秒で数えを止めます");
            }

            if (!InGarage || !fullyStopped)
            {
                GarageStopSeconds = 0f;
                return;
            }

            GarageStopSeconds += dt;
            if (GarageStopSeconds >= FinishHoldSeconds) FinishEstablished = true;
        }

        public LapTimer(List<CourseGate> gates)
        {
            _gates = gates ?? new List<CourseGate>();
            foreach (var g in _gates)
                if (g.Kind == GateKind.Checkpoint) CheckpointTotal++;
        }

        /// <summary>
        /// スタート合図（規約 5.4.5）。走行タイムはここから計る（5.6.1）。
        ///
        /// シミュレータには審判がいないので、スタート操作（フォースセンサを
        /// 押した瞬間）を合図とみなす。実機の競技では合図から操作までに人の
        /// 反応時間が入るぶん、こちらのほうが短く出る。
        /// 操作を繰り返しても計り直さない（5.8）。
        /// </summary>
        public void StartSignal()
        {
            if (Timing || Finished) return;
            Timing = true;
            ElapsedSeconds = 0f;
            CompetitionTiming = true;
            CompetitionElapsedSeconds = 0f;
        }

        public void Reset()
        {
            Running = false;
            Finished = false;
            Timing = false;
            ElapsedSeconds = 0f;
            CompetitionTiming = false;
            CompetitionElapsedSeconds = 0f;
            GarageEntered = false;
            _passedCheckpoints.Clear();
            _hasPrev = false;
            LastGateId = "-";
            FinishEstablished = false;
            InGarage = false;
            GarageStopSeconds = 0f;
        }

        /// <summary>
        /// 1 ステップ進める。footprintMm は走行体の四隅（コース平面、mm）。
        /// NansyoJudge.Footprint が返す配列をそのまま渡してよい
        /// （中身はこちらで控えるので、呼び出し側で使い回して構わない）。
        /// </summary>
        public void Step(Vector2[] footprintMm, float dt)
        {
            if (Timing && !Finished) ElapsedSeconds += dt;
            if (CompetitionTiming && !GarageEntered)
            {
                bool wasUp = TimeUp;
                CompetitionElapsedSeconds += dt;
                if (!wasUp && TimeUp)
                    Debug.Log($"[Etsim] 競技時間 {CompetitionLimitSeconds:F0} 秒が終わりました（残り時間 0）");
            }

            if (footprintMm == null || footprintMm.Length < 4) return;

            if (!_hasPrev)
            {
                Remember(footprintMm);
                _hasPrev = true;
                return;
            }

            foreach (var g in _gates)
            {
                if (!CrossedBy(g, footprintMm)) continue;
                LastGateId = g.Id;

                switch (g.Kind)
                {
                    case GateKind.Start:
                        if (!Running)
                        {
                            Running = true;
                            Finished = false;
                            _passedCheckpoints.Clear();

                            // 合図が無いまま走り出したとき（ゲートだけの試験など）は
                            // ここから計る。合図があったならそちらを優先する。
                            if (!Timing)
                            {
                                Timing = true;
                                ElapsedSeconds = 0f;
                            }
                            if (!CompetitionTiming)
                            {
                                CompetitionTiming = true;
                                CompetitionElapsedSeconds = 0f;
                            }
                        }
                        break;

                    case GateKind.Checkpoint:
                        // 「LAP ゲート到達前に各チェックポイントで 1 度のみ」
                        if (Running && !Finished) _passedCheckpoints.Add(g.Id);
                        break;

                    case GateKind.Goal:
                        // 規約 5.13。チェックポイントを通ったかは条件ではない。
                        // 走行タイムはここで止まる (5.6.1)。
                        if (Running && !Finished)
                        {
                            Finished = true;
                            Running = false;
                            Timing = false;
                        }
                        break;
                }
            }

            Remember(footprintMm);
        }

        void Remember(Vector2[] footprintMm)
        {
            for (int i = 0; i < 4; i++) _prevCorners[i] = footprintMm[i];
        }

        /// <summary>
        /// 走行体の一部が、規定の向きでゲート線分を横切ったか。
        /// 四隅のうち 1 つでも横切れば「一部」が通過したとみなす。
        /// </summary>
        bool CrossedBy(CourseGate g, Vector2[] now)
        {
            for (int i = 0; i < 4; i++)
            {
                Vector2 from = _prevCorners[i];
                Vector2 to = now[i];
                Vector2 move = to - from;
                if (move.sqrMagnitude < 1e-6f) continue;
                if (!SegmentsIntersect(from, to, g.From, g.To)) continue;

                // 向きが指定されていれば、矢印と同じ側へ抜けたときだけ数える
                if (g.Direction.sqrMagnitude > 1e-6f
                    && Vector2.Dot(move, g.Direction) <= 0f) continue;

                return true;
            }
            return false;
        }

        static bool SegmentsIntersect(Vector2 p1, Vector2 p2, Vector2 q1, Vector2 q2)
        {
            float d1 = Cross(q2 - q1, p1 - q1);
            float d2 = Cross(q2 - q1, p2 - q1);
            float d3 = Cross(p2 - p1, q1 - p1);
            float d4 = Cross(p2 - p1, q2 - p1);

            if (((d1 > 0f && d2 < 0f) || (d1 < 0f && d2 > 0f)) &&
                ((d3 > 0f && d4 < 0f) || (d3 < 0f && d4 > 0f)))
                return true;

            // 端点がちょうど線上に乗る縮退ケース
            if (Mathf.Approximately(d1, 0f) && OnSegment(q1, q2, p1)) return true;
            if (Mathf.Approximately(d2, 0f) && OnSegment(q1, q2, p2)) return true;
            if (Mathf.Approximately(d3, 0f) && OnSegment(p1, p2, q1)) return true;
            if (Mathf.Approximately(d4, 0f) && OnSegment(p1, p2, q2)) return true;

            return false;
        }

        static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        static bool OnSegment(Vector2 a, Vector2 b, Vector2 p)
            => Mathf.Min(a.x, b.x) <= p.x && p.x <= Mathf.Max(a.x, b.x)
            && Mathf.Min(a.y, b.y) <= p.y && p.y <= Mathf.Max(a.y, b.y);
    }
}
