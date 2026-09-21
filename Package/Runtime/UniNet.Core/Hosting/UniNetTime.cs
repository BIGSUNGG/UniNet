using System;
using System.Diagnostics;

namespace UniNet.Core.Hosting
{
    /// <summary>
    /// P4 시간 기준 — 서버 권위 단조 시계. 서버(권위)는 로컬 시계가 곧 서버 시간이고,
    /// 클라는 TimeSync 수신으로 계산한 오프셋을 더해 서버 시간 역을 얻는다.
    /// 예측·인터폴레이션·리와인드 훅이 모두 이 시계를 공유한다 (UE ReplicatedWorldTimeSeconds 상응).
    /// 시간 도메인이 하나로 묶이므로 리와인드 질의·버퍼 샘플링에 임의의 시각을 넘길 때 변환이 필요 없다.
    /// </summary>
    public static class UniNetTime
    {
        private static readonly Stopwatch Fallback = Stopwatch.StartNew();

        /// <summary>로컬 단조 시계(초). UniNet.Unity가 Unity unscaled 시계로 교체한다 — 코어 테스트는 기본 스톱워치를 쓴다.</summary>
        public static Func<double> LocalClock { get; set; } = () => Fallback.Elapsed.TotalSeconds;

        private static double _serverOffset;   // 서버시간 = 로컬 + 오프셋 (클라만 갱신 — 서버는 0 유지)
        private static double _lastNow;        // 단조 보장 — TimeSync 평활로 Now가 역행하지 않게 한다
        private static bool _synced;
        private static int _receiveCount;

        /// <summary>서버 시간 역 동기화 여부. 서버·호스트는 로컬 시계가 곧 권위 시간이므로 항상 true,
        /// 순수 클라는 TimeSync를 한 번이라도 받으면 true.</summary>
        public static bool IsSynced => _synced || UniNetEnvironment.Server != null;

        /// <summary>TimeSync 수신 횟수 (와이어 도달 관찰자 — 서버·호스트 무시분도 포함, 진단·테스트용).</summary>
        public static int ReceiveCount => _receiveCount;

        /// <summary>현재 서버 도메인 시각(초). 서버는 로컬 시계, 클라는 로컬+동기화 오프셋. 단조 증가가 보장된다 —
        /// 리와인드 질의·인터폴레이션 버퍼가 역행 시각으로 샘플을 놓치는 것을 막는다.</summary>
        public static double Now
        {
            get
            {
                double now = LocalClock() + _serverOffset;
                if (now < _lastNow) now = _lastNow;
                _lastNow = now;
                return now;
            }
        }

        /// <summary>
        /// 클라 — TimeSync 수신 시 오프셋을 갱신한다. 수신 값은 한 방향 지연을 포함하므로 지수 평활(α=0.25)로
        /// 급변을 흡수한다. 최초 수신은 즉시 채택한다.
        /// 서버·호스트는 무시한다 — 로컬 시계가 곧 권위 시간이며, 오프셋이 들어가면 기록 시계(원본)와
        /// 질의 시계(보정)가 어긋나 최신 샘플 질의가 미래 시각이 되는 결함이 생긴다.
        /// </summary>
        public static void ApplyServerTime(double serverTime)
        {
            _receiveCount++;   // 와이어 도달 관찰 — 서버·호스트 무시분도 센다
            if (UniNetEnvironment.Server != null) return;   // 서버·호스트 — 오프셋 없음 (기록·질의 같은 시계)
            double offset = serverTime - LocalClock();
            if (!_synced)
            {
                _serverOffset = offset;
                _synced = true;
                return;
            }
            _serverOffset += (offset - _serverOffset) * 0.25;
        }

        /// <summary>테스트·재시작용 — 동기화 상태 리셋.</summary>
        public static void Reset()
        {
            _serverOffset = 0;
            _lastNow = 0;
            _synced = false;
            _receiveCount = 0;
        }
    }
}
