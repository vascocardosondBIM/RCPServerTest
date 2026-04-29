using Autodesk.Revit.UI;
using RevitSketchPoC.Integration.Contracts;
using RevitSketchPoC.Integration.Routing;
using RevitSketchPoC.Sketch.Services;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace RevitSketchPoC.Integration.Rpc
{
    internal sealed class RevitExternalEventDispatcher : IExternalEventHandler
    {
        private readonly ConcurrentQueue<RpcWorkItem> _queue = new ConcurrentQueue<RpcWorkItem>();
        private readonly McpCommandRouter _router;

        public RevitExternalEventDispatcher(McpCommandRouter router)
        {
            _router = router;
        }

        public Task<JsonRpcResponse> EnqueueAsync(string method, object? id, string? paramsJson)
        {
            var tcs = new TaskCompletionSource<JsonRpcResponse>();
            _queue.Enqueue(new RpcWorkItem
            {
                Method = method,
                Id = id,
                ParamsJson = paramsJson,
                Completion = tcs
            });
            return tcs.Task;
        }

        public void Execute(UIApplication app)
        {
            while (_queue.TryDequeue(out var item))
            {
                try
                {
                    var result = _router.Route(app, item.Method, item.ParamsJson);
                    item.Completion.SetResult(new JsonRpcResponse
                    {
                        Id = item.Id,
                        Result = result
                    });
                }
                catch (Exception ex)
                {
                    item.Completion.SetResult(new JsonRpcResponse
                    {
                        Id = item.Id,
                        Error = new JsonRpcError
                        {
                            Code = -32000,
                            Message = ex.Message
                        }
                    });
                }
            }
        }

        public string GetName()
        {
            return "RevitSketchPoC RPC Dispatcher";
        }

        private sealed class RpcWorkItem
        {
            public string Method { get; set; } = string.Empty;
            public object? Id { get; set; }
            public string? ParamsJson { get; set; }
            public TaskCompletionSource<JsonRpcResponse> Completion { get; set; } = default!;
        }
    }
}
