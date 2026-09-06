using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Threading;
using Login38.App.Services;
using Login38.App.ViewModels;
using Login38.App.Views;
using Login38.Core.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Login38.App;

/// <summary>
/// Composition root: builds the service provider, then shows the window.
/// </summary>
/// <remarks>
/// One process for the whole session. The patch pipeline and the helper features run
/// here as background tasks, so the launcher stays alive while the game does — which is
/// why the reference's second "stage 2" process is not reproduced.
/// </remarks>
public partial class App : Application
{
    private IHost? _host;

    /// <summary>
    /// Catches what the dispatcher cannot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The dispatcher's handler only sees exceptions once there is a dispatcher pumping
    /// them, which leaves the whole of startup uncovered: a theme dictionary that will not
    /// load, a framework a machine does not have, a service that throws while the container
    /// is building it. All of those end the process before a window exists, and what the
    /// player sees is a double click that does nothing at all.
    /// </para>
    /// <para>
    /// Subscribed from the constructor because that runs before <c>InitializeComponent</c>,
    /// which is itself far enough into startup to fail. Nothing here can keep the process
    /// alive — it is already ending — but it can say why on the way out, which is the whole
    /// difference between a support case and a shrug.
    /// </para>
    /// </remarks>
    public App() =>
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Complain(e.ExceptionObject as Exception);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            Start();
        }
        catch (Exception failure)
        {
            // Deliberately everything. Whatever went wrong, a player looking at a window that
            // never appeared is owed the reason, and there is nothing left to protect: the
            // launcher has not started and is about to stop.
            Complain(failure);
            Shutdown();
        }
    }

    private void Start()
    {
        _host = Host.CreateApplicationBuilder()
            .ConfigureLauncher()
            .Build();

        Stamp(_host.Services.GetService<ILogger<App>>());

        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();

        // Nothing shuts this down but the window's own close, or the notification area's
        // menu. It cannot end with the main window: once a game is running the window is
        // hidden, and hiding it would end the patching and the helper with it. So the end
        // of the session is said here, once, for the one window that is the launcher.
        window.Closed += (_, _) => Shutdown();
    }

    /// <summary>
    /// Says which build wrote the log, in the log.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A launcher is deployed by copying one file over another, and the copy fails silently
    /// while the old one is running. Three separate sessions were then spent reading a log
    /// for behaviour that had already been fixed, because nothing in it said which build had
    /// produced it and the only way to tell was to hash the file afterwards and hope nobody
    /// had swapped it since.
    /// </para>
    /// <para>
    /// The hash of the running executable is what makes the log answer that on its own. It is
    /// the same number a deployment quotes, which is the point — anything derived instead
    /// from the assembly would be correct and still not comparable to what was handed over.
    /// </para>
    /// </remarks>
    private static void Stamp(ILogger<App>? logger)
    {
        if (logger is null)
        {
            return;
        }

        try
        {
            var path = Environment.ProcessPath;

            if (path is null)
            {
                logger.LogInformation("launcher started; the running image could not be named");

                return;
            }

            using var file = File.OpenRead(path);

            logger.LogInformation(
                "launcher started; {Path} build {Build} of {Written:u}",
                path,
                Convert.ToHexString(SHA256.HashData(file))[..16],
                File.GetLastWriteTimeUtc(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Worth a session that cannot name itself rather than one that will not start.
            logger.LogInformation("launcher started; the running image could not be read: {Message}", e.Message);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Reports what would otherwise be a silent disappearance.
    /// </summary>
    /// <remarks>
    /// A launcher that vanishes takes its patch tasks with it and leaves the player with a
    /// game that is running but unpatched, and nothing on screen to explain it.
    /// </remarks>
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Complain(e.Exception);

        e.Handled = true;
    }

    /// <summary>Says what went wrong, on screen and in the log if there is one.</summary>
    /// <remarks>
    /// The log is off unless a support case has asked for it, so the message box is the only
    /// thing most players will ever see. Its own failure is swallowed: this runs on the way
    /// out of a process that is already going, and a second exception here would replace a
    /// readable message with nothing.
    /// </remarks>
    private void Complain(Exception? failure)
    {
        if (failure is null)
        {
            return;
        }

        try
        {
            _host?.Services.GetService<ILogger<App>>()?.LogCritical(failure, "Unhandled exception");

            MessageBox.Show(
                Explain(failure), "Launcher error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception)
        {
            // Nothing left to try. The process is ending either way.
        }
    }

    /// <summary>
    /// The whole chain of an exception, outermost first.
    /// </summary>
    /// <remarks>
    /// Only the outermost used to be shown, and for the failures that reach here that is
    /// the one line with nothing in it. WPF wraps everything a window does in
    /// "Initialization of 'X' threw an exception" or "provide value on
    /// 'StaticResourceHolder' threw"; which resource, which property and which value are
    /// all in the exception underneath. Two of these have now been diagnosed from a
    /// screenshot of a message box that had already thrown that away.
    /// </remarks>
    private static string Explain(Exception exception)
    {
        var chain = new List<string>();

        for (var current = exception; current is not null; current = current.InnerException)
        {
            chain.Add($"{current.GetType().Name}: {current.Message}");
        }

        return string.Join(Environment.NewLine + "  ← ", chain);
    }
}

file static class HostBuilderExtensions
{
    internal static HostApplicationBuilder ConfigureLauncher(this HostApplicationBuilder builder)
    {
        builder.Logging.AddLauncherLogging();

        builder.Services.AddLauncher();
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MainWindow>();

        return builder;
    }
}
