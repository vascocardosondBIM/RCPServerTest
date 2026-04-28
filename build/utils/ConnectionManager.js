import { RevitClientConnection } from "./SocketClient.js";
// Mutex to serialize all Revit connections - prevents race conditions
// when multiple requests are made in parallel
let connectionMutex = Promise.resolve();
/**
 * 连接到Revit客户端并执行操作
 * @param operation 连接成功后要执行的操作函数
 * @returns 操作的结果
 */
export async function withRevitConnection(operation) {
    return withRevitConnectionOptions({}, operation);
}
export async function withRevitConnectionOptions(options, operation) {
    // Wait for any pending connection to complete before starting a new one
    const previousMutex = connectionMutex;
    let releaseMutex;
    connectionMutex = new Promise((resolve) => {
        releaseMutex = resolve;
    });
    await previousMutex;
    const host = options.host || process.env.REVIT_PLUGIN_HOST || "localhost";
    const port = options.port || Number(process.env.REVIT_PLUGIN_PORT || 8080);
    const revitClient = new RevitClientConnection(host, port);
    try {
        // 连接到Revit客户端
        if (!revitClient.isConnected) {
            await new Promise((resolve, reject) => {
                const onConnect = () => {
                    revitClient.socket.removeListener("connect", onConnect);
                    revitClient.socket.removeListener("error", onError);
                    resolve();
                };
                const onError = (error) => {
                    revitClient.socket.removeListener("connect", onConnect);
                    revitClient.socket.removeListener("error", onError);
                    reject(new Error("connect to revit client failed"));
                };
                revitClient.socket.on("connect", onConnect);
                revitClient.socket.on("error", onError);
                revitClient.connect();
                setTimeout(() => {
                    revitClient.socket.removeListener("connect", onConnect);
                    revitClient.socket.removeListener("error", onError);
                    reject(new Error("连接到Revit客户端失败"));
                }, 5000);
            });
        }
        // 执行操作
        return await operation(revitClient);
    }
    finally {
        // 断开连接
        revitClient.disconnect();
        // Release the mutex so the next request can proceed
        releaseMutex();
    }
}
