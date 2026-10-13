using System.Security.Cryptography;
using System.Text;

namespace NexoDesk.Shared.Security;

/// <summary>
/// Senha de acesso do PC REMOTO: em vez de alguém clicar "Permitir", o ADMIN pareado envia uma senha
/// (configurada no PC REMOTO) a cada conexão. O PC REMOTO guarda só o hash PBKDF2-SHA256 (200 mil
/// iterações, sal aleatório), nunca a senha em texto. A verificação é de tempo fixo.
///
/// Formato guardado: "v1|&lt;salHex&gt;|&lt;hashHex&gt;". É um segundo fator além do mTLS: mesmo um
/// ADMIN pareado precisa da senha para abrir sessão (e a sessão roda sem tela de confirmação).
/// </summary>
public static class AccessSecret
{
    public const int MinLength = 6;
    private const int Iterations = 200_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const string Prefix = "v1";

    /// <summary>Gera o hash guardável a partir da senha em texto. Lança se a senha for curta demais.</summary>
    public static string Hash(string password)
    {
        if (password is null || password.Length < MinLength)
            throw new ArgumentException($"A senha de acesso precisa de ao menos {MinLength} caracteres.", nameof(password));

        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Prefix}|{Convert.ToHexString(salt)}|{Convert.ToHexString(hash)}";
    }

    /// <summary>True se a senha em texto corresponde ao hash guardado. False para hash ausente/malformado.</summary>
    public static bool Verify(string? password, string? stored)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(stored))
            return false;

        string[] parts = stored.Split('|');
        if (parts.Length != 3 || parts[0] != Prefix)
            return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromHexString(parts[1]);
            expected = Convert.FromHexString(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }
        if (salt.Length != SaltBytes || expected.Length != HashBytes)
            return false;

        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>True se já existe uma senha configurada.</summary>
    public static bool IsConfigured(string? stored) => !string.IsNullOrWhiteSpace(stored);
}
