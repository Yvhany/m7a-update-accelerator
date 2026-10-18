// 本地更新拦截代理（C# 5 语法，兼容 Windows PowerShell 5.1 的 Add-Type）
// 只做一件事：把 March7th Assistant 的 github.com:443 请求 302 到镜像站，其余域名原样隧道。
//
// 支持多个镜像（--mirror 用逗号分隔，靠前的优先）。后台线程每 3 分钟探一次活，
// 永远选第一个健康的：自己的反代挂了才退到公共镜像站。
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

public static class MirrorProxy
{
    private static string[] _mirrors;
    private static bool[] _healthy;
    private static readonly object _healthLock = new object();
    private static X509Certificate2 _cert;
    private static string _logPath;
    private static readonly object _logLock = new object();

    private const int HealthIntervalSeconds = 180;
    private const int HealthTimeoutMs = 15000;
    private const string HealthPath = "/https://raw.githubusercontent.com/moesnow/March7thAssistant/master/README.md";

    public static void Log(string message)
    {
        try
        {
            lock (_logLock)
            {
                File.AppendAllText(_logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + message + Environment.NewLine);
            }
        }
        catch
        {
        }
    }

    private static string[] ParseMirrors(string spec)
    {
        List<string> list = new List<string>();
        if (spec != null)
        {
            string[] raw = spec.Split(',');
            for (int i = 0; i < raw.Length; i++)
            {
                string item = raw[i].Trim().TrimEnd('/');
                if (item.Length > 0)
                {
                    list.Add(item);
                }
            }
        }
        if (list.Count == 0)
        {
            list.Add("https://gh-proxy.com");
        }
        return list.ToArray();
    }

    public static void Start(int port, string pfxPath, string pfxPassword, string mirror, string logPath)
    {
        // .NET 4.0 的 HttpWebRequest 默认只说 TLS 1.0，Cloudflare 会直接拒绝握手，
        // 导致健康检查永远 DOWN。3072 = Tls12，768 = Tls11
        try
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)(3072 | 768);
        }
        catch
        {
        }

        _mirrors = ParseMirrors(mirror);
        _healthy = new bool[_mirrors.Length];
        for (int i = 0; i < _healthy.Length; i++)
        {
            _healthy[i] = true;
        }
        _logPath = logPath;
        _cert = new X509Certificate2(pfxPath, pfxPassword);

        Log("start listening 127.0.0.1:" + port + " mirrors=" + _mirrors.Length);
        for (int i = 0; i < _mirrors.Length; i++)
        {
            Log("mirror[" + i + "] " + _mirrors[i]);
        }

        Thread health = new Thread(delegate() { HealthLoop(); });
        health.IsBackground = true;
        health.Start();

        TcpListener listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        while (true)
        {
            TcpClient client = listener.AcceptTcpClient();
            Thread thread = new Thread(delegate() { Handle(client); });
            thread.IsBackground = true;
            thread.Start();
        }
    }

    private static void HealthLoop()
    {
        while (true)
        {
            for (int i = 0; i < _mirrors.Length; i++)
            {
                string detail;
                bool ok = CheckHealth(_mirrors[i], out detail);
                bool changed;
                lock (_healthLock)
                {
                    changed = _healthy[i] != ok;
                    _healthy[i] = ok;
                }
                if (changed)
                {
                    Log("health mirror[" + i + "] " + (ok ? "UP" : "DOWN " + detail) + " " + _mirrors[i]);
                }
            }
            Thread.Sleep(HealthIntervalSeconds * 1000);
        }
    }

    private static bool CheckHealth(string mirror, out string detail)
    {
        detail = null;
        try
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(mirror + HealthPath);
            request.Method = "GET";
            request.Timeout = HealthTimeoutMs;
            request.ReadWriteTimeout = HealthTimeoutMs;
            request.AllowAutoRedirect = true;
            request.UserAgent = "m7a-mirror-health";
            request.Proxy = null;
            request.AddRange(0, 63);
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                int code = (int)response.StatusCode;
                if (code >= 200 && code < 400)
                {
                    return true;
                }
                detail = "HTTP " + code;
                return false;
            }
        }
        catch (WebException e)
        {
            HttpWebResponse errorResponse = e.Response as HttpWebResponse;
            detail = errorResponse != null ? ("HTTP " + (int)errorResponse.StatusCode) : e.Status.ToString();
            return false;
        }
        catch (Exception e)
        {
            detail = e.GetType().Name + ": " + e.Message;
            return false;
        }
    }

    private static string PickMirror()
    {
        lock (_healthLock)
        {
            for (int i = 0; i < _mirrors.Length; i++)
            {
                if (_healthy[i])
                {
                    return _mirrors[i];
                }
            }
        }
        return _mirrors[0];
    }

    private static string ReadHead(Stream stream)
    {
        StringBuilder builder = new StringBuilder();
        byte[] buffer = new byte[4096];
        while (builder.Length < 131072)
        {
            int read;
            try
            {
                read = stream.Read(buffer, 0, buffer.Length);
            }
            catch
            {
                break;
            }
            if (read <= 0)
            {
                break;
            }
            builder.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (builder.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal) >= 0)
            {
                break;
            }
        }
        return builder.ToString();
    }

    private static void Write(Stream stream, string text)
    {
        byte[] data = Encoding.ASCII.GetBytes(text);
        stream.Write(data, 0, data.Length);
        stream.Flush();
    }

    private static void Handle(TcpClient client)
    {
        try
        {
            client.NoDelay = true;
            NetworkStream stream = client.GetStream();
            string head = ReadHead(stream);
            if (head.Length == 0)
            {
                client.Close();
                return;
            }

            string[] parts = head.Split('\n')[0].Trim().Split(' ');
            if (parts.Length < 2)
            {
                client.Close();
                return;
            }
            string method = parts[0];
            string target = parts[1];

            if (!string.Equals(method, "CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                Write(stream, "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                client.Close();
                return;
            }

            string host = target;
            int port = 443;
            int colon = target.LastIndexOf(':');
            if (colon > 0)
            {
                host = target.Substring(0, colon);
                int.TryParse(target.Substring(colon + 1), out port);
            }

            Log("CONNECT " + target);
            if (string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase))
            {
                DoIntercept(client, stream, host);
            }
            else
            {
                DoTunnel(client, stream, host, port);
            }
        }
        catch (Exception error)
        {
            Log("error: " + error.Message);
            try { client.Close(); } catch { }
        }
    }

    private static void DoIntercept(TcpClient client, NetworkStream stream, string host)
    {
        Write(stream, "HTTP/1.1 200 Connection Established\r\n\r\n");
        using (SslStream ssl = new SslStream(stream, false))
        {
            try
            {
                ssl.AuthenticateAsServer(_cert, false, SslProtocols.Tls12, false);
            }
            catch (Exception handshakeError)
            {
                Log("tls handshake failed: " + handshakeError.Message);
                return;
            }
            Log("tls ok protocol=" + ssl.SslProtocol.ToString());
            string head = ReadHead(ssl);
            if (head.Length == 0)
            {
                return;
            }
            string[] parts = head.Split('\n')[0].Trim().Split(' ');
            string method = parts.Length > 0 ? parts[0] : "GET";
            string path = parts.Length > 1 ? parts[1] : "/";
            string mirror = PickMirror();
            Log("intercept " + method + " " + path + " via " + mirror);
            string location = mirror + "/https://" + host + path;
            Write(ssl, "HTTP/1.1 302 Found\r\nLocation: " + location + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            Log("302 -> " + location);
        }
        try { client.Close(); } catch { }
    }

    private static void DoTunnel(TcpClient client, NetworkStream stream, string host, int port)
    {
        TcpClient upstream = new TcpClient();
        upstream.NoDelay = true;
        upstream.Connect(host, port);
        NetworkStream upstreamStream = upstream.GetStream();
        Write(stream, "HTTP/1.1 200 Connection Established\r\n\r\n");

        Thread pump = new Thread(delegate()
        {
            try { upstreamStream.CopyTo(stream); } catch { } finally { try { stream.Close(); } catch { } }
        });
        pump.IsBackground = true;
        pump.Start();
        try { stream.CopyTo(upstreamStream); } catch { } finally { try { upstream.Close(); } catch { } }
        pump.Join(2000);
    }
}
