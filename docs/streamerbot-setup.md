# Streamer.bot Setup

## Globals
- tw2_broadcasterId wird beim Event Broadcaster Connected durch Bot Logik gesetzt
- tw2_chatEnabled steuert Chat Output der Clip URL

## Actions und Commands
- Command: !clipauth
  - Action ruft Auth() auf
- Command: !clip
  - Action ruft Clip() auf
  - Args: rawInput, userName oder user oder displayName

## Hinweise
- Tokens liegen als Globals in Streamer.bot
- Exportiere keine Konfiguration mit Tokens in ein öffentliches Repo
