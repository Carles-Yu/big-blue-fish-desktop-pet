
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace BigBlueFish.DesktopHooks;

public sealed class DesktopHookServer : IDisposable
{
    public const string HookPath = "/hook";
    const int HeaderLimit = 8 * 1024;
    const int IoTimeoutMs = 2000;

    readonly TcpListener listener;
    readonly CancellationTokenSource cts = new();
    readonly Action<DesktopHookReminder> onReminder;
    readonly byte[] expectedAuthorization;
    int disposed;

    DesktopHookServer(TcpListener listener, string token, int port, Action<DesktopHookReminder> onReminder)
    {
        this.listener = listener;
        Token = token;
        Port = port;
        Url = $"http://127.0.0.1:{port}{HookPath}";
        BoundAddress = ((IPEndPoint)listener.LocalEndpoint).Address;
        this.onReminder = onReminder;
        expectedAuthorization = Encoding.UTF8.GetBytes("Bearer " + token);
    }

    public int Port { get; }
    public string Url { get; }
    public string Token { get; }
    public IPAddress BoundAddress { get; }

    public static DesktopHookServer Start(Action<DesktopHookReminder> onReminder, string? token = null)
    {
        ArgumentNullException.ThrowIfNull(onReminder);
        token = string.IsNullOrWhiteSpace(token)
            ? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()
            : token;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = new DesktopHookServer(listener, token, port, onReminder);
        _ = Task.Run(server.AcceptLoop);
        return server;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        cts.Cancel();
        try { listener.Stop(); } catch { }
        cts.Dispose();
    }

    async Task AcceptLoop()
    {
        while (!cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cts.Token).ConfigureAwait(false);
            }
            catch
            {
                break;
            }
            _ = Task.Run(() => HandleClient(client), CancellationToken.None);
        }
    }

    void HandleClient(TcpClient client)
    {
        using (client)
        {
            try
            {
                if (client.Client.RemoteEndPoint is not IPEndPoint remote || !IPAddress.IsLoopback(remote.Address))
                    return;
                client.ReceiveTimeout = IoTimeoutMs;
                client.SendTimeout = IoTimeoutMs;
                using var stream = client.GetStream();
                var status = ProcessRequest(stream);
                WriteResponse(stream, status);
            }
            catch
            {
            }
        }
    }

    int ProcessRequest(NetworkStream stream)
    {
        if (!TryReadRequest(stream, out var method, out var path, out var authorization, out var body))
            return 400;
        if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) || path != HookPath)
            return 404;
        if (!AuthorizationMatches(authorization))
            return 401;
        if (body.Length > DesktopHookParser.MaxBodyBytes)
            return 413;
        var json = Encoding.UTF8.GetString(body);
        if (DesktopHookParser.TryParse(null, json, null, out var reminder, out _) && reminder != null)
            onReminder(reminder);
        return 204;
    }

    bool AuthorizationMatches(string? header)
    {
        if (string.IsNullOrEmpty(header))
            return false;
        var provided = Encoding.UTF8.GetBytes(header);
        return provided.Length == expectedAuthorization.Length
            && CryptographicOperations.FixedTimeEquals(provided, expectedAuthorization);
    }

    static bool TryReadRequest(NetworkStream stream, out string method, out string path, out string? authorization, out byte[] body)
    {
        method = "";
        path = "";
        authorization = null;
        body = Array.Empty<byte>();
        var headerBytes = ReadUntilHeaders(stream);
        if (headerBytes is null)
            return false;
        var text = Encoding.ASCII.GetString(headerBytes);
        var lines = text.Split("\r\n");
        if (lines.Length == 0)
            return false;
        var parts = lines[0].Split(' ');
        if (parts.Length < 2)
            return false;
        method = parts[0];
        path = parts[1];
        var contentLength = 0;
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                authorization = value;
            else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(value, out contentLength) || contentLength < 0 || contentLength > DesktopHookParser.MaxBodyBytes)
                    return false;
            }
            else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) && !string.Equals(value, "identity", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        if (contentLength == 0)
            return true;
        body = new byte[contentLength];
        var read = 0;
        while (read < contentLength)
        {
            var n = stream.Read(body, read, contentLength - read);
            if (n <= 0)
                return false;
            read += n;
        }
        return true;
    }

    static byte[]? ReadUntilHeaders(NetworkStream stream)
    {
        var buffer = new MemoryStream();
        var one = new byte[1];
        var matched = 0;
        var pattern = "\r\n\r\n"u8;
        while (buffer.Length < HeaderLimit)
        {
            var n = stream.Read(one, 0, 1);
            if (n <= 0)
                return null;
            buffer.Write(one, 0, 1);
            if (one[0] == pattern[matched])
            {
                matched++;
                if (matched == pattern.Length)
                    return buffer.ToArray();
            }
            else
                matched = one[0] == pattern[0] ? 1 : 0;
        }
        return null;
    }

    static void WriteResponse(NetworkStream stream, int status)
    {
        var reason = status switch
        {
            204 => "No Content",
            401 => "Unauthorized",
            404 => "Not Found",
            413 => "Payload Too Large",
            _ => "Bad Request"
        };
        var payload = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
        stream.Write(payload, 0, payload.Length);
        stream.Flush();
    }
}
