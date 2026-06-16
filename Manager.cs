using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace g_Manager
{
    public sealed class ConsoleOutputEventArgs : EventArgs
    {
        public string Text { get; private set; }

        public ConsoleOutputEventArgs(string text)
        {
            Text = text ?? string.Empty;
        }
    }

    public sealed class ConsoleProcessExitedEventArgs : EventArgs
    {
        public int? ExitCode { get; private set; }

        public bool WasExpected { get; private set; }

        public ConsoleProcessExitedEventArgs(int? exitCode, bool wasExpected)
        {
            ExitCode = exitCode;
            WasExpected = wasExpected;
        }
    }

    public sealed class Manager : IDisposable
    {
        private readonly object _syncLock = new object();
        private readonly Settings _settings;

        private Process _process;
        private bool _shutdownRequested;
        private bool _disposed;

        public event EventHandler<ConsoleOutputEventArgs> OutputReceived;
        public event EventHandler<ConsoleOutputEventArgs> ErrorReceived;
        public event EventHandler<ConsoleProcessExitedEventArgs> ProcessExited;

        public Manager(Settings settings)
        {
            if (settings == null)
                throw new ArgumentNullException("settings");

            _settings = settings;
        }

        public bool IsRunning
        {
            get
            {
                lock (_syncLock)
                {
                    return IsProcessRunningNoLock();
                }
            }
        }

        public void Start()
        {
            ThrowIfDisposed();

            lock (_syncLock)
            {
                if (IsProcessRunningNoLock())
                    throw new InvalidOperationException("The target process is already running.");

                _settings.Load();

                ValidateSettingsBeforeStart(_settings);

                string filePath = _settings.FilePath;
                string arguments = _settings.Arguments ?? string.Empty;
                string workingDirectory = Path.GetDirectoryName(filePath);

                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = filePath;
                startInfo.Arguments = arguments;
                startInfo.WorkingDirectory = workingDirectory;

                // Do not redirect stdin/stdout/stderr. The server reads from its real
                // Windows console input buffer, not from StandardInput.
                startInfo.UseShellExecute = false;
                startInfo.RedirectStandardInput = false;
                startInfo.RedirectStandardOutput = false;
                startInfo.RedirectStandardError = false;
                startInfo.CreateNoWindow = false;

                Process process = new Process();
                process.StartInfo = startInfo;
                process.EnableRaisingEvents = true;
                process.Exited += Process_Exited;

                _shutdownRequested = false;
                _process = process;

                try
                {
                    bool started = process.Start();

                    if (!started)
                        throw new InvalidOperationException("The process did not start.");

                    RaiseOutput("[wrapper] Started process: " + filePath);
                }
                catch
                {
                    _process = null;
                    process.Exited -= Process_Exited;
                    process.Dispose();
                    throw;
                }
            }
        }

        public void SendCommand(string command)
        {
            ThrowIfDisposed();

            if (string.IsNullOrWhiteSpace(command))
                return;

            Process process = GetRunningProcessOrThrow();
            SendCommandToProcess(process, command);
        }

        public void Shutdown()
        {
            ThrowIfDisposed();

            _shutdownRequested = true;
            SendCommand("Shutdown");
        }

        public async Task Reload()
        {
            ThrowIfDisposed();

            Process processToWaitFor = GetRunningProcessOrThrow();

            _shutdownRequested = true;
            SendCommandToProcess(processToWaitFor, "Shutdown");

            RaiseOutput("[wrapper] Reload requested. Waiting for process to exit...");

            await Task.Run(delegate
            {
                processToWaitFor.WaitForExit();
            });

            RaiseOutput("[wrapper] Process exited. Starting again...");

            Start();
        }

        public async Task StopOrKillIfNeeded()
        {
            await StopOrKillIfNeeded(30000);
        }

        public async Task StopOrKillIfNeeded(int gracefulShutdownMilliseconds)
        {
            ThrowIfDisposed();

            Process processToStop;

            lock (_syncLock)
            {
                if (!IsProcessRunningNoLock())
                    return;

                processToStop = _process;
                _shutdownRequested = true;
            }

            try
            {
                SendCommandToProcess(processToStop, "Shutdown");
                RaiseOutput("[wrapper] Shutdown command sent. Waiting for graceful exit...");
            }
            catch
            {
                // The process may already be closing.
            }

            bool exited = await Task.Run(delegate
            {
                return processToStop.WaitForExit(gracefulShutdownMilliseconds);
            });

            if (exited)
                return;

            RaiseError("[wrapper] Process did not exit in time. Killing process...");

            try
            {
                processToStop.Kill();

                await Task.Run(delegate
                {
                    processToStop.WaitForExit();
                });
            }
            catch (Exception ex)
            {
                RaiseError("[wrapper] Failed to kill process: " + ex.Message);
            }
        }

        private void SendCommandToProcess(Process process, string command)
        {
            try
            {
                ConsoleInputInjector.WriteLine(process, command);
            }
            catch (Win32Exception ex)
            {
                throw new InvalidOperationException("Could not write to the process console input buffer.", ex);
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Could not write to the process console input buffer.", ex);
            }
        }

        private void ValidateSettingsBeforeStart(Settings settings)
        {
            if (settings == null)
                throw new ArgumentNullException("settings");

            if (string.IsNullOrWhiteSpace(settings.FilePath))
                throw new InvalidOperationException("No target executable path is configured in settings.txt.");

            if (!File.Exists(settings.FilePath))
                throw new FileNotFoundException("The configured target executable does not exist.", settings.FilePath);

            string extension = Path.GetExtension(settings.FilePath);

            if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The configured target file is not an .exe file.");
        }

        private Process GetRunningProcessOrThrow()
        {
            lock (_syncLock)
            {
                if (!IsProcessRunningNoLock())
                    throw new InvalidOperationException("The target process is not running.");

                return _process;
            }
        }

        private bool IsProcessRunningNoLock()
        {
            if (_process == null)
                return false;

            try
            {
                return !_process.HasExited;
            }
            catch
            {
                return false;
            }
        }

        private void Process_Exited(object sender, EventArgs e)
        {
            Process exitedProcess = sender as Process;

            int? exitCode = null;

            try
            {
                if (exitedProcess != null)
                    exitCode = exitedProcess.ExitCode;
            }
            catch
            {
                exitCode = null;
            }

            bool wasExpected;

            lock (_syncLock)
            {
                wasExpected = _shutdownRequested;

                if (object.ReferenceEquals(_process, exitedProcess))
                    _process = null;
            }

            RaiseProcessExited(exitCode, wasExpected);

            if (exitedProcess != null)
            {
                exitedProcess.Exited -= Process_Exited;
                exitedProcess.Dispose();
            }
        }

        private void RaiseOutput(string text)
        {
            EventHandler<ConsoleOutputEventArgs> handler = OutputReceived;

            if (handler != null)
                handler(this, new ConsoleOutputEventArgs(text));
        }

        private void RaiseError(string text)
        {
            EventHandler<ConsoleOutputEventArgs> handler = ErrorReceived;

            if (handler != null)
                handler(this, new ConsoleOutputEventArgs(text));
        }

        private void RaiseProcessExited(int? exitCode, bool wasExpected)
        {
            EventHandler<ConsoleProcessExitedEventArgs> handler = ProcessExited;

            if (handler != null)
                handler(this, new ConsoleProcessExitedEventArgs(exitCode, wasExpected));
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException("ConsoleProcessManager");
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            Process processToDispose = null;

            lock (_syncLock)
            {
                processToDispose = _process;
                _process = null;
            }

            if (processToDispose != null)
            {
                try
                {
                    processToDispose.Exited -= Process_Exited;
                    processToDispose.Dispose();
                }
                catch
                {
                    // Ignore dispose errors.
                }
            }
        }
    }

    internal static class ConsoleInputInjector
    {
        private const ushort KEY_EVENT = 0x0001;
        private const ushort VK_RETURN = 0x0D;
        private const int STD_INPUT_HANDLE = -10;
        private const uint MAPVK_VK_TO_VSC = 0;

        private const uint RIGHT_ALT_PRESSED = 0x0001;
        private const uint LEFT_ALT_PRESSED = 0x0002;
        private const uint RIGHT_CTRL_PRESSED = 0x0004;
        private const uint LEFT_CTRL_PRESSED = 0x0008;
        private const uint SHIFT_PRESSED = 0x0010;

        private static readonly object SyncRoot = new object();
        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        public static void WriteLine(Process process, string command)
        {
            if (process == null)
                throw new ArgumentNullException("process");

            if (command == null)
                throw new ArgumentNullException("command");

            if (process.HasExited)
                throw new InvalidOperationException("The target process has exited.");

            string line = command.TrimEnd('\r', '\n') + "\r";
            INPUT_RECORD[] records = BuildInputRecords(line);

            lock (SyncRoot)
            {
                // A process can only be attached to one console at a time.
                // Detach first, then attach to the server console.
                FreeConsole();

                if (!AttachConsole(unchecked((uint)process.Id)))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "AttachConsole failed.");

                try
                {
                    IntPtr inputHandle = GetStdHandle(STD_INPUT_HANDLE);

                    if (inputHandle == IntPtr.Zero || inputHandle == InvalidHandleValue)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "GetStdHandle(STD_INPUT_HANDLE) failed.");

                    uint written;

                    if (!WriteConsoleInput(inputHandle, records, (uint)records.Length, out written))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "WriteConsoleInput failed.");

                    if (written != records.Length)
                        throw new InvalidOperationException("WriteConsoleInput wrote fewer records than expected.");
                }
                finally
                {
                    FreeConsole();
                }
            }
        }

        private static INPUT_RECORD[] BuildInputRecords(string text)
        {
            List<INPUT_RECORD> records = new List<INPUT_RECORD>(text.Length * 2);

            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                records.Add(CreateKeyEvent(ch, true));
                records.Add(CreateKeyEvent(ch, false));
            }

            return records.ToArray();
        }

        private static INPUT_RECORD CreateKeyEvent(char ch, bool keyDown)
        {
            ushort virtualKeyCode = 0;
            ushort virtualScanCode = 0;
            uint controlKeyState = 0;

            if (ch == '\r')
            {
                virtualKeyCode = VK_RETURN;
                virtualScanCode = (ushort)MapVirtualKey(VK_RETURN, MAPVK_VK_TO_VSC);
            }
            else
            {
                short key = VkKeyScan(ch);

                if (key != -1)
                {
                    virtualKeyCode = (ushort)(key & 0xff);
                    virtualScanCode = (ushort)MapVirtualKey(virtualKeyCode, MAPVK_VK_TO_VSC);

                    byte shiftState = (byte)((key >> 8) & 0xff);

                    if ((shiftState & 1) != 0)
                        controlKeyState |= SHIFT_PRESSED;

                    if ((shiftState & 2) != 0)
                        controlKeyState |= LEFT_CTRL_PRESSED;

                    if ((shiftState & 4) != 0)
                        controlKeyState |= LEFT_ALT_PRESSED;
                }
            }

            INPUT_RECORD record = new INPUT_RECORD();
            record.EventType = KEY_EVENT;
            record.KeyEvent.bKeyDown = keyDown;
            record.KeyEvent.wRepeatCount = 1;
            record.KeyEvent.wVirtualKeyCode = virtualKeyCode;
            record.KeyEvent.wVirtualScanCode = virtualScanCode;
            record.KeyEvent.UnicodeChar = ch;
            record.KeyEvent.dwControlKeyState = controlKeyState;
            return record;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "WriteConsoleInputW")]
        private static extern bool WriteConsoleInput(
            IntPtr hConsoleInput,
            INPUT_RECORD[] lpBuffer,
            uint nLength,
            out uint lpNumberOfEventsWritten);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern short VkKeyScan(char ch);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUT_RECORD
        {
            [FieldOffset(0)]
            public ushort EventType;

            [FieldOffset(4)]
            public KEY_EVENT_RECORD KeyEvent;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct KEY_EVENT_RECORD
        {
            [MarshalAs(UnmanagedType.Bool)]
            public bool bKeyDown;

            public ushort wRepeatCount;
            public ushort wVirtualKeyCode;
            public ushort wVirtualScanCode;
            public char UnicodeChar;
            public uint dwControlKeyState;
        }
    }
}
