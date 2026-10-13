using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using NexoDesk.App.ViewModels;
using NexoDesk.Shared.Messages;
using NexoDesk.Ui.Controls;

namespace NexoDesk.App.Views;

public partial class SessionWindow : NxWindow
{
    private readonly SessionViewModel _vm;

    public SessionWindow(SessionViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.CloseRequested += Close;

        // Controle: captura mouse e teclado sobre a imagem e envia ao PC REMOTO.
        RemoteImage.MouseMove += OnMouseMove;
        RemoteImage.MouseDown += OnMouseDown;
        RemoteImage.MouseUp += OnMouseUp;
        RemoteImage.MouseWheel += OnMouseWheel;
        RemoteImage.MouseEnter += (_, _) => { if (_vm.CanControl) RemoteImage.Focus(); };
        PreviewKeyDown += OnKeyDown;
        PreviewKeyUp += OnKeyUp;
    }

    /// <summary>
    /// Converte a posição do mouse sobre a imagem (ajustada com letterbox pelo Stretch=Uniform)
    /// em coordenadas normalizadas 0..1 da tela remota. Retorna false se estiver fora da imagem.
    /// </summary>
    private bool TryNormalize(MouseEventArgs e, out double nx, out double ny)
    {
        nx = ny = 0;
        double containerW = RemoteImage.ActualWidth;
        double containerH = RemoteImage.ActualHeight;
        if (containerW <= 0 || containerH <= 0)
            return false;

        double srcW = _vm.RemoteWidth;
        double srcH = _vm.RemoteHeight;
        double scale = Math.Min(containerW / srcW, containerH / srcH);
        double contentW = srcW * scale;
        double contentH = srcH * scale;
        double offsetX = (containerW - contentW) / 2;
        double offsetY = (containerH - contentH) / 2;

        Point p = e.GetPosition(RemoteImage);
        double x = (p.X - offsetX) / contentW;
        double y = (p.Y - offsetY) / contentH;
        if (x < 0 || x > 1 || y < 0 || y > 1)
            return false;
        nx = x;
        ny = y;
        return true;
    }

    private static PointerButton Button(MouseButton b) => b switch
    {
        MouseButton.Left => PointerButton.Left,
        MouseButton.Right => PointerButton.Right,
        MouseButton.Middle => PointerButton.Middle,
        _ => PointerButton.None,
    };

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_vm.CanControl && TryNormalize(e, out double nx, out double ny))
            _vm.SendPointer(nx, ny, PointerButton.None, PointerAction.Move);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_vm.CanControl)
            return;
        PointerButton button = Button(e.ChangedButton);
        if (button != PointerButton.None && TryNormalize(e, out double nx, out double ny))
        {
            RemoteImage.Focus();
            RemoteImage.CaptureMouse();
            _vm.SendPointer(nx, ny, button, PointerAction.Down);
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_vm.CanControl)
            return;
        PointerButton button = Button(e.ChangedButton);
        if (button != PointerButton.None && TryNormalize(e, out double nx, out double ny))
            _vm.SendPointer(nx, ny, button, PointerAction.Up);
        RemoteImage.ReleaseMouseCapture();
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_vm.CanControl && TryNormalize(e, out double nx, out double ny))
            _vm.SendPointer(nx, ny, PointerButton.None, PointerAction.Wheel, e.Delta);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (SendKey(e, KeyAction.Down))
            e.Handled = true;
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (SendKey(e, KeyAction.Up))
            e.Handled = true;
    }

    private bool SendKey(KeyEventArgs e, KeyAction action)
    {
        if (!_vm.CanControl)
            return false;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk <= 0)
            return false;
        uint scan = MapVirtualKey((uint)vk, 0);
        bool extended = IsExtended(key);
        _vm.SendKey((ushort)scan, (ushort)vk, extended, action);
        return true;
    }

    private static bool IsExtended(Key key) => key is
        Key.Up or Key.Down or Key.Left or Key.Right or
        Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Insert or Key.Delete or
        Key.RightCtrl or Key.RightAlt or Key.Apps or Key.LWin or Key.RWin or Key.PrintScreen;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    /// <summary>Fechar a janela encerra a sessão de forma ordenada (avisa o PC REMOTO).</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel && !_vm.IsEnded)
            _ = _vm.DisconnectAsync();
    }
}
