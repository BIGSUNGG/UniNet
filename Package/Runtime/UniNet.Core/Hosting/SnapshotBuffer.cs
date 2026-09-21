using System;

namespace UniNet.Core.Hosting
{
    /// <summary>
    /// P4 인터폴레이션 버퍼 — 클라가 수신한 상태 스냅샷을 시간축에 적재하고 과거 시점(renderTime)으로
    /// 보간 질의한다. 리모트 오브젝트의 "적용 시점"을 게임이 제어하게 하는 틱 스케줄링 훅의 클라 측 상응물
    /// (UE 클라 보간 버퍼 상응 — 서버 시간보다 InterpolationDelay 만큼 뒤처진 시점으로 렌더).
    /// 값은 유한한 링 버퍼에 적재되며, 범위 밖 질의는 가장 가까운 끝 값을 홀드해 반환한다(스타베이션 시 프리즈).
    /// </summary>
    public sealed class SnapshotBuffer<T>
    {
        private readonly (double Time, T Value)[] _ring;
        private readonly Func<T, T, double, T> _lerp;
        private int _head;    // 다음 쓰기 슬롯
        private int _count;

        /// <summary>capacity는 보유 스냅샷 상한(≥2), lerp는 t∈[0,1] 구간 보간 함수다.</summary>
        public SnapshotBuffer(int capacity, Func<T, T, double, T> lerp)
        {
            if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity), "인터폴레이션 버퍼는 최소 2 샘플이 필요하다");
            _ring = new (double, T)[capacity];
            _lerp = lerp ?? throw new ArgumentNullException(nameof(lerp));
        }

        /// <summary>수신 스냅샷 적재 — 시간 역전(늦게 도착한 과거 스냅샷)은 폐기해 단조 시간축을 유지한다.</summary>
        public void Add(double time, T value)
        {
            if (_count > 0)
            {
                ref readonly var last = ref Peek(-1);
                if (time <= last.Time) return;
            }
            _ring[_head] = (time, value);
            _head = (_head + 1) % _ring.Length;
            _count = Math.Min(_count + 1, _ring.Length);
        }

        /// <summary>가장 최근 스냅샷 시각 — 버퍼가 비어 있으면 false.</summary>
        public bool TryGetLatestTime(out double time)
        {
            if (_count == 0) { time = 0; return false; }
            time = Peek(-1).Time;
            return true;
        }

        /// <summary>
        /// renderTime 시점 보간 값 — 두 스냅샷 사이는 lerp, 범위 밖은 끝 값 홀드. 버퍼가 비어 있으면 false.
        /// </summary>
        public bool TrySample(double renderTime, out T value)
        {
            if (_count == 0) { value = default; return false; }

            int oldest = (_head - _count + _ring.Length) % _ring.Length;
            for (int i = 1; i < _count; i++)
            {
                var (t0, v0) = _ring[(oldest + i - 1) % _ring.Length];
                var (t1, v1) = _ring[(oldest + i) % _ring.Length];
                if (renderTime <= t1)
                {
                    double span = t1 - t0;
                    double alpha = span > 0 ? Math.Min(1.0, Math.Max(0.0, (renderTime - t0) / span)) : 1.0;
                    value = _lerp(v0, v1, alpha);
                    return true;
                }
            }
            value = Peek(-1).Value;   // 최신 이후 — 홀드 (스타베이션 시 프리즈)
            return true;
        }

        /// <summary>버퍼 비우기 (재접속·오브젝트 교체 등).</summary>
        public void Clear()
        {
            _head = 0;
            _count = 0;
        }

        /// <summary>현재 보유 샘플 수 (진단용).</summary>
        public int SampleCount => _count;

        private ref readonly (double Time, T Value) Peek(int offsetFromHead)
        {
            int index = ((_head + offsetFromHead) % _ring.Length + _ring.Length) % _ring.Length;
            return ref _ring[index];
        }
    }
}
