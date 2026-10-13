using System.Windows;
using System.Windows.Threading;
using NexoDesk.Agent.Views;
using NexoDesk.Shared.Session;
using NexoDesk.Ui;
using NexoDesk.Ui.Controls;

namespace NexoDesk.Agent;

/// <summary>
/// NexoDesk Agent: roda na sessão interativa do usuário (nunca como serviço), sem elevação.
/// Uso: NexoDesk.Agent.exe [--minimized]
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstance;
    private AgentController? _controller;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private SessionIndicatorWindow? _indicator;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(initiallyOwned: true, @"Local\NexoDesk.Agent", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("O NexoDesk Agent já está em execução. Procure o ícone na área de notificação.",
                "NexoDesk Agent", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandled;
        ThemeManager.Install(this);

        _controller = new AgentController(Dispatcher);
        _window = new MainWindow(_controller);
        _tray = new TrayIcon(ShowWindow, () => _controller.EndSession(), () => _ = ExitAsync());

        _controller.StateChanged += () => _tray.Update(_controller.State, _controller.SessionAdmin);
        _controller.SessionActivated += OnSessionActivated;
        _controller.SessionFinished += OnSessionFinished;

        await _controller.StartAsync();
        _tray.Update(_controller.State, _controller.SessionAdmin);

        bool minimized = e.Args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));
        if (!minimized || _controller.State != AgentState.Listening)
            ShowWindow();
        else
            _tray.Notify("NexoDesk Agent", "Em execução. Conexões remotas só com administradores autorizados.");
    }

    private void OnSessionActivated(AgentSession session)
    {
        _indicator?.CloseForSessionEnd();
        _indicator = new SessionIndicatorWindow(_controller!, session);
        _indicator.Show();
        _tray?.Update(AgentState.InSession, session.Client.Name);
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

    private async Task ExitAsync()
    {
        if (_exiting || _controller is null)
            return;

        if (_controller.HasSession)
        {
            ShowWindow();
            bool confirm = NxDialogWindow.Ask(_window, new DialogOptions
            {
                Kind = DialogKind.Warning,
                Title = "Sair do NexoDesk Agent?",
                Text = $"{_controller.SessionAdmin} está acessando este computador. Sair encerra o acesso e para de aceitar conexões.",
                PrimaryText = "Encerrar e sair",
                SecondaryText = "Continuar",
                PrimaryIsDanger = true,
            });
            if (!confirm)
                return;
        }

        _exiting = true;
        await _controller.DisposeAsync();
        _indicator?.CloseForSessionEnd();
        _tray?.Dispose();
        if (_window is not null)
        {
            _window.AllowClose = true;
            _window.Close();
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        if (_singleInstance is not null)
        {
            try { _singleInstance.ReleaseMutex(); } catch (ApplicationException) { }
            _singleInstance.Dispose();
        }
        base.OnExit(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        _controller?.AddLog(LogLevel.Error, $"Erro inesperado: {e.Exception.GetType().Name}: {e.Exception.Message}");
    }
}
