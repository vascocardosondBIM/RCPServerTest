using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitSketchPoC.Integration.Contracts;
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RevitSketchPoC.Integration.Rpc
{
    internal sealed class TcpJsonRpcServer : IDisposable
    {
        private readonly int _port;
        private readonly RevitExternalEventDispatcher _dispatcher;
        private readonly ExternalEvent _externalEvent;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private TcpListener? _listener;

        public TcpJsonRpcServer(int port, RevitExternalEventDispatcher dispatcher, ExternalEvent externalEvent)
        {
            _port = port;
            _dispatcher = dispatcher;
            _externalEvent = externalEvent;
        }

        public void Start()
        {
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
            _ = Task.Run(ListenLoopAsync);
        }

        private async Task ListenLoopAsync()
        {
            while (!_cts.IsCancellationRequested && _listener != null)
            {
                TcpClient? client = null;
                try
                {
                    client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    _ = Task.Run(() => HandleClientAsync(client));
                }
                catch when (_cts.IsCancellationRequested)
                {
                    client?.Dispose();
                    return;
                }
                catch
                {
                    client?.Dispose();
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var requestJson = await ReadOneJsonAsync(stream).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(requestJson))
                {
                    return;
                }

                JsonRpcResponse response;
                try
                {
                    var request = JsonConvert.DeserializeObject<JsonRpcRequest>(requestJson);
                    if (request == null || string.IsNullOrWhiteSpace(request.Method))
                    {
                        response = new JsonRpcResponse
                        {
                            Id = null,
                            Error = new JsonRpcError { Code = -32600, Message = "Invalid Request" }
                        };
                    }
                    else
                    {
                        var id = ReadId(request.Id);
                        var paramsJson = request.Params != null ? request.Params.ToString(Formatting.None) : null;
                        var pending = _dispatcher.EnqueueAsync(request.Method, id, paramsJson);
                        _externalEvent.Raise();
                        response = await pending.ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    response = new JsonRpcResponse
                    {
                        Id = null,
                        Error = new JsonRpcError { Code = -32700, Message = ex.Message }
                    };
                }

                var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(response));
                await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
        }

        private static async Task<string> ReadOneJsonAsync(NetworkStream stream)
        {
            var buffer = new byte[4096];
            var sb = new StringBuilder();
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                while (!timeout.IsCancellationRequested)
                {
                    var read = await stream.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false);
                    if (read <= 0) break;

                    sb.Append(Encoding.UTF8.GetString(buffer, 0, read));
                    var candidate = sb.ToString();
                    if (TryParseJson(candidate))
                    {
                        return candidate;
                    }
                }
            }
            return sb.ToString();
        }

        private static bool TryParseJson(string input)
        {
            try
            {
                JToken.Parse(input);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static object? ReadId(JToken? id)
        {
            if (id == null) return null;
            if (id.Type == JTokenType.String) return id.Value<string>();
            if (id.Type == JTokenType.Integer) return id.Value<long>();
            return id.ToString(Formatting.None);
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener?.Stop(); } catch { }
            _cts.Dispose();
        }
    }
}
