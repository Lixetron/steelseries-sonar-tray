using System.Runtime.InteropServices;

namespace SonarQuickMixer.Tray;

internal sealed record TrayMenuCommand(string Text, Action Invoke);

/// <summary>
/// Shows the tray context menu as a native Win32 popup menu, so Windows draws
/// it with the standard dark menu style (hover highlight, keyboard support,
/// click-outside dismissal). Pair with <see cref="NativeDarkMode"/>.
/// </summary>
internal static class NativeTrayMenu
{
    private const uint MfString = 0x0000;
    private const uint TpmLeftAlign = 0x0000;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmBottomAlign = 0x0020;
    private const uint TpmReturnCmd = 0x0100;
    private const uint WmNull = 0x0000;

    public static void Show(IntPtr owner, System.Drawing.Point screenPoint, IReadOnlyList<TrayMenuCommand> commands)
    {
        if (owner == IntPtr.Zero || commands.Count == 0)
        {
            return;
        }

        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var actions = new Dictionary<uint, Action>(commands.Count);
            var commandId = 1u;

            foreach (var command in commands)
            {
                if (!AppendMenu(menu, MfString, commandId, command.Text))
                {
                    return;
                }

                actions[commandId] = command.Invoke;
                commandId++;
            }

            // The owner window must be foreground, otherwise the popup stays open
            // when the user clicks somewhere else.
            _ = SetForegroundWindow(owner);

            var selected = TrackPopupMenuEx(
                menu,
                TpmLeftAlign | TpmRightButton | TpmBottomAlign | TpmReturnCmd,
                screenPoint.X,
                screenPoint.Y,
                owner,
                IntPtr.Zero);

            // Classic Win32 workaround so the next popup dismisses reliably.
            _ = PostMessage(owner, WmNull, IntPtr.Zero, IntPtr.Zero);

            if (actions.TryGetValue(selected, out var action))
            {
                action();
            }
        }
        finally
        {
            _ = DestroyMenu(menu);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, uint itemId, string text);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr parameters);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
