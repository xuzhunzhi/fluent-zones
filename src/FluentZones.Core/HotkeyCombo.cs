using System.Text;

namespace FluentZones.Core;

/// <summary>一个热键组合（修饰键 + 虚拟键码），可以与字符串互相转换。</summary>
public readonly record struct HotkeyCombo(uint Modifiers, uint VirtualKey)
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    public const uint VkLeft = 0x25;
    public const uint VkUp = 0x26;
    public const uint VkRight = 0x27;
    public const uint VkDown = 0x28;

    /// <summary>必须带 Ctrl / Alt / Win 之一（单 Shift 不算，避免误触）。</summary>
    public bool HasPrimaryModifier =>
        (Modifiers & (ModControl | ModAlt | ModWin)) != 0;

    public bool IsValid => VirtualKey != 0 && (HasPrimaryModifier || IsFunctionKey(VirtualKey));

    public string ToDisplayString()
    {
        var builder = new StringBuilder();
        if ((Modifiers & ModControl) != 0) builder.Append("Ctrl+");
        if ((Modifiers & ModAlt) != 0) builder.Append("Alt+");
        if ((Modifiers & ModShift) != 0) builder.Append("Shift+");
        if ((Modifiers & ModWin) != 0) builder.Append("Win+");
        builder.Append(HotkeyNames.KeyName(VirtualKey));
        return builder.ToString();
    }

    public override string ToString() => ToDisplayString();

    public static HotkeyCombo FromParts(bool ctrl, bool alt, bool shift, bool win, uint virtualKey)
    {
        var mods = 0u;
        if (ctrl) mods |= ModControl;
        if (alt) mods |= ModAlt;
        if (shift) mods |= ModShift;
        if (win) mods |= ModWin;
        return new HotkeyCombo(mods, virtualKey);
    }

    public static bool IsFunctionKey(uint virtualKey) => virtualKey is >= 0x70 and <= 0x87;

    public static bool IsModifierKey(uint virtualKey) => virtualKey is
        0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;
}

public static class HotkeyNames
{
    private static readonly Dictionary<string, uint> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20,
        ["Tab"] = 0x09,
        ["Enter"] = 0x0D,
        ["Backspace"] = 0x08,
        ["Insert"] = 0x2D,
        ["Delete"] = 0x2E,
        ["Home"] = 0x24,
        ["End"] = 0x23,
        ["PageUp"] = 0x21,
        ["PageDown"] = 0x22,
        ["Left"] = 0x25,
        ["Up"] = 0x26,
        ["Right"] = 0x27,
        ["Down"] = 0x28,
        ["Plus"] = 0xBB,
        ["Minus"] = 0xBD,
        ["Comma"] = 0xBC,
        ["Period"] = 0xBE,
        ["Slash"] = 0xBF,
        ["Semicolon"] = 0xBA,
        ["Quote"] = 0xDE,
        ["BracketLeft"] = 0xDB,
        ["BracketRight"] = 0xDD,
        ["Backslash"] = 0xDC,
        ["Backquote"] = 0xC0,
    };

    private static readonly Dictionary<uint, string> DisplayNames =
        NamedKeys.ToDictionary(pair => pair.Value, pair => pair.Key);

    public static string KeyName(uint virtualKey)
    {
        if (virtualKey is >= 0x30 and <= 0x39)
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= 0x41 and <= 0x5A)
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= 0x70 and <= 0x87)
        {
            return "F" + (virtualKey - 0x70 + 1);
        }

        if (DisplayNames.TryGetValue(virtualKey, out var name))
        {
            return name;
        }

        return "VK" + virtualKey.ToString("X2");
    }

    public static bool TryGetVirtualKey(string name, out uint virtualKey)
    {
        name = name.Trim();
        virtualKey = 0;

        if (name.Length == 1)
        {
            var ch = char.ToUpperInvariant(name[0]);
            if (ch is >= '0' and <= '9' or >= 'A' and <= 'Z')
            {
                virtualKey = ch;
                return true;
            }
        }

        if (name.Length is 2 or 3 &&
            (name[0] is 'f' or 'F') &&
            int.TryParse(name[1..], out var fn) &&
            fn is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + fn - 1);
            return true;
        }

        return NamedKeys.TryGetValue(name, out virtualKey);
    }

    /// <summary>解析 "Ctrl+Alt+1" 这类文本。省略修饰词也行：alt+shift+space。</summary>
    public static bool TryParse(string? text, out HotkeyCombo combo, out string? error)
    {
        combo = default;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "热键不能为空";
            return false;
        }

        var modifiers = 0u;
        uint virtualKey = 0;
        var keyCount = 0;

        foreach (var rawPart in text.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = rawPart.Trim();
            if (part.Length == 0)
            {
                continue;
            }

            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                case "ctl":
                    modifiers |= HotkeyCombo.ModControl;
                    continue;
                case "alt":
                    modifiers |= HotkeyCombo.ModAlt;
                    continue;
                case "shift":
                    modifiers |= HotkeyCombo.ModShift;
                    continue;
                case "win":
                case "windows":
                case "cmd":
                case "meta":
                    modifiers |= HotkeyCombo.ModWin;
                    continue;
            }

            if (!TryGetVirtualKey(part, out virtualKey))
            {
                error = $"无法识别的按键：{part}";
                return false;
            }

            keyCount++;
        }

        if (keyCount != 1)
        {
            error = "热键需要且只能有一个主键，例如 Ctrl+Alt+1";
            return false;
        }

        combo = new HotkeyCombo(modifiers, virtualKey);
        if (!combo.IsValid)
        {
            error = "热键至少要包含 Ctrl、Alt 或 Win 之一";
            return false;
        }

        return true;
    }
}

/// <summary>热键文本解析入口。</summary>
public static class HotkeyParser
{
    public static bool TryParse(string? text, out HotkeyCombo combo, out string? error) =>
        HotkeyNames.TryParse(text, out combo, out error);
}
