using System.Runtime.InteropServices;

namespace Rinha.Api.Epoll;

/// <summary>
/// Bindings de syscalls Linux para o event loop epoll mono-thread (P/Invoke cru, AOT-safe).
/// io_uring está bloqueado pelo seccomp padrão do Docker ⇒ epoll é o teto prático de IO.
/// </summary>
internal static unsafe class Native
{
    // ---- epoll ----
    public const int EPOLL_CTL_ADD = 1, EPOLL_CTL_DEL = 2, EPOLL_CTL_MOD = 3;
    public const uint EPOLLIN = 0x001, EPOLLOUT = 0x004, EPOLLERR = 0x008, EPOLLHUP = 0x010;
    public const uint EPOLLRDHUP = 0x2000, EPOLLET = 0x80000000;

    // epoll_event é PACKED no x86_64 (12 bytes: 4 + 8, sem padding).
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct EpollEvent
    {
        public uint Events;
        public ulong Data; // usamos como fd
    }

    [DllImport("libc", SetLastError = true)] public static extern int epoll_create1(int flags);
    [DllImport("libc", SetLastError = true)] public static extern int epoll_ctl(int epfd, int op, int fd, EpollEvent* ev);
    [DllImport("libc", SetLastError = true)] public static extern int epoll_wait(int epfd, EpollEvent* events, int maxevents, int timeout);

    // ---- sockets ----
    public const int AF_UNIX = 1, AF_INET = 2;
    public const int SOCK_STREAM = 1, SOCK_NONBLOCK = 0x800, SOCK_CLOEXEC = 0x80000;
    public const int SOL_SOCKET = 1, SO_REUSEADDR = 2;
    public const int EAGAIN = 11, EINTR = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct SockAddrUn
    {
        public ushort sun_family;
        public fixed byte sun_path[108];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SockAddrIn
    {
        public ushort sin_family;
        public ushort sin_port;   // big-endian
        public uint sin_addr;     // big-endian
        public ulong _pad;
    }

    [DllImport("libc", SetLastError = true)] public static extern int socket(int domain, int type, int protocol);
    [DllImport("libc", SetLastError = true)] public static extern int bind(int fd, void* addr, uint addrlen);
    [DllImport("libc", SetLastError = true)] public static extern int listen(int fd, int backlog);
    [DllImport("libc", SetLastError = true)] public static extern int accept4(int fd, void* addr, uint* addrlen, int flags);
    [DllImport("libc", SetLastError = true)] public static extern int setsockopt(int fd, int level, int optname, void* optval, uint optlen);
    [DllImport("libc", SetLastError = true)] public static extern nint read(int fd, void* buf, nuint count);
    [DllImport("libc", SetLastError = true)] public static extern nint write(int fd, void* buf, nuint count);
    [DllImport("libc", SetLastError = true)] public static extern int close(int fd);
    [DllImport("libc", SetLastError = true)] public static extern int unlink(byte* path);
    [DllImport("libc", SetLastError = true)] public static extern int chmod(byte* path, uint mode);

    // ---- memória (mlock + huge pages) ----
    public const int MCL_CURRENT = 1, MCL_FUTURE = 2;
    public const int MADV_HUGEPAGE = 14, MADV_WILLNEED = 3;
    [DllImport("libc", SetLastError = true)] public static extern int mlock(void* addr, nuint len);
    [DllImport("libc", SetLastError = true)] public static extern int mlockall(int flags);
    [DllImport("libc", SetLastError = true)] public static extern int madvise(void* addr, nuint len, int advice);

    // ---- afinidade de CPU (pin no core dedicado pelo cpuset) ----
    [DllImport("libc", SetLastError = true)] public static extern int sched_setaffinity(int pid, nuint cpusetsize, void* mask);
}
