using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;

public class MessageReceivedEventArgs : EventArgs
{
    public string ClientId { get; }
    public WebSocketMessageType MessageType { get; }
    public byte[] Data { get; }

    public MessageReceivedEventArgs(string clientId, WebSocketMessageType messageType, byte[] data)
    {
        ClientId = clientId;
        MessageType = messageType;
        Data = data;
    }
}

public class WebSocketServer
{
    public event EventHandler<MessageReceivedEventArgs>? OnMessageReceived;

    private readonly HttpListener _listener = new HttpListener();
    private readonly ConcurrentDictionary<string, WebSocket> _clients = new ConcurrentDictionary<string, WebSocket>();

    public WebSocketServer(string prefix)
    {
        _listener.Prefixes.Add(prefix);
    }

    public async Task SendMessageAsync(string clientId, string message)
    {
        if (_clients.TryGetValue(clientId, out WebSocket? webSocket))
        {
            Byte[] buffer = Encoding.UTF8.GetBytes(message);
            await webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _listener.Start();
        cancellationToken.Register(() => _listener.Stop());

        Console.WriteLine("Server started. Listening for connections...");
        var clientProcessingTasks = new List<Task>();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                HttpListenerContext listenerContext = await _listener.GetContextAsync();
                if (listenerContext.Request.IsWebSocketRequest)
                {
                    // Offload to a new task to keep the listener loop non-blocking
                    Task processingTask = ProcessWebSocketRequestAsync(listenerContext, cancellationToken);
                    clientProcessingTasks.Add(processingTask);
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
                Console.WriteLine("HttpListener has been stopped.");
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"HttpListener loop error: {ex.Message}");
            }
        }
        // The listener loop has exited. Wait for all client tasks to complete.
        await Task.WhenAll(clientProcessingTasks);
        Console.WriteLine("All client processing tasks closed.");
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
            if (webSocket != null && webSocket.State != WebSocketState.Closed)
            {
                // Ensure the WebSocket is closed gracefully if possible
                await webSocket.CloseAsync(WebSocketCloseStatus.InternalServerError, "Server error", CancellationToken.None);
                Console.WriteLine($"WebSocket closed for client {clientId}.");
            }

            if (_clients.TryRemove(clientId, out _))
            {
                Console.WriteLine($"Client disconnected: {clientId}");
            }

            webSocket?.Dispose();
        }
    }

    private async Task ReceiveLoopAsync(WebSocket webSocket, string clientId, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 4];

        // Use a MemoryStream to accumulate the message fragments
        using var ms = new MemoryStream();

        while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            WebSocketReceiveResult result;

            do
            {
                // Await the next chunk of data
                result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                // Only write to the stream if the message isn't a close message.
                if (result.MessageType != WebSocketMessageType.Close)
                {
                    ms.Write(buffer, 0, result.Count);
                }
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                // Client initiated graceful closure
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", cancellationToken);
                break;
            }

            Byte[] messageBytes = ms.ToArray();

            OnMessageReceived?.Invoke(this, new MessageReceivedEventArgs(clientId, result.MessageType, messageBytes));

            // Clear the stream for the new message. It's important to do this before the next receive loop.
            ms.SetLength(0);
            ms.Position = 0;
        }
    }
}

public class MessageHandler
{
    private readonly WebSocketServer _server;

    public MessageHandler(WebSocketServer server)
    {
        _server = server;
    }

    // This is the method that will be called when the server receives a message.
    public void HandleMessage(object? sender, MessageReceivedEventArgs e)
    {
        if (e.MessageType == WebSocketMessageType.Text)
        {
            string receivedMessage = Encoding.UTF8.GetString(e.Data);
            Console.WriteLine($"[MessageHandler] Received TEXT from {e.ClientId}: {receivedMessage}");

            // Echo the message back to the client
            string responseMessage = $"Echo from MessageHandler: {receivedMessage}";
            _server.SendMessageAsync(e.ClientId, responseMessage).Wait();
        }
        else if (e.MessageType == WebSocketMessageType.Binary)
        {
            Console.WriteLine($"[MessageHandler] Received BINARY data from {e.ClientId}: {e.Data.Length} bytes");

            string responseMessage = $"Echo from MessageHandler: Received BINARY data of {e.Data.Length} bytes";
            _server.SendMessageAsync(e.ClientId, responseMessage).Wait();
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

        MessageHandler messageHandler = new MessageHandler(server);
        server.OnMessageReceived += messageHandler.HandleMessage;

        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("Stopping server...");
            cts.Cancel();
        };

        await server.StartAsync(cts.Token);
    }
}