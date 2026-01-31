# TW2ClipTool Technische Dokumentation

## 1. Zweck und Überblick
- **Was das Tool macht**  
  TW2ClipTool ist C# Inline Code für Streamer.bot (CPHInline) mit zwei Funktionen.  
  Erstens Authentifizierung über Twitch Device Code Flow und Speicherung der Tokens in Streamer.bot Globals.  
  Zweitens Clip Erstellung über Twitch Helix `/helix/clips` und Ausgabe der Clip URL, optional im Chat.

- **Abhängigkeiten (Streamer.bot Inline C#, Internetzugriff zu Twitch, benötigte Twitch Scopes)**  
  - Streamer.bot mit Inline C# Ausführung und Zugriff auf `CPH`
  - Internetzugriff zu `id.twitch.tv` und `api.twitch.tv`
  - Twitch Scope `clips:edit` (im Code fest hinterlegt)

## 2. Architektur und Datenfluss
- **Überblick der Hauptmethoden: StartupCheck, Auth, Clip**
  - `StartupCheck()` validiert, ob `tw2_broadcasterId` und `tw2_accessToken` vorhanden sind. Danach wird `EnsureValidTokenSilently()` aufgerufen, das einen Refresh versuchen kann. Das Ergebnis dieses Refresh Versuchs beeinflusst den Rückgabewert von `StartupCheck()` aktuell nicht.
  - `Auth()` startet den Device Code Flow, schreibt eine Anleitung in Log und Chat und pollt bis zu 5 Minuten den Token Endpoint. Bei Erfolg speichert es `tw2_accessToken`, optional `tw2_refreshToken` und `tw2_tokenExpiresAtUtc`.
  - `Clip()` erstellt einen Clip über Helix. Es ermittelt User und rawInput aus Streamer.bot Args, verarbeitet optional eine führende Dauer plus Titel, baut einen finalen Titel via Template und sendet einen POST Request an Helix.

- **Sequence: Auth Flow (Device Code) und Clip Flow**

  Auth Flow
  1. POST `https://id.twitch.tv/oauth2/device` mit Form Feldern `client_id` und `scopes`
  2. Aus der Antwort werden `device_code`, `user_code`, `verification_uri`, `interval` extrahiert
  3. Anleitung wird geloggt und per `CPH.SendMessage(...)` in den Chat geschrieben
  4. Polling Loop bis Timeout 5 Minuten
  5. Pro Loop Sleep `interval` Sekunden
  6. POST `https://id.twitch.tv/oauth2/token` mit Form Feldern `client_id`, `device_code`, `grant_type`
  7. Fehlerbehandlung über JSON Feld `error`
     - `authorization_pending` führt zu weiterem Polling
     - `slow_down` erhöht das Intervall um 2 Sekunden und pollt weiter
     - jeder andere Wert bricht ab und wird geloggt
  8. Bei Erfolg Speicherung der Tokens und Ablaufzeit

  Clip Flow
  1. Lesen von `tw2_broadcasterId`
  2. `EnsureValidTokenSilently()` prüft Token Ablauf und refresht bei Bedarf
  3. Lesen von User Feldern aus Args mit Fallbacks
  4. Lesen von `rawInput`
  5. Optionales Parsing von Dauer und Titel
  6. Fallback Titel, Template Ersetzung, Kürzung auf 140 Zeichen
  7. Dauer Auswahl aus Global `tw2_clipDuration` oder Override aus Parsing
  8. POST Request an `https://api.twitch.tv/helix/clips` mit Query Parametern und Headern
  9. Parsing von `data[0].id` und Aufbau einer Public URL `https://clips.twitch.tv/<id>`
  10. Optionaler Chat Output der URL, abhängig von `tw2_chatEnabled`

- **Wo Daten gespeichert werden (Globals) und warum**
  - Tokens und Ablaufzeit werden als Globals persistiert, damit Clip Requests ohne erneute Authentifizierung möglich sind.
  - `tw2_broadcasterId` wird als Global benötigt, weil Helix `/clips` diese ID als Pflichtparameter nutzt.

## 3. Konfiguration

### 3.1 Erforderliche Globals
Liste aller Keys, die das Tool nutzt, inklusive Bedeutung, Typ, Beispielwerte und Default Verhalten.

- `tw2_broadcasterId`  
  Bedeutung: Broadcaster ID für Helix Query Parameter `broadcaster_id`  
  Typ: string  
  Beispiel: `80239592`  
  Default Verhalten wenn fehlt: `StartupCheck()` und `Clip()` loggen einen Fehler und geben `false` zurück

- `tw2_accessToken`  
  Bedeutung: OAuth Access Token für Header `Authorization: Bearer <token>`  
  Typ: string  
  Beispiel: `<access token>`  
  Default Verhalten wenn fehlt: `StartupCheck()` und `Clip()` loggen einen Fehler und geben `false` zurück

- `tw2_refreshToken`  
  Bedeutung: OAuth Refresh Token für Refresh Request  
  Typ: string  
  Beispiel: `<refresh token>`  
  Default Verhalten wenn fehlt: Refresh kann nicht ausgeführt werden, abgelaufener Token führt in `Clip()` zu `false`

- `tw2_tokenExpiresAtUtc`  
  Bedeutung: Ablaufzeitpunkt als UTC Ticks String, berechnet als `UtcNow + expires_in - 30 Sekunden`  
  Typ: string, Inhalt ist eine long Zahl als Text  
  Beispiel: `638738123456789012`  
  Default Verhalten wenn fehlt: Token wird als gültig angenommen, es findet kein Refresh statt

- `tw2_chatEnabled`  
  Bedeutung: steuert, ob die Clip URL per `CPH.SendMessage` gepostet wird  
  Typ: string, muss als bool parsebar sein  
  Beispiel: `True` oder `False`  
  Default Verhalten wenn fehlt oder nicht parsebar: `true`

- `tw2_chatAsBot`  
  Bedeutung: Global Key ist vorhanden, wird in dieser Codeversion nicht verwendet  
  Typ: nicht anwendbar in dieser Implementierung  
  Beispiel: nicht anwendbar  
  Default Verhalten wenn fehlt: keine Wirkung

- `tw2_clipDefaultTitle`  
  Bedeutung: Fallback Titel, wenn aus rawInput kein Titel entsteht  
  Typ: string  
  Beispiel: `Clip` oder `Highlight`  
  Default Verhalten wenn fehlt oder leer: Fallback ist `Clip`

- `tw2_clipTitleTemplate`  
  Bedeutung: Template für finalen Titel  
  Typ: string  
  Beispiel: `%title% - Clipped by %user%`  
  Default Verhalten wenn fehlt oder leer: `%title% - Clipped by %user%`

- `tw2_clipDuration`  
  Bedeutung: Standard Dauer in Sekunden, genutzt wenn kein Override geparst wird  
  Typ: string, wird als int geparst  
  Beispiel: `60`  
  Default Verhalten wenn fehlt, nicht parsebar oder kleiner gleich 0: `60`

Default Verhalten wenn Keys fehlen, zusammengefasst
- Fehlende Broadcaster ID oder fehlender Access Token führt zu sofortigem Abbruch in `StartupCheck()` und `Clip()`
- Fehlende Ablaufzeit führt dazu, dass Refresh nie ausgelöst wird
- Fehlender Refresh Token führt dazu, dass ein abgelaufener Token nicht erneuert werden kann
- Fehlende Titel Globals führen zu Default Titel `Clip` und Default Template `%title% - Clipped by %user%`
- Fehlendes `tw2_chatEnabled` führt zu Chat Output als Standard

### 3.2 Notwendige Trigger in Streamer.bot
Typische Trigger und Actions, die für den Betrieb benötigt werden.

- Broadcaster Connected Trigger setzt `tw2_broadcasterId`  
  In deiner Umgebung wird die Broadcaster ID beim Broadcaster Connected Event durch Bot Logik ermittelt und in die Global Variable geschrieben. Dieser Teil ist nicht in diesem C# Code implementiert, aber Voraussetzung.

- Chat Command `!clipauth` ruft `Auth()` auf  
  `Auth()` schreibt die Device Flow Anleitung aktuell in den Chat und in die Logs.

- Chat Command `!clip` ruft `Clip()` auf  
  `Clip()` benötigt Args `rawInput` sowie User Felder.

## 4. Authentifizierung

### 4.1 Device Code Flow Ablauf
- Endpoints
  - Device Code: `https://id.twitch.tv/oauth2/device`
  - Token Polling: `https://id.twitch.tv/oauth2/token`

- Polling Verhalten
  - Timeout nach 5 Minuten
  - Intervall aus Feld `interval`, Default 5 Sekunden
  - `authorization_pending` führt zu weiterem Polling
  - `slow_down` erhöht Intervall um 2 Sekunden
  - anderer Fehler bricht ab und wird geloggt

- Speicherung von accessToken, refreshToken, expiresAtTicks
  Bei Erfolg werden gespeichert
  - `tw2_accessToken`
  - `tw2_refreshToken` nur wenn vorhanden
  - `tw2_tokenExpiresAtUtc` als UTC Ticks String mit 30 Sekunden Safety Buffer

- Sicherheitsaspekte: was wird ins Chat geschrieben, was nicht
  - In den Chat wird aktuell die Anleitung mit `verification_uri` und `user_code` geschrieben
  - Tokens werden nicht in den Chat geschrieben
  - Tokens werden in Globals gespeichert  
  Offene Umsetzung: PN statt Chat ist in dieser Codeversion nicht enthalten

### 4.2 Token Refresh Verhalten
- Wann `EnsureValidTokenSilently()` refresh auslöst
  - Nur wenn `tw2_accessToken` vorhanden ist
  - Wenn `tw2_tokenExpiresAtUtc` fehlt oder nicht parsebar ist, wird kein Refresh ausgelöst
  - Wenn der Ablaufzeitpunkt erreicht ist, wird `TryRefreshToken()` aufgerufen

- Was passiert, wenn refreshToken fehlt oder refresh fehlschlägt
  - Fehlt `tw2_refreshToken`, liefert `TryRefreshToken()` `false`
  - Bei Fehlern im Refresh Request liefert `TryRefreshToken()` `false`, Exceptions werden geschluckt
  - `Clip()` bricht dann ab und loggt, dass ein gültiges Token fehlt

## 5. Clip Command Verhalten

### 5.1 Eingaben und Argumente
- Arg Keys aus Streamer.bot
  - `userName`
  - `user`
  - `displayName`
  - `rawInput`

- Fallbacks für user  
  Reihenfolge
  1. `userName`
  2. `user`
  3. `displayName`
  4. Fallback String `someone`

### 5.2 Parsing: Dauer und Titel
Regeln von `TryParseDurationAndTitle`.

- optional `!clip` Prefix entfernen  
  Wenn der getrimmte rawInput mit `!clip` beginnt, wird dieses Präfix entfernt und links getrimmt.

- führende Zahl als Dauer  
  Es werden nur Ziffern am Anfang akzeptiert. Keine führende Zahl führt zu reject.

- Separator Regeln  
  Nach der Zahl ist erlaubt
  - Whitespace
  - optional `,` oder `;` nach optionalem Whitespace
  - Ende des Strings  
  Wenn direkt nach der Zahl ein anderes Zeichen folgt und kein Whitespace und kein Ende vorhanden ist, wird rejected. Beispiel `30s`.

- Reject Cases
  - `30s titel` reject
  - `titel 30` reject
  - `x30 titel` reject

Wichtiges Verhalten im Caller  
Wenn Parsing rejected, aber rawInput nicht leer ist, wird rawInput dennoch als Titel genutzt, optional ohne führendes `!clip`. In diesem Pfad gibt es keinen Duration Override.

- Clamping 5 bis 90 und Logging  
  Overrides werden auf 5 bis 90 geklemmt. Bei Clamp wird `CPH.LogInfo` geschrieben.  
  Hinweis: Der Code clampet `tw2_clipDuration` als Global Wert nicht.

- Titel kann leer sein, dann greift Fallback Titel  
  Leerer Titel führt später zu `tw2_clipDefaultTitle` oder `Clip`.

Beispiele

| Input rawInput | Dauer | Titel | Ergebnis (accept/reject) | Bemerkung |
| --- | --- | --- | --- | --- |
| `30 highlight` | 30 | `highlight` | accept | Whitespace Separator |
| `30, highlight` | 30 | `highlight` | accept | Komma Separator |
| `30 ;  highlight` | 30 | `highlight` | accept | Semikolon nach Whitespace |
| `30` | 30 |  | accept | Titel leer, Fallback greift |
| `2 test` | 5 | `test` | accept | Override Clamp auf 5, LogInfo |
| `120 epic` | 90 | `epic` | accept | Override Clamp auf 90, LogInfo |
| `30s epic` | n/a | n/a | reject | Parser reject, Caller nutzt rawInput als Titel |
| `epic 30` | n/a | n/a | reject | Parser reject, Caller nutzt rawInput als Titel |
| `!clip 45, nice` | 45 | `nice` | accept | !clip Prefix wird entfernt |
| `hello world` | n/a | `hello world` | accept | Parser reject, Caller nutzt rawInput als Titel |
| `!clip` | n/a | n/a | reject | rawInput wird leer, kein Titel |
| `15;` | 15 |  | accept | Titel leer, Fallback greift |

### 5.3 Titel Template und Limit
- Template Key und Default
  - Global: `tw2_clipTitleTemplate`
  - Default: `%title% - Clipped by %user%`

- Platzhalter Ersetzung
  - `%title%` wird durch den Basis Titel ersetzt
  - `%user%` wird durch den ermittelten User ersetzt

- Basis Titel
  - Wenn Parsing accept liefert, wird `titleOverride` genutzt, auch wenn leer
  - Wenn Parsing reject liefert und rawInput nicht leer ist, wird rawInput als Titel genutzt, optional ohne `!clip` Prefix
  - Wenn Basis Titel leer ist, wird `tw2_clipDefaultTitle` genutzt
  - Wenn auch das leer ist, wird `Clip` genutzt

- Limit
  - Finaler Titel wird hart auf 140 Zeichen gekürzt.

### 5.4 Helix Request
- URL Aufbau  
  POST Request an  
  `https://api.twitch.tv/helix/clips?broadcaster_id=<id>&duration=<sec>&title=<title>`  
  `broadcaster_id` und `title` werden URL encoded.

- Headers
  - `Client-Id` wird gesetzt
  - `Authorization` wird gesetzt als `Bearer <access token>`

- Timeout Verhalten
  - 15 Sekunden

- Parsing der Antwort
  - Es wird `data[0].id` extrahiert. Der Parser unterstützt nur Index 0.
  - Fehlt die ID, wird ein Fehler geloggt und zusätzlich der komplette Body angehängt.

### 5.5 Chat Output
- Wann URL in Chat gepostet wird
  - Wenn `tw2_chatEnabled` als true ausgewertet wird. Default ist true.

- Was ins Log geht
  - Erfolg: `TW2ClipTool :: Clip erstellt: <url>`
  - Parsing Fail: `TW2ClipTool :: Clip: Keine Clip-ID erhalten. Body: <body>`
  - Exceptions: `TW2ClipTool :: Clip Exception: <message>`

## 6. Logging und Fehlerbilder
- Typische Fehler und Ursache
  - Broadcaster ID fehlt  
    Ursache: `tw2_broadcasterId` nicht gesetzt  
    Effekt: `StartupCheck()` und `Clip()` geben `false` zurück
  - Access Token fehlt  
    Ursache: `tw2_accessToken` nicht gesetzt  
    Effekt: `StartupCheck()` und `Clip()` geben `false` zurück
  - Device Flow Antwort unvollständig  
    Ursache: Antwort enthält nicht alle erwarteten Felder oder JSON Helper findet sie nicht  
    Effekt: `Auth()` bricht ab
  - Token Polling Fehler  
    Ursache: Token Endpoint liefert `error` ungleich `authorization_pending` und `slow_down`  
    Effekt: `Auth()` bricht ab
  - Refresh schlägt fehl  
    Ursache: Refresh Token fehlt, Request scheitert oder Antwort ohne `access_token`  
    Effekt: `EnsureValidTokenSilently()` liefert `false`, `Clip()` bricht ab
  - Clip Request Fehler  
    Ursache: Netzwerk, HTTP Fehler, Twitch API Fehler, Timeout  
    Effekt: Exception wird geloggt, ohne Response Body

- Welche Log Messages auftauchen und wie man sie interpretiert  
  Alle Logs verwenden Prefix `TW2ClipTool ::` und sind nach Methode segmentiert, zum Beispiel `Auth`, `StartupCheck`, `Clip`.

- Umgang mit WebException und leere JSON Bodies  
  `HttpPostForm()` liest bei WebException nach Möglichkeit den Error Body und gibt ihn zurück. Wenn das nicht gelingt, wird `{}` zurückgegeben. Dadurch kann es Folgefehler geben, weil erwartete JSON Felder fehlen.

## 7. Betrieb, Wartung, Erweiterung
- Wo man Scope erweitert  
  Konstante `TWITCH_SCOPES`. Sie wird im Device Endpoint Form Feld `scopes` verwendet.

- Wo man Parser erweitert  
  Methode `TryParseDurationAndTitle`.

- Empfehlungen für Robustheit (ohne Code zu ändern, nur als Hinweise)
  - Setze `tw2_chatEnabled` für Tests auf `False`, damit keine URLs in den Chat laufen
  - Stelle sicher, dass `tw2_broadcasterId` zuverlässig beim Broadcaster Connected Event gesetzt wird, bevor `!clip` verwendet wird
  - Setze `tw2_clipDuration` im erlaubten Bereich, weil der Global Wert aktuell nicht geclamped wird
  - Plane ein, dass Refresh still scheitern kann und dann manuell `!clipauth` nötig ist, solange StartupCheck nicht auto reauth auslöst

## 8. Offene Punkte und Annahmen
- `tw2_chatAsBot` ist als Global vorgesehen, aber im Code nicht umgesetzt. Es ist offen, wie die Umschaltung im Streamer.bot Kontext technisch erfolgen soll.
- Device Flow Anleitung wird aktuell in den öffentlichen Chat gesendet. Die geplante PN Ausgabe ist eine Erweiterung, nicht Teil dieser Codeversion.
- Mindestdauer Vorgabe 15 Sekunden passt nicht zur aktuellen Override Clamp Logik 5 bis 90 und es gibt keine Clamp für `tw2_clipDuration`.
- Live Kontext Vorgabe ist nicht im Code verankert. Wenn Twitch außerhalb live ablehnt, landet das im Fehlerpfad, aber ohne explizite Erkennung.
- StartupCheck soll bei fehlgeschlagenem Refresh automatisch reauth triggern. Das ist aktuell nicht implementiert.

## Optionale Verbesserungen
- StartupCheck kann bei abgelaufenem Token und fehlgeschlagenem Refresh automatisch `Auth()` aufrufen oder einen klaren Status zurückgeben, statt nur still weiterzulaufen.
- Clamp Regeln für Dauer vereinheitlichen, mindestens 15 und maximal 90, sowohl für Overrides als auch für `tw2_clipDuration`.
- Fehlerlogging kürzen, statt vollständige Bodies zu loggen, zum Beispiel nur HTTP Status, `error` Felder und eine kurze Zusammenfassung.
- `tw2_chatAsBot` implementieren, indem der Chat Output Pfad abhängig vom Global zwischen Broadcaster und Bot Identität umschaltet, sofern Streamer.bot das in Inline Code unterstützt.
