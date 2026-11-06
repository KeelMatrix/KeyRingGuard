using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace KeyRingGuard.PublicationFixture;

// A deliberately finite V3 upload fixture. It has no outbound client, redirects,
// configurable resource URLs, real credentials, or real publishing destination.
public sealed class LoopbackFeed : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly ConcurrentBag<Task> _clients = new();
    private readonly Task _accept;
    private volatile bool _stopped;
    private int _symbolCount;
    public readonly ConcurrentQueue<Upload> Uploads = new();
    public readonly ConcurrentQueue<string> Errors = new();
    public int PrimaryStatus = 201;
    public int SymbolStatus = 201;
    public string Fault = "";
    public int Port { get; }

    public LoopbackFeed()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accept = AcceptAsync();
    }

    public void Reset(int primaryStatus, int symbolStatus, string fault)
    {
        while (Uploads.TryDequeue(out _)) { }
        while (Errors.TryDequeue(out _)) { }
        _symbolCount = 0;
        PrimaryStatus = primaryStatus;
        SymbolStatus = symbolStatus;
        Fault = fault;
    }

    private async Task AcceptAsync()
    {
        while (!_stopped)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync();
                _clients.Add(Task.Run(() => HandleAsync(client)));
            }
            catch (SocketException) when (_stopped) { }
            catch (ObjectDisposedException) when (_stopped) { }
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 5000;
                using var stream = client.GetStream();
                var header = ReadHeader(stream);
                var lines = header.Split(new[] { "\r\n" }, StringSplitOptions.None);
                var request = lines[0].Split(' ');
                var headers = lines.Skip(1).Where(line => line.Contains(':')).Select(line => line.Split(new[] { ':' }, 2))
                    .ToDictionary(pair => pair[0], pair => pair[1].Trim(), StringComparer.OrdinalIgnoreCase);
                var path = request[1].TrimEnd('/');
                if (request[0] == "GET" && path == "/index.json")
                {
                    var origin = "http://127.0.0.1:" + Port;
                    await RespondAsync(stream, 200, "{\"version\":\"3.0.0\",\"resources\":[{\"@id\":\"" + origin + "/packages\",\"@type\":\"PackagePublish/2.0.0\"},{\"@id\":\"" + origin + "/symbols\",\"@type\":\"SymbolPackagePublish/4.9.0\"}]}");
                    return;
                }
                if (request[0] != "PUT" || (path != "/packages" && path != "/symbols"))
                {
                    Errors.Enqueue("Unexpected method or resource.");
                    await RespondAsync(stream, 400, "unsupported resource");
                    return;
                }
                if (headers.TryGetValue("Expect", out var expect) && expect.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"));
                }
                var body = ReadBody(stream, headers);
                var type = headers["Content-Type"];
                var boundary = type.Substring(type.IndexOf("boundary=", StringComparison.OrdinalIgnoreCase) + 9).Trim('"');
                var start = Find(body, Encoding.ASCII.GetBytes("\r\n\r\n"), 0) + 4;
                var end = Find(body, Encoding.ASCII.GetBytes("\r\n--" + boundary), start);
                if (start < 4 || end < start) { throw new InvalidDataException(); }
                var hash = Convert.ToHexString(SHA256.HashData(body.AsSpan(start, end - start)));
                var symbols = path == "/symbols";
                var status = symbols ? (++_symbolCount == 1 ? SymbolStatus : 409) : PrimaryStatus;
                if (!headers.TryGetValue("X-NuGet-ApiKey", out var key) || key != "local-fixture-only") { status = 401; }
                Uploads.Enqueue(new Upload(path, hash, status));
                if (!symbols && Fault == "lost") { return; }
                if (!symbols && Fault == "delay") { await Task.Delay(4000); }
                await RespondAsync(stream, status, "synthetic response");
            }
            catch (IOException) { /* A timed-out client can close before the fixture responds. */ }
            catch (Exception exception) { Errors.Enqueue(exception.GetType().Name); }
        }
    }

    private static string ReadHeader(Stream stream)
    {
        var bytes = new List<byte>();
        while (bytes.Count < 65536)
        {
            var value = stream.ReadByte();
            if (value < 0) { throw new IOException(); }
            bytes.Add((byte)value);
            var n = bytes.Count;
            if (n >= 4 && bytes[n - 4] == 13 && bytes[n - 3] == 10 && bytes[n - 2] == 13 && bytes[n - 1] == 10)
            { return Encoding.ASCII.GetString(bytes.ToArray(), 0, n - 4); }
        }
        throw new InvalidDataException();
    }

    private static byte[] ReadBody(Stream stream, Dictionary<string, string> headers)
    {
        if (headers.TryGetValue("Content-Length", out var length)) { return ReadExactly(stream, int.Parse(length, System.Globalization.CultureInfo.InvariantCulture)); }
        if (!headers.TryGetValue("Transfer-Encoding", out var encoding) || encoding != "chunked") { throw new InvalidDataException(); }
        using var body = new MemoryStream();
        while (true)
        {
            var line = new StringBuilder();
            int value;
            while ((value = stream.ReadByte()) != 13) { if (value < 0) { throw new IOException(); } line.Append((char)value); }
            if (stream.ReadByte() != 10) { throw new InvalidDataException(); }
            var size = int.Parse(line.ToString().Split(';')[0], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
            if (size == 0) { ReadExactly(stream, 2); break; }
            var chunk = ReadExactly(stream, size);
            body.Write(chunk, 0, chunk.Length);
            ReadExactly(stream, 2);
        }
        return body.ToArray();
    }

    private static byte[] ReadExactly(Stream stream, int length)
    {
        if (length < 0 || length > 10 * 1024 * 1024) { throw new InvalidDataException(); }
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = stream.Read(buffer, offset, length - offset);
            if (read == 0) { throw new IOException(); }
            offset += read;
        }
        return buffer;
    }

    private static int Find(byte[] bytes, byte[] pattern, int start)
    {
        for (var index = start; index <= bytes.Length - pattern.Length; index++)
        { if (bytes.AsSpan(index, pattern.Length).SequenceEqual(pattern)) { return index; } }
        return -1;
    }

    private static async Task RespondAsync(Stream stream, int status, string text)
    {
        var body = Encoding.UTF8.GetBytes(text);
        var header = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " Fixture\r\nContent-Type: application/json\r\nConnection: close\r\nContent-Length: " + body.Length + "\r\n\r\n");
        await stream.WriteAsync(header);
        await stream.WriteAsync(body);
    }

    public void Dispose()
    {
        _stopped = true;
        _listener.Stop();
        _accept.GetAwaiter().GetResult();
        if (!Task.WaitAll(_clients.ToArray(), 6000)) { throw new TimeoutException("Fixture clients did not drain."); }
    }
}

public sealed class Upload(string path, string hash, int status)
{
    public string Path { get; } = path;
    public string Hash { get; } = hash;
    public int Status { get; } = status;
}
