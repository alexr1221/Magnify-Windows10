using System;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Threading;

namespace Magnify_Console_Opera
{
    class Program
    {
        // Déclarations P/Invoke pour Magnification.dll
        [DllImport("Magnification.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern bool MagInitialize();

        [DllImport("Magnification.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern bool MagUninitialize();

        [DllImport("Magnification.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern bool MagSetFullscreenTransform(float magnificationFactor, int xOffset, int yOffset);

        // Déclarations P/Invoke pour user32.dll
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr CreateWindowEx(
            uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll")]
        private static extern uint GetLastError();

        // Structures
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        // Constantes
        private const string MAGNIFIER_CLASS_NAME = "Magnifier";
        private const string TargetApp = "opera"; // Nom du processus cible (Opera)
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const int SCREEN_WIDTH = 1920; // Ajustez selon votre résolution
        private const int SCREEN_HEIGHT = 1080; // Ajustez selon votre résolution
        private const float EDGE_THRESHOLD = 0.25f; // Seuil de proximité des bords (25% de la taille de l'écran)
        private const int OFFSET_STABILITY_THRESHOLD = 5; // Seuil réduit pour plus de réactivité
        private const float smoothingFactor = 0.725f;
        private const float ZOOM_LEVEL = 3.33f; // Variable globale pour le niveau de zoom

        // Variables d'état
        private static int lastXOffset = 0; // Offset X précédent pour le lissage
        private static int lastYOffset = 0; // Offset Y précédent pour le lissage
        private static bool lastZoomDisabledState = false; // Indique si le zoom a été désactivé
        private static bool lastZoomEnabledState = false; // Indique si le zoom actif a été appliqué
        private static float lastZoomLevel = 0.0f; // Dernier niveau de zoom utilisé
        private static int lastAppliedXOffset = 0; // Dernier xOffset appliqué
        private static int lastAppliedYOffset = 0; // Dernier yOffset appliqué
        static IntPtr hwndTarget;
        static int visibleWidth, visibleHeight, xOffset, yOffset, targetXOffset, targetYOffset, maxXOffset, maxYOffset;
        static bool isOperaActive;

        static void Main(string[] args)
        {
            // Initialiser l'API de Magnification
            if (!MagInitialize())
            {
                Console.WriteLine("Échec de l'initialisation de la magnification. Erreur : " + GetLastError());
                return;
            }

            // Obtenir le handle d'instance de l'application
            IntPtr hInstance = GetModuleHandle(null);
            if (hInstance == IntPtr.Zero)
            {
                Console.WriteLine("Échec de l'obtention du handle d'instance. Erreur : " + GetLastError());
                MagUninitialize();
                return;
            }

            // Créer une fenêtre native pour la magnification
            IntPtr magnifierWindow = CreateWindowEx(
                0, // dwExStyle
                MAGNIFIER_CLASS_NAME, // Classe MSH_MAGNIFIER
                "MagnifierWindow", // Nom de la fenêtre
                0, // Style (invisible)
                0, 0, SCREEN_WIDTH, SCREEN_HEIGHT, // Position et taille
                IntPtr.Zero, // Parent
                IntPtr.Zero, // Menu
                hInstance, // Instance
                IntPtr.Zero); // Paramètres

            if (magnifierWindow == IntPtr.Zero)
            {
                Console.WriteLine("Échec de la création de la fenêtre de magnification. Erreur : " + GetLastError());
                MagUninitialize();
                return;
            }

            // Positionner la fenêtre pour couvrir l'écran
            if (!SetWindowPos(magnifierWindow, IntPtr.Zero, 0, 0, SCREEN_WIDTH, SCREEN_HEIGHT, SWP_NOZORDER | SWP_NOACTIVATE))
            {
                Console.WriteLine("Échec du positionnement de la fenêtre. Erreur : " + GetLastError());
                DestroyWindow(magnifierWindow);
                MagUninitialize();
                return;
            }

            Console.WriteLine($"Surveillance des fenêtres en cours (cible : {TargetApp}). Appuyez sur Ctrl+C pour quitter.");

            // Boucle principale pour vérifier la fenêtre active
            try
            {
                while (true)
                {
                    ApplyMagnification(magnifierWindow, ZOOM_LEVEL); // Utilisation de la variable globale
                    Thread.Sleep(0); // Réduit à 0ms pour un suivi plus réactif
                }
            }
            finally
            {
                // Nettoyage
                MagUninitialize();
                DestroyWindow(magnifierWindow);
            }
        }

        private static void ApplyMagnification(IntPtr magnifierWindow, float zoomLevel)
        {
            GetActiveWindowProcessName();
            isOperaActive = activeWindowProcessName != null && activeWindowProcessName.Equals(TargetApp, StringComparison.OrdinalIgnoreCase);

            if (!isOperaActive)
            {
                // Vérifier si le zoom a déjà été désactivé
                if (!lastZoomDisabledState)
                {
                    if (!MagSetFullscreenTransform(1.0f, 0, 0))
                    {
                        Console.WriteLine("Échec de la désactivation du zoom. Erreur : " + GetLastError());
                    }
                    lastZoomDisabledState = true;
                    lastZoomEnabledState = false;
                }
                //wasOperaActive = false;
                return;
            }

            hwndTarget = GetForegroundWindow();
            if (hwndTarget == IntPtr.Zero || hwndTarget == magnifierWindow)
            {
                return;
            }

            visibleWidth = (int)((float)SCREEN_WIDTH / (float)zoomLevel);
            visibleHeight = (int)((float)SCREEN_HEIGHT / (float)zoomLevel);

            xOffset = 0; yOffset = 0;

            if (!GetCursorPos(out POINT cursorPos))
            {
                Console.WriteLine("Échec de l'obtention de la position de la souris. Erreur : " + GetLastError());
                return;
            }

            // Calculer l’offset pour centrer la souris (position réelle, pas multipliée)
            targetXOffset = (int)(cursorPos.X * 1.275f)/* - visibleWidth /10*/;
            targetYOffset = (int)(cursorPos.Y * 1.275f) /*- visibleHeight /10*/;

            // Limiter pour rester dans les bornes de l’écran
            maxXOffset = SCREEN_WIDTH - visibleWidth;
            maxYOffset = SCREEN_HEIGHT - visibleHeight;
            //targetXOffset = Math.Max(0, Math.Min(maxXOffset, targetXOffset));
            //targetYOffset = Math.Max(0, Math.Min(maxYOffset, targetYOffset));

            // Lissage doux (ajustable)
            xOffset = (int)(lastXOffset + (targetXOffset - lastXOffset) * smoothingFactor);
            yOffset = (int)(lastYOffset + (targetYOffset - lastYOffset) * smoothingFactor);

            // Vérifier si le zoom a déjà été appliqué avec les mêmes paramètres
            if (!lastZoomEnabledState || lastZoomLevel != zoomLevel || lastAppliedXOffset != xOffset || lastAppliedYOffset != yOffset)
            {
                // Appliquer le zoom plein écran
                if (!MagSetFullscreenTransform(zoomLevel, xOffset, yOffset))
                {
                    Console.WriteLine("Échec de la configuration du zoom plein écran. Erreur : " + GetLastError());
                    return;
                }
                lastZoomEnabledState = true;
                lastZoomDisabledState = false;
                lastZoomLevel = zoomLevel;
                lastAppliedXOffset = xOffset;
                lastAppliedYOffset = yOffset;
            }

            // Mémoriser les derniers offsets pour le lissage
            lastXOffset = xOffset;
            lastYOffset = yOffset;

            //Console.WriteLine($"Zoom x{zoomLevel} | Souris : ({cursorPos.X}, {cursorPos.Y}) | Offsets : ({xOffset}, {yOffset})");
        }

        static IntPtr hwnd;
        static Process process;
        static string activeWindowProcessName;
        private static void GetActiveWindowProcessName()
        {
            hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                Console.WriteLine("Aucune fenêtre active détectée.");
                activeWindowProcessName = null;
            }

            GetWindowThreadProcessId(hwnd, out uint pid);
            try
            {
                process = Process.GetProcessById((int)pid);
                activeWindowProcessName = process.ProcessName;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erreur lors de la récupération du nom du processus : {ex.Message}");
                activeWindowProcessName = null;
            }
        }
    }
}
