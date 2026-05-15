using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RevitSketchPoC.Phase1_VectorExtraction.Utils
{
    /// <summary>
    /// Gera IDs determinísticos e estáveis entre execuções para a mesma geometria.
    /// </summary>
    public static class StableIdGenerator
    {
        public static string Create(string prefix, params object[] parts)
        {
            using var sha = SHA256.Create();
            var builder = new StringBuilder();
            foreach (var part in parts)
            {
                builder.Append('|');
                builder.Append(Convert.ToString(part, CultureInfo.InvariantCulture) ?? string.Empty);
            }

            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
            var hex = BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            return prefix + "_" + hex.Substring(0, 6);
        }
    }
}
