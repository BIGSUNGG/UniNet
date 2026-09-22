using MessageProtocol;

namespace UniNet.Tests
{
    /// <summary>Verification child message — passed as a parent-typed parameter to prove receiver-side casting.</summary>
    [Message]
    public partial class DerivedPayloadMsg : PayloadMsg
    {
        public int Bonus { get; set; }
    }
}
