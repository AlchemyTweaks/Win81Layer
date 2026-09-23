using System;
using System.Windows;
using System.Windows.Threading;

namespace Win81Layer;

internal static class SwitcherEdgeDiagnostics
{
    public static void Begin(Application app)
    {
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        AppSwitcher switcher = new AppSwitcher();
        try
        {
            switcher.ShowSwitcher();
            Logger.Log("SWITCHEREDGETEST: native switcher shown");

            DispatcherTimer hide = new DispatcherTimer(DispatcherPriority.Send)
            {
                Interval = TimeSpan.FromMilliseconds(700.0)
            };
            hide.Tick += delegate
            {
                hide.Stop();
                switcher.HideSwitcher();
                Logger.Log("SWITCHEREDGETEST: hide requested");
            };
            hide.Start();

            DispatcherTimer finish = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
            {
                Interval = TimeSpan.FromMilliseconds(2500.0)
            };
            finish.Tick += delegate
            {
                finish.Stop();
                Logger.Log("SWITCHEREDGETEST: completed");
                app.Shutdown(0);
            };
            finish.Start();
        }
        catch (Exception ex)
        {
            Logger.Log("SWITCHEREDGETEST failed: " + ex);
            app.Shutdown(1);
        }
    }
}
