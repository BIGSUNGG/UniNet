using System.Text;

namespace UniNet.Core.Hosting
{
    /// <summary>FNV-1a 해시 — 네트워크 ID·메서드 ID의 결정적 할당에 사용 (양단 같은 코드 → 같은 ID, 무합의).</summary>
    public static class Fnv1a
    {
        /// <summary>64비트 FNV-1a.</summary>
        public static ulong Hash64(string value)
        {
            ulong hash = 14695981039346656037;
            foreach (var c in value)
            {
                hash ^= c;
                hash *= 1099511628211;
            }
            return hash;
        }

        /// <summary>32비트 FNV-1a. RPC 메서드 ID용 — 시스템 예약(0~63)과 겹치지 않게 64 이상을 보장한다.</summary>
        public static int MethodId(string value)
        {
            uint hash = 2166136261;
            foreach (var c in value)
            {
                hash ^= c;
                hash *= 16777619;
            }
            int id = (int)(hash & 0x7FFFFFFF);
            return id < 64 ? id + 64 : id;
        }
    }
}
