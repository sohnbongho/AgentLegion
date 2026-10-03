using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentLegion.Services
{
    /// <summary>
    /// One interactive process attached to a Windows pseudo console (ConPTY).
    /// Output is kept in a bounded buffer so a browser can re-attach later and see the screen.
    /// </summary>
    public sealed class PtySession : IDisposable
    {
        private const int MaxBufferBytes = 256 * 1024;

        private readonly object _gate = new();
        private readonly List<byte> _buffer = new();
        private readonly List<Action<byte[]>> _listeners = new();

        private IntPtr _hPc;
        private FileStream? _input;
        private FileStream? _output;
        private Process? _process;
        private bool _disposed;

        public bool IsRunning { get; private set; }
        public int? ExitCode { get; private set; }
        public event Action? Exited;

        /// <summary>Starts <paramref name="commandLine"/> in a new pseudo console of the given size.</summary>
        public static PtySession Start(string commandLine, int cols, int rows)
        {
            var s = new PtySession();
            try { s.StartCore(commandLine, cols, rows); }
            catch { s.Dispose(); throw; }
            return s;
        }

        private void StartCore(string commandLine, int cols, int rows)
        {
            if (!NativePty.CreatePipe(out var inRead, out var inWrite, IntPtr.Zero, 0) ||
                !NativePty.CreatePipe(out var outRead, out var outWrite, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var size = new NativePty.COORD { X = (short)cols, Y = (short)rows };
            var hr = NativePty.CreatePseudoConsole(size, inRead, outWrite, 0, out _hPc);
            // ConPTY duplicates what it needs; our copies of its ends must be closed.
            inRead.Dispose();
            outWrite.Dispose();
            if (hr != 0) throw new Win32Exception(hr, "CreatePseudoConsole failed");

            _input = new FileStream(inWrite, FileAccess.Write, 1, false);
            _output = new FileStream(outRead, FileAccess.Read, 1, false);

            var si = new NativePty.STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<NativePty.STARTUPINFOEX>();
            // Null std handles: otherwise a redirected stdout/stdin in *this* process leaks into the child
            // and its output bypasses the pseudo console.
            si.StartupInfo.dwFlags = NativePty.STARTF_USESTDHANDLES;
            var listSize = IntPtr.Zero;
            NativePty.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref listSize);
            si.lpAttributeList = Marshal.AllocHGlobal(listSize);
            try
            {
                if (!NativePty.InitializeProcThreadAttributeList(si.lpAttributeList, 1, 0, ref listSize))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!NativePty.UpdateProcThreadAttribute(si.lpAttributeList, 0, (IntPtr)NativePty.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                        _hPc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                if (!NativePty.CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                        NativePty.EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero, null, ref si, out var pi))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                NativePty.CloseHandle(pi.hThread);
                NativePty.CloseHandle(pi.hProcess);
                _process = Process.GetProcessById(pi.dwProcessId);
                _process.EnableRaisingEvents = true;
                _process.Exited += (_, _) => OnProcessExited();
            }
            finally
            {
                NativePty.DeleteProcThreadAttributeList(si.lpAttributeList);
                Marshal.FreeHGlobal(si.lpAttributeList);
            }

            IsRunning = true;
            var reader = new Thread(ReadLoop) { IsBackground = true, Name = "pty-reader" };
            reader.Start();
            if (_process.HasExited) OnProcessExited();
        }

        private void ReadLoop()
        {
            var buf = new byte[8192];
            try
            {
                int n;
                while (_output is not null && (n = _output.Read(buf, 0, buf.Length)) > 0)
                {
                    var chunk = new byte[n];
                    Array.Copy(buf, chunk, n);
                    Action<byte[]>[] targets;
                    lock (_gate)
                    {
                        _buffer.AddRange(chunk);
                        if (_buffer.Count > MaxBufferBytes) _buffer.RemoveRange(0, _buffer.Count - MaxBufferBytes);
                        targets = _listeners.ToArray();
                    }
                    foreach (var t in targets)
                    {
                        try { t(chunk); } catch { /* a broken listener must not stop the pump */ }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }

        private void OnProcessExited()
        {
            lock (_gate)
            {
                if (!IsRunning) return;
                IsRunning = false;
                try { ExitCode = _process?.ExitCode; } catch { /* not available */ }
            }
            // Closing the pseudo console flushes remaining output and ends the read loop.
            // Run it off this callback: it blocks until the output pipe is drained.
            Task.Run(() =>
            {
                Thread.Sleep(200);
                CloseConsole();
                Exited?.Invoke();
            });
        }

        /// <summary>
        /// Delivers the buffered output to <paramref name="listener"/>, then every later chunk, in order.
        /// The listener runs under an internal lock for the snapshot, so it must only enqueue.
        /// </summary>
        public void Attach(Action<byte[]> listener)
        {
            lock (_gate)
            {
                if (_buffer.Count > 0) listener(_buffer.ToArray());
                _listeners.Add(listener);
            }
        }

        public void Detach(Action<byte[]> listener)
        {
            lock (_gate) _listeners.Remove(listener);
        }

        public void Write(string text)
        {
            if (!IsRunning || _input is null) return;
            var bytes = Encoding.UTF8.GetBytes(text);
            try
            {
                lock (_input)
                {
                    _input.Write(bytes, 0, bytes.Length);
                    _input.Flush();
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }

        public void Resize(int cols, int rows)
        {
            if (!IsRunning || _hPc == IntPtr.Zero || cols <= 0 || rows <= 0) return;
            NativePty.ResizePseudoConsole(_hPc, new NativePty.COORD { X = (short)cols, Y = (short)rows });
        }

        /// <summary>Ends the process tree started by this session.</summary>
        public void Kill()
        {
            try { _process?.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }

        private void CloseConsole()
        {
            IntPtr pc;
            lock (_gate)
            {
                pc = _hPc;
                _hPc = IntPtr.Zero;
            }
            if (pc != IntPtr.Zero) NativePty.ClosePseudoConsole(pc);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Kill();
            CloseConsole();
            _input?.Dispose();
            _output?.Dispose();
            _process?.Dispose();
        }

        private static class NativePty
        {
            public const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
            public const int STARTF_USESTDHANDLES = 0x00000100;
            public const uint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

            [StructLayout(LayoutKind.Sequential)]
            public struct COORD { public short X; public short Y; }

            [StructLayout(LayoutKind.Sequential)]
            public struct STARTUPINFO
            {
                public int cb;
                public IntPtr lpReserved, lpDesktop, lpTitle;
                public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
                public short wShowWindow, cbReserved2;
                public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct STARTUPINFOEX
            {
                public STARTUPINFO StartupInfo;
                public IntPtr lpAttributeList;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct PROCESS_INFORMATION
            {
                public IntPtr hProcess, hThread;
                public int dwProcessId, dwThreadId;
            }

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr attrs, uint size);

            [DllImport("kernel32.dll")]
            public static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint flags, out IntPtr phPC);

            [DllImport("kernel32.dll")]
            public static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

            [DllImport("kernel32.dll")]
            public static extern void ClosePseudoConsole(IntPtr hPC);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value,
                IntPtr cbSize, IntPtr previousValue, IntPtr returnSize);

            [DllImport("kernel32.dll")]
            public static extern void DeleteProcThreadAttributeList(IntPtr list);

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern bool CreateProcess(string? appName, string commandLine, IntPtr procAttrs, IntPtr threadAttrs,
                bool inheritHandles, uint flags, IntPtr env, string? cwd, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool CloseHandle(IntPtr handle);
        }
    }
}
