using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace ProjectEcho.App;

/// <summary>Explicit developer probe: no MainWindow, library, devices, Host or audio access.</summary>
internal static class SingleInstanceProbe
{
    internal static async Task RunAsync(Application app, string[] args)
    {
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string Option(string name, string? fallback = null)
        {
            int index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1]
                : fallback ?? throw new ArgumentException("Missing single-instance probe option: " + name);
        }
        string scope = Option("--instance-scope");
        if (!Guid.TryParseExact(scope, "N", out _)) throw new ArgumentException("Probe scope must be a random N-format GUID.");
        string output = Path.GetFullPath(Option("--probe-output"));
        string allowed = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), ".local", "M3F")) + Path.DirectorySeparatorChar;
        if (!output.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Probe output must be inside .local/M3F.");
        Directory.CreateDirectory(output);
        string reportPath = Path.Combine(output, Environment.ProcessId + ".json");
        int delayMs = Math.Clamp(int.Parse(Option("--probe-startup-delay-ms", "0")), 0, 10000);
        int holdMs = Math.Clamp(int.Parse(Option("--probe-hold-ms", "30000")), 1000, 60000);
        bool minimized = args.Contains("--probe-minimized");
        var events = new List<object>();
        Window? window = null;
        bool pending = false;
        int activationCount = 0;
        string role = "waiting";
        string phase = "starting";
        var elapsed = Stopwatch.StartNew();
        void Save()
        {
            string content = JsonSerializer.Serialize(new
            {
                Role = role, Phase = phase, ProcessId = Environment.ProcessId,
                Os = Environment.OSVersion.Version.ToString(), ElapsedMs = elapsed.ElapsedMilliseconds,
                ActivationCount = activationCount, WindowState = window?.WindowState.ToString(),
                WindowVisible = window?.IsVisible, Events = events,
                CaptureStarted = false, MicrophoneStarted = false, ProductionInstanceContacted = false
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(reportPath + ".new", content);
            File.Move(reportPath + ".new", reportPath, overwrite: true);
        }
        void Activate()
        {
            activationCount++;
            bool beforeReady = window is null;
            bool? foreground = null;
            if (beforeReady) pending = true;
            else foreground = WindowActivation.Restore(window!);
            events.Add(new { Event = "activation", BeforeWindowReady = beforeReady, ForegroundGranted = foreground,
                WindowState = window?.WindowState.ToString(), ElapsedMs = elapsed.ElapsedMilliseconds });
            Save();
        }
        try
        {
            using var instance = await SingleInstanceCoordinator.AcquireOrActivateAsync(
                () => app.Dispatcher.BeginInvoke(new Action(Activate)), scope);
            if (instance is null)
            {
                role = "secondary"; phase = "forwarded"; Save(); app.Shutdown(0); return;
            }
            role = "primary"; phase = "owned-before-window"; Save();
            if (delayMs > 0) await Task.Delay(delayMs);
            window = new Window
            {
                Title = "ProjectEcho single-instance probe", Width = 380, Height = 140,
                Content = new TextBlock { Text = "M3F single-instance test — no audio or library access", Margin = new Thickness(18) },
                ShowInTaskbar = false
            };
            app.MainWindow = window;
            window.Show();
            if (minimized) window.WindowState = WindowState.Minimized;
            if (pending)
            {
                bool foreground = WindowActivation.Restore(window);
                events.Add(new { Event = "pending-activation-applied", ForegroundGranted = foreground,
                    WindowState = window.WindowState.ToString(), ElapsedMs = elapsed.ElapsedMilliseconds });
            }
            phase = "window-ready"; Save();
            var hold = Stopwatch.StartNew();
            while (hold.ElapsedMilliseconds < holdMs && !File.Exists(Path.Combine(output, "stop-" + Environment.ProcessId)))
                await Task.Delay(50);
            phase = "closed"; Save();
            window.Close();
        }
        catch (Exception ex)
        {
            phase = "failed";
            events.Add(new { Event = "failure", Detail = ex.ToString() });
            Save(); app.Shutdown(1); return;
        }
        app.Shutdown(0);
    }
}
