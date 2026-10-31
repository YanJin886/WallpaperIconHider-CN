using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;

class Program
{
    // --------------------------------------------------------------------
    // STRUCTURES WINDOWS
    // --------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    // --------------------------------------------------------------------
    // API WINDOWS
    // --------------------------------------------------------------------

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    // --------------------------------------------------------------------
    // CURSEURS SYSTÈME
    // --------------------------------------------------------------------

    // SetSystemCursor remplace un curseur système de manière globale.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetSystemCursor(
        IntPtr hcur,
        uint id);

    // Recharge les curseurs système configurés par Windows.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(
        uint uiAction,
        uint uiParam,
        IntPtr pvParam,
        uint fWinIni);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateCursor(
        IntPtr hInst,
        int xHotSpot,
        int yHotSpot,
        int nWidth,
        int nHeight,
        byte[] pvANDPlane,
        byte[] pvXORPlane);

    private const uint SPI_SETCURSORS = 0x0057;
    private const uint SPIF_SENDCHANGE = 0x0002;

    // Identifiants des principaux curseurs système Windows.
    private static readonly uint[] SystemCursorIds =
    {
        32512, // OCR_NORMAL
        32513, // OCR_IBEAM
        32514, // OCR_WAIT
        32515, // OCR_CROSS
        32516, // OCR_UP
        32642, // OCR_SIZENWSE
        32643, // OCR_SIZENESW
        32644, // OCR_SIZEWE
        32645, // OCR_SIZENS
        32646, // OCR_SIZEALL
        32648, // OCR_NO
        32649, // OCR_HAND
        32650, // OCR_APPSTARTING
        32651  // OCR_HELP
    };

    private const int VK_LBUTTON = 0x01;
    private const int VK_RBUTTON = 0x02;

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(
        IntPtr hWnd,
        out RECT lpRect);

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromWindow(
        IntPtr hWnd,
        uint dwFlags);

    [DllImport("user32.dll")]
    static extern bool GetMonitorInfo(
        IntPtr hMonitor,
        ref MONITORINFO lpmi);

    // Instrumentation : récupérer le PID associé à une fenêtre.
    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(
        IntPtr hWnd,
        out uint processId);

    // Instrumentation : récupérer le titre de la fenêtre.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(
        IntPtr hWnd,
        StringBuilder lpString,
        int nMaxCount);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    // --------------------------------------------------------------------
    // CONFIGURATION
    // --------------------------------------------------------------------

    // Chemin détecté automatiquement vers Wallpaper Engine.
    private static string? wallpaperEnginePath;

    private const int DefaultInactivityDelaySeconds = 5;
    private const int MinimumDelaySeconds = 1;
    private const int MaximumDelaySeconds = 10;

    private static int inactivityDelaySeconds =
        DefaultInactivityDelaySeconds;

    private static bool programEnabled = true;
    private static bool iconsAreVisible = true;
    private static bool cursorIsHidden = false;

    // Indique que l'application a remplacé les curseurs système par des
    // curseurs transparents. Le remplacement est global à Windows.
    private static bool systemCursorsReplaced = false;

    // --------------------------------------------------------------------
    // SURVEILLANCE SOURIS
    // --------------------------------------------------------------------

    private static int previousMouseX;
    private static int previousMouseY;
    private static bool mousePositionInitialized = false;
    private static DateTime lastMouseMovement;

    private static NotifyIcon? trayIcon;
    private static System.Windows.Forms.Timer? mouseTimer;

    // Icônes de la zone de notification.
    // Elles sont embarquées directement dans l'exécutable.
    private static Icon? trayIconOpen;
    private static Icon? trayIconClosed;

    // --------------------------------------------------------------------
    // JOURNAL DE DIAGNOSTIC
    // --------------------------------------------------------------------

    private static readonly object logLock = new object();

    private static string lastWindowDescription = "";

    private static bool lastCoveringScreenState = false;
    private static bool coveringStateInitialized = false;

    private static bool inactivityBlockedByFullscreen = false;

    // --------------------------------------------------------------------
    // OUTILS DE DIAGNOSTIC FENÊTRE
    // --------------------------------------------------------------------

    private static string GetWindowTitle(IntPtr hWnd)
    {
        try
        {
            if (hWnd == IntPtr.Zero)
                return "Aucune";

            StringBuilder title =
                new StringBuilder(512);

            int length =
                GetWindowText(
                    hWnd,
                    title,
                    title.Capacity);

            if (length <= 0)
                return "(sans titre)";

            return title.ToString();
        }
        catch
        {
            return "(titre inaccessible)";
        }
    }

    private static string GetWindowProcessName(
        IntPtr hWnd,
        out uint processId)
    {
        processId = 0;

        try
        {
            if (hWnd == IntPtr.Zero)
                return "Aucun";

            GetWindowThreadProcessId(
                hWnd,
                out processId);

            if (processId == 0)
                return "Inconnu";

            using Process process =
                Process.GetProcessById(
                    (int)processId);

            try
            {
                return process.ProcessName;
            }
            catch
            {
                return "Inconnu";
            }
        }
        catch
        {
            return "Inconnu";
        }
    }

    private static string GetDetailedWindowDescription(
        IntPtr hWnd,
        bool coveringScreen)
    {
        if (hWnd == IntPtr.Zero)
            return "Fenêtre active : inconnue";

        uint processId;

        string processName =
            GetWindowProcessName(
                hWnd,
                out processId);

        string title =
            GetWindowTitle(hWnd);

        if (IsZoomed(hWnd))
        {
            return
                $"Fenêtre active : " +
                $"handle=0x{hWnd.ToInt64():X}, " +
                $"processus={processName}, " +
                $"PID={processId}, " +
                $"titre=\"{title}\", " +
                $"état=MAXIMISÉE, " +
                $"écran couvert=OUI";
        }

        if (coveringScreen)
        {
            return
                $"Fenêtre active : " +
                $"handle=0x{hWnd.ToInt64():X}, " +
                $"processus={processName}, " +
                $"PID={processId}, " +
                $"titre=\"{title}\", " +
                $"état=PLEIN ÉCRAN/COUVRE L'ÉCRAN, " +
                $"écran couvert=OUI";
        }

        return
            $"Fenêtre active : " +
            $"handle=0x{hWnd.ToInt64():X}, " +
            $"processus={processName}, " +
            $"PID={processId}, " +
            $"titre=\"{title}\", " +
            $"état=NORMALE, " +
            $"écran couvert=NON";
    }

    // --------------------------------------------------------------------
    // JOURNAL
    // --------------------------------------------------------------------

    private const long MaxLogFileSizeBytes =
        200L * 1024L * 1024L;

    private static string GetLogPath()
    {
        return Path.Combine(
            GetSettingsDirectory(),
            "log.txt");
    }

    private static void WriteLog(string message)
    {
        try
        {
            Directory.CreateDirectory(
                GetSettingsDirectory());

            string line =
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";

            lock (logLock)
            {
                string logPath =
                    GetLogPath();

                if (File.Exists(logPath))
                {
                    FileInfo logFile =
                        new FileInfo(logPath);

                    if (logFile.Length >=
                        MaxLogFileSizeBytes)
                    {
                        File.Delete(logPath);
                    }
                }

                File.AppendAllText(
                    logPath,
                    line + Environment.NewLine);
            }
        }
        catch
        {
            // Le journal ne doit jamais pouvoir perturber
            // le fonctionnement de l'application.
        }
    }

    // --------------------------------------------------------------------
    // MENU CONTEXTUEL PERSONNALISÉ
    // --------------------------------------------------------------------

    // Un seul popup peut être ouvert à la fois.
    private static ModernMenuForm? activeMenuForm;

    // Point d'ancrage mémorisé lorsque le menu est ouvert depuis l'icône
    // de la zone de notification. Les sous-menus réutilisent exactement
    // cette ancre au lieu de suivre la position de la souris.
    private static Point menuAnchorPoint;
    private static Rectangle menuWorkingArea;
    private static bool menuAnchorInitialized = false;

    // --------------------------------------------------------------------
    // CONFIGURATION LOCALE
    // --------------------------------------------------------------------

    private class Settings
    {
        public int InactivityDelaySeconds { get; set; } =
            DefaultInactivityDelaySeconds;

        public bool StartWithWindows { get; set; } = false;

        public bool HideCursorWithIcons { get; set; } = false;

        public string? InstalledExecutablePath { get; set; }
    }

    private static string GetSettingsDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "WallpaperIconHider");
    }

    private static string GetSettingsPath()
    {
        return Path.Combine(
            GetSettingsDirectory(),
            "settings.json");
    }

    private static void LoadSettings()
    {
        try
        {
            string path =
                GetSettingsPath();

            if (!File.Exists(path))
            {
                inactivityDelaySeconds =
                    DefaultInactivityDelaySeconds;

                return;
            }

            string json =
                File.ReadAllText(path);

            Settings? settings =
                JsonSerializer.Deserialize<Settings>(
                    json);

            if (settings == null)
            {
                inactivityDelaySeconds =
                    DefaultInactivityDelaySeconds;

                return;
            }

            if (settings.InactivityDelaySeconds <
                    MinimumDelaySeconds ||
                settings.InactivityDelaySeconds >
                    MaximumDelaySeconds)
            {
                inactivityDelaySeconds =
                    DefaultInactivityDelaySeconds;
            }
            else
            {
                inactivityDelaySeconds =
                    settings.InactivityDelaySeconds;
            }

            startWithWindows =
                settings.StartWithWindows;

            hideCursorWithIcons =
                settings.HideCursorWithIcons;

            installedExecutablePath =
                settings.InstalledExecutablePath;
        }
        catch
        {
            inactivityDelaySeconds =
                DefaultInactivityDelaySeconds;

            startWithWindows = false;
            installedExecutablePath = null;
        }
    }

    private static void SaveSettings()
    {
        try
        {
            string directory =
                GetSettingsDirectory();

            Directory.CreateDirectory(
                directory);

            Settings settings =
                new Settings
                {
                    InactivityDelaySeconds =
                        inactivityDelaySeconds,

                    StartWithWindows =
                        startWithWindows,

                    HideCursorWithIcons =
                        hideCursorWithIcons,

                    InstalledExecutablePath =
                        installedExecutablePath
                };

            JsonSerializerOptions options =
                new JsonSerializerOptions
                {
                    WriteIndented = true
                };

            string json =
                JsonSerializer.Serialize(
                    settings,
                    options);

            File.WriteAllText(
                GetSettingsPath(),
                json);
        }
        catch
        {
            // Une erreur de sauvegarde ne doit jamais
            // faire planter l'application.
        }
    }

    // --------------------------------------------------------------------
    // DÉMARRAGE WINDOWS
    // --------------------------------------------------------------------

    private static bool startWithWindows = false;
    private static bool hideCursorWithIcons = false;

    private const string StartupShortcutName =
        "Wallpaper Icon Hider.lnk";

    // --------------------------------------------------------------------
    // INSTALLATION
    // --------------------------------------------------------------------

    private const string InstalledExecutableSetting =
        "InstalledExecutablePath";

    private static bool installationCompleted = false;

    private static string? installedExecutablePath;

    private static string GetInstalledExecutablePath()
    {
        if (!string.IsNullOrWhiteSpace(installedExecutablePath))
            return installedExecutablePath;

        try
        {
            string path = GetSettingsPath();

            if (!File.Exists(path))
                return "";

            string json = File.ReadAllText(path);
            using JsonDocument document = JsonDocument.Parse(json);

            if (document.RootElement.TryGetProperty(
                    InstalledExecutableSetting,
                    out JsonElement element))
            {
                return element.GetString() ?? "";
            }
        }
        catch
        {
            // Une erreur de lecture ne doit pas faire planter
            // l'application.
        }

        return "";
    }

    private static bool IsInstallationCompleted()
    {
        string installedPath = GetInstalledExecutablePath();

        if (string.IsNullOrWhiteSpace(installedPath))
            return false;

        return File.Exists(installedPath);
    }

    private static bool SaveInstalledExecutablePath(string executablePath)
    {
        try
        {
            string directory = GetSettingsDirectory();
            Directory.CreateDirectory(directory);

            Settings settings;

            string settingsPath = GetSettingsPath();

            if (File.Exists(settingsPath))
            {
                string json = File.ReadAllText(settingsPath);

                settings =
                    JsonSerializer.Deserialize<Settings>(json)
                    ?? new Settings();
            }
            else
            {
                settings = new Settings();
            }

            settings.InstalledExecutablePath =
                executablePath;

            JsonSerializerOptions options =
                new JsonSerializerOptions
                {
                    WriteIndented = true
                };

            File.WriteAllText(
                settingsPath,
                JsonSerializer.Serialize(
                    settings,
                    options));

            installedExecutablePath =
                executablePath;

            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed class InstallationForm : Form
    {
        private readonly TextBox destinationTextBox;

        public string SelectedPath =>
            destinationTextBox.Text.Trim();

        public InstallationForm()
        {
            Text =
                "安装 Wallpaper Icon Hider";

            StartPosition =
                FormStartPosition.CenterScreen;

            FormBorderStyle =
                FormBorderStyle.FixedDialog;

            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;

            ClientSize =
                new Size(520, 270);

            Font =
                new Font(
                    "Microsoft YaHei UI",
                    9.5f,
                    FontStyle.Regular,
                    GraphicsUnit.Point);

            Label titleLabel =
                new Label
                {
                    AutoSize = false,
                    Location = new Point(28, 22),
                    Size = new Size(460, 40),
                    Text =
                        "欢迎使用 Wallpaper Icon Hider",
                    Font =
                        new Font(
                            "Microsoft YaHei UI",
                            14f,
                            FontStyle.Bold),
                    ForeColor =
                        Color.FromArgb(32, 32, 32)
                };

            Controls.Add(titleLabel);

            Label descriptionLabel =
                new Label
                {
                    AutoSize = false,
                    Location = new Point(30, 66),
                    Size = new Size(455, 44),
                    Text =
                        "请选择 Wallpaper Icon Hider 的安装文件夹。",
                    ForeColor =
                        Color.FromArgb(80, 80, 80)
                };

            Controls.Add(descriptionLabel);

            destinationTextBox =
                new TextBox
                {
                    Location = new Point(30, 121),
                    Size = new Size(350, 28),
                    Text =
                        Path.Combine(
                            Environment.GetFolderPath(
                                Environment.SpecialFolder.LocalApplicationData),
                            "Programs",
                            "Wallpaper Icon Hider")
                };

            Controls.Add(destinationTextBox);

            Button browseButton =
                new Button
                {
                    Text = "浏览…",
                    Location = new Point(390, 120),
                    Size = new Size(100, 36)
                };

            browseButton.Click +=
                (_, _) =>
                {
                    using FolderBrowserDialog dialog =
                        new FolderBrowserDialog
                        {
                            Description =
                                "请选择安装文件夹。",
                            UseDescriptionForTitle = true,
                            SelectedPath =
                                Directory.Exists(
                                    destinationTextBox.Text)
                                    ? destinationTextBox.Text
                                    : Environment.GetFolderPath(
                                        Environment.SpecialFolder.LocalApplicationData)
                        };

                    if (dialog.ShowDialog(this) ==
                        DialogResult.OK)
                    {
                        destinationTextBox.Text =
                            dialog.SelectedPath;
                    }
                };

            Controls.Add(browseButton);

            Label infoLabel =
                new Label
                {
                    AutoSize = false,
                    Location = new Point(30, 160),
                    Size = new Size(460, 52),
                    Text =
                        "需要通过 Steam 安装 Wallpaper Engine。\n" +
                        "本程序不会修改 Wallpaper Engine。",
                    ForeColor =
                        Color.FromArgb(95, 95, 95)
                };

            Controls.Add(infoLabel);

            Button cancelButton =
                new Button
                {
                    Text = "取消",
                    DialogResult =
                        DialogResult.Cancel,
                    Location = new Point(300, 222),
                    Size = new Size(90, 36)
                };

            Controls.Add(cancelButton);

            Button installButton =
                new Button
                {
                    Text = "安装",
                    DialogResult =
                        DialogResult.OK,
                    Location = new Point(400, 222),
                    Size = new Size(90, 36)
                };

            installButton.Click +=
                (_, _) =>
                {
                    if (string.IsNullOrWhiteSpace(
                            destinationTextBox.Text))
                    {
                        MessageBox.Show(
                            this,
                            "请选择安装文件夹。",
                            "安装",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);

                        DialogResult =
                            DialogResult.None;
                    }
                };

            Controls.Add(installButton);

            AcceptButton = installButton;
            CancelButton = cancelButton;
        }
    }

    private sealed class InstallationCompleteForm : Form
    {
        public bool CreateDesktopShortcut =>
            shortcutCheckBox.Checked;

        public bool LaunchApplication =>
            launchCheckBox.Checked;

        private readonly CheckBox shortcutCheckBox;
        private readonly CheckBox launchCheckBox;

        public InstallationCompleteForm()
        {
            Text =
                "安装完成";

            StartPosition =
                FormStartPosition.CenterScreen;

            FormBorderStyle =
                FormBorderStyle.FixedDialog;

            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;

            ClientSize =
                new Size(520, 280);

            Font =
                new Font(
                    "Microsoft YaHei UI",
                    9.5f,
                    FontStyle.Regular,
                    GraphicsUnit.Point);

            Label titleLabel =
                new Label
                {
                    AutoSize = false,
                    Location = new Point(28, 22),
                    Size = new Size(460, 40),
                    Text =
                        "安装完成",
                    Font =
                        new Font(
                            "Microsoft YaHei UI",
                            14f,
                            FontStyle.Bold),
                    ForeColor =
                        Color.FromArgb(32, 32, 32)
                };

            Controls.Add(titleLabel);

            Label descriptionLabel =
                new Label
                {
                    AutoSize = false,
                    Location = new Point(30, 66),
                    Size = new Size(455, 45),
                    Text =
                        "Wallpaper Icon Hider 已安装成功。",
                    ForeColor =
                        Color.FromArgb(80, 80, 80)
                };

            Controls.Add(descriptionLabel);

            shortcutCheckBox =
                new CheckBox
                {
                    AutoSize = true,
                    Location = new Point(30, 120),
                    Text =
                        "创建桌面快捷方式",
                    Checked = true
                };

            Controls.Add(shortcutCheckBox);

            launchCheckBox =
                new CheckBox
                {
                    AutoSize = true,
                    Location = new Point(30, 150),
                    Text =
                        "立即启动 Wallpaper Icon Hider",
                    Checked = true
                };

            Controls.Add(launchCheckBox);

            Label infoLabel =
                new Label
                {
                    AutoSize = false,
                    Location = new Point(30, 180),
                    Size = new Size(460, 38),
                    Text =
                        "这些选项之后仍可更改。",
                    ForeColor =
                        Color.FromArgb(95, 95, 95)
                };

            Controls.Add(infoLabel);

            Button finishButton =
                new Button
                {
                    Text = "完成",
                    DialogResult =
                        DialogResult.OK,
                    Location = new Point(390, 232),
                    Size = new Size(100, 36)
                };

            Controls.Add(finishButton);

            AcceptButton = finishButton;
        }
    }

    private static bool InstallAndRelaunchIfNeeded()
    {
        string currentExecutable =
            GetExecutablePath();

        if (IsInstallationCompleted())
        {
            string installedExecutable =
                GetInstalledExecutablePath();

            if (!string.Equals(
                    currentExecutable,
                    installedExecutable,
                    StringComparison.OrdinalIgnoreCase) &&
                File.Exists(installedExecutable))
            {
                try
                {
                    Process.Start(
                        new ProcessStartInfo
                        {
                            FileName =
                                installedExecutable,
                            UseShellExecute = true
                        });

                    return false;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        "无法启动已安装的程序。\n\n" +
                        ex.Message,
                        "Wallpaper Icon Hider",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);

                    return false;
                }
            }

            installationCompleted = true;
            return true;
        }

        using InstallationForm form =
            new InstallationForm();

        DialogResult result =
            form.ShowDialog();

        if (result != DialogResult.OK)
        {
            return false;
        }

        string destinationDirectory =
            form.SelectedPath;

        try
        {
            Directory.CreateDirectory(
                destinationDirectory);

            string installedExecutable =
                Path.Combine(
                    destinationDirectory,
                    "WallpaperIconHider.exe");

            File.Copy(
                currentExecutable,
                installedExecutable,
                true);

            if (!SaveInstalledExecutablePath(
                    installedExecutable))
            {
                MessageBox.Show(
                    "程序已复制，但无法保存" +
                    "安装信息。",
                    "Wallpaper Icon Hider",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }

            installedExecutablePath =
                installedExecutable;

            using InstallationCompleteForm completionForm =
                new InstallationCompleteForm();

            DialogResult completionResult =
                completionForm.ShowDialog();

            if (completionResult ==
                DialogResult.OK &&
                completionForm.CreateDesktopShortcut)
            {
                if (CreateDesktopShortcut(
                        installedExecutable))
                {
                    WriteLog(
                        $"Raccourci bureau créé : " +
                        $"{GetDesktopShortcutPath()}");
                }
                else
                {
                    MessageBox.Show(
                        "安装已完成，但无法创建" +
                        "桌面快捷方式。",
                        "Wallpaper Icon Hider",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            }
            else
            {
                WriteLog(
                    "Raccourci bureau : non créé.");
            }

            WriteLog(
                $"Installation terminée : {installedExecutable}");

            if (completionForm.LaunchApplication)
            {
                try
                {
                    Process.Start(
                        new ProcessStartInfo
                        {
                            FileName =
                                installedExecutable,
                            UseShellExecute = true
                        });

                    WriteLog(
                        "Lancement immédiat de l'application demandé.");
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        "安装已完成，但无法" +
                        "启动程序。\n\n" +
                        ex.Message,
                        "Wallpaper Icon Hider",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);

                    WriteLog(
                        $"ERREUR lancement immédiat : " +
                        $"{ex.GetType().Name}: {ex.Message}");
                }
            }
            else
            {
                WriteLog(
                    "Lancement immédiat de l'application désactivé.");
            }

            return false;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "安装失败。\n\n" +
                ex.Message,
                "Wallpaper Icon Hider",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);

            return false;
        }
    }

    private static string GetStartupDirectory()
    {
        return Environment.GetFolderPath(
            Environment.SpecialFolder.Startup);
    }

    private static string GetStartupShortcutPath()
    {
        return Path.Combine(
            GetStartupDirectory(),
            StartupShortcutName);
    }

    private static string GetExecutablePath()
    {
        return Environment.ProcessPath
            ?? Application.ExecutablePath;
    }

    // --------------------------------------------------------------------
    // DÉTECTION DE WALLPAPER ENGINE
    // --------------------------------------------------------------------

    private static string? GetSteamInstallPathFromRegistry()
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key =
                Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Valve\Steam");

            string? path = key?.GetValue("SteamPath") as string;

            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                return path;
        }
        catch { }

        try
        {
            using Microsoft.Win32.RegistryKey? key =
                Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\WOW6432Node\Valve\Steam");

            string? path = key?.GetValue("InstallPath") as string;

            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                return path;
        }
        catch { }

        return null;
    }

    private static IEnumerable<string> GetSteamLibraryPaths()
    {
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        string? steamPath = GetSteamInstallPathFromRegistry();

        if (string.IsNullOrWhiteSpace(steamPath))
            return paths;

        paths.Add(Path.GetFullPath(steamPath));

        string libraryFile = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");

        if (!File.Exists(libraryFile))
            return paths;

        try
        {
            foreach (string line in File.ReadLines(libraryFile))
            {
                Match match = Regex.Match(
                    line,
                    "\"path\"\\s+\"([^\"]+)\"",
                    RegexOptions.IgnoreCase);

                if (!match.Success)
                    continue;

                string libraryPath = match.Groups[1].Value.Replace(@"\\", @"\");

                if (Directory.Exists(libraryPath))
                    paths.Add(Path.GetFullPath(libraryPath));
            }
        }
        catch { }

        return paths;
    }

    private static string? FindWallpaperEnginePath()
    {
        foreach (string libraryPath in GetSteamLibraryPaths())
        {
            string candidate = Path.Combine(
                libraryPath, "steamapps", "common",
                "wallpaper_engine", "wallpaper64.exe");

            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static bool EnsureWallpaperEnginePath()
    {
        if (!string.IsNullOrWhiteSpace(wallpaperEnginePath) &&
            File.Exists(wallpaperEnginePath))
            return true;

        wallpaperEnginePath = FindWallpaperEnginePath();

        if (!string.IsNullOrWhiteSpace(wallpaperEnginePath))
        {
            WriteLog($"Wallpaper Engine détecté automatiquement : {wallpaperEnginePath}");
            return true;
        }

        WriteLog("ERREUR : Wallpaper Engine introuvable.");
        return false;
    }

    private static bool IsStartupShortcutPresent()
    {
        return File.Exists(
            GetStartupShortcutPath());
    }

    private static void CreateStartupShortcut()
    {
        try
        {
            string shortcutPath =
                GetStartupShortcutPath();

            string executablePath =
                GetExecutablePath();

            if (string.IsNullOrWhiteSpace(
                    executablePath))
            {
                return;
            }

            Type? shellType =
                Type.GetTypeFromProgID(
                    "WScript.Shell");

            if (shellType == null)
                return;

            dynamic shell =
                Activator.CreateInstance(
                    shellType)!;

            dynamic shortcut =
                shell.CreateShortcut(
                    shortcutPath);

            shortcut.TargetPath =
                executablePath;

            shortcut.WorkingDirectory =
                Path.GetDirectoryName(
                    executablePath);

            shortcut.Description =
                "随 Windows 启动 Wallpaper Icon Hider。";

            shortcut.IconLocation =
                executablePath;

            shortcut.Save();

            Marshal.FinalReleaseComObject(
                shortcut);

            Marshal.FinalReleaseComObject(
                shell);
        }
        catch
        {
            // Une erreur de création du raccourci
            // ne doit pas faire planter le programme.
        }
    }

    private static string GetDesktopShortcutPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.DesktopDirectory),
            "Wallpaper Icon Hider.lnk");
    }

    private static bool CreateDesktopShortcut(
        string executablePath)
    {
        try
        {
            string shortcutPath =
                GetDesktopShortcutPath();

            Type? shellType =
                Type.GetTypeFromProgID(
                    "WScript.Shell");

            if (shellType == null)
                return false;

            dynamic shell =
                Activator.CreateInstance(
                    shellType)!;

            dynamic shortcut =
                shell.CreateShortcut(
                    shortcutPath);

            shortcut.TargetPath =
                executablePath;

            shortcut.WorkingDirectory =
                Path.GetDirectoryName(
                    executablePath);

            shortcut.Description =
                "启动 Wallpaper Icon Hider。";

            shortcut.IconLocation =
                executablePath;

            shortcut.Save();

            Marshal.FinalReleaseComObject(
                shortcut);

            Marshal.FinalReleaseComObject(
                shell);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void RemoveStartupShortcut()
    {
        try
        {
            string shortcutPath =
                GetStartupShortcutPath();

            if (File.Exists(shortcutPath))
            {
                File.Delete(shortcutPath);
            }
        }
        catch
        {
            // Une erreur de suppression ne doit pas
            // faire planter le programme.
        }
    }

    private static void UpdateStartupSetting(
        bool enabled)
    {
        startWithWindows = enabled;

        if (enabled)
        {
            CreateStartupShortcut();
        }
        else
        {
            RemoveStartupShortcut();
        }

        SaveSettings();

        WriteLog(
            $"Démarrage Windows : " +
            $"{(enabled ? "ACTIVÉ" : "DÉSACTIVÉ")}.");

        UpdateStartupMenu();
    }

    private static void UpdateStartupMenu()
    {
        // Le menu personnalisé est reconstruit à chaque ouverture.
        // Aucun contrôle persistant n'a donc besoin d'être mis à jour ici.
    }

    // --------------------------------------------------------------------
    // WALLPAPER ENGINE
    // --------------------------------------------------------------------

    private static bool SendWallpaperCommand(
        string command,
        bool waitForExit = false)
    {
        try
        {
            if (!EnsureWallpaperEnginePath())
                return false;

            ProcessStartInfo startInfo =
                new ProcessStartInfo
                {
                    FileName =
                        wallpaperEnginePath!,

                    UseShellExecute = false,

                    CreateNoWindow = true
                };

            startInfo.ArgumentList.Add(
                "-control");

            startInfo.ArgumentList.Add(
                command);

            WriteLog(
                $"Wallpaper Engine : " +
                $"lancement de la commande {command}.");

            Stopwatch stopwatch =
                Stopwatch.StartNew();

            using Process? process =
                Process.Start(startInfo);

            if (process == null)
            {
                WriteLog(
                    $"ERREUR Wallpaper Engine : " +
                    $"impossible de démarrer le processus " +
                    $"pour {command}.");

                return false;
            }

            WriteLog(
                $"Wallpaper Engine : processus lancé — " +
                $"PID={process.Id}, " +
                $"commande={command}.");

            if (waitForExit)
            {
                bool exited =
                    process.WaitForExit(2000);

                stopwatch.Stop();

                if (exited)
                {
                    WriteLog(
                        $"Wallpaper Engine : " +
                        $"processus terminé — " +
                        $"PID={process.Id}, " +
                        $"ExitCode={process.ExitCode}, " +
                        $"durée={stopwatch.ElapsedMilliseconds} ms.");
                }
                else
                {
                    WriteLog(
                        $"AVERTISSEMENT Wallpaper Engine : " +
                        $"processus toujours actif après 2000 ms — " +
                        $"PID={process.Id}, " +
                        $"durée={stopwatch.ElapsedMilliseconds} ms.");
                }
            }
            else
            {
                stopwatch.Stop();

                WriteLog(
                    $"Wallpaper Engine : " +
                    $"commande {command} envoyée — " +
                    $"PID={process.Id}, " +
                    $"durée lancement=" +
                    $"{stopwatch.ElapsedMilliseconds} ms.");
            }

            return true;
        }
        catch (Exception ex)
        {
            WriteLog(
                $"ERREUR Wallpaper Engine : " +
                $"{command} — " +
                $"{ex.GetType().Name}: {ex.Message}");

            return false;
        }
    }

    // --------------------------------------------------------------------
    // CURSEUR DE SOURIS
    // --------------------------------------------------------------------

    private static bool ReplaceSystemCursorsWithInvisible()
    {
        try
        {
            // Curseur monochrome totalement transparent :
            // AND = 1 partout, XOR = 0 partout.
            const int cursorSize = 32;
            int planeSize = cursorSize * cursorSize / 8;

            byte[] andMask = new byte[planeSize];
            byte[] xorMask = new byte[planeSize];

            Array.Fill(andMask, (byte)0xFF);
            Array.Fill(xorMask, (byte)0x00);

            int replacedCount = 0;

            foreach (uint cursorId in SystemCursorIds)
            {
                IntPtr invisibleCursor =
                    CreateCursor(
                        IntPtr.Zero,
                        0,
                        0,
                        cursorSize,
                        cursorSize,
                        andMask,
                        xorMask);

                if (invisibleCursor == IntPtr.Zero)
                    continue;

                if (SetSystemCursor(
                        invisibleCursor,
                        cursorId))
                {
                    replacedCount++;
                }
                else
                {
                    // SetSystemCursor détruit normalement le handle fourni.
                    // En cas d'échec, le système reste responsable du handle
                    // selon son propre comportement. On n'effectue donc pas
                    // de destruction manuelle ici.
                }
            }

            if (replacedCount == 0)
                return false;

            systemCursorsReplaced = true;
            cursorIsHidden = true;

            WriteLog(
                $"Curseurs système : MASQUÉS " +
                $"({replacedCount}/{SystemCursorIds.Length} types remplacés).");

            return true;
        }
        catch (Exception ex)
        {
            WriteLog(
                $"ERREUR masquage curseurs système : " +
                $"{ex.GetType().Name}: {ex.Message}");

            return false;
        }
    }

    private static void RestoreSystemCursors()
    {
        if (!systemCursorsReplaced &&
            !cursorIsHidden)
        {
            return;
        }

        try
        {
            bool restored =
                SystemParametersInfo(
                    SPI_SETCURSORS,
                    0,
                    IntPtr.Zero,
                    SPIF_SENDCHANGE);

            systemCursorsReplaced = false;
            cursorIsHidden = false;

            WriteLog(
                restored
                    ? "Curseurs système : RESTAURÉS."
                    : "AVERTISSEMENT : restauration des curseurs système échouée.");
        }
        catch (Exception ex)
        {
            systemCursorsReplaced = false;
            cursorIsHidden = false;

            WriteLog(
                $"ERREUR restauration curseurs système : " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void HideMouseCursor()
    {
        if (!hideCursorWithIcons ||
            cursorIsHidden)
        {
            return;
        }

        ReplaceSystemCursorsWithInvisible();
    }

    private static void ShowMouseCursor()
    {
        if (!cursorIsHidden &&
            !systemCursorsReplaced)
        {
            return;
        }

        RestoreSystemCursors();
    }

    private static void EnforceHiddenMouseCursor()
    {
        // Tant que les icônes sont cachées et que l’option est active,
        // s’assurer que les curseurs système restent remplacés par la
        // version transparente. Cette vérification est appelée par le
        // timer et ne fait rien tant que le remplacement est déjà actif.
        if (!hideCursorWithIcons ||
            iconsAreVisible ||
            systemCursorsReplaced)
        {
            return;
        }

        ReplaceSystemCursorsWithInvisible();
    }

    private static void ShowIcons()
    {
        ShowMouseCursor();

        if (iconsAreVisible)
            return;

        if (SendWallpaperCommand(
                "showIcons"))
        {
            iconsAreVisible = true;

            WriteLog(
                "État interne : icônes = VISIBLES.");
        }
    }

    private static void HideIcons()
    {
        // Garde-fou : aucune commande ne doit être envoyée si les
        // icônes sont déjà cachées.
        if (!iconsAreVisible)
        {
            return;
        }

        WriteLog(
            "HideIcons() : tentative d'envoi " +
            "de la commande hideIcons.");

        if (SendWallpaperCommand(
                "hideIcons"))
        {
            iconsAreVisible = false;

            WriteLog(
                "État interne : icônes = CACHÉES.");

            HideMouseCursor();
        }
        else
        {
            WriteLog(
                "HideIcons() : " +
                "SendWallpaperCommand() a signalé " +
                "un ÉCHEC.");
        }
    }

    // --------------------------------------------------------------------

    // DÉTECTION FENÊTRE MAXIMISÉE / PLEIN ÉCRAN
    // --------------------------------------------------------------------

    private static bool IsForegroundWindowCoveringScreen()
    {
        IntPtr foregroundWindow =
            GetForegroundWindow();

        if (foregroundWindow == IntPtr.Zero)
            return false;

        // ------------------------------------------------------------
        // IMPORTANT :
        // "Program Manager" est la fenêtre du bureau Windows.
        // Windows peut la considérer comme une fenêtre couvrant
        // l'écran, mais elle ne doit évidemment PAS bloquer
        // le masquage des icônes.
        //
        // On l'identifie de manière robuste par sa classe de fenêtre
        // "Progman" et par GetShellWindow(), plutôt que par son titre.
        // ------------------------------------------------------------

        IntPtr shellWindow = GetShellWindow();

        if (foregroundWindow == shellWindow)
            return false;

        string windowClass =
            GetWindowClassName(foregroundWindow);

        if (windowClass.Equals(
                "Progman",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // ------------------------------------------------------------
        // FENÊTRE MAXIMISÉE
        // ------------------------------------------------------------

        if (IsZoomed(foregroundWindow))
            return true;

        // ------------------------------------------------------------
        // FENÊTRE QUI COUVRE PHYSIQUEMENT TOUT LE MONITEUR
        // ------------------------------------------------------------

        if (!GetWindowRect(
                foregroundWindow,
                out RECT windowRect))
        {
            return false;
        }

        IntPtr monitor =
            MonitorFromWindow(
                foregroundWindow,
                MONITOR_DEFAULTTONEAREST);

        if (monitor == IntPtr.Zero)
            return false;

        MONITORINFO monitorInfo =
            new MONITORINFO
            {
                cbSize =
                    Marshal.SizeOf<MONITORINFO>()
            };

        if (!GetMonitorInfo(
                monitor,
                ref monitorInfo))
        {
            return false;
        }

        RECT screenRect =
            monitorInfo.rcMonitor;

        return
            windowRect.Left <= screenRect.Left &&
            windowRect.Top <= screenRect.Top &&
            windowRect.Right >= screenRect.Right &&
            windowRect.Bottom >= screenRect.Bottom;
    }

    // Retourne la classe Windows d'une fenêtre.
    // Permet notamment d'identifier la fenêtre du bureau
    // "Progman" sans dépendre de son titre affiché.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(
        IntPtr hWnd,
        StringBuilder lpClassName,
        int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    private static string GetWindowClassName(
        IntPtr hWnd)
    {
        try
        {
            if (hWnd == IntPtr.Zero)
                return "";

            StringBuilder className =
                new StringBuilder(256);

            int length =
                GetClassName(
                    hWnd,
                    className,
                    className.Capacity);

            if (length <= 0)
                return "";

            return className.ToString();
        }
        catch
        {
            return "";
        }
    }

    // --------------------------------------------------------------------
    // ACTIVATION / DÉSACTIVATION
    // --------------------------------------------------------------------

    private static void EnableProgram()
    {
        programEnabled = true;

        lastMouseMovement =
            DateTime.Now;

        inactivityBlockedByFullscreen =
            false;

        ShowMouseCursor();

        WriteLog(
            "SURVEILLANCE ACTIVÉE.");

        ShowIcons();

        UpdateTrayIcon();
    }

    private static void DisableProgram()
    {
        programEnabled = false;

        WriteLog(
            "SURVEILLANCE DÉSACTIVÉE.");

        ShowMouseCursor();
        ShowIcons();

        UpdateTrayIcon();
    }

    // --------------------------------------------------------------------
    // SURVEILLANCE DE LA SOURIS
    // --------------------------------------------------------------------

    private static void CheckMenuOutsideClick()
    {
        if (activeMenuForm == null ||
            activeMenuForm.IsDisposed ||
            !activeMenuForm.Visible)
        {
            return;
        }

        bool leftClick =
            (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;

        bool rightClick =
            (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;

        if (!leftClick && !rightClick)
            return;

        Point cursor = Cursor.Position;

        if (!activeMenuForm.Bounds.Contains(cursor))
        {
            CloseCustomMenus();
        }
    }

    private static void CheckMouse()
    {
        try
        {
            // NotifyIcon + popup personnalisé : Deactivate n'est pas
            // suffisamment fiable lorsque Windows garde le focus sur la
            // zone de notification. On ferme donc explicitement le popup
            // si un clic est détecté en dehors de celui-ci.
            CheckMenuOutsideClick();

            if (!programEnabled)
                return;

            if (!GetCursorPos(
                    out POINT position))
            {
                int errorCode =
                    Marshal.GetLastWin32Error();

                WriteLog(
                    $"AVERTISSEMENT : " +
                    $"GetCursorPos a échoué — " +
                    $"code erreur Windows={errorCode}.");

                return;
            }

            bool coveringScreen =
                IsForegroundWindowCoveringScreen();

            // ------------------------------------------------------------
            // DIAGNOSTIC DE LA FENÊTRE ACTIVE
            // ------------------------------------------------------------

            IntPtr foregroundWindow =
                GetForegroundWindow();

            string windowDescription =
                GetDetailedWindowDescription(
                    foregroundWindow,
                    coveringScreen);

            // On journalise uniquement les changements d'état
            // de la fenêtre pour éviter un fichier énorme
            // avec un timer à 100 ms.
            if (windowDescription !=
                lastWindowDescription)
            {
                WriteLog(
                    windowDescription);

                lastWindowDescription =
                    windowDescription;
            }

            // ------------------------------------------------------------
            // ÉTAT ÉCRAN COUVERT
            // ------------------------------------------------------------

            if (!coveringStateInitialized ||
                coveringScreen !=
                lastCoveringScreenState)
            {
                WriteLog(
                    $"État écran couvert : " +
                    $"{(coveringScreen ? "OUI" : "NON")}.");

                lastCoveringScreenState =
                    coveringScreen;

                coveringStateInitialized =
                    true;
            }

            // ------------------------------------------------------------
            // INITIALISATION POSITION SOURIS
            // ------------------------------------------------------------

            if (!mousePositionInitialized)
            {
                previousMouseX =
                    position.X;

                previousMouseY =
                    position.Y;

                mousePositionInitialized =
                    true;

                lastMouseMovement =
                    DateTime.Now;

                WriteLog(
                    $"Position souris initialisée : " +
                    $"X={position.X}, " +
                    $"Y={position.Y}");

                return;
            }

            // ------------------------------------------------------------
            // DÉTECTION MOUVEMENT SOURIS
            // ------------------------------------------------------------

            bool mouseMoved =
                position.X != previousMouseX ||
                position.Y != previousMouseY;

            if (mouseMoved)
            {
                ShowMouseCursor();

                previousMouseX =
                    position.X;

                previousMouseY =
                    position.Y;

                lastMouseMovement =
                    DateTime.Now;

                WriteLog(
                    $"Souris déplacée : " +
                    $"X={position.X}, " +
                    $"Y={position.Y}. " +
                    $"{windowDescription}");

                inactivityBlockedByFullscreen =
                    false;

                ShowIcons();

                return;
            }

            // ------------------------------------------------------------
            // CALCUL INACTIVITÉ
            // ------------------------------------------------------------

            TimeSpan inactivity =
                DateTime.Now -
                lastMouseMovement;

            if (inactivity.TotalSeconds <
                inactivityDelaySeconds)
            {
                return;
            }

            // ------------------------------------------------------------
            // ÉCRAN COUVERT
            // ------------------------------------------------------------

            if (coveringScreen)
            {
                if (!inactivityBlockedByFullscreen)
                {
                    WriteLog(
                        $"Inactivité atteinte " +
                        $"({inactivity.TotalSeconds:F1} s), " +
                        "mais masquage BLOQUÉ : " +
                        "écran couvert. " +
                        windowDescription);

                    inactivityBlockedByFullscreen =
                        true;
                }

                return;
            }

            // ------------------------------------------------------------
            // SORTIE D'UN ÉCRAN COUVERT
            // ------------------------------------------------------------

            if (inactivityBlockedByFullscreen)
            {
                WriteLog(
                    "Écran redevenu visible après " +
                    "un blocage du masquage.");

                inactivityBlockedByFullscreen =
                    false;
            }

            // ------------------------------------------------------------
            // MASQUAGE
            // ------------------------------------------------------------

            // Une fois les icônes cachées, inutile de redemander
            // hideIcons toutes les 100 ms. Le prochain appel utile
            // sera déclenché par un mouvement de souris.
            if (iconsAreVisible)
            {
                HideIcons();
            }

            // Tant que les icônes restent cachées, on réapplique
            // le masquage du curseur à chaque tick pour éviter qu'il
            // soit réinjecté par Windows.
            EnforceHiddenMouseCursor();
        }
        catch (Exception ex)
        {
            WriteLog(
                $"ERREUR CheckMouse : " +
                $"{ex.GetType().Name}: {ex.Message}");

            // L'exception est absorbée afin que le timer
            // et l'interface WinForms puissent continuer
            // à fonctionner.
        }
    }

    // --------------------------------------------------------------------
    // ICÔNES SYSTRAY
    // --------------------------------------------------------------------

    private static Icon? LoadEmbeddedIcon(string resourceName)
    {
        try
        {
            Assembly assembly =
                Assembly.GetExecutingAssembly();

            using Stream? stream =
                assembly.GetManifestResourceStream(
                    resourceName);

            if (stream == null)
            {
                WriteLog(
                    $"ERREUR : ressource d'icône introuvable : " +
                    $"{resourceName}.");

                return null;
            }

            // On clone l'icône avant de fermer le flux afin que
            // l'icône reste autonome pendant toute la durée du programme.
            using Icon icon =
                new Icon(stream);

            return (Icon)icon.Clone();
        }
        catch (Exception ex)
        {
            WriteLog(
                $"ERREUR chargement icône {resourceName} : " +
                $"{ex.GetType().Name}: {ex.Message}");

            return null;
        }
    }

    private static void LoadTrayIcons()
    {
        trayIconOpen =
            LoadEmbeddedIcon("Eye_open.ico");

        trayIconClosed =
            LoadEmbeddedIcon("Eye_close.ico");
    }

    private static void ShowStartupNotification()
    {
        try
        {
            Point cursorPosition = Cursor.Position;
            Screen screen =
                Screen.FromPoint(cursorPosition);

            Rectangle workingArea =
                screen.WorkingArea;

            Form notification =
                new Form
                {
                    FormBorderStyle =
                        FormBorderStyle.None,

                    ShowInTaskbar =
                        false,

                    ShowIcon =
                        false,

                    TopMost =
                        true,

                    StartPosition =
                        FormStartPosition.Manual,

                    ClientSize =
                        new Size(360, 90),

                    BackColor =
                        Color.FromArgb(249, 249, 250)
                };

            notification.FormClosed +=
                (_, _) => notification.Dispose();

            // Coins arrondis.
            using (GraphicsPath path =
                CreateRoundedNotificationPath(
                    new Rectangle(
                        0,
                        0,
                        notification.Width,
                        notification.Height),
                    14))
            {
                notification.Region =
                    new Region(path);
            }

            // Ombre native Windows discrète.
            notification.CreateControl();
            SetNotificationShadow(notification);

            Label titleLabel =
                new Label
                {
                    AutoSize = false,
                    Location = new Point(18, 10),
                    Size = new Size(324, 30),
                    Text =
                        "Wallpaper Icon Hider",
                    Font =
                        new Font(
                            "Microsoft YaHei UI",
                            10f,
                            FontStyle.Bold),
                    ForeColor =
                        Color.FromArgb(32, 32, 32)
                };

            notification.Controls.Add(titleLabel);

            Label messageLabel =
                new Label
                {
                    AutoSize = false,
                    Location = new Point(18, 44),
                    Size = new Size(324, 30),
                    Text =
                        "程序已启动，正在后台运行。",
                    Font =
                        new Font(
                            "Microsoft YaHei UI",
                            9f,
                            FontStyle.Regular),
                    ForeColor =
                        Color.FromArgb(80, 80, 80)
                };

            notification.Controls.Add(messageLabel);

            // Placement au-dessus de la zone de notification / barre des tâches.
            int margin = 12;

            int x =
                workingArea.Right -
                notification.Width -
                margin;

            int y =
                workingArea.Bottom -
                notification.Height -
                12;

            notification.Location =
                new Point(x, y);

            notification.Show();
            notification.BringToFront();
            notification.Activate();

            System.Windows.Forms.Timer closeTimer =
                new System.Windows.Forms.Timer
                {
                    Interval = 3500
                };

            closeTimer.Tick +=
                (_, _) =>
                {
                    closeTimer.Stop();
                    closeTimer.Dispose();

                    if (!notification.IsDisposed)
                        notification.Close();
                };

            closeTimer.Start();
        }
        catch
        {
            // La notification ne doit jamais empêcher
            // l'application de fonctionner.
        }
    }

    private static GraphicsPath CreateRoundedNotificationPath(
        Rectangle bounds,
        int radius)
    {
        int diameter =
            radius * 2;

        GraphicsPath path =
            new GraphicsPath();

        path.AddArc(
            bounds.X,
            bounds.Y,
            diameter,
            diameter,
            180,
            90);

        path.AddArc(
            bounds.Right - diameter,
            bounds.Y,
            diameter,
            diameter,
            270,
            90);

        path.AddArc(
            bounds.Right - diameter,
            bounds.Bottom - diameter,
            diameter,
            diameter,
            0,
            90);

        path.AddArc(
            bounds.X,
            bounds.Bottom - diameter,
            diameter,
            diameter,
            90,
            90);

        path.CloseFigure();

        return path;
    }

    private static void SetNotificationShadow(
        Form notification)
    {
        try
        {
            notification.GetType()
                .GetProperty("FormBorderStyle");

            notification.ShowInTaskbar = false;
        }
        catch
        {
            // L'ombre est purement visuelle et non indispensable.
        }
    }

    private static void UpdateTrayIcon()
    {
        if (trayIcon == null)
            return;

        Icon? newIcon =
            programEnabled
                ? trayIconOpen
                : trayIconClosed;

        if (newIcon != null)
        {
            trayIcon.Icon = newIcon;
        }
    }

    // --------------------------------------------------------------------
    // MENU CONTEXTUEL PERSONNALISÉ
    // --------------------------------------------------------------------

    private static void CloseCustomMenus()
    {
        if (activeMenuForm != null)
        {
            ModernMenuForm menuToClose = activeMenuForm;
            activeMenuForm = null;
            menuToClose.Close();
            menuToClose.Dispose();
        }

        menuAnchorInitialized = false;
    }

    private static void ShowMainMenu()
    {
        // Ouverture depuis l'icône de notification :
        // on mémorise une seule fois l'ancre et le moniteur.
        Point cursorPosition = Cursor.Position;
        Screen screen = Screen.FromPoint(cursorPosition);

        menuAnchorPoint = cursorPosition;
        menuWorkingArea = screen.WorkingArea;
        menuAnchorInitialized = true;

        CloseCustomMenus();

        // CloseCustomMenus() réinitialise l'état d'ancrage ; on le restaure
        // immédiatement avec les valeurs que nous venons de calculer.
        menuAnchorPoint = cursorPosition;
        menuWorkingArea = screen.WorkingArea;
        menuAnchorInitialized = true;

        activeMenuForm = ModernMenuForm.CreateMainMenu(
            programEnabled,
            () =>
            {
                EnableProgram();
                CloseCustomMenus();
            },
            () =>
            {
                DisableProgram();
                CloseCustomMenus();
            },
            () =>
            {
                ShowSettingsMenu();
            },
            () =>
            {
                Application.Exit();
            });

        ShowMenuAtTray(activeMenuForm);
    }

    private static void ShowSettingsMenu()
    {
        // Le menu principal disparaît et est remplacé par le sous-menu.
        // L'ancre d'origine est conservée.
        CloseCurrentMenuOnly();

        activeMenuForm = ModernMenuForm.CreateSettingsMenu(
            inactivityDelaySeconds,
            startWithWindows,
            hideCursorWithIcons,
            () =>
            {
                ShowDelayMenu();
            },
            () =>
            {
                UpdateStartupSetting(!startWithWindows);
                ShowSettingsMenu();
            },
            () =>
            {
                hideCursorWithIcons =
                    !hideCursorWithIcons;

                SaveSettings();

                if (!hideCursorWithIcons)
                    ShowMouseCursor();

                WriteLog(
                    $"Masquage du curseur avec les icônes : " +
                    $"{(hideCursorWithIcons ? "ACTIVÉ" : "DÉSACTIVÉ")}.");

                ShowSettingsMenu();
            },
            () =>
            {
                ShowMainMenuAtSameAnchor();
            });

        ShowMenuAtTray(activeMenuForm);
    }

    private static void ShowDelayMenu()
    {
        CloseCurrentMenuOnly();

        activeMenuForm = ModernMenuForm.CreateDelayMenu(
            inactivityDelaySeconds,
            value =>
            {
                inactivityDelaySeconds = value;
                SaveSettings();
                lastMouseMovement = DateTime.Now;
                ShowIcons();
                CloseCustomMenus();
            },
            () =>
            {
                ShowSettingsMenu();
            });

        ShowMenuAtTray(activeMenuForm);
    }

    private static void ShowMainMenuAtSameAnchor()
    {
        CloseCurrentMenuOnly();

        activeMenuForm = ModernMenuForm.CreateMainMenu(
            programEnabled,
            () =>
            {
                EnableProgram();
                CloseCustomMenus();
            },
            () =>
            {
                DisableProgram();
                CloseCustomMenus();
            },
            () =>
            {
                ShowSettingsMenu();
            },
            () =>
            {
                Application.Exit();
            });

        ShowMenuAtTray(activeMenuForm);
    }

    private static void CloseCurrentMenuOnly()
    {
        if (activeMenuForm == null)
            return;

        ModernMenuForm menuToClose = activeMenuForm;
        activeMenuForm = null;
        menuToClose.Close();
        menuToClose.Dispose();
    }

    private static void ShowMenuAtTray(Form menuForm)
    {
        if (!menuAnchorInitialized)
        {
            Point cursorPosition = Cursor.Position;
            menuAnchorPoint = cursorPosition;
            menuWorkingArea =
                Screen.FromPoint(cursorPosition).WorkingArea;
            menuAnchorInitialized = true;
        }

        // Le popup est ancré au-dessus de la barre des tâches avec
        // une marge visuelle supplémentaire.
        // Les sous-menus gardent exactement le même axe horizontal
        // et la même hauteur d'ancrage.
        int x =
            menuAnchorPoint.X -
            menuForm.Width / 2;

        // WorkingArea exclut la barre des tâches.
        // On laisse volontairement un espace supplémentaire afin que le
        // menu ne soit pas visuellement collé à la zone de notification.
        // Cette marge s'applique à tous les sous-menus puisqu'ils utilisent
        // exactement la même ancre.
        const int MenuBottomMargin = 42;

        int y =
            menuWorkingArea.Bottom -
            menuForm.Height -
            MenuBottomMargin;

        x = Math.Clamp(
            x,
            menuWorkingArea.Left + 8,
            menuWorkingArea.Right - menuForm.Width - 8);

        y = Math.Clamp(
            y,
            menuWorkingArea.Top + 8,
            menuWorkingArea.Bottom - menuForm.Height - MenuBottomMargin);

        menuForm.StartPosition =
            FormStartPosition.Manual;

        menuForm.Location =
            new Point(x, y);

        menuForm.Show();
        menuForm.BringToFront();
        menuForm.Activate();
    }

    // --------------------------------------------------------------------
    // FENÊTRE DU MENU MODERNE
    // --------------------------------------------------------------------

    private enum MenuIconKind
    {
        None,
        Text,
        Windows,
        Cursor
    }

    private sealed class ModernMenuForm : Form
    {
        private const int MenuWidth = 360;
        private const int RowHeight = 42;
        private const int HeaderHeight = 44;
        private const int HorizontalPadding = 8;
        private const int CornerRadius = 14;

        private static readonly Color MenuBackground =
            Color.FromArgb(249, 249, 250);

        private static readonly Color MenuBorder =
            Color.FromArgb(224, 224, 228);

        private static readonly Color HoverBackground =
            Color.FromArgb(231, 240, 255);

        private static readonly Color AccentBlue =
            Color.FromArgb(0, 120, 212);

        private static readonly Color PrimaryText =
            Color.FromArgb(32, 32, 32);

        private static readonly Color SecondaryText =
            Color.FromArgb(95, 95, 95);

        private ModernMenuForm(int height)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            ShowIcon = false;

            // Passe au-dessus du panneau de débordement des icônes
            // de la zone de notification et de la barre des tâches.
            TopMost = true;

            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(MenuWidth, height);

            BackColor = MenuBackground;
            Font = new Font(
                "Microsoft YaHei UI",
                9.5f,
                FontStyle.Regular,
                GraphicsUnit.Point);

            DoubleBuffered = true;

            // Ombre native Windows discrète.
            SetStyle(
                ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer,
                true);

            // Un clic hors du popup (bureau, autre application, barre
            // des tâches, etc.) lui fait perdre le focus et ferme le menu.
            Deactivate += (_, _) =>
            {
                if (!ContainsFocus)
                {
                    if (ReferenceEquals(activeMenuForm, this))
                        activeMenuForm = null;

                    Close();
                }
            };

            FormClosed += (_, _) =>
            {
                if (ReferenceEquals(activeMenuForm, this))
                    activeMenuForm = null;
            };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ApplyRoundedRegion();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ApplyRoundedRegion();
        }

        private void ApplyRoundedRegion()
        {
            using GraphicsPath path =
                CreateRoundedPath(
                    new Rectangle(
                        0,
                        0,
                        Width,
                        Height),
                    CornerRadius);

            Region?.Dispose();
            Region = new Region(path);
        }

        private static GraphicsPath CreateRoundedPath(
            Rectangle bounds,
            int radius)
        {
            int diameter = radius * 2;

            GraphicsPath path = new GraphicsPath();

            path.AddArc(
                bounds.X,
                bounds.Y,
                diameter,
                diameter,
                180,
                90);

            path.AddArc(
                bounds.Right - diameter,
                bounds.Y,
                diameter,
                diameter,
                270,
                90);

            path.AddArc(
                bounds.Right - diameter,
                bounds.Bottom - diameter,
                diameter,
                diameter,
                0,
                90);

            path.AddArc(
                bounds.X,
                bounds.Bottom - diameter,
                diameter,
                diameter,
                90,
                90);

            path.CloseFigure();

            return path;
        }

        protected override void OnPaintBackground(
            PaintEventArgs e)
        {
            e.Graphics.SmoothingMode =
                System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            using Brush backgroundBrush =
                new SolidBrush(MenuBackground);

            e.Graphics.FillRectangle(
                backgroundBrush,
                ClientRectangle);

            using Pen borderPen =
                new Pen(MenuBorder, 1f);

            Rectangle border =
                new Rectangle(
                    0,
                    0,
                    Width - 1,
                    Height - 1);

            using GraphicsPath path =
                CreateRoundedPath(
                    border,
                    CornerRadius);

            e.Graphics.DrawPath(
                borderPen,
                path);
        }

        private void AddHeader(
            string title,
            Action? backAction)
        {
            if (backAction == null)
            {
                Label titleLabel =
                    new Label
                    {
                        Text = title,
                        AutoSize = false,
                        Location =
                            new Point(
                                18,
                                0),
                        Size =
                            new Size(
                                MenuWidth - 36,
                                HeaderHeight),
                        TextAlign =
                            ContentAlignment.MiddleLeft,
                        Font =
                            new Font(
                                "Microsoft YaHei UI",
                                10f,
                                FontStyle.Bold),
                        ForeColor = PrimaryText
                    };

                Controls.Add(titleLabel);
                return;
            }

            MenuRow backRow =
                new MenuRow(
                    "‹",
                    null,
                    title,
                    false,
                    false,
                    true,
                    AccentBlue,
                    SecondaryText,
                    () => backAction());

            backRow.Location =
                new Point(
                    HorizontalPadding,
                    2);

            backRow.Size =
                new Size(
                    MenuWidth -
                    HorizontalPadding * 2,
                    HeaderHeight - 4);

            Controls.Add(backRow);
        }

        private void AddSeparator(int y)
        {
            Panel separator =
                new Panel
                {
                    Location =
                        new Point(
                            14,
                            y),
                    Size =
                        new Size(
                            MenuWidth - 28,
                            1),
                    BackColor =
                        MenuBorder
                };

            Controls.Add(separator);
        }

        private void AddRow(
            string icon,
            string text,
            int y,
            Action action,
            bool checkedState = false,
            bool chevron = false,
            Icon? image = null,
            MenuIconKind iconKind = MenuIconKind.Text)
        {
            MenuRow row =
                new MenuRow(
                    icon,
                    image,
                    text,
                    checkedState,
                    chevron,
                    true,
                    AccentBlue,
                    PrimaryText,
                    action,
                    iconKind);

            row.Location =
                new Point(
                    HorizontalPadding,
                    y);

            row.Size =
                new Size(
                    MenuWidth -
                    HorizontalPadding * 2,
                    RowHeight);

            Controls.Add(row);
        }

        public static ModernMenuForm CreateMainMenu(
            bool enabled,
            Action enableAction,
            Action disableAction,
            Action settingsAction,
            Action exitAction)
        {
            ModernMenuForm form =
                new ModernMenuForm(
                    HeaderHeight +
                    RowHeight * 2 +
                    2 +
                    RowHeight +
                    2 +
                    RowHeight +
                    12);

            form.AddHeader(
                "Wallpaper Icon Hider",
                null);

            form.AddRow(
                "",
                "启用",
                HeaderHeight,
                enableAction,
                enabled,
                false,
                trayIconOpen);

            form.AddRow(
                "",
                "停用",
                HeaderHeight + RowHeight,
                disableAction,
                !enabled,
                false,
                trayIconClosed);

            int separator1 =
                HeaderHeight +
                RowHeight * 2;

            form.AddSeparator(separator1);

            form.AddRow(
                "⚙",
                "设置",
                separator1 + 2,
                settingsAction,
                false,
                true);

            int separator2 =
                separator1 + 2 + RowHeight;

            form.AddSeparator(separator2);

            form.AddRow(
                "×",
                "退出",
                separator2 + 2,
                exitAction);

            return form;
        }

        public static ModernMenuForm CreateSettingsMenu(
            int delay,
            bool startupEnabled,
            bool hideCursorEnabled,
            Action delayAction,
            Action startupAction,
            Action cursorAction,
            Action backAction)
        {
            ModernMenuForm form =
                new ModernMenuForm(
                    HeaderHeight +
                    RowHeight * 3 +
                    2 +
                    12);

            form.AddHeader(
                "设置",
                backAction);

            int firstRowY = HeaderHeight;

            form.AddRow(
                "⏱",
                $"隐藏延迟：{delay} 秒",
                firstRowY,
                delayAction,
                false,
                true);

            form.AddRow(
                "",
                "随 Windows 启动",
                firstRowY + RowHeight,
                startupAction,
                startupEnabled,
                false,
                null,
                MenuIconKind.Windows);

            form.AddRow(
                "",
                "隐藏图标时同时隐藏鼠标指针",
                firstRowY + RowHeight * 2,
                cursorAction,
                hideCursorEnabled,
                false,
                null,
                MenuIconKind.Cursor);

            return form;
        }

        public static ModernMenuForm CreateDelayMenu(
            int selectedDelay,
            Action<int> selectAction,
            Action backAction)
        {
            int height =
                HeaderHeight +
                RowHeight * MaximumDelaySeconds +
                12;

            ModernMenuForm form =
                new ModernMenuForm(height);

            form.AddHeader(
                "隐藏延迟",
                backAction);

            for (int i = MinimumDelaySeconds;
                 i <= MaximumDelaySeconds;
                 i++)
            {
                int value = i;

                form.AddRow(
                    " ",
                    $"{value} 秒",
                    HeaderHeight +
                    (value - 1) * RowHeight,
                    () => selectAction(value),
                    value == selectedDelay);
            }

            return form;
        }

        private sealed class MenuRow : Control
        {
            private readonly string iconText;
            private readonly Icon? rowIcon;
            private readonly MenuIconKind iconKind;
            private readonly string rowText;
            private readonly bool checkState;
            private readonly bool showChevron;
            private readonly bool selectable;
            private readonly Color accentColor;
            private readonly Color textColor;
            private readonly Action action;

            private bool hovered;

            public MenuRow(
                string iconText,
                Icon? rowIcon,
                string rowText,
                bool checkState,
                bool showChevron,
                bool selectable,
                Color accentColor,
                Color textColor,
                Action action,
                MenuIconKind iconKind = MenuIconKind.Text)
            {
                this.iconText = iconText;
                this.rowIcon = rowIcon;
                this.iconKind = iconKind;
                this.rowText = rowText;
                this.checkState = checkState;
                this.showChevron = showChevron;
                this.selectable = selectable;
                this.accentColor = accentColor;
                this.textColor = textColor;
                this.action = action;

                Cursor =
                    Cursors.Hand;

                DoubleBuffered = true;
            }

            protected override void OnPaint(
                PaintEventArgs e)
            {
                base.OnPaint(e);

                Graphics g = e.Graphics;
                g.SmoothingMode =
                    System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint =
                    System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                Rectangle background =
                    new Rectangle(
                        0,
                        1,
                        Width,
                        Height - 2);

                if (hovered)
                {
                    using Brush hoverBrush =
                        new SolidBrush(
                            HoverBackground);

                    using GraphicsPath hoverPath =
                        CreateRoundedPath(
                            background,
                            8);

                    g.FillPath(
                        hoverBrush,
                        hoverPath);
                }

                Color iconColor =
                    hovered
                        ? accentColor
                        : SecondaryText;

                if (rowIcon != null)
                {
                    g.DrawIcon(
                        rowIcon,
                        new Rectangle(
                            12,
                            (Height - 18) / 2,
                            18,
                            18));
                }
                else if (iconKind == MenuIconKind.Windows)
                {
                    DrawWindowsIcon(g, iconColor);
                }
                else if (iconKind == MenuIconKind.Cursor)
                {
                    DrawCursorIcon(g, iconColor);
                }
                else if (!string.IsNullOrEmpty(iconText))
                {
                    using Font iconFont =
                        new Font(
                            "Segoe UI Symbol",
                            11f,
                            FontStyle.Regular,
                            GraphicsUnit.Point);

                    using Brush iconBrush =
                        new SolidBrush(iconColor);

                    g.DrawString(
                        iconText,
                        iconFont,
                        iconBrush,
                        new PointF(
                            12,
                            (Height - iconFont.GetHeight(g)) / 2f - 1));
                }

                using Font textFont =
                    new Font(
                        "Microsoft YaHei UI",
                        9.5f,
                        FontStyle.Regular,
                        GraphicsUnit.Point);

                using Brush textBrush =
                    new SolidBrush(textColor);

                g.DrawString(
                    rowText,
                    textFont,
                    textBrush,
                    new PointF(
                        43,
                        (Height - textFont.GetHeight(g)) / 2f - 1));

                if (checkState)
                {
                    using Font checkFont =
                        new Font(
                            "Segoe UI Symbol",
                            10f,
                            FontStyle.Bold,
                            GraphicsUnit.Point);

                    using Brush checkBrush =
                        new SolidBrush(accentColor);

                    SizeF checkSize =
                        g.MeasureString(
                            "✓",
                            checkFont);

                    g.DrawString(
                        "✓",
                        checkFont,
                        checkBrush,
                        new PointF(
                            Width - checkSize.Width - 13,
                            (Height - checkFont.GetHeight(g)) / 2f - 1));
                }

                if (showChevron)
                {
                    using Font chevronFont =
                        new Font(
                            "Segoe UI Symbol",
                            12f,
                            FontStyle.Regular,
                            GraphicsUnit.Point);

                    using Brush chevronBrush =
                        new SolidBrush(SecondaryText);

                    g.DrawString(
                        "›",
                        chevronFont,
                        chevronBrush,
                        new PointF(
                            Width - 25,
                            (Height - chevronFont.GetHeight(g)) / 2f - 1));
                }
            }

            private void DrawWindowsIcon(Graphics g, Color color)
            {
                using Brush brush = new SolidBrush(color);

                int x = 12;
                int y = (Height - 17) / 2;

                g.FillRectangle(brush, x, y, 8, 7);
                g.FillRectangle(brush, x + 10, y, 8, 7);
                g.FillRectangle(brush, x, y + 10, 8, 7);
                g.FillRectangle(brush, x + 10, y + 10, 8, 7);
            }

            private void DrawCursorIcon(Graphics g, Color color)
            {
                PointF[] points =
                {
                    new PointF(12, 12),
                    new PointF(12, 30),
                    new PointF(17, 25),
                    new PointF(22, 32),
                    new PointF(26, 29.5f),
                    new PointF(21, 23),
                    new PointF(29, 23)
                };

                using Brush brush = new SolidBrush(color);
                using Pen pen = new Pen(MenuBackground, 1.5f)
                {
                    LineJoin = LineJoin.Round
                };

                g.FillPolygon(brush, points);
                g.DrawPolygon(pen, points);
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                hovered = true;
                Invalidate();
                base.OnMouseEnter(e);
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                hovered = false;
                Invalidate();
                base.OnMouseLeave(e);
            }

            protected override void OnMouseUp(
                MouseEventArgs e)
            {
                if (selectable &&
                    e.Button == MouseButtons.Left)
                {
                    action();
                }

                base.OnMouseUp(e);
            }
        }
    }

    // --------------------------------------------------------------------
    // PROGRAMME PRINCIPAL
    // --------------------------------------------------------------------

    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        // Première exécution :
        // proposer à l'utilisateur le dossier d'installation,
        // copier l'exécutable et relancer la version installée.
        if (!InstallAndRelaunchIfNeeded())
        {
            return;
        }

        // Charger les préférences.
        LoadSettings();

        WriteLog(
            "============================================================");

        WriteLog(
            $"APPLICATION DÉMARRÉE — " +
            $"délai={inactivityDelaySeconds}s, " +
            $"démarrage Windows=" +
            $"{(startWithWindows ? "activé" : "désactivé")}.");

        // Si le démarrage automatique est activé,
        // s'assurer que le raccourci existe.
        if (startWithWindows)
        {
            CreateStartupShortcut();
        }

        // ---------------------------------------------------------------
        // SÉCURITÉ AU DÉMARRAGE
        // ---------------------------------------------------------------

        SendWallpaperCommand(
            "showIcons",
            true);

        iconsAreVisible =
            true;

        WriteLog(
            "Sécurité au démarrage : " +
            "commande showIcons envoyée.");

        ShowMouseCursor();

        lastMouseMovement =
            DateTime.Now;

        // ---------------------------------------------------------------
        // ICÔNE SYSTRAY
        // ---------------------------------------------------------------

        // Charger les deux icônes embarquées dans l'exécutable.
        LoadTrayIcons();

        trayIcon =
            new NotifyIcon();

        trayIcon.Icon =
            trayIconOpen
            ?? SystemIcons.Application;

        trayIcon.Text =
            "Wallpaper Icon Hider";

        trayIcon.Visible = true;

        // Indiquer clairement à l'utilisateur que l'application
        // fonctionne désormais en arrière-plan.
        ShowStartupNotification();

        // ---------------------------------------------------------------
        // MENU CONTEXTUEL PERSONNALISÉ
        // ---------------------------------------------------------------

        // Le menu natif ContextMenuStrip est remplacé par notre propre
        // fenêtre WinForms afin de pouvoir reproduire un rendu moderne :
        // coins arrondis, ombre, couleurs Windows 11 et survol personnalisé.
        trayIcon.MouseUp +=
            (_, e) =>
            {
                if (e.Button == MouseButtons.Right)
                {
                    ShowMainMenu();
                }
            };

        // ---------------------------------------------------------------
        // TIMER
        // ---------------------------------------------------------------

        mouseTimer =
            new System.Windows.Forms.Timer();

        mouseTimer.Interval =
            100;

        mouseTimer.Tick +=
            (_, _) =>
                CheckMouse();

        mouseTimer.Start();

        WriteLog(
            "SURVEILLANCE SOURIS DÉMARRÉE — " +
            "timer 100 ms.");

        // ---------------------------------------------------------------
        // ARRÊT PROPRE
        // ---------------------------------------------------------------

        Application.ApplicationExit +=
            (_, _) =>
            {
                WriteLog(
                    "ARRÊT DE L'APPLICATION.");

                // Toujours restaurer les icônes
                // et le curseur avant de quitter.
                ShowMouseCursor();

                SendWallpaperCommand(
                    "showIcons",
                    true);

                if (trayIcon != null)
                {
                    trayIcon.Visible =
                        false;

                    trayIcon.Dispose();
                }

                mouseTimer?.Stop();
                mouseTimer?.Dispose();
            };

        Application.Run();
    }
}
