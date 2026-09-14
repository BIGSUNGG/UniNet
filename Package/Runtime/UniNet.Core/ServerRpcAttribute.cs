using System;

namespace UniNet.Core
{
    /// <summary>클라이언트 → 서버 RPC. 서버에서만 실행된다 (서버 권위).</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class ServerRpcAttribute : Attribute
    {
    }
}
