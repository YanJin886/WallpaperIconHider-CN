using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
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
    private static extern short GetAsyncKeyState(int vKey);

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

        UpdateTrayIcon();
    }

    private static void DisableProgram()
    {
        programEnabled = false;

        WriteLog(
            "SURVEILLANCE DÉSACTIVÉE.");

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

    private sealed class ModernMenuForm : Form
    {
        private const int MenuWidth = 260;
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
                "Segoe UI",
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
                                "Segoe UI Semibold",
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
            Icon? image = null)
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
                    action);

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
                "Activer",
                HeaderHeight,
                enableAction,
                enabled,
                false,
                trayIconOpen);

            form.AddRow(
                "",
                "Désactiver",
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
                "Paramètres",
                separator1 + 2,
                settingsAction,
                false,
                true);

            int separator2 =
                separator1 + 2 + RowHeight;

            form.AddSeparator(separator2);

            form.AddRow(
                "×",
                "Quitter",
                separator2 + 2,
                exitAction);

            return form;
        }

        public static ModernMenuForm CreateSettingsMenu(
            int delay,
            bool startupEnabled,
            Action delayAction,
            Action startupAction,
            Action backAction)
        {
            ModernMenuForm form =
                new ModernMenuForm(
                    HeaderHeight +
                    RowHeight * 2 +
                    2 +
                    12);

            form.AddHeader(
                "Paramètres",
                backAction);

            int firstRowY = HeaderHeight;

            form.AddRow(
                "⏱",
                $"Délai de masquage : {delay} s",
                firstRowY,
                delayAction,
                false,
                true);

            form.AddRow(
                "□",
                "Démarrer avec Windows",
                firstRowY + RowHeight,
                startupAction,
                startupEnabled);

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
                "Délai de masquage",
                backAction);

            for (int i = MinimumDelaySeconds;
                 i <= MaximumDelaySeconds;
                 i++)
            {
                int value = i;

                form.AddRow(
                    " ",
                    $"{value} seconde" +
                    (value > 1 ? "s" : ""),
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
                Action action)
            {
                this.iconText = iconText;
                this.rowIcon = rowIcon;
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
                else if (!string.IsNullOrEmpty(iconText))
                {
                    using Font iconFont =
                        new Font(
                            "Segoe UI Symbol",
                            11f,
                            FontStyle.Regular,
                            GraphicsUnit.Point);

                    using Brush iconBrush =
                        new SolidBrush(
                            hovered
                                ? accentColor
                                : SecondaryText);

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
                        "Segoe UI",
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