using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;

public class WebSocketServer
{
    private readonly HttpListener _listener = new HttpListener();
    private readonly ConcurrentDictionary<string, WebSocket> _clients = new ConcurrentDictionary<string, WebSocket>();

    public WebSocketServer(string prefix)
    {
        _listener.Prefixes.Add(prefix);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _listener.Start();
        Console.WriteLine("Server started. Listening for connections...");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                HttpListenerContext listenerContext = await _listener.GetContextAsync();
                if (listenerContext.Request.IsWebSocketRequest)
                {
                    // Offload to a new task to keep the listener loop non-blocking
                    _ = ProcessWebSocketRequestAsync(listenerContext, cancellationToken);
                }
                else
                {
                    listenerContext.Response.StatusCode = 400; // Bad Request
                    listenerContext.Response.Close();
                }
            }
            catch (HttpListenerException)
            {
                // Listener has been stopped.
                Console.WriteLine("Listener has been stopped because of a HttpListenerException.");
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Listener loop error: {ex.Message}");
            }
        }
        _listener.Stop();
        Console.WriteLine("Server stopped.");
    }

    private async Task ProcessWebSocketRequestAsync(HttpListenerContext listenerContext, CancellationToken cancellationToken)
    {
        WebSocket? webSocket = null;
        string clientId = Guid.NewGuid().ToString();

        try
        {
            HttpListenerWebSocketContext webSocketContext = await listenerContext.AcceptWebSocketAsync(subProtocol: null);
            webSocket = webSocketContext.WebSocket;

            _clients.TryAdd(clientId, webSocket);
            Console.WriteLine($"Client connected: {clientId}");

            // The main loop for receiving messages from a client
            await ReceiveLoopAsync(webSocket, clientId, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error processing client {clientId}: {ex.Message}");
        }
        finally
        {
            // Cleanup on disconnect
            if (_clients.TryRemove(clientId, out _))
            {
                Console.WriteLine($"Client disconnected: {clientId}");
            }

            if (webSocket != null && webSocket.State != WebSocketState.Closed)
            {
                // Ensure the WebSocket is closed gracefully if possible
                await webSocket.CloseAsync(WebSocketCloseStatus.InternalServerError, "Server error", CancellationToken.None);
                Console.WriteLine($"WebSocket for client {clientId} closed.");
            }
            webSocket?.Dispose();
        }
    }

    private async Task ReceiveLoopAsync(WebSocket webSocket, string clientId, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 4];
        while (webSocket.State == WebSocketState.Open &&!cancellationToken.IsCancellationRequested)
        {
            WebSocketReceiveResult result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                // Client initiated graceful closure
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", cancellationToken);
                break;
            }
            
            if (result.MessageType == WebSocketMessageType.Text)
            {
                string receivedMessage = Encoding.UTF8.GetString(buffer, 0, result.Count);
                Console.WriteLine($"Received from {clientId}: {receivedMessage}");

                // Echo the message back
                string responseMessage = $"Echo: {receivedMessage}";
                byte[] responseBuffer = Encoding.UTF8.GetBytes(responseMessage);
                await webSocket.SendAsync(new ArraySegment<byte>(responseBuffer), WebSocketMessageType.Text, true, cancellationToken);
            }
        }
    }
}

// Main entry point to run the server
class Server
{
    public static async Task Main()
    {
        var server = new WebSocketServer("http://localhost:8000/");
        var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("Stopping server...");
            cts.Cancel();
        };

        await server.StartAsync(cts.Token);
    }
}