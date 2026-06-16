using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using g_Manager;

namespace g_Manager
{
    public partial class MainUI : Form
    {
        private Settings _settings;
        private Manager _processManager;
        private bool _allowClose;

        public MainUI()
        {
            InitializeComponent();

            _settings = new Settings();
            _processManager = new Manager(_settings);

            _processManager.OutputReceived += ProcessManager_OutputReceived;
            _processManager.ErrorReceived += ProcessManager_ErrorReceived;
            _processManager.ProcessExited += ProcessManager_ProcessExited;

            Load += Form1_Load;
            FormClosing += Form1_FormClosing;

            buttonStart.Click += buttonStart_Click;
            buttonReload.Click += buttonReload_Click;
            buttonShutdown.Click += buttonShutdown_Click;
            buttonOpenLocation.Click += buttonOpenLocation_Click;
            buttonChangeFile.Click += buttonChangeFile_Click;

            textBoxCommand.KeyDown += textBoxCommand_KeyDown;

            // Save arguments when the user leaves the arguments box.
            // You can replace this with a Save button if preferred.
            textBoxArguments.Leave += textBoxArguments_Leave;
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            try
            {
                _settings.Load();
                textBoxArguments.Text = _settings.Arguments ?? string.Empty;

                AppendOutputSafe("[wrapper] Settings loaded from: " + _settings.SettingsFilePath + Environment.NewLine);

                if (string.IsNullOrWhiteSpace(_settings.FilePath))
                {
                    AppendOutputSafe("[wrapper] No target .exe selected yet." + Environment.NewLine);
                }
                else
                {
                    AppendOutputSafe("[wrapper] Target .exe: " + _settings.FilePath + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                ShowError("Failed to load settings: " + ex.Message);
            }
        }

        private void buttonStart_Click(object sender, EventArgs e)
        {
            try
            {
                SaveArgumentsFromTextBox();

                _processManager.Start();
            }
            catch (Exception ex)
            {
                ShowError("Start failed: " + ex.Message);
            }
        }

        private async void buttonReload_Click(object sender, EventArgs e)
        {
            try
            {
                SaveArgumentsFromTextBox();

                buttonReload.Enabled = false;
                buttonStart.Enabled = false;

                await _processManager.Reload();
            }
            catch (Exception ex)
            {
                ShowError("Reload failed: " + ex.Message);
            }
            finally
            {
                buttonReload.Enabled = true;
                buttonStart.Enabled = true;
            }
        }

        private void buttonShutdown_Click(object sender, EventArgs e)
        {
            try
            {
                _processManager.Shutdown();
                AppendOutputSafe("> shutdown" + Environment.NewLine);
            }
            catch (Exception ex)
            {
                ShowError("Shutdown failed: " + ex.Message);
            }
        }

        private void buttonOpenLocation_Click(object sender, EventArgs e)
        {
            try
            {
                _settings.Load();

                if (string.IsNullOrWhiteSpace(_settings.FilePath))
                {
                    MessageBox.Show(
                        this,
                        "No target .exe path is configured.",
                        "Open Folder Location",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);

                    return;
                }

                if (!File.Exists(_settings.FilePath))
                {
                    MessageBox.Show(
                        this,
                        "The configured .exe file does not exist:" + Environment.NewLine + _settings.FilePath,
                        "Open Folder Location",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);

                    return;
                }

                string folder = Path.GetDirectoryName(_settings.FilePath);

                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                {
                    MessageBox.Show(
                        this,
                        "The folder containing the .exe could not be found.",
                        "Open Folder Location",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);

                    return;
                }

                ProcessStartInfo explorerInfo = new ProcessStartInfo();
                explorerInfo.FileName = "explorer.exe";
                explorerInfo.Arguments = "\"" + folder + "\"";
                explorerInfo.UseShellExecute = true;

                Process.Start(explorerInfo);
            }
            catch (Exception ex)
            {
                ShowError("Could not open folder: " + ex.Message);
            }
        }

        private void buttonChangeFile_Click(object sender, EventArgs e)
        {
            try
            {
                using (OpenFileDialog dialog = new OpenFileDialog())
                {
                    dialog.Title = "Select target console executable";
                    dialog.Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*";
                    dialog.CheckFileExists = true;
                    dialog.Multiselect = false;

                    if (dialog.ShowDialog(this) != DialogResult.OK)
                        return;

                    _settings.Load();
                    _settings.FilePath = dialog.FileName;
                    _settings.Save();

                    AppendOutputSafe("[wrapper] Target .exe updated: " + dialog.FileName + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                ShowError("Could not change file location: " + ex.Message);
            }
        }

        private void textBoxArguments_Leave(object sender, EventArgs e)
        {
            try
            {
                SaveArgumentsFromTextBox();
            }
            catch (Exception ex)
            {
                ShowError("Could not save arguments: " + ex.Message);
            }
        }

        private void textBoxCommand_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter)
                return;

            e.SuppressKeyPress = true;
            SendCustomCommandFromTextBox();
        }

        private void SendCustomCommandFromTextBox()
        {
            string command = textBoxCommand.Text.Trim();

            if (command.Length == 0)
                return;

            try
            {
                _processManager.SendCommand(command);

                AppendOutputSafe("> " + command + Environment.NewLine);

                textBoxCommand.Clear();
            }
            catch (Exception ex)
            {
                ShowError("Could not send command: " + ex.Message);
            }
        }

        private void SaveArgumentsFromTextBox()
        {
            _settings.Load();
            _settings.Arguments = textBoxArguments.Text ?? string.Empty;
            _settings.Save();
        }

        private void ProcessManager_OutputReceived(object sender, ConsoleOutputEventArgs e)
        {
            AppendOutputSafe(e.Text + Environment.NewLine);
        }

        private void ProcessManager_ErrorReceived(object sender, ConsoleOutputEventArgs e)
        {
            AppendOutputSafe("[stderr] " + e.Text + Environment.NewLine);
        }

        private void ProcessManager_ProcessExited(object sender, ConsoleProcessExitedEventArgs e)
        {
            string exitCodeText = e.ExitCode.HasValue ? e.ExitCode.Value.ToString() : "unknown";

            if (e.WasExpected)
            {
                AppendOutputSafe("[wrapper] Process exited. Exit code: " + exitCodeText + Environment.NewLine);
            }
            else
            {
                AppendOutputSafe("[wrapper] Process exited unexpectedly. Exit code: " + exitCodeText + Environment.NewLine);
            }
        }

        private async void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (_allowClose)
                return;

            if (_processManager != null && _processManager.IsRunning)
            {
                e.Cancel = true;

                AppendOutputSafe("[wrapper] Application closing. Attempting graceful server shutdown..." + Environment.NewLine);

                try
                {
                    await _processManager.StopOrKillIfNeeded(30000);
                }
                catch (Exception ex)
                {
                    AppendOutputSafe("[wrapper] Error during shutdown: " + ex.Message + Environment.NewLine);
                }

                _allowClose = true;
                Close();
            }
        }

        private void AppendOutputSafe(string text)
        {
            if (richTextBoxOutput == null || richTextBoxOutput.IsDisposed)
                return;

            if (richTextBoxOutput.InvokeRequired)
            {
                richTextBoxOutput.BeginInvoke(new Action<string>(AppendOutputSafe), text);
                return;
            }

            OutputTextBoxHelper.AppendConsoleText(richTextBoxOutput, text);
        }

        private void ShowError(string message)
        {
            AppendOutputSafe("[wrapper error] " + message + Environment.NewLine);

            MessageBox.Show(
                this,
                message,
                "Console Wrapper",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    public static class OutputTextBoxHelper
    {
        private const int EM_GETFIRSTVISIBLELINE = 0x00CE;
        private const int EM_LINESCROLL = 0x00B6;
        private const int EM_GETLINECOUNT = 0x00BA;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(
            IntPtr hWnd,
            int msg,
            IntPtr wParam,
            IntPtr lParam);

        public static void AppendConsoleText(TextBoxBase textBox, string text)
        {
            if (textBox == null)
                return;

            if (text == null)
                text = string.Empty;

            bool wasNearBottom = IsScrolledNearBottom(textBox);
            int firstVisibleLine = GetFirstVisibleLine(textBox);

            int selectionStart = textBox.SelectionStart;
            int selectionLength = textBox.SelectionLength;

            textBox.AppendText(text);

            if (wasNearBottom)
            {
                textBox.SelectionStart = textBox.TextLength;
                textBox.SelectionLength = 0;
                textBox.ScrollToCaret();
            }
            else
            {
                int safeSelectionStart = Math.Min(selectionStart, textBox.TextLength);
                int safeSelectionLength = Math.Min(selectionLength, textBox.TextLength - safeSelectionStart);

                textBox.SelectionStart = safeSelectionStart;
                textBox.SelectionLength = safeSelectionLength;

                ScrollToFirstVisibleLine(textBox, firstVisibleLine);
            }
        }

        public static bool IsScrolledNearBottom(TextBoxBase textBox)
        {
            if (textBox == null)
                return true;

            int totalLines = GetLineCount(textBox);
            int firstVisibleLine = GetFirstVisibleLine(textBox);

            int visibleLineCount = Math.Max(1, textBox.ClientSize.Height / Math.Max(1, textBox.Font.Height));
            int lastVisibleLine = firstVisibleLine + visibleLineCount;

            const int toleranceLines = 2;

            return totalLines - lastVisibleLine <= toleranceLines;
        }

        private static int GetFirstVisibleLine(TextBoxBase textBox)
        {
            return SendMessage(
                textBox.Handle,
                EM_GETFIRSTVISIBLELINE,
                IntPtr.Zero,
                IntPtr.Zero).ToInt32();
        }

        private static int GetLineCount(TextBoxBase textBox)
        {
            return SendMessage(
                textBox.Handle,
                EM_GETLINECOUNT,
                IntPtr.Zero,
                IntPtr.Zero).ToInt32();
        }

        private static void ScrollToFirstVisibleLine(TextBoxBase textBox, int targetFirstVisibleLine)
        {
            int currentFirstVisibleLine = GetFirstVisibleLine(textBox);
            int lineDelta = targetFirstVisibleLine - currentFirstVisibleLine;

            if (lineDelta == 0)
                return;

            SendMessage(
                textBox.Handle,
                EM_LINESCROLL,
                IntPtr.Zero,
                new IntPtr(lineDelta));
        }
    }
}
