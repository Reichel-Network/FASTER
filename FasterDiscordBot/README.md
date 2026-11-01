# FasterDiscordBot

Slash-command bot for the game box. Calls FASTER’s localhost A3S API (`127.0.0.1:8787`).

## Commands

| Slash | Effect |
| --- | --- |
| `/faster-status` | `GET /v1/status` |
| `/faster-events repo:` or `url:` | List modsets |
| `/faster-deploy event:` + `repo:` **or** `url:` (+ optional `profile:`) | Sync → apply → deploy → **auto-launch** |

## Environment

| Variable | Required | Notes |
| --- | --- | --- |
| `DISCORD_BOT_TOKEN` | yes | Discord application bot token |
| `FASTER_LOCAL_API_TOKEN` | yes | Same bearer token as FASTER local API |
| `FASTER_LOCAL_API_BASE` | no | Default `http://127.0.0.1:8787` |
| `DISCORD_GUILD_ID` | recommended | Guild for instant command registration |
| `DISCORD_ALLOW_USER_IDS` | recommended | Comma-separated Discord user IDs |
| `DISCORD_ALLOW_ROLE_IDS` | optional | Comma-separated role IDs |
| `FASTER_DEFAULT_REPO` | optional | Default Mode A repo |
| `FASTER_DEFAULT_PROFILE` | optional | Default profile name |

## Run (game box)

1. FASTER running with local API enabled (`FASTER_LOCAL_API_ENABLED=1` + token).
2. Create a Discord Application → Bot → copy token; invite with `applications.commands` scope.
3. Publish and start:

```powershell
dotnet publish .\FasterDiscordBot\FasterDiscordBot.csproj -c Release -r win-x64 --self-contained true -o C:\Tools\FasterDiscordBot
$env:DISCORD_BOT_TOKEN = "…"
$env:FASTER_LOCAL_API_TOKEN = "…"
$env:DISCORD_GUILD_ID = "…"
C:\Tools\FasterDiscordBot\FasterDiscordBot.exe
```

Do not commit tokens.
