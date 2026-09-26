using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace Etrobo.Sim.Net
{
    /// <summary>
    /// 制御プロセスとの UDP 接続。
    ///
    /// 既定でループバックにのみバインドする。外部ネットワークへ開くと
    /// 誰でも走行体を操作できてしまうため、意図的に選ばない限り公開しない。
    ///
    /// 受信は専用スレッドで行い、最新パケットだけを保持する（古い指令は捨てる）。
    /// UDP なので順序は保証されないが、seq で逆転を落としている。
    /// </summary>
    public sealed class SimServer : IDisposable
    {
        readonly object _lock = new object();
        UdpClient _udp;
        Thread _rxThread;
        volatile bool _running;

        IPEndPoint _controllerEndPoint;
        ActuatorPacket _latest;
        bool _hasNew;
        uint _lastSeq;
        long _receivedCount;
        long _lastReceiveTicks;
        volatile int _connectionGeneration;

        readonly byte[] _txBuffer = new byte[Etsim.SensorPacketSize];

        public int Port { get; private set; }
        public bool IsListening => _running;
        public bool ControllerConnected => _controllerEndPoint != null;

        /// <summary>
        /// 接続の世代。相手（ポート）が変わるたびに増える。
        /// 制御プロセスを起動し直したことを、パケットの中身に頼らず知るため。
        /// </summary>
        public int ConnectionGeneration => _connectionGeneration;

        /// <summary>いま指令の送り主として覚えている相手。世代が変わった理由を追うため。</summary>
        public string ControllerPeer
        {
            get { lock (_lock) { return _controllerEndPoint?.ToString() ?? "(なし)"; } }
        }
        public long ReceivedCount => Interlocked.Read(ref _receivedCount);

        /// <summary>最後に指令を受け取ってからの経過秒。未接続なら無限大。</summary>
        public float SecondsSinceLastPacket
        {
            get
            {
                long ticks = Interlocked.Read(ref _lastReceiveTicks);
                if (ticks == 0) return float.PositiveInfinity;
                return (float)(DateTime.UtcNow.Ticks - ticks) / TimeSpan.TicksPerSecond;
            }
        }

        public void Start(int port, bool allowRemote = false)
        {
            Stop();

            Port = port;
            var bindAddress = allowRemote ? IPAddress.Any : IPAddress.Loopback;
            _udp = new UdpClient(new IPEndPoint(bindAddress, port));
            _udp.Client.ReceiveTimeout = 200;

            _running = true;
            _rxThread = new Thread(ReceiveLoop)
            {
                IsBackground = true,
                Name = "EtsimSimServerRx"
            };
            _rxThread.Start();

            Debug.Log($"[Etsim] UDP 待ち受け開始 {bindAddress}:{port}"
                      + (allowRemote ? " (外部ホストからの接続を許可)" : " (ループバックのみ)"));
        }

        void ReceiveLoop()
        {
            var remote = new IPEndPoint(IPAddress.Any, 0);

            while (_running)
            {
                byte[] data;
                try
                {
                    data = _udp.Receive(ref remote);
                }
                catch (SocketException e)
                {
                    // ReceiveTimeout での中断は正常系
                    if (e.SocketErrorCode == SocketError.TimedOut) continue;
                    if (!_running) break;
                    // 相手が落ちた直後の ICMP 由来エラーは無視して待ち受けを続ける
                    if (e.SocketErrorCode == SocketError.ConnectionReset) continue;
                    Debug.LogWarning($"[Etsim] 受信エラー: {e.SocketErrorCode}");
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (!ActuatorPacket.TryParse(data, data.Length, out var packet)) continue;

                lock (_lock)
                {
                    // 相手のポートが変わっていたら、制御プロセスを起動し直したということ。
                    // seq の連続性で判断すると、短い走行のあと繋ぎ直したときに
                    // 「巻き戻り」と区別できず、新しいプロセスのパケットを捨ててしまう。
                    bool samePeer = _controllerEndPoint != null
                                    && _controllerEndPoint.Port == remote.Port
                                    && _controllerEndPoint.Address.Equals(remote.Address);

                    // 同じ相手での seq の巻き戻りは、経路上で追い越された古いパケット。
                    if (samePeer && packet.Seq < _lastSeq && (_lastSeq - packet.Seq) <= 1000u)
                        continue;

                    if (!samePeer)
                    {
                        _controllerEndPoint = new IPEndPoint(remote.Address, remote.Port);
                        _connectionGeneration++;
                    }

                    _lastSeq = packet.Seq;
                    _latest = packet;
                    _hasNew = true;
                }

                Interlocked.Increment(ref _receivedCount);
                Interlocked.Exchange(ref _lastReceiveTicks, DateTime.UtcNow.Ticks);
            }
        }

        /// <summary>
        /// 直近の指令を取り出す。hasNew が false なら前回から更新されていない
        /// （実機でも指令が来なければ前の指令が続くので、値自体は有効）。
        /// </summary>
        public ActuatorPacket TakeLatest(out bool hasNew)
        {
            lock (_lock)
            {
                hasNew = _hasNew;
                _hasNew = false;
                return _latest;
            }
        }

        public void Send(in SensorPacket packet)
        {
            IPEndPoint target;
            lock (_lock) target = _controllerEndPoint;
            if (target == null || _udp == null) return;

            packet.WriteTo(_txBuffer);
            try
            {
                _udp.Send(_txBuffer, _txBuffer.Length, target);
            }
            catch (SocketException)
            {
                // 制御プロセスが落ちた直後。次の指令が来たら送信先が張り直される。
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>制御プロセスが落ちたとみなして送信先を忘れる。</summary>
        public void ForgetController()
        {
            lock (_lock)
            {
                _controllerEndPoint = null;
                _hasNew = false;
                _latest = default;
                _lastSeq = 0;
            }
            Interlocked.Exchange(ref _lastReceiveTicks, 0);
        }

        public void Stop()
        {
            _running = false;

            if (_udp != null)
            {
                try { _udp.Close(); } catch { /* 破棄中の例外は無視 */ }
                _udp = null;
            }

            if (_rxThread != null)
            {
                if (!_rxThread.Join(500)) Debug.LogWarning("[Etsim] 受信スレッドが時間内に停止しませんでした");
                _rxThread = null;
            }

            ForgetController();
        }

        public void Dispose() => Stop();
    }
}
