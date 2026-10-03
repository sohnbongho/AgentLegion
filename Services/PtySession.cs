using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace AgentLegion.Services
{
    public enum SessionState
    {
        Stopped,
        /// <summary>Output is flowing (e.g. Claude's spinner): the agent is busy.</summary>
        Working,
        /// <summary>Running but silent: the agent is waiting for the user.</summary>
        Waiting,
    }

    public enum SessionStateDetection
    {
        /// <summary>Read Claude Code's terminal title (◐/◑ = busy, ✳ = idle).</summary>
        Title,
        /// <summary>Treat continuing output as work. For programs that don't publish a status title.</summary>
        Activity,
    }

    public static class SessionStateExtensions
    {
        public static string Label(this SessionState s) => s switch
        {
            SessionState.Working => "진행 중",
            SessionState.Waiting => "응답 대기",
            _ => "중지",
        };

        public static string Css(this SessionState s) => s switch
        {
            SessionState.Working => "working",
            SessionState.Waiting => "waiting",
            _ => "stopped",
        };
    }

    /// <summary>
    /// One interactive process attached to a Windows pseudo console (ConPTY).
    /// Output is kept in a bounded buffer so a browser can re-attach later and see the screen.
    /// </summary>
    public sealed class PtySession : IDisposable
    {
        private const int MaxBufferBytes = 256 * 1024;

        // How the session decides between Working and Waiting.
        //  Title:    Claude Code puts its status in the terminal title (OSC 0): a rotating glyph while busy, a
        //            static one while idle/asking. That signal is independent of screen repaints, so it is right
        //            even when nobody is viewing the session.
        //  Activity: fallback for other programs: output flowing = working (flaps if an idle program repaints).
        private SessionStateDetection _detection = SessionStateDetection.Title;
        private bool _titleBusy;
        private volatile bool _sawStatusTitle;
        private readonly long _startedAt = Environment.TickCount64;
        // If the program never publishes a status title (titles disabled, or not Claude), don't stay on
        // "Waiting" forever: after this long, fall back to judging by output activity.
        private const long TitleGraceMs = 20_000;

        private const long WorkingWindowMs = 2500; // Activity mode only
        private const long EchoWindowMs = 500;     // keystroke echo is not agent activity
        private const long RepaintWindowMs = 1500; // nor is the repaint after a resize

        private long _lastActivity = Environment.TickCount64;
        private long _ignoreUntil;

        private static readonly Regex OscTitle = new(@"\x1b\][02];([^\x07\x1b]*)(?:\x07|\x1b\\)", RegexOptions.Compiled);
        private string _oscCarry = "";

        /// <summary>What last changed the state (title text or output preview), for diagnostics.</summary>
        public string LastActivityPreview { get; private set; } = "";

        private void TrackActivity(byte[] chunk)
        {
            var text = Encoding.UTF8.GetString(chunk);
            // Always record output activity too: it is the fallback when no status title ever shows up.
            TrackOutputActivity(text);
            if (_detection == SessionStateDetection.Title) TrackTitle(text);
        }

        private void TrackOutputActivity(string text)
        {
            var now = Environment.TickCount64;
            if (now < Volatile.Read(ref _ignoreUntil)) return;
            Volatile.Write(ref _lastActivity, now);
            if (_detection == SessionStateDetection.Activity) LastActivityPreview = Preview(text);
        }

        private void TrackTitle(string text)
        {
            // A title sequence can be split across reads; keep an unfinished one for the next chunk.
            var data = _oscCarry + text;
            foreach (Match m in OscTitle.Matches(data))
            {
                var title = m.Groups[1].Value.TrimStart();
                var busy = ClassifyTitle(title);
                if (busy is null) continue; // not Claude's title (e.g. the wsl.exe window title)
                _titleBusy = busy.Value;
                _sawStatusTitle = true;
                LastActivityPreview = $"title=\"{title}\"";
            }
            var open = data.LastIndexOf("\u001b]", StringComparison.Ordinal);
            var closed = OscTitle.Match(data[(open < 0 ? 0 : open)..]).Success;
            _oscCarry = open >= 0 && !closed && data.Length - open < 512 ? data[open..] : "";
        }

        // Claude Code: "◐ title" / "◑ title" while busy, "✳ title" otherwise. Older builds use braille spinners.
        private static bool? ClassifyTitle(string title)
        {
            if (title.Length == 0) return null;
            var c = title[0];
            if (c is '\u25D0' or '\u25D1' or '\u25D2' or '\u25D3') return true;
            if (c >= '\u2800' && c <= '\u28FF') return true;
            if (c == '\u2733') return false;
            return null;
        }

        private static string Preview(string text)
        {
            var t = text.Replace("\u001b", "<E>").Replace("\r", "\r").Replace("\n", "\n");
            return t.Length > 120 ? t[..120] + "..." : t;
        }

        private readonly object _gate = new();
        private readonly List<byte> _buffer = new();
        private readonly List<Action<byte[]>> _listeners = new();

        private IntPtr _hPc;
        private FileStream? _input;
        private FileStream? _output;
        private Process? _process;
        private bool _disposed;

        public bool IsRunning { get; private set; }

        public SessionState State
        {
            get
            {
                if (!IsRunning) return SessionState.Stopped;
                var titleMode = _detection == SessionStateDetection.Title &&
                                (_sawStatusTitle || Environment.TickCount64 - _startedAt < TitleGraceMs);
                if (titleMode)
                    return Volatile.Read(ref _titleBusy) ? SessionState.Working : SessionState.Waiting;
                var idleMs = Environment.TickCount64 - Volatile.Read(ref _lastActivity);
                return idleMs < WorkingWindowMs ? SessionState.Working : SessionState.Waiting;
            }
        }
        public int? ExitCode { get; private set; }
        public event Action? Exited;

        /// <summary>Starts <paramref name="commandLine"/> in a new pseudo console of the given size.</summary>
        public static PtySession Start(string commandLine, int cols, int rows,
            SessionStateDetection detection = SessionStateDetection.Title)
        {
            var s = new PtySession { _detection = detection };
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
                    TrackActivity(chunk);
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
            Volatile.Write(ref _ignoreUntil, Environment.TickCount64 + EchoWindowMs);
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
            Volatile.Write(ref _ignoreUntil, Environment.TickCount64 + RepaintWindowMs);
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
