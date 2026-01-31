# TW2ClipTool

C# Inline Code für Streamer.bot zur Twitch Device Code Auth und Clip Erstellung über Helix /clips.

## Voraussetzungen
- Streamer.bot mit Inline C# Support
- Internetzugriff zu id.twitch.tv und api.twitch.tv
- Twitch Scope: clips:edit

## Installation
1. Inhalt aus src/TW2ClipTool.CPHInline.cs in eine Streamer.bot Inline C# Action kopieren
2. Globals konfigurieren
3. Commands !clipauth und !clip auf die Methoden Auth() und Clip() routen

## Wichtige Globals
- tw2_broadcasterId
- tw2_accessToken
- tw2_refreshToken
- tw2_tokenExpiresAtUtc
- tw2_chatEnabled
- tw2_chatAsBot
- tw2_clipDefaultTitle
- tw2_clipTitleTemplate
- tw2_clipDuration

Details siehe docs/technical-documentation.md und docs/streamerbot-setup.md.

## Verhalten in Kurzform
- !clipauth startet Device Code Flow und schreibt aktuell die Auth Anleitung in den Chat
- !clip erstellt Clip über Helix /clips und postet die URL optional in den Chat
- rawInput kann optional mit Dauer starten und danach Titel enthalten
- Titel wird über Template aufgebaut und auf 140 Zeichen gekürzt

## Einschränkungen
- Code erzwingt keinen Live Kontext. Clip wird immer versucht.
- Dauer Clamp im Parser ist 5 bis 90 Sekunden. Global tw2_clipDuration wird nicht geclamped.
