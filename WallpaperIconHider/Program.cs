using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
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

    private const string WallpaperEnginePath =
        @"C:\Program Files (x86)\Steam\steamapps\common\wallpaper_engine\wallpaper64.exe";

    private const int DefaultInactivityDelaySeconds = 5;
    private const int MinimumDelaySeconds = 1;
    private const int MaximumDelaySeconds = 10;

    private static int inactivityDelaySeconds =
        DefaultInactivityDelaySeconds;

    private static bool programEnabled = true;
    private static bool iconsAreVisible = true;

    // --------------------------------------------------------------------
    // SURVEILLANCE SOURIS
    // --------------------------------------------------------------------

    private static int previousMouseX;
    private static int previousMouseY;
    private static bool mousePositionInitialized = false;
    private static DateTime lastMouseMovement;

    private static NotifyIcon? trayIcon;
    private static System.Windows.Forms.Timer? mouseTimer;

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
                File.AppendAllText(
                    GetLogPath(),
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
    // ÉLÉMENTS DU MENU
    // --------------------------------------------------------------------

    private static ToolStripMenuItem? enableItem;
    private static ToolStripMenuItem? disableItem;
    private static ToolStripMenuItem? delayItem;
    private static ToolStripMenuItem? startupItem;

    // --------------------------------------------------------------------
    // CONFIGURATION LOCALE
    // --------------------------------------------------------------------

    private class Settings
    {
        public int InactivityDelaySeconds { get; set; } =
            DefaultInactivityDelaySeconds;

        public bool StartWithWindows { get; set; } = false;
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
        }
        catch
        {
            inactivityDelaySeconds =
                DefaultInactivityDelaySeconds;

            startWithWindows = false;
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
                        startWithWindows
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

    private const string StartupShortcutName =
        "Wallpaper Icon Hider.lnk";

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
                "Lance Wallpaper Icon Hider au démarrage de Windows.";

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
        if (startupItem == null)
            return;

        startupItem.Checked =
            startWithWindows;
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
            ProcessStartInfo startInfo =
                new ProcessStartInfo
                {
                    FileName =
                        WallpaperEnginePath,

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

    private static void ShowIcons()
    {
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

        WriteLog(
            "SURVEILLANCE ACTIVÉE.");

        ShowIcons();

        UpdateTrayMenu();
    }

    private static void DisableProgram()
    {
        programEnabled = false;

        WriteLog(
            "SURVEILLANCE DÉSACTIVÉE.");

        ShowIcons();

        UpdateTrayMenu();
    }

    // --------------------------------------------------------------------
    // SURVEILLANCE DE LA SOURIS
    // --------------------------------------------------------------------

    private static void CheckMouse()
    {
        try
        {
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
    // MENU SYSTRAY
    // --------------------------------------------------------------------

    private static void UpdateTrayMenu()
    {
        if (enableItem == null ||
            disableItem == null ||
            delayItem == null)
        {
            return;
        }

        enableItem.Checked =
            programEnabled;

        disableItem.Checked =
            !programEnabled;

        delayItem.Text =
            $"⏱ Délai : " +
            $"{inactivityDelaySeconds} seconde" +
            (inactivityDelaySeconds > 1
                ? "s"
                : "");

        UpdateStartupMenu();
    }

    // --------------------------------------------------------------------
    // CONTRÔLE DE SÉLECTION DU DÉLAI
    // --------------------------------------------------------------------

    private class DelaySelector : Control
    {
        private int selectedValue;

        [DesignerSerializationVisibility(
            DesignerSerializationVisibility.Hidden)]
        public int SelectedValue
        {
            get => selectedValue;

            set
            {
                int newValue =
                    Math.Clamp(
                        value,
                        MinimumDelaySeconds,
                        MaximumDelaySeconds);

                if (selectedValue ==
                    newValue)
                {
                    return;
                }

                selectedValue =
                    newValue;

                Invalidate();
            }
        }

        public DelaySelector(
            int initialValue)
        {
            selectedValue =
                Math.Clamp(
                    initialValue,
                    MinimumDelaySeconds,
                    MaximumDelaySeconds);

            DoubleBuffered = true;

            Height = 55;

            Cursor =
                Cursors.Hand;

            TabStop = false;
        }

        protected override void OnPaint(
            PaintEventArgs e)
        {
            base.OnPaint(e);

            Graphics g =
                e.Graphics;

            int segmentWidth =
                Width /
                MaximumDelaySeconds;

            for (int i = 1;
                 i <= MaximumDelaySeconds;
                 i++)
            {
                int left =
                    (i - 1) *
                    segmentWidth;

                int right =
                    i ==
                    MaximumDelaySeconds
                        ? Width
                        : i *
                          segmentWidth;

                Rectangle segment =
                    new Rectangle(
                        left + 1,
                        1,
                        right - left - 2,
                        32);

                if (i <= selectedValue)
                {
                    using Brush brush =
                        new SolidBrush(
                            SystemColors.Highlight);

                    g.FillRectangle(
                        brush,
                        segment);
                }
                else
                {
                    using Brush brush =
                        new SolidBrush(
                            SystemColors.ControlLight);

                    g.FillRectangle(
                        brush,
                        segment);
                }

                using Pen pen =
                    new Pen(
                        SystemColors.ControlDark);

                g.DrawRectangle(
                    pen,
                    segment);

                string text =
                    i.ToString();

                SizeF textSize =
                    g.MeasureString(
                        text,
                        Font);

                float textX =
                    segment.Left +
                    (segment.Width -
                     textSize.Width) / 2;

                float textY =
                    segment.Top +
                    (segment.Height -
                     textSize.Height) / 2;

                using Brush textBrush =
                    new SolidBrush(
                        i <= selectedValue
                            ? SystemColors.HighlightText
                            : SystemColors.ControlText);

                g.DrawString(
                    text,
                    Font,
                    textBrush,
                    textX,
                    textY);
            }
        }

        protected override void OnMouseClick(
            MouseEventArgs e)
        {
            if (e.Button !=
                MouseButtons.Left)
            {
                return;
            }

            int segmentWidth =
                Math.Max(
                    1,
                    Width /
                    MaximumDelaySeconds);

            int selected =
                (e.X /
                 segmentWidth) + 1;

            selected =
                Math.Clamp(
                    selected,
                    MinimumDelaySeconds,
                    MaximumDelaySeconds);

            SelectedValue =
                selected;

            base.OnMouseClick(e);
        }
    }

    // --------------------------------------------------------------------
    // FENÊTRE DE MODIFICATION DU DÉLAI
    // --------------------------------------------------------------------

    private static void OpenDelaySettings()
    {
        using Form dialog =
            new Form();

        dialog.Text =
            "Modifier le délai";

        dialog.FormBorderStyle =
            FormBorderStyle.FixedDialog;

        dialog.StartPosition =
            FormStartPosition.CenterScreen;

        dialog.MinimizeBox = false;
        dialog.MaximizeBox = false;
        dialog.ShowInTaskbar = false;

        dialog.ClientSize =
            new Size(400, 185);

        Label label =
            new Label();

        label.Text =
            "Délai avant masquage :";

        label.AutoSize =
            true;

        label.Location =
            new Point(20, 20);

        DelaySelector selector =
            new DelaySelector(
                inactivityDelaySeconds);

        selector.Location =
            new Point(20, 50);

        selector.Width =
            360;

        Label valueLabel =
            new Label();

        valueLabel.Text =
            $"{selector.SelectedValue} seconde" +
            (selector.SelectedValue > 1
                ? "s"
                : "");

        valueLabel.AutoSize =
            false;

        valueLabel.TextAlign =
            ContentAlignment.MiddleCenter;

        valueLabel.Width =
            360;

        valueLabel.Height =
            25;

        valueLabel.Location =
            new Point(20, 105);

        selector.MouseClick +=
            (_, _) =>
            {
                valueLabel.Text =
                    $"{selector.SelectedValue} seconde" +
                    (selector.SelectedValue > 1
                        ? "s"
                        : "");
            };

        Button cancelButton =
            new Button();

        cancelButton.Text =
            "Annuler";

        cancelButton.DialogResult =
            DialogResult.Cancel;

        cancelButton.Location =
            new Point(215, 145);

        Button saveButton =
            new Button();

        saveButton.Text =
            "Enregistrer";

        saveButton.DialogResult =
            DialogResult.OK;

        saveButton.Location =
            new Point(305, 145);

        dialog.Controls.Add(label);
        dialog.Controls.Add(selector);
        dialog.Controls.Add(valueLabel);
        dialog.Controls.Add(cancelButton);
        dialog.Controls.Add(saveButton);

        dialog.AcceptButton =
            saveButton;

        dialog.CancelButton =
            cancelButton;

        DialogResult result =
            dialog.ShowDialog();

        if (result !=
            DialogResult.OK)
        {
            return;
        }

        inactivityDelaySeconds =
            selector.SelectedValue;

        SaveSettings();

        lastMouseMovement =
            DateTime.Now;

        ShowIcons();

        UpdateTrayMenu();
    }

    // --------------------------------------------------------------------
    // PROGRAMME PRINCIPAL
    // --------------------------------------------------------------------

    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

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

        lastMouseMovement =
            DateTime.Now;

        // ---------------------------------------------------------------
        // ICÔNE SYSTRAY
        // ---------------------------------------------------------------

        trayIcon =
            new NotifyIcon();

        trayIcon.Icon =
            SystemIcons.Application;

        trayIcon.Text =
            "Wallpaper Icon Hider";

        // ---------------------------------------------------------------
        // MENU PRINCIPAL
        // ---------------------------------------------------------------

        ContextMenuStrip menu =
            new ContextMenuStrip();

        enableItem =
            new ToolStripMenuItem(
                "Activer");

        disableItem =
            new ToolStripMenuItem(
                "Désactiver");

        delayItem =
            new ToolStripMenuItem();

        delayItem.Enabled =
            false;

        // ---------------------------------------------------------------
        // SOUS-MENU PARAMÈTRES
        // ---------------------------------------------------------------

        ToolStripMenuItem settingsMenu =
            new ToolStripMenuItem(
                "⚙ Paramètres");

        ToolStripMenuItem modifyDelayItem =
            new ToolStripMenuItem(
                "⏱ Modifier le délai...");

        startupItem =
            new ToolStripMenuItem(
                "Démarrer avec Windows");

        startupItem.CheckOnClick =
            true;

        startupItem.Checked =
            startWithWindows;

        settingsMenu.DropDownItems.Add(
            modifyDelayItem);

        settingsMenu.DropDownItems.Add(
            startupItem);

        // ---------------------------------------------------------------
        // QUITTER
        // ---------------------------------------------------------------

        ToolStripMenuItem exitItem =
            new ToolStripMenuItem(
                "✕ Quitter");

        // ---------------------------------------------------------------
        // CONSTRUCTION DU MENU
        // ---------------------------------------------------------------

        menu.Items.Add(
            enableItem);

        menu.Items.Add(
            disableItem);

        menu.Items.Add(
            new ToolStripSeparator());

        menu.Items.Add(
            delayItem);

        menu.Items.Add(
            settingsMenu);

        menu.Items.Add(
            new ToolStripSeparator());

        menu.Items.Add(
            exitItem);

        trayIcon.ContextMenuStrip =
            menu;

        // ---------------------------------------------------------------
        // ACTIONS
        // ---------------------------------------------------------------

        enableItem.Click +=
            (_, _) =>
                EnableProgram();

        disableItem.Click +=
            (_, _) =>
                DisableProgram();

        modifyDelayItem.Click +=
            (_, _) =>
                OpenDelaySettings();

        startupItem.Click +=
            (_, _) =>
            {
                UpdateStartupSetting(
                    startupItem.Checked);
            };

        exitItem.Click +=
            (_, _) =>
                Application.Exit();

        UpdateTrayMenu();

        trayIcon.Visible =
            true;

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
                // avant de quitter.
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