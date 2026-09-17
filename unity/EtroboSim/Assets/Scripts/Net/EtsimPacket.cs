using System;

namespace Etrobo.Sim.Net
{
    /// <summary>
    /// protocol/etsim_protocol.h と 1 バイト単位で対応する定数とパケット表現。
    /// Marshal に頼らず明示オフセットで読み書きすることで、
    /// C# 側のパディング規則に結果が左右されないようにしている。
    /// </summary>
    public static class Etsim
    {
        public const ushort ProtocolVersion = 1;

        public const uint MagicSensor = 0x53535445u;   // 'E','T','S','S'
        public const uint MagicActuator = 0x41535445u; // 'E','T','S','A'

        public const int SensorPacketSize = 152;
        public const int ActuatorPacketSize = 44;

        public const int DefaultPort = 54001;

        // センサ flags
        public const ushort SFlagRunning = 0x0001;
        public const ushort SFlagLockstep = 0x0002;
        public const ushort SFlagGoal = 0x0004;
        public const ushort SFlagOutOfCourse = 0x0008;
        public const ushort SFlagResetDone = 0x0010;

        // アクチュエータ flags
        public const ushort AFlagLockstep = 0x0001;
        public const ushort AFlagReset = 0x0002;
        public const ushort AFlagShutdown = 0x0004;
        public const ushort AFlagStart = 0x0008;

        public const byte MotorModeCoast = 0;
        public const byte MotorModeBrake = 1;
        public const byte MotorModeHold = 2;
        public const byte MotorModePower = 3;

        public const int MotorLeft = 0;
        public const int MotorRight = 1;
        public const int MotorArm = 2;
        public const int MotorCount = 3;

        public const byte ButtonCenter = 0x01;
        public const byte ButtonLeft = 0x02;
        public const byte ButtonRight = 0x04;
        public const byte ButtonBluetooth = 0x08;

        // pup_color_t 相当
        public const byte ColorNone = 0;
        public const byte ColorBlack = 1;
        public const byte ColorGray = 2;
        public const byte ColorWhite = 3;
        public const byte ColorRed = 4;
        public const byte ColorOrange = 5;
        public const byte ColorYellow = 6;
        public const byte ColorGreen = 7;
        public const byte ColorCyan = 8;
        public const byte ColorBlue = 9;
        public const byte ColorViolet = 10;
        public const byte ColorMagenta = 11;
    }

    /// <summary>Unity -> 制御プロセス。</summary>
    public struct SensorPacket
    {
        public ushort Flags;
        public uint Seq;
        public uint AckSeq;
        public uint SimTimeUs;

        public byte ColorId;
        public int Reflection;
        public int Ambient;
        public ushort R, G, B;        // 0..1024
        public ushort HsvH;           // 0..359
        public byte HsvS, HsvV;       // 0..100

        public int UltrasonicMm;      // -1 = 範囲外
        public byte UltrasonicPresence;
        public byte ForcePressed;
        public float ForceNewton;

        public float YawDeg, PitchDeg, RollDeg;
        public float GyroX, GyroY, GyroZ;
        public float AccelX, AccelY, AccelZ;

        public int CountL, CountR, CountA;
        public int SpeedL, SpeedR, SpeedA;
        public int PowerL, PowerR, PowerA;

        public byte ButtonState;
        public int BatteryMv;

        public float TruthXMm, TruthYMm, TruthHeadingDeg, TruthSpeedMmps;

        public void WriteTo(byte[] buf)
        {
            if (buf == null || buf.Length < Etsim.SensorPacketSize)
                throw new ArgumentException("センサパケット用バッファが小さすぎます", nameof(buf));

            Array.Clear(buf, 0, Etsim.SensorPacketSize);
            var w = new PacketWriter(buf);

            w.U32(0, Etsim.MagicSensor);
            w.U16(4, Etsim.ProtocolVersion);
            w.U16(6, Flags);
            w.U32(8, Seq);
            w.U32(12, AckSeq);
            w.U32(16, SimTimeUs);

            w.U8(20, ColorId);
            // 21..23 パディング
            w.I32(24, Reflection);
            w.I32(28, Ambient);
            w.U16(32, R);
            w.U16(34, G);
            w.U16(36, B);
            w.U16(38, HsvH);
            w.U8(40, HsvS);
            w.U8(41, HsvV);
            // 42..43 パディング

            w.I32(44, UltrasonicMm);
            w.U8(48, UltrasonicPresence);
            w.U8(49, ForcePressed);
            // 50..51 パディング
            w.F32(52, ForceNewton);

            w.F32(56, YawDeg);
            w.F32(60, PitchDeg);
            w.F32(64, RollDeg);
            w.F32(68, GyroX); w.F32(72, GyroY); w.F32(76, GyroZ);
            w.F32(80, AccelX); w.F32(84, AccelY); w.F32(88, AccelZ);

            w.I32(92, CountL); w.I32(96, CountR); w.I32(100, CountA);
            w.I32(104, SpeedL); w.I32(108, SpeedR); w.I32(112, SpeedA);
            w.I32(116, PowerL); w.I32(120, PowerR); w.I32(124, PowerA);

            w.U8(128, ButtonState);
            // 129..131 パディング
            w.I32(132, BatteryMv);

            w.F32(136, TruthXMm);
            w.F32(140, TruthYMm);
            w.F32(144, TruthHeadingDeg);
            w.F32(148, TruthSpeedMmps);
        }
    }

    /// <summary>制御プロセス -> Unity。</summary>
    public struct ActuatorPacket
    {
        public ushort Flags;
        public uint Seq;
        public uint CtrlTimeUs;

        public int PowerL, PowerR, PowerA;
        public byte ModeL, ModeR, ModeA;
        public byte ResetCountMask;
        public byte LedR, LedG, LedB;
        public byte ResetImu;

        public bool Lockstep => (Flags & Etsim.AFlagLockstep) != 0;
        public bool RequestReset => (Flags & Etsim.AFlagReset) != 0;
        public bool Shutdown => (Flags & Etsim.AFlagShutdown) != 0;
        public bool RequestStart => (Flags & Etsim.AFlagStart) != 0;

        /// <summary>
        /// 受信バイト列を解釈する。長さ・マジック・バージョンが合わなければ false。
        /// 外部から届くデータなので、ここを通らない入力は一切信用しない。
        /// </summary>
        public static bool TryParse(byte[] buf, int length, out ActuatorPacket packet)
        {
            packet = default;
            if (buf == null || length < Etsim.ActuatorPacketSize) return false;

            var r = new PacketReader(buf);
            if (r.U32(0) != Etsim.MagicActuator) return false;
            if (r.U16(4) != Etsim.ProtocolVersion) return false;

            packet.Flags = r.U16(6);
            packet.Seq = r.U32(8);
            packet.CtrlTimeUs = r.U32(12);

            packet.PowerL = Clamp100(r.I32(16));
            packet.PowerR = Clamp100(r.I32(20));
            packet.PowerA = Clamp100(r.I32(24));

            packet.ModeL = ClampMode(r.U8(28));
            packet.ModeR = ClampMode(r.U8(29));
            packet.ModeA = ClampMode(r.U8(30));
            packet.ResetCountMask = r.U8(31);

            packet.LedR = r.U8(32);
            packet.LedG = r.U8(33);
            packet.LedB = r.U8(34);
            packet.ResetImu = r.U8(35);
            // 36..40 display, 41..43 パディング （現状は未使用）

            return true;
        }

        static int Clamp100(int v) => v < -100 ? -100 : (v > 100 ? 100 : v);
        static byte ClampMode(byte m) => m > Etsim.MotorModePower ? Etsim.MotorModeCoast : m;
    }

    // float <-> uint のビット変換。unsafe を使わずに済ませるための共用体。
    // (Unity の "Allow unsafe code" 設定に依存させたくない)
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
    struct FloatBits
    {
        [System.Runtime.InteropServices.FieldOffset(0)] public float F;
        [System.Runtime.InteropServices.FieldOffset(0)] public uint U;
    }

    // BitConverter はエンディアン依存かつ毎回配列を作るので、明示的に組み立てる。
    // プロトコルはリトルエンディアン固定。
    readonly struct PacketWriter
    {
        readonly byte[] _b;
        public PacketWriter(byte[] b) { _b = b; }

        public void U8(int o, byte v) { _b[o] = v; }

        public void U16(int o, ushort v)
        {
            _b[o] = (byte)v;
            _b[o + 1] = (byte)(v >> 8);
        }

        public void U32(int o, uint v)
        {
            _b[o] = (byte)v;
            _b[o + 1] = (byte)(v >> 8);
            _b[o + 2] = (byte)(v >> 16);
            _b[o + 3] = (byte)(v >> 24);
        }

        public void I32(int o, int v) => U32(o, unchecked((uint)v));

        public void F32(int o, float v)
        {
            U32(o, new FloatBits { F = v }.U);
        }
    }

    readonly struct PacketReader
    {
        readonly byte[] _b;
        public PacketReader(byte[] b) { _b = b; }

        public byte U8(int o) => _b[o];

        public ushort U16(int o) => (ushort)(_b[o] | (_b[o + 1] << 8));

        public uint U32(int o) =>
            (uint)(_b[o] | (_b[o + 1] << 8) | (_b[o + 2] << 16) | (_b[o + 3] << 24));

        public int I32(int o) => unchecked((int)U32(o));

        public float F32(int o)
        {
            return new FloatBits { U = U32(o) }.F;
        }
    }
}
