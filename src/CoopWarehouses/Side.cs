using System;
using System.IO;
using System.Linq;
using TaleWorlds.MountAndBlade;

namespace CoopWarehouses;

/// <summary>Which game this is: single player, a co-op server, or a co-op player's game.</summary>
public static class Side
{
    /// <summary>
    /// The dedicated server. Coop's own IsServer flag is still false while its handlers are built, so this checks the
    /// engine's startup info and the server binary folder instead.
    /// </summary>
    public static bool IsServer
    {
        get
        {
            try { if (Module.CurrentModule?.StartupInfo?.DedicatedServerType != DedicatedServerType.None) return true; } catch { }
            return Directory.GetCurrentDirectory().EndsWith("Win64_Shipping_Server", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static bool CoopLoaded => AppDomain.CurrentDomain.GetAssemblies().Any(a => !a.IsDynamic && a.GetName().Name == "Coop.Core");

    /// <summary>A co-op player's game: never changes a warehouse itself, asks the server.</summary>
    public static bool IsCoopClient => !IsServer && CoopLoaded;

    /// <summary>This game owns the warehouses: single player or the co-op server.</summary>
    public static bool IsAuthority => !IsCoopClient;
}

/// <summary>The Coop adapter fills these in; the game-side code never references Coop.</summary>
public static class Net
{
    /// <summary>Player's game -> server. Null until the client handler is armed (not connected).</summary>
    public static Action<WireRequest>? Send;
}

/// <summary>Console (the server launcher captures it) plus Documents\Mount and Blade II Bannerlord\Configs\ModLogs.</summary>
public static class Log
{
    private const string Prefix = "[CoopWarehouses] ";
    private static readonly object Gate = new object();
    private static string? _file;
    private static bool _fileTried;

    public static void Info(string msg) => Write(Prefix + msg);
    public static void Warn(string msg) => Write(Prefix + "WARNING " + msg);

    private static void Write(string line)
    {
        Console.WriteLine(line);
        try
        {
            lock (Gate)
            {
                if (!_fileTried)
                {
                    _fileTried = true;
                    var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    var dir = Path.Combine(docs, "Mount and Blade II Bannerlord", "Configs", "ModLogs");
                    Directory.CreateDirectory(dir);
                    _file = Path.Combine(dir, "CoopWarehouses-" + (Side.IsServer ? "server" : "client") + ".log");
                    File.WriteAllText(_file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} log started{Environment.NewLine}");
                }
                if (_file != null) File.AppendAllText(_file, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
