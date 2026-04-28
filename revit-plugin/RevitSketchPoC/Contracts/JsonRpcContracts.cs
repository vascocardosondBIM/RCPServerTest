using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitSketchPoC.Contracts
{
    public sealed class JsonRpcRequest
    {
        [JsonProperty("jsonrpc")]
        public string JsonRpc { get; set; } = "2.0";

        [JsonProperty("method")]
        public string Method { get; set; } = string.Empty;

        [JsonProperty("params")]
        public JToken? Params { get; set; }

        [JsonProperty("id")]
        public JToken? Id { get; set; }
    }

    public sealed class JsonRpcResponse
    {
        [JsonProperty("jsonrpc")]
        public string JsonRpc { get; set; } = "2.0";

        [JsonProperty("result", NullValueHandling = NullValueHandling.Ignore)]
        public object? Result { get; set; }

        [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)]
        public JsonRpcError? Error { get; set; }

        [JsonProperty("id")]
        public object? Id { get; set; }
    }

    public sealed class JsonRpcError
    {
        [JsonProperty("code")]
        public int Code { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; } = string.Empty;
    }
}
