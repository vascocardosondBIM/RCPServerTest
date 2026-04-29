namespace RevitSketchPoC.Chat.Contracts
{
    /// <summary>One chat turn for the LLM API (text + optional image for user turns).</summary>
    public sealed class ChatLlmTurn
    {
        public bool IsUser { get; set; }

        public string Text { get; set; } = string.Empty;

        /// <summary>Optional base64 (no data: prefix) for user image.</summary>
        public string? ImageBase64 { get; set; }

        public string? ImageMimeType { get; set; }
    }
}
