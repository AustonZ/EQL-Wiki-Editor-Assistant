using EQLWikiEditorAssistant.Capture;
using EQLWikiEditorAssistant.Core.Input;
using EQLWikiEditorAssistant.Core.Ocr;
using EQLWikiEditorAssistant.TestSupport;

// Milestone 1 spike tool: validate WindowCapturer (Windows Graphics Capture) and GlobalHotKey against real
// windows on this machine, since the game itself isn't necessarily running.
//
// Usage:
//   CaptureSpike list [titleFilter]                     list visible windows (optionally filtered)
//   CaptureSpike capture <titleSubstring> <outPngPath>   capture that window once, save as PNG
//   CaptureSpike hotkey <modifiers> <vkHex>               register a hotkey and print when it fires (Ctrl+C to exit)
//     modifiers: combination of A(lt) C(ontrol) S(hift) W(in), e.g. "CA" for Ctrl+Alt
//     vkHex: virtual-key code in hex, e.g. 73 for VK_F4 (0x73)

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: CaptureSpike list [titleFilter] | capture <titleSubstring> <outPngPath> | hotkey <modifiers> <vkHex>");
    return 1;
}

switch (args[0])
{
    case "list":
    {
        string? filter = args.Length > 1 ? args[1] : null;
        var windows = filter is null
            ? WindowFinder.EnumerateVisibleWindows()
            : WindowFinder.FindByTitleSubstring(filter);
        foreach (var w in windows)
            // The process name is here because a title alone cannot identify the game — see WindowFinder.
            Console.WriteLine($"{w.Handle,12:X}  {w.ProcessName,-16} {w.Title}");
        Console.WriteLine($"{windows.Count} window(s)");
        return 0;
    }

    case "capture":
    {
        if (args.Length < 3) { Console.Error.WriteLine("usage: capture <titleSubstring> <outPngPath>"); return 1; }
        var matches = WindowFinder.FindByTitleSubstring(args[1]);
        if (matches.Count == 0) { Console.Error.WriteLine($"No visible window matching '{args[1]}'."); return 1; }
        var target = matches[0];
        Console.WriteLine($"Capturing '{target.Title}' (handle {target.Handle:X})...");

        using var capturer = new WindowCapturer();
        CapturedImage? image = await capturer.CaptureAsync(target.Handle);
        if (image is null) { Console.Error.WriteLine("Capture failed (window may have closed)."); return 1; }

        Console.WriteLine($"Captured {image.Width}x{image.Height}");
        await ImageFile.SavePngAsync(image, args[2]);
        Console.WriteLine($"Saved to {args[2]}");
        return 0;
    }

    case "hotkey":
    {
        if (args.Length < 3) { Console.Error.WriteLine("usage: hotkey <modifiers> <vkHex>"); return 1; }
        HotKeyModifiers mods = HotKeyModifiers.None;
        foreach (char c in args[1].ToUpperInvariant())
        {
            mods |= c switch
            {
                'A' => HotKeyModifiers.Alt,
                'C' => HotKeyModifiers.Control,
                'S' => HotKeyModifiers.Shift,
                'W' => HotKeyModifiers.Windows,
                _ => throw new ArgumentException($"Unknown modifier '{c}'"),
            };
        }
        uint vk = Convert.ToUInt32(args[2], 16);

        using var hotkey = new GlobalHotKey(mods, vk);
        int count = 0;
        hotkey.Pressed += (_, _) => Console.WriteLine($"Hotkey fired! ({++count})");
        Console.WriteLine($"Registered {mods} + 0x{vk:X2}. Waiting for presses (this process exits after 30s)...");
        await Task.Delay(TimeSpan.FromSeconds(30));
        Console.WriteLine($"Done. Total presses: {count}");
        return 0;
    }

    default:
        Console.Error.WriteLine($"Unknown command '{args[0]}'.");
        return 1;
}
