namespace UniNet.Core.Hosting
{
    /// <summary>
    /// 스폰 메시지에 실을 변환(위치·회전) 제공 계약 — NetworkBehaviour가 구현한다.
    /// Core는 UnityEngine을 참조하지 않으므로 크로스 레이어 값 전달용 인터페이스다 (IUniNetSystemChannel과 같은 패턴).
    /// </summary>
    public interface IUniNetSpawnTransform
    {
        /// <summary>현재 변환을 스폰 와이어 포맷(단정밀도 7개)으로 출력한다.</summary>
        void GetSpawnTransform(out float px, out float py, out float pz, out float qx, out float qy, out float qz, out float qw);
    }
}
