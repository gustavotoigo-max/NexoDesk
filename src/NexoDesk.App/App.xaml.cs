using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using NexoDesk.App.Services;
using NexoDesk.App.ViewModels;
using NexoDesk.App.Views;
using NexoDesk.Shared.Configuration;
using NexoDesk.Shared.Session;
using NexoDesk.Ui;
using NexoDesk.Ui.Controls;

namespace NexoDesk.App;

/// <summary>
/// NexoDesk: o mesmo programa para ADMIN e PC REMOTO. Roda na sessão do usuário, sem elevação.
/// Uso: NexoDesk.exe [--minimized]
/// </summary>
public partial class App : Application
{
    private const string MutexName = @"Local\NexoDesk.Instance";
    private const string ShowEventName = @"Local\NexoDesk.Show";

    private Mutex? _singleInstance;
    private EventWaitHandle? _showSignal;
    private AppState? _state;
    private MainWindow? _window;
    private RemoteViewModel? _remote;
    private AdminViewModel? _admin;
    private TrayIcon? _tray;
    private SessionIndicatorWindow? _indicator;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(initiallyOwned: true, MutexName, out bool isFirst);
        if (!isFirst)
        {
            // Já aberto: pede para a cópia em execução aparecer e sai.
            try
            {
                using EventWaitHandle show = EventWaitHandle.OpenExisting(ShowEventName);
                show.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }
        ListenForShowRequests();

        DispatcherUnhandledException += OnUnhandled;
        ThemeManager.Install(this);

        try
        {
            _state = AppState.Load();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Não foi possível preparar o NexoDesk neste computador.\n\n{ex.Message}",
                "NexoDesk", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        if (_state.IsFirstRun)
        {
            var setup = new SetupWindow(new SetupViewModel(_state));
            if (setup.ShowDialog() != true || _state.Config.Role is null)
            {
                Shutdown();
                return;
            }
        }

        bool minimized = e.Args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));
        await StartRoleAsync(minimized);
    }

    private async Task StartRoleAsync(bool minimized)
    {
        AppState state = _state!;
        if (state.Config.Role == DeviceRole.Remote)
        {
            _remote = new RemoteViewModel(state, Dispatcher);
            _window = new MainWindow(new ShellViewModel(state, _remote, SwitchRole));
            _window.Closing += OnMainWindowClosing;
            _tray = new TrayIcon(ShowWindow, () => _remote.EndSession(), () => _ = ExitAsync());
            _remote.StatusChanged += () => _tray.Update(_remote.IsReady, _remote.HasSession, _remote.SessionAdmin);
            _remote.SessionActivated += OnSessionActivated;
            _remote.SessionFinished += OnSessionFinished;

            if (!minimized)
                ShowWindow();
            await _remote.StartAsync();
            _tray.Update(_remote.IsReady, _remote.HasSession, _remote.SessionAdmin);
            if (minimized && !_remote.IsReady)
                ShowWindow();
            else if (minimized)
                _tray.Notify("NexoDesk", "PC REMOTO pronto. O acesso só acontece com o ADMIN pareado.");
        }
        else
        {
            Autostart.Set(false);
            _admin = new AdminViewModel(state);
            _window = new MainWindow(new ShellViewModel(state, _admin, SwitchRole));
            _window.Closing += OnMainWindowClosing;
            ShowWindow();
            _admin.Initialize();
        }
    }

    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting)
            return;

        if (_remote is not null)
        {
            // PC REMOTO continua disponível na área de notificação.
            e.Cancel = true;
            _window!.Hide();
            _tray?.Notify("NexoDesk continua aberto", "O ADMIN ainda pode acessar este computador. Para sair, use o ícone perto do relógio.");
            return;
        }

        if (_admin is { IsConnecting: true } || _admin is { HasOpenSessions: true })
        {
            bool close = NxDialogWindow.Ask(_window, new DialogOptions
            {
                Kind = DialogKind.Warning,
                Title = "Fechar o NexoDesk?",
                Text = "Há acessos abertos. Eles serão encerrados e os PCs REMOTOS serão avisados.",
                PrimaryText = "Fechar",
                SecondaryText = "Continuar usando",
                PrimaryIsDanger = true,
            });
            if (!close)
            {
                e.Cancel = true;
                return;
            }
        }

        e.Cancel = true;
        _ = ExitAsync();
    }

    private void OnSessionActivated(AgentSession session)
    {
        _indicator?.CloseForSessionEnd();
        _indicator = new SessionIndicatorWindow(() => _remote?.EndSession(), session);
        _indicator.Show();
    }

    private void OnSessionFinished()
    {
        _indicator?.CloseForSessionEnd();
        _indicator = null;
        _tray?.Notify("NexoDesk", "O acesso remoto foi encerrado.");
    }

    private void ShowWindow()
    {
        if (_window is null)
            return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void SwitchRole()
    {
        if (_state is null)
            return;
        bool toRemote = _state.Config.Role == DeviceRole.Admin;
        bool confirm = NxDialogWindow.Ask(_window, new DialogOptions
        {
            Kind = DialogKind.Warning,
            Title = toRemote ? "Transformar este computador em PC REMOTO?" : "Transformar este computador em ADMIN?",
            Text = toRemote
                ? "Ele deixará de acessar outros computadores e passará a poder ser acessado pelo ADMIN pareado. O NexoDesk será reiniciado. Os pareamentos são mantidos."
                : "Ele deixará de poder ser acessado e passará a acessar os PCs REMOTOS. O NexoDesk será reiniciado. Os pareamentos são mantidos.",
            PrimaryText = "Mudar e reiniciar",
            SecondaryText = "Cancelar",
        });
        if (!confirm)
            return;

        _state.Update(c => c.Role = toRemote ? DeviceRole.Remote : DeviceRole.Admin);
        _ = ExitAsync(restart: true);
    }

    private async Task ExitAsync(bool restart = false)
    {
        if (_exiting)
            return;

        if (_remote is { HasSession: true } && !restart)
        {
            ShowWindow();
            bool confirm = NxDialogWindow.Ask(_window, new DialogOptions
            {
                Kind = DialogKind.Warning,
                Title = "Sair do NexoDesk?",
                Text = $"O ADMIN \"{_remote.SessionAdmin}\" está acessando este computador. Sair encerra o acesso e o ADMIN não conseguirá mais conectar até o NexoDesk ser aberto de novo.",
                PrimaryText = "Encerrar e sair",
                SecondaryText = "Continuar",
                PrimaryIsDanger = true,
            });
            if (!confirm)
                return;
        }

        _exiting = true;
        foreach (SessionWindow w in Windows.OfType<SessionWindow>().ToList())
            w.Close();
        if (_remote is not null)
            await _remote.DisposeAsync();
        _indicator?.CloseForSessionEnd();
        _tray?.Dispose();
        _window?.Close();

        if (restart)
        {
            ReleaseSingleInstance();
            Process.Start(new ProcessStartInfo(AppPaths.ExecutablePath) { UseShellExecute = true });
        }
        Shutdown();
    }

    private void ListenForShowRequests()
    {
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var thread = new Thread(() =>
        {
            while (_showSignal.WaitOne())
            {
                if (_exiting)
                    break;
                Dispatcher.InvokeAsync(ShowWindow);
            }
        })
        { IsBackground = true, Name = "NexoDesk.Show" };
        thread.Start();
    }

    private void ReleaseSingleInstance()
    {
        if (_singleInstance is null)
            return;
        try { _singleInstance.ReleaseMutex(); } catch (ApplicationException) { }
        _singleInstance.Dispose();
        _singleInstance = null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        ReleaseSingleInstance();
        base.OnExit(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        string text = $"Erro inesperado: {e.Exception.GetType().Name}: {e.Exception.Message}";
        if (_remote is not null)
            _remote.AddLog(LogLevel.Error, text);
        else if (_admin is not null)
            _admin.AddLog(LogLevel.Error, text);
        else
            MessageBox.Show(text, "NexoDesk", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
