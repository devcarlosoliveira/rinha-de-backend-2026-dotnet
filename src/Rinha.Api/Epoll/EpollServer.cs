using System.Collections.Generic;
using System.Runtime.InteropServices;
using Rinha.Core;
using static Rinha.Api.Epoll.Native;

namespace Rinha.Api.Epoll;

/// <summary>
/// Servidor HTTP/1.1 num único event loop epoll (sem thread pool, sem GC no caminho quente).
/// Pinado a um core dedicado (cpuset) ⇒ zero contenção/migração ⇒ latência determinística.
/// Reusa o IVF (E=0) e as respostas pré-serializadas. Robusto: erro ⇒ fallback 200, nunca 5xx.
/// </summary>
internal sealed unsafe class EpollServer
{
    sealed class Conn
    {
        public int Fd;
        public byte[] R = new byte[8192];
        public int RLen;
        public byte[] W = new byte[256];
        public int WPos, WLen;     // bytes pendentes em W[WPos..WLen)
        public bool WantWrite;
    }

    readonly IvfIndex _index;
    readonly int _nLow, _nHigh;
    int _epfd;

    public EpollServer(IvfIndex index, int nLow, int nHigh)
    {
        _index = index; _nLow = nLow; _nHigh = nHigh;
    }

    public void Run(string? socketPath, int port)
    {
        int lfd = socketPath is { Length: > 0 } ? CreateUnixListener(socketPath) : CreateTcpListener(port);
        _epfd = epoll_create1(0);
        if (_epfd < 0) throw new InvalidOperationException("epoll_create1 falhou");
        AddFd(lfd, EPOLLIN);

        var conns = new Dictionary<int, Conn>(1024);
        const int MAXEV = 1024;
        var events = stackalloc EpollEvent[MAXEV];

        while (true)
        {
            int n = epoll_wait(_epfd, events, MAXEV, -1);
            if (n < 0) { if (Marshal.GetLastPInvokeError() == EINTR) continue; break; }

            for (int i = 0; i < n; i++)
            {
                int fd = (int)events[i].Data;
                uint ev = events[i].Events;

                if (fd == lfd) { AcceptLoop(lfd, conns); continue; }
                if (!conns.TryGetValue(fd, out var c)) continue;

                if ((ev & (EPOLLHUP | EPOLLERR)) != 0) { CloseConn(c, conns); continue; }
                if ((ev & EPOLLOUT) != 0 && c.WantWrite) { if (!Flush(c)) { CloseConn(c, conns); continue; } }
                if ((ev & (EPOLLIN | EPOLLRDHUP)) != 0) { if (!OnReadable(c, conns)) continue; }
            }
        }
    }

    void AcceptLoop(int lfd, Dictionary<int, Conn> conns)
    {
        while (true)
        {
            int cfd = accept4(lfd, null, null, SOCK_NONBLOCK | SOCK_CLOEXEC);
            if (cfd < 0) break; // EAGAIN: sem mais conexões
            var c = new Conn { Fd = cfd };
            conns[cfd] = c;
            AddFd(cfd, EPOLLIN | EPOLLRDHUP);
        }
    }

    // Retorna false se a conexão foi fechada (removida).
    bool OnReadable(Conn c, Dictionary<int, Conn> conns)
    {
        while (true)
        {
            if (c.RLen == c.R.Length) Array.Resize(ref c.R, c.R.Length * 2);
            nint r;
            fixed (byte* p = c.R) r = read(c.Fd, p + c.RLen, (nuint)(c.R.Length - c.RLen));
            if (r > 0) { c.RLen += (int)r; ProcessBuffer(c); if (c.RLen == c.R.Length) continue; break; }
            if (r == 0) { CloseConn(c, conns); return false; } // EOF
            int err = Marshal.GetLastPInvokeError();
            if (err == EINTR) continue;
            if (err == EAGAIN) break;
            CloseConn(c, conns); return false;
        }
        if (c.WantWrite && !Flush(c)) { CloseConn(c, conns); return false; }
        return true;
    }

    // Consome requests completas de R[0..RLen), enfileira respostas em W e tenta escrever.
    void ProcessBuffer(Conn c)
    {
        int start = 0;
        while (true)
        {
            int avail = c.RLen - start;
            if (avail <= 0) break;
            int he = FindHeaderEnd(c.R, start, c.RLen);
            if (he < 0) break;
            bool isPost = c.R[start] == (byte)'P';
            int cl = isPost ? ParseContentLength(c.R, start, he) : 0;
            int total = (he - start) + cl;
            if (avail < total) break; // request incompleta

            byte[] resp = isPost ? Process(c.R.AsSpan(he, cl)) : Responses.HttpReady;
            EnqueueWrite(c, resp);
            start += total;
        }
        if (start > 0)
        {
            Array.Copy(c.R, start, c.R, 0, c.RLen - start);
            c.RLen -= start;
        }
    }

    byte[] Process(ReadOnlySpan<byte> body)
    {
        try
        {
            var fr = PayloadParser.Parse(body);
            Span<float> v = stackalloc float[Vectorizer.Dims];
            Vectorizer.Vectorize(fr, v);
            var res = _index.SearchAdaptive(v, _nLow, _nHigh, out _);
            int fc = (int)MathF.Round(res.Score * 5f);
            return Responses.HttpByFraudCount[fc];
        }
        catch { return Responses.HttpFallback; }
    }

    void EnqueueWrite(Conn c, byte[] resp)
    {
        int need = c.WLen + resp.Length;
        if (need > c.W.Length) { int s = c.W.Length; while (s < need) s *= 2; Array.Resize(ref c.W, s); }
        Array.Copy(resp, 0, c.W, c.WLen, resp.Length);
        c.WLen += resp.Length;
        Flush(c);
    }

    // Escreve W[WPos..WLen). Retorna false em erro fatal de socket.
    bool Flush(Conn c)
    {
        while (c.WPos < c.WLen)
        {
            nint w;
            fixed (byte* p = c.W) w = write(c.Fd, p + c.WPos, (nuint)(c.WLen - c.WPos));
            if (w > 0) { c.WPos += (int)w; continue; }
            int err = Marshal.GetLastPInvokeError();
            if (err == EINTR) continue;
            if (err == EAGAIN) { if (!c.WantWrite) { c.WantWrite = true; ModFd(c.Fd, EPOLLIN | EPOLLRDHUP | EPOLLOUT); } return true; }
            return false;
        }
        c.WPos = 0; c.WLen = 0;
        if (c.WantWrite) { c.WantWrite = false; ModFd(c.Fd, EPOLLIN | EPOLLRDHUP); }
        return true;
    }

    void CloseConn(Conn c, Dictionary<int, Conn> conns)
    {
        EpollEvent dummy = default;
        epoll_ctl(_epfd, EPOLL_CTL_DEL, c.Fd, &dummy);
        close(c.Fd);
        conns.Remove(c.Fd);
    }

    // ---- helpers epoll ----
    void AddFd(int fd, uint ev) { var e = new EpollEvent { Events = ev, Data = (ulong)fd }; epoll_ctl(_epfd, EPOLL_CTL_ADD, fd, &e); }
    void ModFd(int fd, uint ev) { var e = new EpollEvent { Events = ev, Data = (ulong)fd }; epoll_ctl(_epfd, EPOLL_CTL_MOD, fd, &e); }

    // ---- listeners ----
    static int CreateUnixListener(string path)
    {
        int fd = socket(AF_UNIX, SOCK_STREAM | SOCK_NONBLOCK | SOCK_CLOEXEC, 0);
        if (fd < 0) throw new InvalidOperationException("socket(AF_UNIX) falhou");
        var bytes = System.Text.Encoding.UTF8.GetBytes(path);
        fixed (byte* bp = bytes) unlink(bp);
        var addr = new SockAddrUn { sun_family = AF_UNIX };
        for (int i = 0; i < bytes.Length && i < 107; i++) addr.sun_path[i] = bytes[i];
        if (bind(fd, &addr, (uint)(2 + bytes.Length + 1)) < 0) throw new InvalidOperationException("bind UDS falhou");
        fixed (byte* bp = bytes) chmod(bp, 0b110_110_110); // 0666
        if (listen(fd, 512) < 0) throw new InvalidOperationException("listen falhou");
        return fd;
    }

    static int CreateTcpListener(int port)
    {
        int fd = socket(AF_INET, SOCK_STREAM | SOCK_NONBLOCK | SOCK_CLOEXEC, 0);
        if (fd < 0) throw new InvalidOperationException("socket(AF_INET) falhou");
        int one = 1; setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &one, 4);
        var addr = new SockAddrIn
        {
            sin_family = AF_INET,
            sin_port = (ushort)((port << 8) | (port >> 8)), // htons
            sin_addr = 0, // INADDR_ANY
        };
        if (bind(fd, &addr, (uint)sizeof(SockAddrIn)) < 0) throw new InvalidOperationException("bind TCP falhou");
        if (listen(fd, 512) < 0) throw new InvalidOperationException("listen falhou");
        return fd;
    }

    // ---- parsing HTTP (sobre R[start..len)) ----
    static int FindHeaderEnd(byte[] b, int start, int len)
    {
        for (int i = start; i + 3 < len; i++)
            if (b[i] == 13 && b[i + 1] == 10 && b[i + 2] == 13 && b[i + 3] == 10) return i + 4;
        return -1;
    }

    static readonly byte[] CLKey = "content-length:"u8.ToArray();

    static int ParseContentLength(byte[] b, int start, int headerEnd)
    {
        for (int i = start; i + CLKey.Length <= headerEnd; i++)
        {
            bool m = true;
            for (int k = 0; k < CLKey.Length; k++)
            {
                byte ch = b[i + k];
                if (ch >= 'A' && ch <= 'Z') ch = (byte)(ch + 32);
                if (ch != CLKey[k]) { m = false; break; }
            }
            if (!m) continue;
            int j = i + CLKey.Length;
            while (j < headerEnd && (b[j] == ' ' || b[j] == '\t')) j++;
            int v = 0;
            while (j < headerEnd && b[j] >= '0' && b[j] <= '9') { v = v * 10 + (b[j] - '0'); j++; }
            return v;
        }
        return 0;
    }
}
