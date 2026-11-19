using System.Runtime.InteropServices;
using System.Security.Principal;

namespace AppWatcher.Native;

/// <summary>Whether this user can run the two-minute file-access detection, which needs administrator rights.</summary>
internal static class Elevation
{
    public enum Kind
    {
        /// <summary>Already running with administrator rights (or UAC is off for an administrator): no prompt needed.</summary>
        Elevated,
        /// <summary>An administrator under UAC: Windows asks once for permission.</summary>
        CanElevate,
        /// <summary>Not an administrator: the detection is skipped.</summary>
        NotAdmin,
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);

    private const int TokenElevationType = 18; // 1 default, 2 full, 3 limited

    public static Kind Check()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(id);
            if (GetTokenInformation(id.Token, TokenElevationType, out var type, sizeof(int), out _))
            {
                if (type == 2) return Kind.Elevated;
                if (type == 3) return Kind.CanElevate;
            }
            return principal.IsInRole(WindowsBuiltInRole.Administrator) ? Kind.Elevated : Kind.NotAdmin;
        }
        catch { return Kind.NotAdmin; }
    }
}
