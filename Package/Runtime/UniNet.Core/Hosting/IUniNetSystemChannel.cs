namespace UniNet.Core.Hosting
{
/// <summary>Contract for sending system messages (Welcome, ownership, replication, spawn, destroy) — implemented by the generated server hub.</summary>
public interface IUniNetSystemChannel
{
    /// <summary>Connection ID assigned by the server (set on AttachConnection) — used to identify ServerRpc senders.</summary>
    long UniNetConnId { get; set; }

    /// <summary>Tells a client its own connection ID.</summary>
    void SendWelcome(long connId);

    /// <summary>Notifies clients that an object's owner changed (per object).</summary>
    void SendOwnerUpdate(ulong netId, long ownerConnId);

    /// <summary>Sends a sub-object replication delta (methodId is the generated per-type value).</summary>
    void SendReplicate(ulong netId, byte subId, int methodId, byte[] payload);

    /// <summary>
    /// Sends a dynamic object spawn — the seven transform values place it initially; the prefab id (ADR-0024
    /// stamped NetworkSpawnable GUID hash) tells the client which prefab to instantiate; the sub count plus
    /// per-sub typeKey/state verify composition and seed every NetworkBehaviour's full state
    /// (sub slots are implied by array order).
    /// </summary>
    void SendSpawn(ulong netId, float px, float py, float pz, float qx, float qy, float qz, float qw,
        ulong prefabId, byte subCount, ulong[] typeKeys, byte[][] states);

    /// <summary>Sends a dynamic object destruction (the whole object).</summary>
    void SendDestroy(ulong netId);

    /// <summary>P4 time sync — sends the authoritative server time (UniNetTime domain, seconds) to clients (periodic broadcast).</summary>
    void SendTimeSync(double serverTime);

    /// <summary>
    /// Tells the client why the session is about to end (wire system message 6, ADR-0027) — flushed to the wire
    /// before the caller closes the connection, so the client can distinguish kick/server-shutdown from an
    /// unexplained connection loss. Best effort: a notice lost with a dying connection degrades to ConnectionLost.
    /// </summary>
    System.Threading.Tasks.Task SendDisconnectNoticeAsync(UniNetDisconnectReason reason);

    /// <summary>Sends a generic RPC payload (assembly-agnostic send path).</summary>
    void UniNetSend(int methodId, byte[] payload, DRPC.RpcDeliveryMode mode);
}

}
