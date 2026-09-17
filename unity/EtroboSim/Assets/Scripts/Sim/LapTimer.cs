using System.Collections.Generic;
using UnityEngine;

namespace Etrobo.Sim
{
    /// <summary>
    /// 計測ゲートの通過判定とタイム計測。
    ///
    /// 走行体の位置を毎ステップ「前回位置 -> 今回位置」の線分として扱い、
    /// ゲート線分との交差で判定する。点が範囲に入ったかを見る方式だと
    /// 高速通過時に取りこぼすため。
    /// </summary>
    public sealed class LapTimer
    {
        readonly List<CourseGate> _gates;
        readonly HashSet<string> _passedCheckpoints = new HashSet<string>();

        Vector2 _prevPositionMm;
        bool _hasPrev;

        public bool Running { get; private set; }
        public bool Finished { get; private set; }
        public float ElapsedSeconds { get; private set; }
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

        public void Reset()
        {
            Running = false;
            Finished = false;
            ElapsedSeconds = 0f;
            _passedCheckpoints.Clear();
            _hasPrev = false;
            LastGateId = "-";
            FinishEstablished = false;
            InGarage = false;
            GarageStopSeconds = 0f;
        }

        public void Step(Vector2 positionMm, float dt)
        {
            if (Running && !Finished) ElapsedSeconds += dt;

            if (!_hasPrev)
            {
                _prevPositionMm = positionMm;
                _hasPrev = true;
                return;
            }

            Vector2 from = _prevPositionMm;
            Vector2 to = positionMm;
            _prevPositionMm = positionMm;

            if ((to - from).sqrMagnitude < 1e-6f) return;

            foreach (var g in _gates)
            {
                if (!SegmentsIntersect(from, to, g.From, g.To)) continue;
                LastGateId = g.Id;

                switch (g.Kind)
                {
                    case GateKind.Start:
                        if (!Running)
                        {
                            Running = true;
                            Finished = false;
                            ElapsedSeconds = 0f;
                            _passedCheckpoints.Clear();
                        }
                        break;

                    case GateKind.Checkpoint:
                        if (Running) _passedCheckpoints.Add(g.Id);
                        break;

                    case GateKind.Goal:
                        // 全チェックポイントを通っていなければゴールとみなさない。
                        // スタート直後にゴール線をまたいで即完走になるのを防ぐ。
                        if (Running && !Finished && _passedCheckpoints.Count >= CheckpointTotal)
                        {
                            Finished = true;
                            Running = false;
                        }
                        break;
                }
            }
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
