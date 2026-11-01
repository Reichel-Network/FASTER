# FasterDiscordBot — **deprecated**

The standalone `FasterDiscordBot.exe` is **deprecated**. Slash commands now run **inside FASTER** (`FasterDiscordBotHost`).

## Use FASTER instead

1. Open **Settings → Discord bot (in-process)**.
2. Enable **Enable Discord bot**, paste the bot token, set Guild ID (recommended), optional allowlists / default repo.
3. Optionally keep **Localhost API** enabled if other tools need HTTP; Discord no longer requires a separate process.
4. **Save** and **restart FASTER**.

Slash commands: `/faster-status`, `/faster-events`, `/faster-deploy`, `/faster-start`, `/faster-stop` (optional `profile:`).

Env overrides (same names as before): `DISCORD_BOT_ENABLED`, `DISCORD_BOT_TOKEN`, `DISCORD_GUILD_ID`, `DISCORD_ALLOW_USER_IDS`, `DISCORD_ALLOW_ROLE_IDS`, `FASTER_DEFAULT_REPO`.

On the game box: stop `FasterDiscordBot.exe` / scheduled tasks and run only `C:\Tools\Faster\FASTER.exe`.

This project folder is kept for reference only and is no longer built by `FASTER.sln`.
