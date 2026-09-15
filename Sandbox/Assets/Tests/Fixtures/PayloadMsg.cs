using MessageProtocol;

namespace UniNet.Tests
{
    /// <summary>검증용 부모 메시지 — [Message] 자동 계층 추론(파생 존재 → Parent).</summary>
    [Message]
    public partial class PayloadMsg
    {
        public int Value { get; set; }
    }
}
