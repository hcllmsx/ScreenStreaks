// ScreenStreaks —— 屏幕纵向线条缺陷模拟工具
// Copyright (C) 2026 hcllmsx
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace ScreenStreaks.Models;

/// <summary>
/// 全局热键定义。存储的是 Win32 语义的修饰符位与虚拟键码，
/// 这样 JSON 里可读性差一点，但注册时零转换、不会出错。
/// </summary>
public sealed class HotkeyDefinition
{
    public const uint ModifierAlt = 0x0001;
    public const uint ModifierControl = 0x0002;
    public const uint ModifierShift = 0x0004;
    public const uint ModifierWin = 0x0008;

    /// <summary>修饰符位组合，取值见 ModifierXxx 常量</summary>
    public uint Modifiers { get; set; }

    /// <summary>
    /// Win32 虚拟键码。0 表示「未设置」。
    /// 默认不占用任何热键 —— 全局热键很容易和别的软件冲突，
    /// 与其硬塞一个组合键，不如让用户自己决定要不要用。
    /// </summary>
    public uint VirtualKey { get; set; }

    /// <summary>一个未设置的、即「不启用热键」的定义</summary>
    public static HotkeyDefinition CreateEmpty() => new();

    /// <summary>至少要有一个主键才算合法</summary>
    [JsonIgnore]
    public bool IsValid => VirtualKey != 0;

    public HotkeyDefinition Clone() => new()
    {
        Modifiers = Modifiers,
        VirtualKey = VirtualKey,
    };

    public override string ToString()
    {
        if (!IsValid)
        {
            return "未设置";
        }

        var parts = new List<string>(4);
        if ((Modifiers & ModifierControl) != 0) parts.Add("Ctrl");
        if ((Modifiers & ModifierAlt) != 0) parts.Add("Alt");
        if ((Modifiers & ModifierShift) != 0) parts.Add("Shift");
        if ((Modifiers & ModifierWin) != 0) parts.Add("Win");
        parts.Add(DescribeKey(VirtualKey));
        return string.Join(" + ", parts);
    }

    private static string DescribeKey(uint virtualKey)
    {
        Key key;
        try
        {
            key = KeyInterop.KeyFromVirtualKey((int)virtualKey);
        }
        catch (ArgumentException)
        {
            return $"0x{virtualKey:X2}";
        }

        if (key == Key.None)
        {
            return $"0x{virtualKey:X2}";
        }

        string name = key.ToString();
        if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1]))
        {
            return name[1].ToString();
        }

        if (name.StartsWith("NumPad", StringComparison.Ordinal) && name.Length == 7)
        {
            return "小键盘 " + name[6];
        }

        return name switch
        {
            "OemPlus" => "+",
            "OemMinus" => "-",
            "OemComma" => ",",
            "OemPeriod" => ".",
            "OemQuestion" => "/",
            "OemSemicolon" => ";",
            "OemQuotes" => "'",
            "OemOpenBrackets" => "[",
            "OemCloseBrackets" => "]",
            "OemPipe" => "\\",
            "OemTilde" => "`",
            "Return" => "Enter",
            "Capital" => "CapsLock",
            _ => name,
        };
    }
}
