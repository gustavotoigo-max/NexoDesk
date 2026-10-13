using System.Runtime.InteropServices;
using NexoDesk.Shared.Messages;
using NexoDesk.Shared.Session;

namespace NexoDesk.App.Services;

/// <summary>
/// Aplica mouse e teclado no PC REMOTO com SendInput. O mouse usa coordenadas normalizadas (0..1)
/// convertidas para a tela primária; o teclado usa scan codes para independer do layout.
/// Mantém o controle de botões/teclas pressionados para soltar tudo ao fim da sessão (ReleaseAll).
/// </summary>
public sealed class SendInputSink : IInputSink
{
    private readonly object _gate = new();
    private readonly HashSet<ushort> _keysDown = [];
    private readonly HashSet<PointerButton> _buttonsDown = [];

    public void Apply(PointerEventMessage pointer)
    {
        int screenW = Math.Max(GetSystemMetrics(0), 1);
        int screenH = Math.Max(GetSystemMetrics(1), 1);
        // SendInput absoluto usa 0..65535 sobre a tela primária.
        int ax = (int)Math.Round(pointer.X * 65535.0);
        int ay = (int)Math.Round(pointer.Y * 65535.0);

        var inputs = new List<INPUT>(2);
        // Sempre move o cursor para a posição antes de clicar/rolar.
        inputs.Add(Mouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE, ax, ay, 0));

        uint flag = 0;
        int data = 0;
        switch (pointer.Action)
        {
            case PointerAction.Down:
                flag = DownFlag(pointer.Button);
                lock (_gate) _buttonsDown.Add(pointer.Button);
                break;
            case PointerAction.Up:
                flag = UpFlag(pointer.Button);
                lock (_gate) _buttonsDown.Remove(pointer.Button);
                break;
            case PointerAction.Wheel:
                flag = MOUSEEVENTF_WHEEL;
                data = pointer.WheelDelta;
                break;
            case PointerAction.Move:
                break;
        }
        if (flag != 0)
            inputs.Add(Mouse(flag, ax, ay, data));

        Send(inputs);
    }

    public void Apply(KeyEventMessage key)
    {
        bool up = key.Action == KeyAction.Up;
        lock (_gate)
        {
            if (up)
                _keysDown.Remove(key.ScanCode);
            else
                _keysDown.Add(key.ScanCode);
        }
        Send([KeyScan(key.ScanCode, key.IsExtended, up)]);
    }

    public void ReleaseAll()
    {
        var inputs = new List<INPUT>();
        lock (_gate)
        {
            foreach (ushort sc in _keysDown)
                inputs.Add(KeyScan(sc, false, up: true));
            _keysDown.Clear();
            foreach (PointerButton b in _buttonsDown)
                inputs.Add(Mouse(UpFlag(b), 0, 0, 0));
            _buttonsDown.Clear();
        }
        if (inputs.Count > 0)
            Send(inputs);
    }

    private static uint DownFlag(PointerButton b) => b switch
    {
        PointerButton.Left => MOUSEEVENTF_LEFTDOWN,
        PointerButton.Right => MOUSEEVENTF_RIGHTDOWN,
        PointerButton.Middle => MOUSEEVENTF_MIDDLEDOWN,
        _ => 0,
    };

    private static uint UpFlag(PointerButton b) => b switch
    {
        PointerButton.Left => MOUSEEVENTF_LEFTUP,
        PointerButton.Right => MOUSEEVENTF_RIGHTUP,
        PointerButton.Middle => MOUSEEVENTF_MIDDLEUP,
        _ => 0,
    };

    private static INPUT Mouse(uint flags, int ax, int ay, int data) => new()
    {
        type = INPUT_MOUSE,
        u = new INPUTUNION { mi = new MOUSEINPUT { dx = ax, dy = ay, mouseData = data, dwFlags = flags } },
    };

    private static INPUT KeyScan(ushort scanCode, bool extended, bool up)
    {
        uint flags = KEYEVENTF_SCANCODE;
        if (up) flags |= KEYEVENTF_KEYUP;
        if (extended) flags |= KEYEVENTF_EXTENDEDKEY;
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION { ki = new KEYBDINPUT { wVk = 0, wScan = scanCode, dwFlags = flags } },
        };
    }

    private static void Send(List<INPUT> inputs)
    {
        if (inputs.Count == 0)
            return;
        INPUT[] array = [.. inputs];
        SendInput((uint)array.Length, array, Marshal.SizeOf<INPUT>());
    }

    // --- interop ---
    private const int INPUT_MOUSE = 0;
    private const int INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx; public int dy; public int mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public int type; public INPUTUNION u; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
