using MessageProtocol;

namespace Usage
{
    /// <summary>
    /// 데미지 정보 메시지(부모) — RPC 매개변수로 전달되는 [Message] 타입 예제.
    /// [Message]만 붙이면 MP가 계층을 자동 추론한다(파생이 있으므로 Parent 취급).
    /// </summary>
    [Message]
    public partial class DamageMsg
    {
        /// <summary>기본 데미지량.</summary>
        public int Amount { get; set; }
    }
}
