using System;

namespace FasterDiscordBot;

/// <summary>
/// Deprecated standalone entrypoint. Discord slash commands live in FASTER
/// (<c>FASTER.Services.A3S.FasterDiscordBotHost</c>).
/// </summary>
internal static class Program
{
    public static int Main(string[] args)
    {
        Console.Error.WriteLine(
            "FasterDiscordBot.exe is deprecated. Enable Discord under FASTER Settings → " +
            "\"Discord bot (in-process)\" and run FASTER.exe only.");
        return 1;
    }
}
