using MessageProtocol;

namespace UniNet.Tests
{
    /// <summary>검증용 자식 메시지 — 부모 선언 파라미터로 전달해 수신측 캐스팅을 증명한다.</summary>
    [Message]
    public partial class DerivedPayloadMsg : PayloadMsg
    {
        public int Bonus { get; set; }
    }
}
