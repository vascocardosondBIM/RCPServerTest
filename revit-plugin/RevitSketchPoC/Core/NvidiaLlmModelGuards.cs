using System;

namespace RevitSketchPoC.Core
{
    /// <summary>
    /// NVIDIA chat-completions slugs vary in vision support; fail fast for models we know are text-only.
    /// Unknown slugs are allowed so new VLMs (e.g. gpt-oss) are not blocked by an incomplete list.
    /// </summary>
    public static class NvidiaLlmModelGuards
    {
        public static void ThrowIfVisionRequiredButModelIsTextOnly(string model, string prefix)
        {
            if (string.IsNullOrWhiteSpace(model))
            {
                return;
            }

            var m = model.Trim();
            if (LooksVisionCapableBySlug(m))
            {
                return;
            }

            if (!IsKnownTextOnlyNvidiaSlug(m))
            {
                return;
            }

            throw new InvalidOperationException(
                prefix +
                "o modelo \"" + m + "\" na API NVIDIA é só texto (sem image_url). " +
                "Escolhe um modelo com visão no catálogo NVIDIA (ex.: slugs com -vl-, llava, gemma-… multimodal, etc.).");
        }

        private static bool LooksVisionCapableBySlug(string model)
        {
            var lower = model.ToLowerInvariant();
            if (lower.Contains("-vl"))
            {
                return true;
            }

            if (lower.Contains("vl-"))
            {
                return true;
            }

            if (lower.Contains("vision"))
            {
                return true;
            }

            if (lower.Contains("llava"))
            {
                return true;
            }

            if (lower.Contains("pixtral"))
            {
                return true;
            }

            if (lower.Contains("gemini"))
            {
                return true;
            }

            if (lower.Contains("gpt-4o"))
            {
                return true;
            }

            if (lower.Contains("gpt-4-turbo"))
            {
                return true;
            }

            // Open-weight GPT-OSS line used on several hosts; treat as vision-capable for guard purposes.
            if (lower.Contains("gpt-oss"))
            {
                return true;
            }

            if (lower.Contains("moondream"))
            {
                return true;
            }

            if (lower.Contains("qwen2-vl") || lower.Contains("qwen2.5-vl"))
            {
                return true;
            }

            if (lower.Contains("nvila"))
            {
                return true;
            }

            return false;
        }

        private static bool IsKnownTextOnlyNvidiaSlug(string model)
        {
            var lower = model.ToLowerInvariant();
            // Common NIM / integrate slugs that reject multimodal image_url payloads.
            if (lower.StartsWith("meta/llama-3.1-", StringComparison.Ordinal) &&
                lower.Contains("-instruct"))
            {
                return true;
            }

            if (string.Equals(lower, "mistralai/mistral-7b-instruct-v0.2", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(lower, "mistralai/mixtral-8x7b-instruct-v0.1", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (lower.StartsWith("deepseek-ai/deepseek-r1", StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }
    }
}
