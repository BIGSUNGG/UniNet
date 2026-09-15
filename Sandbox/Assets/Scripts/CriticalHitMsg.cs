using MessageProtocol;

namespace Usage
{
    /// <summary>
    /// 치명타 메시지(자식) — DamageMsg 파라미터에 이 인스턴스를 넣어 보내면
    /// 수신측에서 (CriticalHitMsg) 캐스팅으로 확장 필드까지 온전히 받는다 (다형성 공식 지원).
    /// </summary>
    [Message]
    public partial class CriticalHitMsg : DamageMsg
    {
        /// <summary>치명타 배율.</summary>
        public float Multiplier { get; set; } = 2f;
    }
}
