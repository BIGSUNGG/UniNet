using UniNet.Core;
using UniNet.Unity;

namespace UniNet.Tests
{
    /// <summary>채널 예산 검증용 2번 타입 픽스처 — 단일 무조건 필드 (델타 = mask 4바이트 + int 4바이트 = 8바이트).</summary>
    public sealed partial class PulseBeacon : NetworkBehaviour
    {
        [Replicated] public int Charge;
    }
}
