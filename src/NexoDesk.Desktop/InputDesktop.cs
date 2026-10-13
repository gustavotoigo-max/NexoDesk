using System.Runtime.InteropServices;
using System.Text;

namespace NexoDesk.Desktop;

/// <summary>
/// Prende a thread atual ao "desktop de entrada" ativo da sessão (Default quando desbloqueado,
/// Winlogon na tela de bloqueio/login/UAC). Só tem efeito útil quando o processo roda como SYSTEM
/// (serviço): aí a captura e a injeção acompanham a troca de desktop e funcionam na tela bloqueada.
///
/// Cada thread que captura ou injeta deve chamar <see cref="EnsureCurrentThreadOnInputDesktop"/>
/// no começo do seu ciclo; a reanexação só acontece quando o desktop ativo muda de nome.
/// </summary>
public static class InputDesktop
{
    [ThreadStatic] private static string? _attachedName;

    /// <summary>Nome do desktop de entrada atual (ex.: "Default", "Winlogon"), ou null se indisponível.</summary>
    public static string? CurrentInputDesktopName()
    {
        IntPtr hdesk = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (hdesk == IntPtr.Zero)
            return null;
        try
        {
            return GetDesktopName(hdesk);
        }
        finally
        {
            CloseDesktop(hdesk);
        }
    }

    /// <summary>
    /// Garante que a thread atual está no desktop de entrada ativo. Reanexa só quando o desktop
    /// mudou desde a última vez nesta thread. Devolve true se está (ou passou a estar) anexada.
    /// </summary>
    public static bool EnsureCurrentThreadOnInputDesktop()
    {
        IntPtr hdesk = OpenInputDesktop(0, false, GENERIC_ALL);
        if (hdesk == IntPtr.Zero)
            return false;
        try
        {
            string? name = GetDesktopName(hdesk);
            if (name is not null && string.Equals(name, _attachedName, StringComparison.OrdinalIgnoreCase))
                return true; // já estamos no desktop certo.

            if (!SetThreadDesktop(hdesk))
                return _attachedName is not null; // falhou a troca; segue no que já estava.

            _attachedName = name;
            return true;
        }
        finally
        {
            // SetThreadDesktop referencia o objeto; fechar o handle local é seguro — o desktop
            // permanece associado à thread. Fechar evita vazamento de handles a cada ciclo.
            CloseDesktop(hdesk);
        }
    }

    private static string? GetDesktopName(IntPtr hdesk)
    {
        var sb = new StringBuilder(256);
        if (GetUserObjectInformation(hdesk, UOI_NAME, sb, sb.Capacity, out _))
            return sb.ToString();
        return null;
    }

    private const uint DESKTOP_READOBJECTS = 0x0001;
    private const uint GENERIC_ALL = 0x10000000;
    private const int UOI_NAME = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex, StringBuilder pvInfo, int nLength, out int lpnLengthNeeded);
}
