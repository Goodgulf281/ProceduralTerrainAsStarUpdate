// ============================================================
// Script:      StableHash.cs
// Description: Deterministic string hashing (FNV-1a, 32-bit).
//              Unlike string.GetHashCode() or Object.GetInstanceID(),
//              the result is identical across processes, platforms,
//              editor and builds. Required so that every machine in a
//              networked session places the same prefabs from a seed.
// Author:      Goodgulf
// ============================================================

namespace Goodgulf.TerrainUtils
{
    /// <summary>
    /// Deterministic hashing helpers for seed and cache-key generation.
    /// </summary>
    public static class StableHash
    {
        private const uint FnvOffsetBasis = 2166136261;
        private const uint FnvPrime       = 16777619;

        /// <summary>
        /// Returns the 32-bit FNV-1a hash of <paramref name="text"/> over its UTF-16 code units.
        /// A null string hashes to the offset basis.
        /// </summary>
        public static int Fnv1a(string text)
        {
            uint hash = FnvOffsetBasis;

            if (text != null)
            {
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    hash = (hash ^ (byte)c) * FnvPrime;
                    hash = (hash ^ (byte)(c >> 8)) * FnvPrime;
                }
            }

            return unchecked((int)hash);
        }
    }
}
