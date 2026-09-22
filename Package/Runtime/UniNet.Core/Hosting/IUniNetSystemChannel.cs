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
    /// Sends a dynamic object spawn — the seven transform values place it initially, and the sub count
    /// plus per-sub typeKey/state let the client create and verify every NetworkBehaviour on the object
    /// (sub slots are implied by array order).
    /// </summary>
    void SendSpawn(ulong netId, float px, float py, float pz, float qx, float qy, float qz, float qw,
        byte subCount, ulong[] typeKeys, byte[][] states);

    /// <summary>Sends a dynamic object destruction (the whole object).</summary>
    void SendDestroy(ulong netId);

    /// <summary>P4 time sync — sends the authoritative server time (UniNetTime domain, seconds) to clients (periodic broadcast).</summary>
    void SendTimeSync(double serverTime);

    /// <summary>Sends a generic RPC payload (assembly-agnostic send path).</summary>
    void UniNetSend(int methodId, byte[] payload, DRPC.RpcDeliveryMode mode);
}

}
