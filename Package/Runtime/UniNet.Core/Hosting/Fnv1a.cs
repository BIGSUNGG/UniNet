using System.Text;

namespace UniNet.Core.Hosting
{
    /// <summary>Deterministic FNV-1a hashing — assigns network and method IDs so both sides compute the same
    /// ID from the same string without any coordination.</summary>
    public static class Fnv1a
    {
        /// <summary>Computes the 64-bit FNV-1a hash of a string.</summary>
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

        /// <summary>Computes a 32-bit FNV-1a hash for RPC method IDs. Guarantees a value of 64 or higher
        /// so it never collides with the system-reserved range (0–63).</summary>
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
