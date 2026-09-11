<#
.SYNOPSIS
    Reports which of F2-F5 Windows is willing to hand to an application, and what happens when one
    is pressed.

.DESCRIPTION
    The shortcut layer has one failure mode that leaves no trace anywhere: RegisterHotKey succeeds,
    nothing else is wrong, and the keypress still never arrives. When that happens nothing is logged
    and nothing is drawn, so the user is left unable to distinguish "the shortcut was never
    registered" from "the shortcut fired and the display write failed".

    This separates the two. It registers the same four identifiers the toolkit uses, listens for the
    messages, and prints what it sees. Run it with the toolkit closed, so the toolkit's own
    registrations do not stand in the way.

    Read the result like this:

      - "already held by another window" for a key means something else owns it and the toolkit
        cannot take it. Close whatever that is, or pick different keys.
      - Registering succeeds but pressing the key prints nothing means Windows registered the
        shortcut and is not delivering the keypress. That is almost always one of:
            * the game is running as administrator and the toolkit is not, so Windows withholds the
              message from the lower-privilege process;
            * the keyboard's Fn lock is on, so F2-F5 are sending media keys instead of function keys;
            * another process is holding a low-level keyboard hook that swallows the key first.
      - Pressing a key and seeing it reported here means the shortcut layer is working, and a toolkit
        that does nothing on the same key has a problem further along.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class HotkeyProbe
{
    public const uint WmHotkey = 0x0312;
    public const uint ModNoRepeat = 0x4000;

    // The identifiers the toolkit registers, so a "held by another window" answer here is the same
    // answer the toolkit got.
    private static readonly int[] Ids = { 0xEF20, 0xEF21, 0xEF22, 0xEF23 };
    private static readonly string[] Presets = { "Original (F2)", "Low (F3)", "Medium (F4)", "High (F5)" };
    private static readonly uint[] VirtualKeys = { 0x71, 0x72, 0x73, 0x74 };

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public UIntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int x;
        public int y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG message, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern int PeekMessage(out MSG message, IntPtr hwnd, uint min, uint max, uint remove);

    public enum Outcome { Held, Free, Taken }

    public static Outcome TryRegister(int index, out int error)
    {
        bool ok = RegisterHotKey(IntPtr.Zero, Ids[index], ModNoRepeat, VirtualKeys[index]);
        error = Marshal.GetLastWin32Error();

        if (ok) { return Outcome.Free; }
        return error == 1409 ? Outcome.Held : Outcome.Taken;
    }

    public static string[] Describe()
    {
        List<string> lines = new List<string>();

        for (int i = 0; i < Ids.Length; i++)
        {
            int error;
            Outcome outcome = TryRegister(i, out error);

            switch (outcome)
            {
                case Outcome.Free:
                    lines.Add(string.Format("{0,-16} free - the toolkit can claim it", Presets[i]));
                    break;
                case Outcome.Held:
                    lines.Add(string.Format("{0,-16} already held by another window (1409)", Presets[i]));
                    break;
                default:
                    lines.Add(string.Format("{0,-16} registration failed, win32 error {1}", Presets[i], error));
                    break;
            }

            if (outcome != Outcome.Held) { UnregisterHotKey(IntPtr.Zero, Ids[i]); }
        }

        return lines.ToArray();
    }

    /// <summary>Registers every key still available and blocks until the caller stops it.</summary>
    public static void Watch(Action<string> report)
    {
        List<int> registered = new List<int>();

        for (int i = 0; i < Ids.Length; i++)
        {
            int error;
            if (TryRegister(i, out error) == Outcome.Free) { registered.Add(i); }
        }

        if (registered.Count == 0) { return; }

        try
        {
            while (true)
            {
                MSG message;
                int result = GetMessage(out message, IntPtr.Zero, WmHotkey, WmHotkey);
                if (result <= 0) { break; }

                int id = (int)(uint)message.wParam;
                int index = Array.IndexOf(Ids, id);
                report(index >= 0 ? Presets[index] : string.Format("unknown id 0x{0:X}", id));
            }
        }
        finally
        {
            foreach (int index in registered) { UnregisterHotKey(IntPtr.Zero, Ids[index]); }
        }
    }
}
'@

Write-Host 'What Windows says about the four shortcuts:'
Write-Host ''

foreach ($line in [HotkeyProbe]::Describe()) {
    Write-Host ('  ' + $line)
}

Write-Host ''
Write-Host 'Now press F2, F3, F4 and F5. Each one that arrives is printed below.'
Write-Host 'Press Ctrl+C when you have tried them all.'
Write-Host ''

# Blocks while printing, so the keys that do arrive are visible as they arrive. The toolkit must be
# closed first: two windows cannot hold the same shortcut, and the message would go to whichever
# asked for it first.
[HotkeyProbe]::Watch({ param($preset) Write-Host ("  arrived: $preset") })
