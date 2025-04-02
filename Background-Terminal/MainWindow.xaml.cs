using CoreMeter;
using Newtonsoft.Json;
using Renci.SshNet;
using Renci.SshNet.Common;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Background_Terminal.Extensions;
using Background_Terminal.DTOs;
using System.Runtime.InteropServices;

namespace Background_Terminal {
    public partial class MainWindow : Window {
        // Static Fields
        private static BrushConverter _brushConverter = new BrushConverter();

        private static DirectoryInfo _appDataDirectory = new DirectoryInfo(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BackgroundTerminal"));
        private static string _configFile = "config.json";
        private static string _configPath = System.IO.Path.Combine(_appDataDirectory.FullName, _configFile);

        // TerminalWindow
        private TerminalWindow _terminalWindow;

        // Main Process
        private Process _process;

        // CoreMeter
        private CoreMeterUtility _coreMeterUtility;

        // Settings Container
        private BackgroundTerminalSettings _settings;

        // SSH Handling
        private SshClient _sshClient;
        private ShellStream _sshStream;
        private bool _sshMode = false;
        private string _sshServer = String.Empty;
        private string _sshUsername = String.Empty;
        private string _sshCurrentDirectory = String.Empty;

        // UI List Bindings
        private ObservableCollection<string> _terminalData = new ObservableCollection<string>();
        public ObservableCollection<NewlineTrigger> NewlineTriggers { get; set; }

        // ANSI parsing
        private AnsiEscapeCodeParser _ansiParser = new AnsiEscapeCodeParser();
        
        // Newline State Handling
        private string _currentTrigger = null;
        private string _newlineString = Environment.NewLine;

        // CMD Process ID
        private int _cmdProcessId;

        // TerminalWindow UI State Handling
        private bool _terminalWindowActive = false;
        private bool _terminalWindowLocked = true;

        private bool _awaitingKey1 = false;
        private bool _awaitingKey2 = false;

        private Key? _key1 = null;
        private Key? _key2 = null;

        // Filter to apply to output
        private Regex _regex;
        
        // Flag to temporarily ignore output for clear commands
        private bool _ignoringOutput = false;

        #region Constructor
        public MainWindow() {
            InitializeComponent();

            // Create TerminalWindow
            _terminalWindow = new TerminalWindow(SendCommand, KillProcess, SendInterrupt, TerminalWindowUIUpdate);
            _terminalWindow.Show();

            // Apply changes in terminal data to TerminalWindow
            _terminalData.CollectionChanged += new System.Collections.Specialized.NotifyCollectionChangedEventHandler(
                (object o, System.Collections.Specialized.NotifyCollectionChangedEventArgs target) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (_settings.EnableAnsiEscapeCodes)
                        {
                            foreach (string newItem in target.NewItems)
                            {
                                string processedText = newItem;
                                if (_regex != null)
                                {
                                    processedText = _regex.Replace(processedText, "");
                                }
                                
                                // Process through ANSI parser
                                _ansiParser.ProcessText(processedText + Environment.NewLine);
                                
                                // Update document with parsed content
                                _terminalWindow.TerminalData_TextBox.Document = _ansiParser.RenderToFlowDocument();
                            }
                        }
                        else
                        {
                            // Original non-ANSI behavior
                            foreach (string newItem in target.NewItems) 
                            {
                                if (_regex != null) {
                                    _terminalWindow.TerminalData_TextBox.AppendText(_regex.Replace(newItem, "") + Environment.NewLine);
                                } else {
                                    _terminalWindow.TerminalData_TextBox.AppendText(newItem + Environment.NewLine);
                                }
                            }
                        }
                        
                        _terminalWindow.TerminalData_TextBox.ScrollToEnd();
                    });
                });

            // Get TerminalWindow handle
            IntPtr hWnd = new WindowInteropHelper(_terminalWindow).Handle;

            // Hide window from alt tab menu
            Win32Interop.HideWindowFromAltTabMenu(hWnd);

            // Initialize CoreMeterUtility for TerminalWindow
            _coreMeterUtility = new CoreMeterUtility(hWnd);

            // Initially lock TerminalWindow
            _coreMeterUtility.Lock();

            // Load settings from json file
            _settings = JsonConvert.DeserializeObject<BackgroundTerminalSettings>(File.ReadAllText(_configPath));

            ApplySettingsToTerminalWindow();

            _key1 = KeyInterop.KeyFromVirtualKey(_settings.Key1);
            _key2 = KeyInterop.KeyFromVirtualKey(_settings.Key2);
            
            if (_settings.RegexFilter != null) {
                try {
                    _regex = new Regex(_settings.RegexFilter);
                } catch { 
                    // Should not happen, unless someone fiddles with the JS file manually
                }
            }

            Process_TextBox.Text = _settings.ProcessPath;
            Key1_Button.Content = _key1.ToString();
            Key2_Button.Content = _key2.ToString();
            FontSize_TextBox.Text = _settings.FontSize.ToString();
            FontColor_TextBox.Text = _settings.FontColor;
            FontFamily_TextBox.Text = _settings.FontFamily;
            PosX_TextBox.Text = _settings.PosX.ToString();
            PosY_TextBox.Text = _settings.PosY.ToString();
            Width_TextBox.Text = _settings.Width.ToString();
            Height_TextBox.Text = _settings.Height.ToString();
            RegexFilter_TextBox.Text = _settings.RegexFilter;
            AnsiEscapeCodes_CheckBox.IsChecked = _settings.EnableAnsiEscapeCodes;

            if (_settings.NewlineTriggers == null)
                _settings.NewlineTriggers = new List<NewlineTrigger>();

            NewlineTriggers = new ObservableCollection<NewlineTrigger>(_settings.NewlineTriggers);

            // Set KeyTriggered callback delegate
            Win32Interop.KeyTriggered = KeyTriggered;

            // Initialize Global Keyhook
            Win32Interop.SetKeyhook();

            // Begin terminal process
            RunTerminalProcessAsync();

            DataContext = this;
        }
        #endregion

        #region General Functions
        private void KillProcess() {
            if (_sshMode) {
                _sshClient.Disconnect();
                _sshClient.Dispose();

                _sshMode = false;

                _terminalData.Add("SSH Session Disconnected");
            } else {
                KillChildren();
            }
        }
        
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(uint dwProcessId);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeConsole();

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleCtrlHandler(IntPtr HandlerRoutine, bool Add);

        private void SendInterrupt() {
            if (_sshMode) {
                // Send Ctrl+C character for SSH
                try {
                    _sshStream?.Write(new byte[] { 0x03 });  // ASCII code for Ctrl+C
                    _terminalData.Add("^C"); // Visual feedback
                } catch (Exception e) {
                    _terminalData.Add("Error sending interrupt: " + e.Message);
                }
            } else {
                try {
                    // Visual feedback
                    _terminalData.Add("^C");
                    
                    // First attempt: Send Ctrl+C character directly to stdin
                    _process.StandardInput.Write("\x03");
                    
                    // Second attempt: Send a "break" command for CMD processes
                    _process.StandardInput.WriteLine();
                    
                    // Third attempt: More aggressive approach using console attach
                    if (_cmdProcessId != 0) {
                        Thread ctrlCThread = new Thread(() => {
                            try {
                                // Detach from our console
                                FreeConsole();
                                
                                // Disable our own ctrl handler temporarily
                                SetConsoleCtrlHandler(IntPtr.Zero, true);
                                
                                // Attach to target process console
                                if (AttachConsole((uint)_cmdProcessId)) {
                                    // Send Ctrl+C to the attached console
                                    GenerateConsoleCtrlEvent(ConsoleCtrlEvent.CTRL_C_EVENT, 0);
                                    
                                    // Sleep to let the signal take effect
                                    Thread.Sleep(100);
                                    
                                    // Detach from the console
                                    FreeConsole();
                                }
                                
                                // Re-enable our console handler
                                SetConsoleCtrlHandler(IntPtr.Zero, false);
                            }
                            catch (Exception) {
                                // Silently ignore errors in our fallback approach
                            }
                        });
                        
                        // Start the control thread with appropriate permissions
                        ctrlCThread.SetApartmentState(ApartmentState.STA);
                        ctrlCThread.Start();
                        
                        // Wait a reasonable time for the thread to complete
                        ctrlCThread.Join(500);
                    }
                } catch (Exception e) {
                    _terminalData.Add("Error sending interrupt: " + e.Message);
                }
            }
        }
        
        // P/Invoke to generate console control event
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool GenerateConsoleCtrlEvent(ConsoleCtrlEvent ctrlEvent, uint processGroupId);
        
        private enum ConsoleCtrlEvent
        {
            CTRL_C_EVENT = 0,
            CTRL_BREAK_EVENT = 1
        }

        private void OutputSSHUsage() {
            _terminalData.Add("Background Terminal manually handles SSH connection. (Ctrl + C to quit)");
            _terminalData.Add("Usage: ssh <server>");
        }
        #endregion

        #region UI State Functions
        private void ApplySettingsToTerminalWindow() {
            _terminalWindow.TerminalData_TextBox.FontSize = _settings.FontSize;
            _terminalWindow.Input_TextBox.FontSize = _settings.FontSize;
            if (_settings.FontFamily != null) {
                _terminalWindow.TerminalData_TextBox.FontFamily = new FontFamily(_settings.FontFamily);
            }
            _terminalWindow.TerminalData_TextBox.Foreground = (Brush)_brushConverter.ConvertFromString(_settings.FontColor);
            _terminalWindow.Input_TextBox.Foreground = (Brush)_brushConverter.ConvertFromString(_settings.FontColor);
            _terminalWindow.Left = _settings.PosX;
            _terminalWindow.Top = _settings.PosY;
            _terminalWindow.Width = _settings.Width;
            _terminalWindow.Height = _settings.Height;
        }

        private void TerminalWindowUIUpdate() {
            PosX_TextBox.Text = _terminalWindow.Left.ToString();
            PosY_TextBox.Text = _terminalWindow.Top.ToString();

            Width_TextBox.Text = _terminalWindow.Width.ToString();
            Height_TextBox.Text = _terminalWindow.Height.ToString();
        }
        #endregion

        #region Process Helper Functions
        private List<Process> GetProcessChildren() {
            List<Process> children = new List<Process>();
            ManagementObjectSearcher managementObjectSearcher = new ManagementObjectSearcher(String.Format("Select * From Win32_Process Where ParentProcessID={0}", _process.Id));

            foreach (ManagementObject managementObject in managementObjectSearcher.Get()) {
                children.Add(Process.GetProcessById(Convert.ToInt32(managementObject["ProcessID"])));
            }

            return children;
        }

        private void KillChildren() {
            List<Process> children = GetProcessChildren();

            foreach (Process child in children) {
                if (!child.Id.Equals(_cmdProcessId)) {
                    child.Kill();
                }
            }
        }
        #endregion

        #region Terminal Data Handlers
        private async Task<int> RunTerminalProcessAsync() {
            TaskCompletionSource<int> taskCompletionSource = new TaskCompletionSource<int>();

            // Reset ANSI parser when starting a new process
            _ansiParser.Reset();
            
            // Ensure we have a clean UI
            if (_settings.EnableAnsiEscapeCodes) {
                Dispatcher.Invoke(() => {
                    _terminalWindow.TerminalData_TextBox.Document = _ansiParser.RenderToFlowDocument();
                });
            }
            
            try {
                _process?.Kill();
            } catch (Exception e) { }

            _process = new Process();

            _process.StartInfo.FileName = _settings.ProcessPath;
            _process.StartInfo.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            _process.StartInfo.UseShellExecute = false;
            _process.StartInfo.CreateNoWindow = true;
            _process.StartInfo.RedirectStandardInput = true;
            _process.StartInfo.RedirectStandardOutput = true;
            _process.StartInfo.RedirectStandardError = true;

            _process.EnableRaisingEvents = true;
            _process.OutputDataReceived += OutputDataReceived;
            _process.ErrorDataReceived += ErrorDataReceived;

            _process.Exited += new EventHandler((sender, args) => {
                Process process = (Process)sender;
                taskCompletionSource.SetResult(process.ExitCode);
                process.Dispose();
            });

            try {
                _process.Start();

                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();

                List<Process> children = GetProcessChildren();
                if (children.Count > 0)
                    _cmdProcessId = children[0].Id;
            } catch (Exception e) {
                Show();
                WindowState = WindowState.Normal;
                Topmost = true;

                System.Windows.MessageBox.Show("There was an error starting the process. Check your Process input and Apply Changes to retry. Details: " + Environment.NewLine + Environment.NewLine + e.Message);
                taskCompletionSource.SetException(e);
            }

            return await taskCompletionSource.Task;
        }

        // Batch update timer for ANSI rendering
        private System.Timers.Timer _updateTimer;
        private bool _pendingUpdate = false;
        
        private void OutputDataReceived(object sender, DataReceivedEventArgs e) {
            // Don't add null data (end of stream)
            if (e.Data == null)
                return;
                
            // Skip output if we're ignoring it (for cls/clear commands)
            if (_ignoringOutput) {
                return;
            }
                
            // Direct ANSI processing for more efficient handling of control sequences
            if (_settings.EnableAnsiEscapeCodes) {
                string processedText = e.Data;
                if (_regex != null) {
                    processedText = _regex.Replace(processedText, "");
                }
                
                // Process through ANSI parser (can be done outside UI thread)
                _ansiParser.ProcessText(processedText + Environment.NewLine);
                
                // Use batched updates for better performance
                if (_updateTimer == null) {
                    _updateTimer = new System.Timers.Timer(50); // Update every 50ms max
                    _updateTimer.AutoReset = false;
                    _updateTimer.Elapsed += (s, args) => {
                        Dispatcher.Invoke(() => {
                            // Only update if there are pending changes
                            if (_pendingUpdate) {
                                _terminalWindow.TerminalData_TextBox.Document = _ansiParser.RenderToFlowDocument();
                                _terminalWindow.TerminalData_TextBox.ScrollToEnd();
                                _pendingUpdate = false;
                            }
                        });
                    };
                }
                
                _pendingUpdate = true;
                if (!_updateTimer.Enabled) {
                    _updateTimer.Start();
                }
            } else {
                _terminalData.Add(e.Data);
            }
        }

        private void ErrorDataReceived(object sender, DataReceivedEventArgs e) {
            if (e.Data == null)
                return;
            
            // Skip output if we're ignoring it (for cls/clear commands)
            if (_ignoringOutput) {
                return;
            }
                
            if (_settings.EnableAnsiEscapeCodes) {
                string processedText = e.Data;
                if (_regex != null) {
                    processedText = _regex.Replace(processedText, "");
                }
                
                // Process through ANSI parser with red color for errors
                _ansiParser.ProcessText("\x1B[31m" + processedText + "\x1B[0m" + Environment.NewLine);
                
                // Use the same batched update mechanism as OutputDataReceived
                _pendingUpdate = true;
                if (_updateTimer != null && !_updateTimer.Enabled) {
                    _updateTimer.Start();
                }
            } else {
                _terminalData.Add(e.Data);
            }
        }

        private async Task<string> SendCommandSSH(string command, bool silent = false) {
            // Handle SSH login connection
            if (_sshUsername.Equals(String.Empty)) {
                _sshUsername = command;
                _terminalData.Add("Enter password:");

                _terminalWindow._passwordMode = true;
            } else if (_terminalWindow._passwordMode) {
                _terminalData.Add("Connecting...");

                // Attempt connection
                _sshClient = new SshClient(_sshServer, _sshUsername, _terminalWindow._password);
                try {
                    _sshClient.Connect();

                    _terminalWindow._passwordMode = false;
                    _terminalWindow._password = String.Empty;

                    var modes = new Dictionary<Renci.SshNet.Common.TerminalModes, uint>();
                    _sshStream = _sshClient.CreateShellStream("bgtTerm", 255, 50, 800, 600, 1024, modes);

                    _sshStream.DataReceived += async (object sender, ShellDataEventArgs e) => {
                        if (_sshStream != null && _sshStream.CanRead) {
                            byte[] buffer = new byte[2048];
                            int i = 0;

                            if ((i = await _sshStream.ReadAsync(buffer, 0, buffer.Length)) != 1) {
                                _terminalData.Add(_sshClient.ConnectionInfo.Encoding.GetString(buffer, 0, i));
                            }
                        }
                    };

                    if (_sshClient.IsConnected) {
                        _terminalData.Add("Connected to " + _sshServer);
                    } else {
                        _terminalData.Add("There was a problem connecting.");

                        _sshMode = false;
                        _sshUsername = String.Empty;
                    }
                } catch (Exception e) {
                    _terminalData.Add(e.Message);
                }
            }

              // Handle SSH commands
              else {
                if (_sshClient.IsConnected) {
                    try {
                        _sshStream.WriteLine(command);
                    } catch (Exception e) {
                        _terminalData.Add(e.Message);
                    }
                } else {
                    _terminalData.Add("You are no longer connected to SSH. Exiting.");

                    _sshMode = false;
                    _sshUsername = String.Empty;
                }
            }

            return null;
        }

        private ObservableCollection<string> HelpInfo = new ObservableCollection<string>
        {
          "BGT commands require a subcommand. Available commands:",
          "\tbgt newline <escape_sequence> - Set custom newline character",
          "\tbgt quit - Terminate running process",
          "\tbgt stop - Terminate running process",
          "\tbgt help - Show this help message"  
        };

        private void SendCommandBGT(string command) {
            string[] parts = command.Split(' ');
            
            // Check if there's a subcommand
            if (parts.Length < 2) {
                _terminalData.AddRange(HelpInfo);
            }

            string bgtCommand = parts[1].ToLower();
            
            // Handle each subcommand
            if (bgtCommand.Equals("newline")) {
                if (parts.Length < 3) {
                    _terminalData.Add("Usage: bgt newline <escape_sequence>");
                    return;
                }
                
                try {
                    _newlineString = Regex.Unescape(parts[2]);
                    _terminalData.Add("Newline sequence updated.");
                } catch (Exception e) {
                    _terminalData.Add("Error: " + e.Message);
                }
            }
            else if (bgtCommand.Equals("help")) {
                _terminalData.AddRange(HelpInfo);
            }

            // Special escape commands for terminating hung processes
            else if (bgtCommand.Equals("quit") || bgtCommand.Equals("stop"))
            {
                _terminalData.Add("Attempting to terminate current process...");

                TerminateAllChildProcesses();

                _terminalData.Add("Process termination attempt complete.");
            }

            else
            {
                _terminalData.Add($"Unknown command: {bgtCommand}");
                _terminalData.Add("Type 'bgt help' for a list of commands");
            }
        }

        private void TerminateAllChildProcesses()
        {
            try
            {
                // Get all processes with the same parent process ID
                var childProcesses = Process.GetProcesses()
                    .Where(p =>
                    {
                        try
                        {
                            return GetParentProcessId(p.Id) == _process.Id;
                        }
                        catch
                        {
                            return false;
                        }
                    })
                    .ToList();

                _terminalData.Add($"Found {childProcesses.Count} child processes to terminate.");

                foreach (var childProcess in childProcesses)
                {
                    try
                    {
                        if (!childProcess.HasExited)
                        {
                            _terminalData.Add($"Terminating process: {childProcess.ProcessName} (PID: {childProcess.Id})");

                            // Try multiple termination methods
                            childProcess.CloseMainWindow();

                            if (!childProcess.WaitForExit(1000))
                            {
                                // Send Ctrl+C
                                SendCtrlCToProcess(childProcess);

                                if (!childProcess.WaitForExit(1000))
                                {
                                    // Force kill
                                    childProcess.Kill();
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _terminalData.Add($"Error terminating process {childProcess.ProcessName}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _terminalData.Add($"Error finding child processes: {ex.Message}");
            }
        }

        // Helper method to get parent process ID
        private int GetParentProcessId(int processId)
        {
            int parentPid = 0;
            using (var searcher = new ManagementObjectSearcher(
                $"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = {processId}"))
            {
                var collection = searcher.Get();
                foreach (ManagementObject obj in collection)
                {
                    parentPid = Convert.ToInt32(obj["ParentProcessId"]);
                    break;
                }
            }
            return parentPid;
        }

        // Method to send Ctrl+C to a process
        private void SendCtrlCToProcess(Process process)
        {
            try
            {
                [DllImport("kernel32.dll")]
                static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

                GenerateConsoleCtrlEvent(0, (uint)process.Id); // CTRL_C_EVENT
            }
            catch (Exception ex)
            {
                _terminalData.Add($"Error sending Ctrl+C: {ex.Message}");
            }
        }

        private async void SendCommand(string command) {
            // Handle SSH mode
            if (_sshMode) {
                SendCommandSSH(command);
            }

            // Background-Terminal application commands
            else if (command.ToLower().StartsWith("bgt")) {
                _terminalData.Add(command);
                SendCommandBGT(command);
            }

            // Initialize SSH mode
            else if (command.ToLower().StartsWith("ssh")) {
                _terminalData.Add(command);

                List<string> commandParams = command.Split(' ').ToList();

                if (commandParams.Count != 2) {
                    OutputSSHUsage();
                } else {
                    _sshServer = commandParams[1];

                    OutputSSHUsage();

                    _terminalData.Add("");
                    _terminalData.Add("Enter username:");

                    _sshMode = true;
                }
            }

            // Special handling for clear screen commands
            else if (TerminalCommandRegistry.ClearCommands.Contains(command)) {
                // Clear the terminal display immediately
                if (_settings.EnableAnsiEscapeCodes) {
                    // Reset the ANSI parser buffer
                    _ansiParser.Reset();
                    Dispatcher.Invoke(() => {
                        _terminalWindow.TerminalData_TextBox.Document = _ansiParser.RenderToFlowDocument();
                    });
                } else {
                    // Clear directly
                    Dispatcher.Invoke(() => {
                        _terminalWindow.TerminalData_TextBox.Document.Blocks.Clear();
                        
                        // Initialize with an empty paragraph to ensure proper display
                        Paragraph paragraph = new Paragraph();
                        _terminalWindow.TerminalData_TextBox.Document.Blocks.Add(paragraph);
                    });
                }
                
                // Set the ignore flag to skip processing the next output
                _ignoringOutput = true;
                
                // Send the command to the process without adding to terminal data
                _process.StandardInput.NewLine = _newlineString;
                _process.StandardInput.WriteLine(command);
                
                // Reset the ignore flag after a short delay to allow the command to process
                Task.Delay(300).ContinueWith(_ => {
                    Dispatcher.Invoke(() => {
                        _ignoringOutput = false;
                    });
                });
            }
           
            // Standard command handling
            else {
                _terminalData.Add(command);

                _process.StandardInput.NewLine = _newlineString;
                _process.StandardInput.WriteLine(command);

                // Check for newline trigger activations
                foreach (NewlineTrigger trigger in _settings.NewlineTriggers) {
                    if (command.StartsWith(trigger.TriggerCommand)) {
                        _currentTrigger = trigger.TriggerCommand;

                        _newlineString = Regex.Unescape(trigger.NewlineString);
                    } else if (command.StartsWith(trigger.ExitCommand) && _currentTrigger != null && _currentTrigger.Equals(trigger.TriggerCommand)) {
                        _currentTrigger = null;

                        _newlineString = Environment.NewLine;
                    }
                }
            }
        }
        #endregion

        #region Event Handlers
        private void TrayIcon_LeftMouseDown(object sender, RoutedEventArgs e) {
            if (WindowState == WindowState.Minimized) {
                Show();

                WindowState = WindowState.Normal;

                Topmost = true;
            } else {
                WindowState = WindowState.Minimized;
            }
        }

        private void TerminalWindowLockedButton_Click(object sender, RoutedEventArgs e) {
            if (_terminalWindowLocked) {
                _terminalWindowLocked = false;
                TerminalWindowLocked_Button.Content = "Unlocked";
            } else {
                _terminalWindowLocked = true;
                TerminalWindowLocked_Button.Content = "Locked";
            }

            _terminalWindow.SetWindowLocked(_terminalWindowLocked);
        }

        private void Key1Button_Click(object sender, RoutedEventArgs e) {
            Key1_Button.Content = "Press Key...";

            if (_awaitingKey2) {
                if (_key1 == null)
                    Key1_Button.Content = "";
                else
                    Key1_Button.Content = KeyInterop.VirtualKeyFromKey((Key)_key1).ToString();

                _awaitingKey2 = false;
            }

            _awaitingKey1 = true;
        }

        private void Key2Button_Click(object sender, RoutedEventArgs e) {
            Key2_Button.Content = "Press Key...";

            if (_awaitingKey1) {
                if (_key2 == null)
                    Key2_Button.Content = "";
                else
                    Key2_Button.Content = KeyInterop.VirtualKeyFromKey((Key)_key2).ToString();

                _awaitingKey1 = false;
            }

            _awaitingKey2 = true;
        }

        private void AddNewlineTriggerButton_Click(object sender, RoutedEventArgs e) {
            NewlineTriggers.Add(new NewlineTrigger("Trigger Command", "Exit Command", "Newline Character"));
        }

        private void DeleteNewlineTriggerButton_Click(object sender, RoutedEventArgs e) {
            if (NewlineTrigger_ListBox.SelectedItem != null)
                NewlineTriggers.Remove((NewlineTrigger)NewlineTrigger_ListBox.SelectedItem);
        }

        private void NewlineTriggerTextBox_PreviewMouseDown(object sender, MouseButtonEventArgs e) {
            NewlineTrigger_ListBox.SelectedItem = ((TextBox)sender).DataContext;
        }

        private void ApplyChangesButton_Click(object sender, RoutedEventArgs e) {
            System.Windows.Media.Brush fontColor;

            double fontSize;
            double posX;
            double posY;
            double width;
            double height;

            try {
                fontColor = (System.Windows.Media.Brush)(new BrushConverter()).ConvertFromString(FontColor_TextBox.Text);
            } catch {
                System.Windows.MessageBox.Show("There was an error interpreting font color input");
                return;
            }

            try {
                fontSize = Convert.ToDouble(FontSize_TextBox.Text);
            } catch {
                System.Windows.MessageBox.Show("There was an error interpreting font size input");
                return;
            }

            try {
                new FontFamily(_settings.FontFamily);
            } catch {
                System.Windows.MessageBox.Show("There was an error interpreting font family input");
                return;
            }

            try {
                posX = Convert.ToDouble(PosX_TextBox.Text);
            } catch {
                System.Windows.MessageBox.Show("There was an error interpreting X position input");
                return;
            }

            try {
                posY = Convert.ToDouble(PosY_TextBox.Text);
            } catch {
                System.Windows.MessageBox.Show("There was an error interpreting Y position input");
                return;
            }

            try {
                width = Convert.ToDouble(Width_TextBox.Text);
            } catch {
                System.Windows.MessageBox.Show("There was an error interpreting width input");
                return;
            }

            try {
                height = Convert.ToDouble(Height_TextBox.Text);
            } catch {
                System.Windows.MessageBox.Show("There was an error interpreting height input");
                return;
            }

            try {
                _regex = new Regex(RegexFilter_TextBox.Text);
            } catch {
                System.Windows.MessageBox.Show("There was an error interpreting the regex filter");
                return;
            }

            _settings.Key1 = KeyInterop.VirtualKeyFromKey((Key)_key1);
            _settings.Key2 = KeyInterop.VirtualKeyFromKey((Key)_key2);
            _settings.FontSize = fontSize;
            _settings.FontColor = fontColor.ToString();
            _settings.FontFamily = FontFamily_TextBox.Text;
            _settings.PosX = posX;
            _settings.PosY = posY;
            _settings.Width = width;
            _settings.Height = height;
            _settings.RegexFilter = RegexFilter_TextBox.Text;
            _settings.EnableAnsiEscapeCodes = AnsiEscapeCodes_CheckBox.IsChecked ?? true;

            _settings.NewlineTriggers = new List<NewlineTrigger>(NewlineTriggers);

            if (!_settings.ProcessPath.Equals(Process_TextBox.Text)) {
                _settings.ProcessPath = Process_TextBox.Text;

                // Begin terminal process
                RunTerminalProcessAsync();
            }

            ApplySettingsToTerminalWindow();

            File.WriteAllText(_configPath, JsonConvert.SerializeObject(_settings));
        }

        private void MainWindow_MouseDown(object sender, MouseButtonEventArgs e) {
            if (e.LeftButton == MouseButtonState.Pressed)
                this.DragMove();
        }

        private void MainWindow_Loaded(object sender, EventArgs e) {
            this.WindowState = WindowState.Minimized;
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e) {
            WindowState = WindowState.Minimized;
        }

        private void ExitButton_Click(object sender, RoutedEventArgs e) {
            if (_sshClient != null) {
                if (_sshClient.IsConnected)
                    _sshClient.Disconnect();

                _sshClient.Dispose();
            }

            _process.Kill();

            Close();
        }

        private void MainWindow_StateChanged(object sender, EventArgs e) {
            if (WindowState == WindowState.Minimized)
                Hide();
            else
                Show();
        }

        private void MainWindow_Closed(object sender, EventArgs e) {
            // End global keyhook
            Win32Interop.DestroyKeyhook();

            // Close terminal window
            _terminalWindow.Close();

            Environment.Exit(0);
        }
        #endregion

        #region Key Detection Callback Delegate
        private void KeyTriggered(int keyCode) {
            if (_key1 != null && _key2 != null) {
                int vKey1 = KeyInterop.VirtualKeyFromKey((Key)_key1);
                int vKey2 = KeyInterop.VirtualKeyFromKey((Key)_key2);

                if (keyCode == vKey2 && Win32Interop.IsKeyDown(vKey1)) {
                    _terminalWindow.Input_TextBox.Text = "";

                    if (!_terminalWindowActive) {
                        Win32Interop.ClickSimulateFocus(_terminalWindow);
                        Win32Interop.SetForegroundWindow((new WindowInteropHelper(_terminalWindow)).Handle);
                        Win32Interop.SetActiveWindow((new WindowInteropHelper(_terminalWindow)).Handle);
                        FocusManager.SetFocusedElement(_terminalWindow, _terminalWindow.Input_TextBox);
                        Keyboard.Focus(_terminalWindow.Input_TextBox);

                        _terminalWindowActive = true;
                    } else {
                        _terminalWindowActive = false;

                        Keyboard.ClearFocus();
                    }
                }
            }

            if (_awaitingKey1) {
                _key1 = KeyInterop.KeyFromVirtualKey(keyCode);

                _awaitingKey1 = false;

                Key1_Button.Content = _key1.ToString();
            }

            if (_awaitingKey2) {
                _key2 = KeyInterop.KeyFromVirtualKey(keyCode);

                _awaitingKey2 = false;

                Key2_Button.Content = _key2.ToString();
            }
        }
        #endregion
    }
}
