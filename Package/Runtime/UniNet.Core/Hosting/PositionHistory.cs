using System;

namespace UniNet.Core.Hosting
{
    /// <summary>
    /// P4 위치 히스토리 — 서버가 오브젝트 위치를 서버 시간축에 기록하고 과거 시점으로 되돌려 질의한다
    /// (래그컴펜세이션 훅 — UE FLagCompensation 위치 히스토리 상응). 기록은 서버 권위 틱에서
    /// UniNetTime.Now 도메인으로 수행하고, 질의는 인접 샘플 선형 보간 + 끝 값 홀드(clamp)다.
    /// </summary>
    public sealed class PositionHistory
    {
        private readonly (double Time, float X, float Y, float Z)[] _ring;
        private readonly double _minInterval;
        private int _head;    // 다음 쓰기 슬롯
        private int _count;

        /// <summary>
        /// capacity는 보유 샘플 상한(≥2), minSampleInterval은 최소 기록 간격(초, 0 = 매 기록 수용).
        /// 간격을 두면 고주사 프레임에서도 창(용량×간격)이 프레임레이트와 무관하게 보장된다 (UE 서버 틱 레이트 상응).
        /// </summary>
        public PositionHistory(int capacity = 128, double minSampleInterval = 0.0)
        {
            if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity), "위치 히스토리는 최소 2 샘플이 필요하다");
            _ring = new (double, float, float, float)[capacity];
            _minInterval = Math.Max(0.0, minSampleInterval);
        }

        /// <summary>서버 틱에서 위치 기록 — 시간 역전·중복·최소 간격 미달 샘플은 폐기해 단조 시간축과 창 보장을 유지한다 (SnapshotBuffer.Add와 동일 계약).</summary>
        public void Record(double time, float x, float y, float z)
        {
            if (_count > 0)
            {
                ref var last = ref _ring[(_head - 1 + _ring.Length) % _ring.Length];
                if (time <= last.Time) return;   // 역전·중복 — 폐기
                if (time - last.Time < _minInterval) return;   // 최소 간격 미달 — 폐기 (창 보장)
            }
            _ring[_head] = (time, x, y, z);
            _head = (_head + 1) % _ring.Length;
            _count = Math.Min(_count + 1, _ring.Length);
        }

        /// <summary>
        /// 과거 시점 위치 질의 — 인접 샘플 선형 보간. 히스토리 범위 밖은 가장 가까운 끝 값 홀드.
        /// 비어 있으면 false (리와인드 불가 — 호출자가 현재 위치로 폴백).
        /// </summary>
        public bool Sample(double time, out float x, out float y, out float z)
        {
            if (_count == 0) { x = y = z = 0; return false; }

            int oldest = (_head - _count + _ring.Length) % _ring.Length;
            for (int i = 1; i < _count; i++)
            {
                var a = _ring[(oldest + i - 1) % _ring.Length];
                var b = _ring[(oldest + i) % _ring.Length];
                if (time <= b.Time)
                {
                    double span = b.Time - a.Time;
                    double alpha = span > 0 ? Math.Min(1.0, Math.Max(0.0, (time - a.Time) / span)) : 1.0;
                    x = (float)(a.X + (b.X - a.X) * alpha);
                    y = (float)(a.Y + (b.Y - a.Y) * alpha);
                    z = (float)(a.Z + (b.Z - a.Z) * alpha);
                    return true;
                }
            }
            ref var latest = ref _ring[(_head - 1 + _ring.Length) % _ring.Length];
            x = latest.X;
            y = latest.Y;
            z = latest.Z;
            return true;
        }

        /// <summary>기록된 가장 오래된 시각 — 리와인드 하한 클램프용. 비어 있으면 false.</summary>
        public bool TryGetOldestTime(out double time)
        {
            if (_count == 0) { time = 0; return false; }
            time = _ring[(_head - _count + _ring.Length) % _ring.Length].Time;
            return true;
        }

        /// <summary>기록된 가장 최근 시각. 비어 있으면 false.</summary>
        public bool TryGetLatestTime(out double time)
        {
            if (_count == 0) { time = 0; return false; }
            time = _ring[(_head - 1 + _ring.Length) % _ring.Length].Time;
            return true;
        }

        /// <summary>버퍼 비우기.</summary>
        public void Clear()
        {
            _head = 0;
            _count = 0;
        }

        /// <summary>현재 보유 샘플 수 (진단용).</summary>
        public int SampleCount => _count;
    }
}
