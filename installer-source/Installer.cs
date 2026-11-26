// Build Your Future Installer
// A small desktop app: shows your iPhone, how many days the app has left, the installed vs newest version,
// and refreshes it (auto-refresh when you plug in, or with 1 day left).
// Signing + installing is done by the bundled engine (plumesign, from the open-source Impactor project, MIT):
// you log in with your Apple ID once inside this window; the engine checks it with Apple and saves it on this PC.
//
// Two modes, same program:
//  - personal: next to update-ipa.ps1 (the creator's PC). Installs the admin IPA from ipa-branch\.
//  - public:   anywhere else. Downloads the normal IPA from https://buildyourfuture.fit.
//
// Build: see build.ps1 (uses the C# compiler that comes with Windows / .NET Framework 4).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace BuildYourFuture
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            bool tray = args.Any(a => a.Equals("-Tray", StringComparison.OrdinalIgnoreCase) || a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
            bool createdNew;
            var mutex = new Mutex(true, @"Local\BuildYourFutureInstaller", out createdNew);
            var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\BuildYourFutureInstallerShow");
            if (!createdNew) { showSignal.Set(); return 0; }

            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var installer = new Installer(showSignal, tray);
            installer.Start();
            app.Run();
            GC.KeepAlive(mutex);
            return 0;
        }
    }

    class Installer
    {
        const string BundleId = "com.workingtheproblem.selfimprovement";
        const string WebsiteBase = "https://buildyourfuture.fit/";
        const string AppleInfoUrl = "https://developer.apple.com/support/compare-memberships/";
        const int ValidDays = 7;

        readonly EventWaitHandle showSignal;
        readonly bool startInTray;
        readonly string root, dataDir, ipaDir, workDir, engine, stateFile, settingsFile, logFile, logoPath, icoPath;
        readonly bool personal;
        readonly JavaScriptSerializer json = new JavaScriptSerializer();

        Window win;
        TextBlock subtitle, deviceTitle, deviceSub, daysNumber, daysLabel, daysSub, versionInstalled, versionNewest, versionBadgeText;
        TextBlock accountText, statusText, footer, autoSub;
        Ellipse deviceDot;
        Border daysBar, versionBadge;
        Button minBtn, closeBtn, loginBtn, loginGoBtn, refreshBtn;
        StackPanel loginForm, codePanel;
        TextBox emailBox, codeBox;
        PasswordBox passwordBox;
        CheckBox autoSwitch;
        Hyperlink appleLink;
        Forms.NotifyIcon trayIcon;

        DispatcherTimer pollTimer, workTimer;
        string phase = "idle";          // idle, countdown, update, installing, login
        string loginStage = "creds";    // creds, wait, code, checking
        int count;
        string udid;
        List<string> known = new List<string>();
        string account = "";
        string version = "";
        DateTime started = DateTime.Now;
        DateTime lastAutoAttempt = DateTime.MinValue;
        bool quitting;
        Process proc;
        readonly StringBuilder procOut = new StringBuilder();
        volatile bool downloadDone;
        volatile string downloadError;
        DateTime updaterLogStamp;

        public Installer(EventWaitHandle showSignal, bool tray)
        {
            this.showSignal = showSignal;
            this.startInTray = tray;
            root = AppDomain.CurrentDomain.BaseDirectory;
            personal = File.Exists(System.IO.Path.Combine(root, "update-ipa.ps1"));
            dataDir = personal ? root : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BuildYourFutureInstaller");
            ipaDir = personal ? System.IO.Path.Combine(root, "ipa-branch") : System.IO.Path.Combine(dataDir, "download");
            workDir = System.IO.Path.Combine(dataDir, "refresh-work");
            engine = System.IO.Path.Combine(root, "engine", "plumesign.exe");
            stateFile = System.IO.Path.Combine(dataDir, "iphone-state.json");
            settingsFile = System.IO.Path.Combine(dataDir, "installer-settings.json");
            logFile = System.IO.Path.Combine(dataDir, "iphone-helper.log");
            icoPath = System.IO.Path.Combine(root, "installer.ico");
            logoPath = new[] { System.IO.Path.Combine(root, "icon.png"), System.IO.Path.Combine(root, "site", "icon.png") }.FirstOrDefault(File.Exists);
            Directory.CreateDirectory(dataDir);
            Directory.CreateDirectory(workDir);
            Directory.CreateDirectory(ipaDir);
        }

        // ---------------------------------------------------------------- setup

        public void Start()
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("window.xaml"))
            {
                win = (Window)XamlReader.Load(s);
            }
            subtitle = Find<TextBlock>("Subtitle");
            deviceDot = Find<Ellipse>("DeviceDot");
            deviceTitle = Find<TextBlock>("DeviceTitle");
            deviceSub = Find<TextBlock>("DeviceSub");
            daysNumber = Find<TextBlock>("DaysNumber");
            daysLabel = Find<TextBlock>("DaysLabel");
            daysSub = Find<TextBlock>("DaysSub");
            daysBar = Find<Border>("DaysBar");
            versionInstalled = Find<TextBlock>("VersionInstalled");
            versionNewest = Find<TextBlock>("VersionNewest");
            versionBadge = Find<Border>("VersionBadge");
            versionBadgeText = Find<TextBlock>("VersionBadgeText");
            accountText = Find<TextBlock>("AccountText");
            loginBtn = Find<Button>("LoginBtn");
            loginForm = Find<StackPanel>("LoginForm");
            emailBox = Find<TextBox>("EmailBox");
            passwordBox = Find<PasswordBox>("PasswordBox");
            codePanel = Find<StackPanel>("CodePanel");
            codeBox = Find<TextBox>("CodeBox");
            loginGoBtn = Find<Button>("LoginGoBtn");
            appleLink = (Hyperlink)win.FindName("AppleLink");
            refreshBtn = Find<Button>("RefreshBtn");
            autoSwitch = Find<CheckBox>("AutoSwitch");
            autoSub = Find<TextBlock>("AutoSub");
            statusText = Find<TextBlock>("StatusText");
            footer = Find<TextBlock>("Footer");
            minBtn = Find<Button>("MinBtn");
            closeBtn = Find<Button>("CloseBtn");

            if (logoPath != null)
            {
                var bmp = new BitmapImage();
                bmp.BeginInit(); bmp.UriSource = new Uri(logoPath); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.EndInit();
                Find<Image>("Logo").Source = bmp;
                win.Icon = bmp;
            }
            subtitle.Text = personal ? "Installer  -  your admin version" : "Installer  -  auto refresh";
            footer.Text = personal
                ? "Installs your admin IPA. Signs with your Apple ID (built-in engine). No Sideloadly needed."
                : "Installs Build Your Future from buildyourfuture.fit. Signs with your Apple ID (built-in engine). No Sideloadly needed.";

            SetupTray();

            Find<Grid>("TitleBar").MouseLeftButtonDown += (s, e) => { try { win.DragMove(); } catch { } };
            minBtn.Click += (s, e) => win.Hide();
            closeBtn.Click += (s, e) =>
            {
                win.Hide();
                trayIcon.ShowBalloonTip(3000, "Build Your Future", "Still running in the tray. Plug in your iPhone to refresh.", Forms.ToolTipIcon.Info);
            };
            win.Closing += (s, e) => { if (!quitting) { e.Cancel = true; win.Hide(); } };
            loginBtn.Click += (s, e) => ShowLoginForm(loginForm.Visibility != Visibility.Visible);
            loginGoBtn.Click += (s, e) => { if (loginStage == "code") SendLoginCode(); else StartLogin(); };
            passwordBox.KeyDown += (s, e) => { if (e.Key == Key.Return) StartLogin(); };
            codeBox.KeyDown += (s, e) => { if (e.Key == Key.Return && loginStage == "code") SendLoginCode(); };
            appleLink.Click += (s, e) => OpenUrl(AppleInfoUrl);
            refreshBtn.Click += (s, e) =>
            {
                if (phase == "countdown") { workTimer.Stop(); phase = "idle"; SetStatus("Auto-refresh cancelled.", "#9AA4B5"); UpdateView(); }
                else StartRefresh("manual");
            };
            var settings = LoadSettings();
            autoSwitch.IsChecked = settings;
            autoSwitch.Click += (s, e) =>
            {
                bool on = autoSwitch.IsChecked == true;
                SaveSettings(on);
                if (!personal) SetStartWithWindows(on);
                SetStatus(on ? "Auto-refresh is on." : "Auto-refresh is off.", "#9AA4B5");
            };
            if (!personal)
            {
                autoSub.Text = "When you plug in, or when 1 day is left (starts with Windows)";
                if (settings) SetStartWithWindows(true);
            }

            pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            pollTimer.Tick += (s, e) => Poll();
            workTimer = new DispatcherTimer();
            workTimer.Tick += (s, e) => Work();

            Log("Installer started (" + (personal ? "personal" : "public") + ")");
            EnsureShortcuts();
            UpdateAccount();
            if (!personal) StartDownload(false);
            UpdateView();
            pollTimer.Start();
            if (!startInTray) ShowMain();
        }

        T Find<T>(string name) where T : class { return win.FindName(name) as T; }

        void SetupTray()
        {
            trayIcon = new Forms.NotifyIcon();
            try
            {
                if (File.Exists(icoPath)) trayIcon.Icon = new Drawing.Icon(icoPath, 32, 32);
                else trayIcon.Icon = Drawing.SystemIcons.Application;
            }
            catch { trayIcon.Icon = Drawing.SystemIcons.Application; }
            trayIcon.Text = "Build Your Future Installer";
            trayIcon.Visible = true;
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Open", null, (s, e) => ShowMain());
            menu.Items.Add("Refresh now", null, (s, e) => { ShowMain(); StartRefresh("manual"); });
            menu.Items.Add("-");
            menu.Items.Add("Quit", null, (s, e) => Quit());
            trayIcon.ContextMenuStrip = menu;
            trayIcon.DoubleClick += (s, e) => ShowMain();
        }

        void ShowMain()
        {
            win.Show();
            if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal;
            win.Activate();
            win.Topmost = true; win.Topmost = false;
        }

        void Quit()
        {
            quitting = true;
            trayIcon.Visible = false;
            trayIcon.Dispose();
            win.Close();
            Application.Current.Shutdown();
        }

        // ---------------------------------------------------------------- helpers

        void Log(string message)
        {
            try { File.AppendAllText(logFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine, Encoding.UTF8); } catch { }
        }

        static Brush BrushOf(string hex) { return (Brush)new BrushConverter().ConvertFromString(hex); }

        void SetStatus(string text, string color = "#7090FF")
        {
            statusText.Text = text;
            statusText.Foreground = BrushOf(color);
        }

        static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        bool LoadSettings()
        {
            try
            {
                if (File.Exists(settingsFile))
                {
                    var d = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(settingsFile));
                    object v;
                    if (d != null && d.TryGetValue("autoRefresh", out v) && v is bool) return (bool)v;
                }
            }
            catch { }
            return true;
        }

        void SaveSettings(bool autoRefresh)
        {
            try { File.WriteAllText(settingsFile, json.Serialize(new Dictionary<string, object> { { "autoRefresh", autoRefresh } })); } catch { }
        }

        // ---- install state (when we last installed, per iPhone) ----
        Dictionary<string, Dictionary<string, object>> LoadState()
        {
            try
            {
                if (File.Exists(stateFile))
                {
                    var raw = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(stateFile));
                    var result = new Dictionary<string, Dictionary<string, object>>();
                    if (raw != null)
                        foreach (var kv in raw)
                        {
                            var inner = kv.Value as Dictionary<string, object>;
                            if (inner != null) result[kv.Key] = inner;
                        }
                    return result;
                }
            }
            catch { }
            return new Dictionary<string, Dictionary<string, object>>();
        }

        void SaveInstall(string id, string ver)
        {
            var state = LoadState();
            state[id] = new Dictionary<string, object> { { "installedAt", DateTime.Now.ToString("o") }, { "version", ver } };
            try { File.WriteAllText(stateFile, json.Serialize(state)); } catch { }
        }

        string LastUdid()
        {
            var state = LoadState();
            return state.Keys.LastOrDefault();
        }

        DateTime? InstalledAt(string id)
        {
            var state = LoadState();
            Dictionary<string, object> e;
            if (id == null || !state.TryGetValue(id, out e)) return null;
            object v; DateTime t;
            if (e.TryGetValue("installedAt", out v) && v != null && DateTime.TryParse(v.ToString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out t)) return t.ToLocalTime();
            return null;
        }

        string InstalledVersion(string id)
        {
            var state = LoadState();
            Dictionary<string, object> e; object v;
            if (id != null && state.TryGetValue(id, out e) && e.TryGetValue("version", out v) && v != null) return v.ToString();
            return "";
        }

        int? DaysLeft(string id)
        {
            var at = InstalledAt(id);
            if (at == null) return null;
            double left = ValidDays - (DateTime.Now - at.Value).TotalDays;
            return Math.Max(0, (int)Math.Ceiling(left));
        }

        string ReadText(string name)
        {
            string file = System.IO.Path.Combine(ipaDir, name);
            try { return File.Exists(file) ? File.ReadAllText(file).Trim() : ""; } catch { return ""; }
        }

        // The version of the IPA file we have here (what an install puts on the iPhone).
        string LocalVersion()
        {
            string v = ReadText("VERSION.txt");
            if (v.Length > 0 && personal &&
                (File.Exists(System.IO.Path.Combine(ipaDir, "BuildYourFuture-Admin-Free.ipa")) || File.Exists(System.IO.Path.Combine(ipaDir, "BuildYourFuture-Admin.ipa"))))
                v += " (admin)";
            return v;
        }

        // The newest version there is (public: what the website has right now).
        string NewestVersion()
        {
            if (!personal)
            {
                string remote = ReadText("REMOTE_VERSION.txt");
                if (remote.Length > 0) return remote;
            }
            return LocalVersion();
        }

        // The free-account IPAs have no Screen Time extensions / app group (a free Apple ID can't sign those).
        string IpaToInstall()
        {
            var names = personal
                ? new[] { "BuildYourFuture-Admin-Free.ipa", "BuildYourFuture-Admin.ipa", "BuildYourFuture-Free.ipa", "BuildYourFuture.ipa" }
                : new[] { "BuildYourFuture-Free.ipa", "BuildYourFuture.ipa" };
            foreach (var n in names)
            {
                string p = System.IO.Path.Combine(ipaDir, n);
                if (File.Exists(p)) return p;
            }
            return null;
        }

        bool NeedsRefresh(string id)
        {
            var days = DaysLeft(id);
            string installed = InstalledVersion(id);
            string newest = NewestVersion();
            return days == null || days.Value <= 5 || installed.Length == 0 || (newest.Length > 0 && installed != newest);
        }

        // ---- iPhone over USB: Apple = vendor 05AC, iPhones use product ids 12xx; the USB serial is the UDID ----
        static List<string> ConnectedIPhones()
        {
            var ids = new List<string>();
            try
            {
                using (var searcher = new ManagementObjectSearcher(@"SELECT DeviceID FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB\\VID_05AC&PID_12%'"))
                {
                    foreach (ManagementObject o in searcher.Get())
                    {
                        string id = (o["DeviceID"] ?? "").ToString();
                        string serial = id.Split('\\').Last();
                        if (Regex.IsMatch(serial, "^[0-9A-Fa-f]{24}$")) ids.Add((serial.Substring(0, 8) + "-" + serial.Substring(8)).ToUpperInvariant());
                        else if (Regex.IsMatch(serial, "^[0-9A-Fa-f]{40}$")) ids.Add(serial.ToLowerInvariant());
                    }
                }
            }
            catch { }
            return ids.Distinct().OrderBy(x => x).ToList();
        }

        // ---- Apple's device service (usbmuxd, from iTunes / Apple Devices) ----
        // On Windows the engine wants the service's own number for the iPhone (it changes every time you
        // plug in), so ask the service which number belongs to this UDID right before installing.
        static string UsbmuxDeviceId(string udid)
        {
            try
            {
                string body = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<plist version=\"1.0\"><dict>" +
                              "<key>MessageType</key><string>ListDevices</string>" +
                              "<key>ClientVersionString</key><string>BuildYourFuture</string>" +
                              "<key>ProgName</key><string>BuildYourFuture</string></dict></plist>";
                byte[] payload = Encoding.UTF8.GetBytes(body);
                using (var client = new System.Net.Sockets.TcpClient())
                {
                    client.Connect("127.0.0.1", 27015);
                    var stream = client.GetStream();
                    stream.ReadTimeout = 5000;
                    var header = new byte[16];
                    Array.Copy(BitConverter.GetBytes(16 + payload.Length), 0, header, 0, 4);
                    Array.Copy(BitConverter.GetBytes(1), 0, header, 4, 4);   // version
                    Array.Copy(BitConverter.GetBytes(8), 0, header, 8, 4);   // plist message
                    Array.Copy(BitConverter.GetBytes(1), 0, header, 12, 4);  // tag
                    stream.Write(header, 0, 16);
                    stream.Write(payload, 0, payload.Length);
                    byte[] hdr = ReadExactly(stream, 16);
                    int length = BitConverter.ToInt32(hdr, 0);
                    string xml = Encoding.UTF8.GetString(ReadExactly(stream, length - 16));
                    string want = udid.Replace("-", "").ToUpperInvariant();
                    foreach (Match m in Regex.Matches(xml, @"<key>DeviceID</key>\s*<integer>(\d+)</integer>(.*?)(?=<key>DeviceID</key>|$)", RegexOptions.Singleline))
                    {
                        var sn = Regex.Match(m.Groups[2].Value, @"<key>SerialNumber</key>\s*<string>([^<]+)</string>");
                        if (sn.Success && sn.Groups[1].Value.Replace("-", "").ToUpperInvariant() == want) return m.Groups[1].Value;
                    }
                }
            }
            catch { }
            return null;
        }

        static byte[] ReadExactly(System.Net.Sockets.NetworkStream stream, int count)
        {
            var buf = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buf, read, count - read);
                if (n <= 0) throw new IOException("connection closed");
                read += n;
            }
            return buf;
        }

        // ---- the engine ----
        string RunEngineQuick(string args)
        {
            try
            {
                var psi = new ProcessStartInfo(engine, args)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
                };
                using (var p = Process.Start(psi))
                {
                    p.StandardInput.Close();
                    var err = p.StandardError.ReadToEndAsync();
                    string o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(15000);
                    return o + err.Result;
                }
            }
            catch { return ""; }
        }

        string AppleAccount()
        {
            if (!File.Exists(engine)) return "";
            string o = RunEngineQuick("account list");
            if (o.Contains("No accounts found")) return "";
            var m = Regex.Match(o, @"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}");
            return m.Success ? m.Value : "";
        }

        Process StartEngine(string args, bool keepInput)
        {
            lock (procOut) procOut.Clear();
            var psi = new ProcessStartInfo(engine, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
            };
            psi.EnvironmentVariables["RUST_LOG"] = "info";
            var p = Process.Start(psi);
            if (!keepInput) p.StandardInput.Close();
            StartReader(p.StandardOutput);
            StartReader(p.StandardError);
            return p;
        }

        // Reads everything the engine prints (also prompts without a newline).
        void StartReader(StreamReader reader)
        {
            var t = new Thread(() =>
            {
                var buf = new char[256];
                int n;
                try { while ((n = reader.Read(buf, 0, buf.Length)) > 0) lock (procOut) procOut.Append(buf, 0, n); } catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        string ProcText() { lock (procOut) return procOut.ToString(); }

        static string CleanLine(string line)
        {
            return Regex.Replace(line ?? "", @"^\[[^\]]*\]\s*", "").Trim();
        }

        // ---------------------------------------------------------------- account / login

        void ShowLoginForm(bool show)
        {
            loginForm.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            codePanel.Visibility = Visibility.Collapsed;
            codeBox.Text = "";
            loginGoBtn.Content = "Log in";
            loginGoBtn.IsEnabled = true;
            loginStage = "creds";
            if (show && emailBox.Text.Length == 0 && account.Length > 0) emailBox.Text = account;
        }

        void UpdateAccount()
        {
            account = AppleAccount();
            if (account.Length > 0)
            {
                accountText.Text = account;
                accountText.Foreground = BrushOf("#E8ECF4");
                loginBtn.Content = "Change";
                loginBtn.Visibility = Visibility.Visible;
                if (phase != "login") ShowLoginForm(false);
            }
            else
            {
                accountText.Text = "Log in once to start refreshing";
                accountText.Foreground = BrushOf("#FBBF24");
                loginBtn.Visibility = Visibility.Collapsed;
                if (phase != "login") ShowLoginForm(true);
            }
        }

        // The password goes straight into the engine's input (never on a command line, in a file or in the log)
        // and the password box is cleared right away. The engine checks it with Apple and saves the login on this PC.
        void StartLogin()
        {
            if (phase != "idle" && phase != "countdown") return;
            string email = Regex.Replace(emailBox.Text ?? "", "[\"\\s]", "");
            string password = passwordBox.Password;
            if (email.Length == 0 || password.Length == 0) { SetStatus("Type your Apple ID and password.", "#FBBF24"); return; }
            if (!File.Exists(engine)) { SetStatus("The signing engine is missing (engine\\plumesign.exe).", "#F87171"); return; }
            workTimer.Stop();
            try { proc = StartEngine("account login -u \"" + email + "\"", true); }
            catch (Exception ex) { SetStatus("Couldn't start the login: " + ex.Message, "#F87171"); return; }
            proc.StandardInput.WriteLine(password);
            proc.StandardInput.Flush();
            passwordBox.Clear();
            password = null;
            phase = "login";
            loginStage = "wait";
            started = DateTime.Now;
            loginGoBtn.IsEnabled = false;
            loginGoBtn.Content = "Logging in...";
            refreshBtn.IsEnabled = false;
            SetStatus("Logging in to Apple...");
            workTimer.Interval = TimeSpan.FromMilliseconds(500);
            workTimer.Start();
        }

        void SendLoginCode()
        {
            string code = Regex.Replace(codeBox.Text ?? "", "\\s", "");
            if (code.Length == 0) { SetStatus("Type the code Apple sent you.", "#FBBF24"); return; }
            if (proc == null || proc.HasExited) { ShowLoginForm(true); return; }
            proc.StandardInput.WriteLine(code);
            proc.StandardInput.Flush();
            loginStage = "checking";
            loginGoBtn.IsEnabled = false;
            loginGoBtn.Content = "Checking...";
            SetStatus("Checking the code with Apple...");
        }

        void LoginFailed(string why)
        {
            try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { }
            workTimer.Stop();
            phase = "idle";
            ShowLoginForm(true);
            SetStatus(why, "#F87171");
            Log("Apple ID login failed: " + why);
            UpdateView();
        }

        void LoginTick()
        {
            string o = ProcText();
            if (o.Contains("Successfully logged"))
            {
                try { proc.WaitForExit(3000); } catch { }
                workTimer.Stop();
                phase = "idle";
                UpdateAccount();
                ShowLoginForm(false);
                SetStatus("Logged in as " + account + ". You only had to do this once.", "#34D399");
                Log("Apple ID login saved");
                UpdateView();
                if (udid != null && autoSwitch.IsChecked == true && NeedsRefresh(udid)) StartCountdown();
                return;
            }
            if (loginStage == "wait" && o.Contains("Enter the code sent"))
            {
                loginStage = "code";
                codePanel.Visibility = Visibility.Visible;
                loginGoBtn.Content = "Confirm code";
                loginGoBtn.IsEnabled = true;
                codeBox.Focus();
                SetStatus("Apple sent a code to your iPhone or Mac. Type it and press Confirm code.");
                return;
            }
            string failLine = o.Split('\n').Where(l => Regex.IsMatch(l, @"(?i)\berror\b|failed|incorrect|invalid|panicked")).LastOrDefault();
            if (failLine != null || (proc != null && proc.HasExited))
            {
                string why = failLine != null ? CleanLine(failLine) : "Apple did not accept the login.";
                if (Regex.IsMatch(why, @"(?i)password|credential|-22406|401|unauthor")) why = "Wrong Apple ID or password.";
                if (why.Length > 140) why = why.Substring(0, 140) + "...";
                LoginFailed("Login didn't work: " + why + " Try again.");
                return;
            }
            if ((DateTime.Now - started).TotalMinutes > 6) LoginFailed("Login took too long. Try again.");
        }

        // ---------------------------------------------------------------- view

        void UpdateView()
        {
            bool connected = udid != null;
            if (connected)
            {
                deviceDot.Fill = BrushOf("#34D399");
                deviceTitle.Text = "iPhone connected";
                deviceSub.Text = "ID " + udid.Substring(0, Math.Min(13, udid.Length)) + "...";
            }
            else
            {
                deviceDot.Fill = BrushOf("#5B6475");
                deviceTitle.Text = "Plug in your iPhone";
                deviceSub.Text = "USB cable, phone unlocked";
            }
            string id = udid ?? LastUdid();
            var days = id != null ? DaysLeft(id) : null;
            var at = id != null ? InstalledAt(id) : null;
            if (days == null)
            {
                daysNumber.Text = "-"; daysNumber.Foreground = BrushOf("#E8ECF4");
                daysLabel.Text = "not installed yet";
                daysSub.Text = "Refresh once to start the 7-day counter";
                daysBar.Width = 0;
            }
            else
            {
                daysNumber.Text = days.Value.ToString();
                daysLabel.Text = days.Value == 1 ? "day left" : "days left";
                daysSub.Text = "Expires " + at.Value.AddDays(ValidDays).ToString("ddd d MMM, HH:mm");
                string color = days.Value <= 1 ? "#F87171" : days.Value <= 2 ? "#FBBF24" : "#34D399";
                daysNumber.Foreground = BrushOf(color);
                daysBar.Background = BrushOf(color);
                daysBar.Width = Math.Max(6, 362 * Math.Min(1.0, days.Value / (double)ValidDays));
            }
            string newest = NewestVersion();
            string installed = id != null ? InstalledVersion(id) : "";
            versionInstalled.Text = "On your iPhone: " + (installed.Length > 0 ? installed : "unknown");
            versionNewest.Text = "Newest: " + (newest.Length > 0 ? newest : "checking...");
            if (installed.Length > 0 && newest.Length > 0 && installed == newest)
            {
                versionBadgeText.Text = "Up to date"; versionBadgeText.Foreground = BrushOf("#34D399");
                versionBadge.Background = BrushOf("#10291F"); versionBadge.BorderBrush = BrushOf("#1F5A3F");
            }
            else
            {
                versionBadgeText.Text = "Update available"; versionBadgeText.Foreground = BrushOf("#FBBF24");
                versionBadge.Background = BrushOf("#2A2410"); versionBadge.BorderBrush = BrushOf("#5A4A1F");
            }
            if (phase == "idle")
            {
                refreshBtn.IsEnabled = connected;
                refreshBtn.Content = connected ? "Refresh now" : "Plug in your iPhone to refresh";
                loginBtn.IsEnabled = true;
            }
        }

        // ---------------------------------------------------------------- refresh

        void RefreshFailed(string why)
        {
            Log("Refresh failed for " + udid + ": " + why);
            phase = "idle";
            workTimer.Stop();
            SetStatus(why, "#F87171");
            UpdateView();
            trayIcon.ShowBalloonTip(6000, "Build Your Future", why, Forms.ToolTipIcon.Warning);
        }

        void StartRefresh(string reason)
        {
            if (udid == null) { SetStatus("Plug in your iPhone first.", "#FBBF24"); return; }
            if (phase != "idle" && phase != "countdown") return;
            if (!File.Exists(engine)) { RefreshFailed("The signing engine is missing (engine\\plumesign.exe)."); return; }
            if (account.Length == 0) UpdateAccount();
            if (account.Length == 0)
            {
                phase = "idle";
                SetStatus("Log in with your Apple ID first (one time), under Apple ID.", "#FBBF24");
                ShowLoginForm(true);
                UpdateView();
                return;
            }
            phase = "update";
            refreshBtn.IsEnabled = false;
            loginBtn.IsEnabled = false;
            refreshBtn.Content = "Refreshing...";
            SetStatus("Getting the newest version...");
            started = DateTime.Now;
            if (personal)
            {
                // The creator's PC: the "SelfImprovement IPA Updater" task fetches the newest admin IPA into ipa-branch\.
                string ulog = System.IO.Path.Combine(root, "updater.log");
                updaterLogStamp = File.Exists(ulog) ? File.GetLastWriteTime(ulog) : DateTime.MinValue;
                try
                {
                    Process.Start(new ProcessStartInfo("schtasks.exe", "/run /tn \"SelfImprovement IPA Updater\"")
                    { UseShellExecute = false, CreateNoWindow = true });
                }
                catch { }
            }
            else
            {
                StartDownload(true);
            }
            Log("Refresh started for " + udid + " (" + reason + ")");
            workTimer.Interval = TimeSpan.FromMilliseconds(700);
            workTimer.Start();
        }

        // Public mode: VERSION.txt + BuildYourFuture.ipa from the website (only downloads the IPA when it changed).
        void StartDownload(bool needIpa)
        {
            downloadDone = false;
            downloadError = null;
            var t = new Thread(() =>
            {
                try
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    using (var wc = new WebClient())
                    {
                        wc.Headers[HttpRequestHeader.UserAgent] = "BuildYourFutureInstaller";
                        string remote = wc.DownloadString(WebsiteBase + "VERSION.txt?t=" + DateTime.Now.Ticks).Trim();
                        string localVersionFile = System.IO.Path.Combine(ipaDir, "VERSION.txt");
                        string ipa = System.IO.Path.Combine(ipaDir, "BuildYourFuture-Free.ipa");
                        string local = File.Exists(localVersionFile) ? File.ReadAllText(localVersionFile).Trim() : "";
                        if (remote.Length > 0) File.WriteAllText(System.IO.Path.Combine(ipaDir, "REMOTE_VERSION.txt"), remote);
                        if (needIpa && (remote != local || !File.Exists(ipa)))
                        {
                            string tmp = ipa + ".download";
                            wc.DownloadFile(WebsiteBase + "BuildYourFuture-Free.ipa?t=" + DateTime.Now.Ticks, tmp);
                            if (File.Exists(ipa)) File.Delete(ipa);
                            File.Move(tmp, ipa);
                            File.WriteAllText(localVersionFile, remote);
                        }
                    }
                }
                catch (Exception ex) { downloadError = ex.Message; }
                downloadDone = true;
            });
            t.IsBackground = true;
            t.Start();
        }

        void StartCountdown()
        {
            phase = "countdown";
            count = 5;
            refreshBtn.IsEnabled = true;
            refreshBtn.Content = "Cancel  (auto-refresh in " + count + ")";
            SetStatus("Auto-refresh: installing the newest version...");
            workTimer.Interval = TimeSpan.FromSeconds(1);
            workTimer.Start();
        }

        void Work()
        {
            try
            {
                switch (phase)
                {
                    case "countdown":
                        count--;
                        if (count <= 0) { workTimer.Stop(); phase = "idle"; StartRefresh("auto"); }
                        else refreshBtn.Content = "Cancel  (auto-refresh in " + count + ")";
                        break;
                    case "login":
                        LoginTick();
                        break;
                    case "update":
                        UpdateTick();
                        break;
                    case "installing":
                        InstallTick();
                        break;
                }
            }
            catch (Exception ex) { Log("Work error: " + ex.Message); }
        }

        void UpdateTick()
        {
            double waited = (DateTime.Now - started).TotalSeconds;
            if (personal)
            {
                string ulog = System.IO.Path.Combine(root, "updater.log");
                bool updaterRan = File.Exists(ulog) && File.GetLastWriteTime(ulog) > updaterLogStamp;
                // The updater only writes its log when something changed; give it up to 25 s.
                if (!updaterRan && waited < 25) return;
            }
            else
            {
                if (!downloadDone && waited < 180) return;
                if (downloadError != null && IpaToInstall() == null) { RefreshFailed("Couldn't download the app: " + downloadError); return; }
            }
            version = LocalVersion();
            UpdateView();
            string ipa = IpaToInstall();
            if (ipa == null) { RefreshFailed("Couldn't find the app to install. Check your internet."); return; }
            string copy = System.IO.Path.Combine(workDir, "install.ipa");
            File.Copy(ipa, copy, true);
            string deviceRef = UsbmuxDeviceId(udid);
            if (deviceRef == null) { RefreshFailed("Your iPhone isn't ready: unlock it, tap Trust if it asks, and try again."); return; }
            try
            {
                proc = StartEngine("sign --package \"" + copy + "\" --apple-id --register-and-install --udid " + deviceRef + " --custom-identifier " + BundleId, false);
            }
            catch (Exception ex) { RefreshFailed("Couldn't start the engine: " + ex.Message); return; }
            Log("Engine started for " + udid + " (" + version + ", " + System.IO.Path.GetFileName(ipa) + ")");
            SetStatus("Signing " + version + " with your Apple ID and installing... keep your iPhone plugged in and unlocked.");
            started = DateTime.Now;
            phase = "installing";
            workTimer.Interval = TimeSpan.FromSeconds(1);
            ShowMain();
        }

        void InstallTick()
        {
            string all = ProcText();
            string last = all.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).LastOrDefault();
            if (last != null)
            {
                string clean = CleanLine(last);
                if (clean.Length > 110) clean = clean.Substring(0, 110) + "...";
                SetStatus("Installing " + version + "...  " + clean);
            }
            bool timedOut = (DateTime.Now - started).TotalSeconds > 300;
            if (!proc.HasExited && !timedOut) return;
            if (timedOut && !proc.HasExited) { try { proc.Kill(); } catch { } }
            Thread.Sleep(200);
            all = ProcText();
            try { File.AppendAllText(logFile, all + Environment.NewLine, Encoding.UTF8); } catch { }
            string errorLine = all.Split('\n').FirstOrDefault(l => Regex.IsMatch(l, @"\bERROR\b|panicked|Error:"));
            int exit = 0;
            try { exit = proc.ExitCode; } catch { }
            if (!timedOut && exit == 0 && errorLine == null)
            {
                SaveInstall(udid, version);
                Log("Installed " + version + " on " + udid);
                phase = "idle";
                workTimer.Stop();
                SetStatus("Done! " + version + " is on your iPhone. 7 days left.", "#34D399");
                UpdateView();
                trayIcon.ShowBalloonTip(5000, "Build Your Future", "Refreshed: " + version + ", 7 days left.", Forms.ToolTipIcon.Info);
                return;
            }
            string why;
            if (timedOut) why = "Took too long.";
            else if (Regex.IsMatch(all, @"(?i)login first|log in first|not logged in|no accounts found|invalid credentials|-22406|re-?authenticate|session (has )?expired|unauthori[sz]ed")) why = "Your Apple ID login needs to be renewed: log in again under Apple ID.";
            else if (errorLine != null) why = CleanLine(errorLine);
            else why = "The engine stopped (code " + exit + ").";
            if (why.Length > 160) why = why.Substring(0, 160) + "...";
            RefreshFailed("Refresh didn't work: " + why);
        }

        // ---------------------------------------------------------------- watching for the iPhone

        void Poll()
        {
            try
            {
                if (showSignal.WaitOne(0)) ShowMain();
                var now = ConnectedIPhones();
                string newOne = now.FirstOrDefault(x => !known.Contains(x));
                known = now;
                if (phase == "idle" || phase == "countdown")
                {
                    udid = now.Count > 0 ? now[0] : null;
                    if (phase == "countdown" && udid == null) { workTimer.Stop(); phase = "idle"; SetStatus("iPhone unplugged.", "#9AA4B5"); }
                }
                UpdateView();
                if (newOne != null)
                {
                    Log("iPhone connected: " + newOne);
                    ShowMain();
                    if (account.Length == 0) SetStatus("iPhone connected. Log in with your Apple ID once to start refreshing.", "#FBBF24");
                    else if (autoSwitch.IsChecked == true && phase == "idle")
                    {
                        if (NeedsRefresh(newOne)) StartCountdown();
                        else SetStatus("All good: newest version, plenty of days left.", "#34D399");
                    }
                    else if (phase == "idle") SetStatus("iPhone connected. Press Refresh now when you want.");
                }
                else if (udid != null && account.Length > 0 && autoSwitch.IsChecked == true && phase == "idle")
                {
                    // Plugged in for a long time: refresh when only 1 day is left (at most once an hour).
                    var days = DaysLeft(udid);
                    if (days != null && days.Value <= 1 && (DateTime.Now - lastAutoAttempt).TotalMinutes > 60)
                    {
                        lastAutoAttempt = DateTime.Now;
                        StartRefresh("auto (1 day left)");
                    }
                }
            }
            catch (Exception ex) { Log("Poll error: " + ex.Message); }
        }

        // ---------------------------------------------------------------- shortcuts / start with Windows

        static void CreateShortcut(string lnk, string target, string args, string workDirectory, string icon, string description)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            object sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            Type t = sc.GetType();
            t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
            t.InvokeMember("Arguments", BindingFlags.SetProperty, null, sc, new object[] { args });
            t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { workDirectory });
            if (icon != null) t.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { icon + ",0" });
            t.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { description });
            t.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
        }

        void EnsureShortcuts()
        {
            try
            {
                string exe = Process.GetCurrentProcess().MainModule.FileName;
                string icon = File.Exists(icoPath) ? icoPath : null;
                foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.Programs) })
                {
                    string lnk = System.IO.Path.Combine(dir, "Build Your Future Installer.lnk");
                    if (!File.Exists(lnk)) CreateShortcut(lnk, exe, "", root, icon, "Refresh Build Your Future on your iPhone");
                }
            }
            catch (Exception ex) { Log("Shortcut error: " + ex.Message); }
        }

        // Public mode only (the creator's PC uses a scheduled task): a shortcut in the Startup folder, so
        // auto-refresh keeps working after a restart. Turning auto-refresh off removes it.
        void SetStartWithWindows(bool on)
        {
            try
            {
                string lnk = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Build Your Future Installer.lnk");
                if (on)
                {
                    if (!File.Exists(lnk))
                        CreateShortcut(lnk, Process.GetCurrentProcess().MainModule.FileName, "--tray", root, File.Exists(icoPath) ? icoPath : null, "Build Your Future auto-refresh");
                }
                else if (File.Exists(lnk)) File.Delete(lnk);
            }
            catch (Exception ex) { Log("Startup shortcut error: " + ex.Message); }
        }
    }
}
