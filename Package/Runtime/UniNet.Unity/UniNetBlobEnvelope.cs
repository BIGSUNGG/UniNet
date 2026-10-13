using MessageProtocol;

namespace UniNet.Unity
{
    /// <summary>
    /// Wire envelope carrying a game-serialized movement payload as raw bytes (ADR-0038). Two uses:
    /// (1) the movement input ServerRpc parameter — RPC parameters support primitives and [Message] types only,
    /// so the game's typed input struct (any value fields) is serialized by its own Write/Read delegates
    /// (the ADR-0020 custom serializer contract) and rides this envelope;
    /// (2) the acknowledged movement state replicated OwnerOnly back to the predicting client — a new envelope
    /// instance per changed state makes the reference-equality delta detector send exactly one full replace.
    /// byte[] members serialize as length + contents (MessageProtocol raw data).
    /// Lives in UniNet.Unity (not Core) because the MessageProtocol source generator only covers assemblies
    /// at or below the analyzer's own package folder.
    /// </summary>
    [Message]
    public partial class UniNetBlobEnvelope
    {
        /// <summary>Serialized payload — exactly the bytes the game's writer produced.</summary>
        public byte[] Data { get; set; }
    }
}
