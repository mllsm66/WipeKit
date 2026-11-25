using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace WipeKit
{
    public partial class App : Application
    {
        public App()
        {
#pragma warning disable WPF0001
            // Windows-11-Design (Fluent), folgt hellem/dunklem Modus des Systems
            ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
            DispatcherUnhandledException += OnUnhandledException;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var args = e.Args;

            // Automatische Prüfungen für den Build auf GitHub
            if (args.Length >= 2 && args[0] == "--engine-test")
            {
                Shutdown(SelfTests.EngineTest(args[1]));
                return;
            }

            if (args.Length >= 1 && args[0] == "--health-test")
            {
                Shutdown(SelfTests.HealthTest());
                return;
            }

            // Gegenprobe der Hub-Verschlüsselung (tests/hub_crypto.py)
            if (args.Length >= 3 && args[0] == "--hub-seal")
            {
                try
                {
                    var sealedBytes = Push.Seal(System.Text.Encoding.UTF8.GetBytes("WipeKit Hub Test"), Convert.FromBase64String(args[1].Trim()));
                    File.WriteAllBytes(args[2], sealedBytes);
                    Shutdown(0);
                }
                catch (Exception ex)
                {
                    File.WriteAllText(args[2] + ".err", ex.ToString());
                    Shutdown(1);
                }
                return;
            }

            if (args.Length >= 3 && args[0] == "--disk-test")
            {
                Shutdown(SelfTests.DiskTest(int.Parse(args[1]), args[2]));
                return;
            }

            bool smoke = args.Contains("--smoke-test");
            var window = new ShellWindow(smoke);
            MainWindow = window;
            window.Show();
        }

        void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            try
            {
                string log = Path.Combine(Path.GetTempPath(), "WipeKit-Fehler.log");
                File.AppendAllText(log, $"{DateTime.Now:u}\n{e.Exception}\n\n");
            }
            catch { }
            if (Environment.GetCommandLineArgs().Contains("--smoke-test"))
            {
                Shutdown(1);
                e.Handled = true;
                return;
            }
            MessageBox.Show("Ein unerwarteter Fehler ist aufgetreten:\n\n" + e.Exception.Message,
                "WipeKit", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }
    }
}
