using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Loom;

/// <summary>WinExe has no console; CLI mode attaches to the parent console and reopens the std streams.</summary>
public static class ConsoleHelper
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    private static bool _attached;

    public static void Attach()
    {
        if (_attached) return;
        _attached = true;

        var hasConsole = false;
        try
        {
            hasConsole = AttachConsole(AttachParentProcess);
        }
        catch
        {
            // no console available
        }

        try
        {
            // Even without a console window the process may have inherited redirected std handles
            // (pwsh | Out-String, CI runners). Reopen writers in both cases.
            var stdout = Console.OpenStandardOutput();
            var stderr = Console.OpenStandardError();
            if (stdout is not null)
            {
                Console.SetOut(new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true });
            }

            if (stderr is not null)
            {
                Console.SetError(new StreamWriter(stderr, new UTF8Encoding(false)) { AutoFlush = true });
            }

            if (hasConsole)
            {
                try
                {
                    Console.OutputEncoding = new UTF8Encoding(false);
                }
                catch
                {
                    // redirected output cannot change encoding
                }
            }
        }
        catch
        {
            // best effort; inherited writers remain in place
        }
    }

    public static void Detach()
    {
        if (!_attached) return;
        _attached = false;
        try
        {
            FreeConsole();
        }
        catch
        {
            // ignore
        }
    }
}
