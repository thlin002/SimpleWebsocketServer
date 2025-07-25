using System.Text;
using System.Net;
using System.Net.Sockets;

class Server
{
    public static async Task Main()
    {
        IPEndPoint ipEndPoint = new IPEndPoint(
            IPAddress.Any, 8000);

        using Socket listener = new Socket(
            ipEndPoint.AddressFamily,
            SocketType.Stream,
            ProtocolType.Tcp);

        listener.Bind(ipEndPoint);
        listener.Listen(100);
        Console.WriteLine("Listening on {0}:{1}...", ipEndPoint.Address, ipEndPoint.Port);
        Console.WriteLine("Press Ctrl+C to stop the server.");
        Console.WriteLine("Waiting for connections...");

        while (true)
        {
            try
            {
                Socket handler = await listener.AcceptAsync();
                using NetworkStream stream = new NetworkStream(handler, ownsSocket: true);
                Console.WriteLine("Client connected: {0}", handler.RemoteEndPoint);
                Console.WriteLine("Waiting for messages...");
                while (true)
                {
                    // Receive message.
                    while (!stream.DataAvailable) ;
                    while (handler.Available < 3) ; // match against "get"

                    byte[] buffer = new byte[handler.Available];
                    int received = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (received == 0)
                    {
                        // Client disconnected
                        Console.WriteLine("Client disconnected.");
                        break;
                    }

                    var data = Encoding.UTF8.GetString(buffer, 0, received);
                    if (new System.Text.RegularExpressions.Regex("^GET").IsMatch(data))
                    {
                        const string eol = "\r\n"; // HTTP/1.1 defines the sequence CR LF as the end-of-line marker

                        byte[] response = Encoding.UTF8.GetBytes("HTTP/1.1 101 Switching Protocols" + eol
                            + "Connection: Upgrade" + eol
                            + "Upgrade: websocket" + eol
                            + "Sec-WebSocket-Accept: " + Convert.ToBase64String(
                                System.Security.Cryptography.SHA1.Create().ComputeHash(
                                    Encoding.UTF8.GetBytes(
                                        new System.Text.RegularExpressions.Regex("Sec-WebSocket-Key: (.*)").Match(data).Groups[1].Value.Trim() + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"
                                    )
                                )
                            ) + eol
                            + eol);

                        stream.Write(response, 0, response.Length);
                    }
                    else
                    {
                        bool fin = (buffer[0] & 0b10000000) != 0,
                            mask = (buffer[1] & 0b10000000) != 0; // must be true, "All messages from the client to the server have this bit set"
                        int opcode = buffer[0] & 0b00001111; // expecting 1 - text message
                        ulong offset = 2,
                              msgLen = buffer[1] & (ulong)0b01111111;

                        if (msgLen == 126)
                        {
                            // bytes are reversed because websocket will print them in Big-Endian, whereas
                            // BitConverter will want them arranged in little-endian on windows
                            msgLen = BitConverter.ToUInt16(new byte[] { buffer[3], buffer[2] }, 0);
                            offset = 4;
                        }
                        else if (msgLen == 127)
                        {
                            // To test the below code, we need to manually buffer larger messages — since the NIC's autobuffering
                            // may be too latency-friendly for this code to run (that is, we may have only some of the bytes in this
                            // websocket frame available through client.Available).
                            msgLen = BitConverter.ToUInt64(new byte[] { buffer[9], buffer[8], buffer[7], buffer[6], buffer[5], buffer[4], buffer[3], buffer[2] }, 0);
                            offset = 10;
                        }

                        if (msgLen == 0)
                        {
                            Console.WriteLine("msgLen == 0");
                        }
                        else if (mask)
                        {
                            byte[] decoded = new byte[msgLen];
                            byte[] masks = new byte[4] { buffer[offset], buffer[offset + 1], buffer[offset + 2], buffer[offset + 3] };
                            offset += 4;

                            for (ulong i = 0; i < msgLen; ++i)
                                decoded[i] = (byte)(buffer[offset + i] ^ masks[i % 4]);

                            string text = Encoding.UTF8.GetString(decoded);
                            Console.WriteLine("{0}", text);
                        }
                        else
                        {
                            Console.WriteLine("mask bit not set");
                        }
                        Console.WriteLine();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: {0}", ex.Message);
            }
        }
    }
}