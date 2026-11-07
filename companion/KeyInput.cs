using System.Runtime.InteropServices;

namespace FlexScreen.Companion;

/// <summary>
/// Types text into the focused window as Unicode characters, so what arrives doesn't depend
/// on the keyboard layout (Hebrew swaps "(" and ")", for one). Used by the deck's numpad.
/// </summary>
static class KeyInput
{
    public static void Type(string text)
    {
        var inputs = new List<Input>();
        foreach (var ch in text)
        {
            inputs.Add(Key(ch, 0));
            inputs.Add(Key(ch, KeyeventfKeyup));
        }
        if (inputs.Count > 0) SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Input>());
    }

    public const ushort Control = 0x11;
    public const ushort Alt = 0x12;

    /// <summary>A shortcut such as Ctrl+V: the modifier, then the key (a letter's key code is its capital).</summary>
    public static void Chord(ushort modifier, char key)
    {
        var vk = (ushort)char.ToUpperInvariant(key);
        Input[] inputs = [Vk(modifier, 0), Vk(vk, 0), Vk(vk, KeyeventfKeyup), Vk(modifier, KeyeventfKeyup)];
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    static Input Vk(ushort vk, uint flags) => new() { Type = InputKeyboard, Keyboard = new KeybdInput { Vk = vk, Flags = flags } };

    static Input Key(char ch, uint flags) =>
        new() { Type = InputKeyboard, Keyboard = new KeybdInput { Scan = ch, Flags = KeyeventfUnicode | flags } };

    const uint InputKeyboard = 1;
    const uint KeyeventfKeyup = 0x0002;
    const uint KeyeventfUnicode = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    struct KeybdInput
    {
        public ushort Vk, Scan;
        public uint Flags, Time;
        public IntPtr ExtraInfo;
    }

    // The native INPUT is a type tag and a union whose largest member (the mouse) is 32 bytes.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public KeybdInput Keyboard;
    }

    [DllImport("user32.dll")] static extern uint SendInput(uint count, Input[] inputs, int size);
}
