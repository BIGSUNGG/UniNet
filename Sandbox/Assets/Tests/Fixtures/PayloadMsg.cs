using MessageProtocol;

namespace UniNet.Tests
{
    /// <summary>Verification parent message — [Message] infers the hierarchy automatically (derived type exists → Parent).</summary>
    [Message]
    public partial class PayloadMsg
    {
        public int Value { get; set; }
    }
}
