using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

public class CPHInline
{
    // ====== HARD-CODED ======
    private const string TWITCH_CLIENT_ID = "INSERT CLIENT ID HERE";
    // Scopes für Clips
    private const string TWITCH_SCOPES = "clips:edit";

    // ====== GLOBAL KEYS ======
    private const string G_BROADCASTER_ID = "tw2_broadcasterId";
    private const string G_ACCESS_TOKEN   = "tw2_accessToken";
    private const string G_REFRESH_TOKEN  = "tw2_refreshToken";
    private const string G_EXPIRES_AT     = "tw2_tokenExpiresAtUtc"; // long ticks (UTC)

    private const string G_CHAT_ENABLED   = "tw2_chatEnabled";
    private const string G_CHAT_AS_BOT    = "tw2_chatAsBot";

    // ====== PUBLIC METHODS for Streamer.bot ======

    private bool TryParseDurationAndTitle(string rawInput, out int? durationOverride, out string titleOverride)
    {
        durationOverride = null;
        titleOverride = null;

        if (string.IsNullOrWhiteSpace(rawInput))
            return false;

        string s = rawInput.Trim();

        // optional "!clip" Prefix entfernen
        if (s.StartsWith("!clip", StringComparison.OrdinalIgnoreCase))
            s = s.Substring(5).TrimStart();

        if (string.IsNullOrWhiteSpace(s))
            return false;

        // führende Ziffern lesen
        int i = 0;
        while (i < s.Length && char.IsDigit(s[i]))
            i++;

        if (i == 0)
            return false;

        // wenn direkt nach den Ziffern kein erlaubtes Trennzeichen / Whitespace / Ende kommt -> nicht akzeptieren (z.B. "30s")
        int j = i;

        // optional whitespace nach Zahl
        while (j < s.Length && char.IsWhiteSpace(s[j]))
            j++;

        // optional ',' oder ';' (auch nach whitespace)
        if (j < s.Length && (s[j] == ',' || s[j] == ';'))
        {
            j++;
            while (j < s.Length && char.IsWhiteSpace(s[j]))
                j++;
        }
        else
        {
            // keine ','/';' gefunden
            if (j == s.Length)
            {
                // nur Zahl (oder Zahl + spaces) ist ok
            }
            else if (j == i)
            {
                // keine spaces, kein separator, nicht Ende => reject
                return false;
            }
            // else: whitespace als separator ist ok, Titel startet bei j
        }

        int dur;
        if (!int.TryParse(s.Substring(0, i), out dur))
            return false;

        int clamped = dur;
        if (clamped < 5) clamped = 5;
        if (clamped > 90) clamped = 90;

        if (clamped != dur)
            CPH.LogInfo($"TW2ClipTool :: Clip: Duration {dur} außerhalb 5-90, gecamped auf {clamped}.");

        durationOverride = clamped;

        string title = (j < s.Length) ? s.Substring(j).Trim() : "";
        titleOverride = title; // kann leer sein -> Titel-Fallback greift im Caller

        return true;
    }

   
    public bool StartupCheck()
    {
        var broadcasterId = GetGlobalString(G_BROADCASTER_ID);
        if (string.IsNullOrWhiteSpace(broadcasterId))
        {
            CPH.LogError("TW2ClipTool :: StartupCheck: broadcaster_id fehlt. (Setze global 'tw2_broadcasterId' via Trigger 'Broadcaster Chat Connected')");
            return false;
        }

        var token = GetGlobalString(G_ACCESS_TOKEN);
        if (string.IsNullOrWhiteSpace(token))
        {
            CPH.LogError("TW2ClipTool :: StartupCheck: Kein Token. Bitte !clipauth ausführen.");
            return false;
        }

        // Optional: refresh if expired
        EnsureValidTokenSilently();

        CPH.LogInfo("TW2ClipTool :: StartupCheck: OK (broadcaster_id vorhanden, Token vorhanden).");
        return true;
    }

    
    public bool Auth()
    {
        try
        {
            // 1) Request device code
            string deviceResp = HttpPostForm(
                "https://id.twitch.tv/oauth2/device",
                new Dictionary<string, string>
                {
                    { "client_id", TWITCH_CLIENT_ID },
                    { "scopes", TWITCH_SCOPES }
                },
                headers: null
            );

            string deviceCode = JsonGet(deviceResp, "device_code");
            string userCode   = JsonGet(deviceResp, "user_code");
            string verifyUri  = JsonGet(deviceResp, "verification_uri");
            string intervalS  = JsonGet(deviceResp, "interval");
            int interval = 5;
            int.TryParse(intervalS, out interval);
            if (interval <= 0) interval = 5;

            if (string.IsNullOrWhiteSpace(deviceCode) || string.IsNullOrWhiteSpace(userCode) || string.IsNullOrWhiteSpace(verifyUri))
            {
                CPH.LogError("TW2ClipTool :: Auth: Device-Flow Antwort unvollständig.");
                return false;
            }

            // Log instructions (NOT chat)
            CPH.LogInfo($"TW2ClipTool :: Twitch Auth: Öffne {verifyUri} und gib Code {userCode} ein.");
            CPH.SendMessage($"TW2ClipTool :: Twitch Auth: Öffne {verifyUri} und gib Code {userCode} ein.");

            // 2) Poll token endpoint
            DateTime start = DateTime.UtcNow;
            TimeSpan timeout = TimeSpan.FromMinutes(5);

            while (DateTime.UtcNow - start < timeout)
            {
                System.Threading.Thread.Sleep(interval * 1000);

                string tokenResp = HttpPostForm(
                    "https://id.twitch.tv/oauth2/token",
                    new Dictionary<string, string>
                    {
                        { "client_id", TWITCH_CLIENT_ID },
                        { "device_code", deviceCode },
                        { "grant_type", "urn:ietf:params:oauth:grant-type:device_code" }
                    },
                    headers: null
                );

                // If authorization_pending -> keep polling
                string err = JsonGet(tokenResp, "error");
                if (!string.IsNullOrWhiteSpace(err))
                {
                    if (err == "authorization_pending")
                        continue;

                    if (err == "slow_down")
                    {
                        interval += 2;
                        continue;
                    }

                    CPH.LogError($"TW2ClipTool :: Auth: Fehler beim Token holen: {err}");
                    return false;
                }

                string accessToken  = JsonGet(tokenResp, "access_token");
                string refreshToken = JsonGet(tokenResp, "refresh_token");
                string expiresInS   = JsonGet(tokenResp, "expires_in");

                int expiresIn = 0;
                int.TryParse(expiresInS, out expiresIn);
                if (expiresIn <= 0) expiresIn = 3600;

                if (string.IsNullOrWhiteSpace(accessToken))
                {
                    CPH.LogError("TW2ClipTool :: Auth: access_token fehlt in Antwort.");
                    return false;
                }

                // Persist (NO CHAT OUTPUT)
                SetGlobal(G_ACCESS_TOKEN, accessToken);
                if (!string.IsNullOrWhiteSpace(refreshToken))
                    SetGlobal(G_REFRESH_TOKEN, refreshToken);

                long expiresAtTicks = DateTime.UtcNow.AddSeconds(expiresIn - 30).Ticks; // 30s safety
                SetGlobal(G_EXPIRES_AT, expiresAtTicks.ToString());

                CPH.LogInfo("TW2ClipTool :: Auth erfolgreich. Token gespeichert.");
                return true;
            }

            CPH.LogError("TW2ClipTool :: Auth: Timeout. User hat wohl nicht bestätigt.");
            return false;
        }
        catch (Exception ex)
        {
            CPH.LogError("TW2ClipTool :: Auth Exception: " + ex.Message);
            return false;
        }
    }

    // Clip Command
    public bool Clip()
    {
        try
        {
            var broadcasterId = GetGlobalString(G_BROADCASTER_ID);
            if (string.IsNullOrWhiteSpace(broadcasterId))
            {
                CPH.LogError("TW2ClipTool :: Clip: broadcaster_id fehlt.");
                return false;
            }

            if (!EnsureValidTokenSilently())
            {
                CPH.LogError("TW2ClipTool :: Clip: Kein gültiges Token. Bitte !clipauth.");
                return false;
            }

            var token = GetGlobalString(G_ACCESS_TOKEN);

            // ===== USER =====
            string user = "";
            if (!CPH.TryGetArg("userName", out user) || string.IsNullOrWhiteSpace(user))
                if (!CPH.TryGetArg("user", out user) || string.IsNullOrWhiteSpace(user))
                    if (!CPH.TryGetArg("displayName", out user) || string.IsNullOrWhiteSpace(user))
                        user = "someone";

            // ===== RAW INPUT =====
            string rawInput = "";
            CPH.TryGetArg("rawInput", out rawInput);

            int? durationOverride = null;
            string titleOverride = null;

            string inputTitle = "";
            if (TryParseDurationAndTitle(rawInput, out durationOverride, out titleOverride))
            {
                inputTitle = titleOverride ?? "";
            }
            else if (!string.IsNullOrWhiteSpace(rawInput))
            {
                string s = rawInput.Trim();
                if (s.StartsWith("!clip", StringComparison.OrdinalIgnoreCase))
                    s = s.Substring(5).Trim();

                inputTitle = s;
            }
            // ===== DEFAULT TITLE =====
            string baseTitle = inputTitle;
            if (string.IsNullOrWhiteSpace(baseTitle))
            {
                baseTitle = GetGlobalString("tw2_clipDefaultTitle");
                if (string.IsNullOrWhiteSpace(baseTitle))
                    baseTitle = "Clip";
            }

            // ===== TEMPLATE =====
            string template = GetGlobalString("tw2_clipTitleTemplate");
            if (string.IsNullOrWhiteSpace(template))
                template = "%title% - Clipped by %user%";

            string finalTitle = template
                .Replace("%title%", baseTitle)
                .Replace("%user%", user);

            if (finalTitle.Length > 140)
                finalTitle = finalTitle.Substring(0, 140);

            // ===== DURATION =====
            string durStr = GetGlobalString("tw2_clipDuration");
            int duration = 60;
            int.TryParse(durStr, out duration);
            if (duration <= 0) duration = 60;

            if (durationOverride.HasValue)
                duration = durationOverride.Value;

            //CPH.SendMessage($"Versucht zu Clippen {finalTitle}");

            // ===== HELIX REQUEST (QUERY PARAMS!) =====
            string url =
                "https://api.twitch.tv/helix/clips" +
                "?broadcaster_id=" + Uri.EscapeDataString(broadcasterId) +
                "&duration=" + duration +
                "&title=" + Uri.EscapeDataString(finalTitle);

            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.Headers["Client-Id"] = TWITCH_CLIENT_ID;
            req.Headers["Authorization"] = "Bearer " + token;
            req.ContentLength = 0;
            req.Timeout = 15000;

            string body;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var reader = new StreamReader(resp.GetResponseStream()))
                body = reader.ReadToEnd();

            string clipId = JsonGetFromArray(body, "data", 0, "id");
            if (string.IsNullOrWhiteSpace(clipId))
            {
                CPH.LogError("TW2ClipTool :: Clip: Keine Clip-ID erhalten. Body: " + body);
                return false;
            }

            string publicUrl = "https://clips.twitch.tv/" + clipId;
            CPH.LogInfo("TW2ClipTool :: Clip erstellt: " + publicUrl);

            if (GetGlobalBool(G_CHAT_ENABLED, true))
                CPH.SendMessage(publicUrl);

            return true;
        }
        catch (Exception ex)
        {
            CPH.LogError("TW2ClipTool :: Clip Exception: " + ex.Message);
            return false;
        }
    }



    // ====== TOKEN HELPERS ======

    private bool EnsureValidTokenSilently()
    {
        var token = GetGlobalString(G_ACCESS_TOKEN);
        if (string.IsNullOrWhiteSpace(token))
            return false;

        // If no expiresAt saved, just assume token is okay.
        var expiresAtS = GetGlobalString(G_EXPIRES_AT);
        if (string.IsNullOrWhiteSpace(expiresAtS))
            return true;

        long ticks;
        if (!long.TryParse(expiresAtS, out ticks))
            return true;

        var expiresAt = new DateTime(ticks, DateTimeKind.Utc);
        if (DateTime.UtcNow < expiresAt)
            return true;

        return TryRefreshToken();
    }

    private bool TryRefreshToken()
    {
        try
        {
            var refresh = GetGlobalString(G_REFRESH_TOKEN);
            if (string.IsNullOrWhiteSpace(refresh))
                return false;

            string tokenResp = HttpPostForm(
                "https://id.twitch.tv/oauth2/token",
                new Dictionary<string, string>
                {
                    { "grant_type", "refresh_token" },
                    { "refresh_token", refresh },
                    { "client_id", TWITCH_CLIENT_ID }
                },
                headers: null
            );

            string accessToken  = JsonGet(tokenResp, "access_token");
            string refreshToken = JsonGet(tokenResp, "refresh_token");
            string expiresInS   = JsonGet(tokenResp, "expires_in");

            if (string.IsNullOrWhiteSpace(accessToken))
                return false;

            int expiresIn = 0;
            int.TryParse(expiresInS, out expiresIn);
            if (expiresIn <= 0) expiresIn = 3600;

            SetGlobal(G_ACCESS_TOKEN, accessToken);
            if (!string.IsNullOrWhiteSpace(refreshToken))
                SetGlobal(G_REFRESH_TOKEN, refreshToken);

            long expiresAtTicks = DateTime.UtcNow.AddSeconds(expiresIn - 30).Ticks;
            SetGlobal(G_EXPIRES_AT, expiresAtTicks.ToString());

            CPH.LogInfo("TW2ClipTool :: Token refreshed.");
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ====== STREAMER.BOT GLOBALS ======

    private string GetGlobalString(string key)
    {
        try
        {
            return CPH.GetGlobalVar<string>(key, true);
        }
        catch
        {
            return null;
        }
    }

    private bool GetGlobalBool(string key, bool defaultValue)
    {
        try
        {
            // might be stored as "True"/"False" string depending on how user set it
            var s = CPH.GetGlobalVar<string>(key, true);
            if (string.IsNullOrWhiteSpace(s))
                return defaultValue;

            bool b;
            if (bool.TryParse(s, out b))
                return b;

            return defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    private void SetGlobal(string key, string value)
    {
        // persisted = true
        CPH.SetGlobalVar(key, value, true);
    }

    // ====== HTTP + SIMPLE JSON EXTRACT ======

    private string HttpPostForm(string url, Dictionary<string, string> form, Dictionary<string, string> headers)
    {
        string postData = BuildForm(form);

        var req = (HttpWebRequest)WebRequest.Create(url);
        req.Method = "POST";
        req.ContentType = "application/x-www-form-urlencoded";
        req.Timeout = 15000;

        if (headers != null)
        {
            foreach (var kv in headers)
                req.Headers[kv.Key] = kv.Value;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(postData);
        req.ContentLength = bytes.Length;

        using (var stream = req.GetRequestStream())
            stream.Write(bytes, 0, bytes.Length);

        try
        {
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var reader = new StreamReader(resp.GetResponseStream()))
                return reader.ReadToEnd();
        }
        catch (WebException wex)
        {
            // Read error response body (still do NOT leak secrets to chat)
            try
            {
                using (var resp = (HttpWebResponse)wex.Response)
                using (var reader = new StreamReader(resp.GetResponseStream()))
                    return reader.ReadToEnd();
            }
            catch
            {
                return "{}";
            }
        }
    }

    private string BuildForm(Dictionary<string, string> form)
    {
        var sb = new StringBuilder();
        bool first = true;
        foreach (var kv in form)
        {
            if (!first) sb.Append("&");
            first = false;
            sb.Append(Uri.EscapeDataString(kv.Key));
            sb.Append("=");
            sb.Append(Uri.EscapeDataString(kv.Value ?? ""));
        }
        return sb.ToString();
    }

    // Very small JSON helpers (string values, int values)
    private string JsonGet(string json, string key)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(key))
            return null;

        // match: "key":"value" OR "key":123
        string needle = "\"" + key + "\"";
        int i = json.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;

        i = json.IndexOf(":", i);
        if (i < 0) return null;
        i++;

        // skip whitespace
        while (i < json.Length && char.IsWhiteSpace(json[i])) i++;

        if (i >= json.Length) return null;

        if (json[i] == '"')
        {
            i++;
            int end = json.IndexOf("\"", i, StringComparison.OrdinalIgnoreCase);
            if (end < 0) return null;
            return json.Substring(i, end - i);
        }
        else
        {
            // number / bareword until comma or end brace
            int end = i;
            while (end < json.Length && json[end] != ',' && json[end] != '}' && !char.IsWhiteSpace(json[end]))
                end++;
            return json.Substring(i, end - i);
        }
    }

    private string JsonGetFromArray(string json, string arrayKey, int index, string key)
    {
        // crude but works for helix: "data":[{...}]
        string needle = "\"" + arrayKey + "\"";
        int i = json.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;

        int arrStart = json.IndexOf("[", i);
        if (arrStart < 0) return null;

        // find first object "{"
        int objStart = json.IndexOf("{", arrStart);
        if (objStart < 0) return null;

        // We only support index 0 for now (enough for helix clips)
        if (index != 0) return null;

        int objEnd = json.IndexOf("}", objStart);
        if (objEnd < 0) return null;

        string obj = json.Substring(objStart, objEnd - objStart + 1);
        return JsonGet(obj, key);
    }
}
