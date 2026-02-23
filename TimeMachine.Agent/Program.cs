// Program.cs — TimeMachine.Agent PRODUCT MODE v4
// .NET 8/10, NuGet: Microsoft.Data.Sqlite
// Keys: S=General, F=Focus, P=Pattern, D=Distractions

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Data.Sqlite;

internal class Program
{
    // ===== Tunables =====
    private const int PollIntervalMs = 250;
    private const int IdleThresholdSec = 60;
    private const int GraceSwitchMs = 5000;
    private const int MinSessionWriteMs = 1000;

    private const int DeepThresholdMin = 20;
    private const int DistractionShortSec = 90;
    private const double SwitchPenalty = 0.5;

    private const int GitPollIntervalMs = 60_000;
    private const int GitEventsPerPage = 30;

    // ===== WinAPI: active window =====
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // ===== WinAPI: idle time =====
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("kernel32.dll")]
    private static extern uint GetTickCount();

    private static long NowUnixMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static int GetIdleSeconds()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref lii)) return 0;
        uint idleMs = GetTickCount() - lii.dwTime;
        return (int)(idleMs / 1000);
    }

    private static (string app, int pid, string? exePath) TryGetProcessInfo(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            string name = p.ProcessName;

            string? path = null;
            try { path = p.MainModule?.FileName; } catch { }

            return (name, (int)pid, path);
        }
        catch
        {
            return ("unknown", (int)pid, null);
        }
    }

    private static string GetActiveWindowTitle(out uint pid)
    {
        pid = 0;
        var hWnd = GetForegroundWindow();
        if (hWnd == IntPtr.Zero) return "";

        GetWindowThreadProcessId(hWnd, out pid);

        var sb = new StringBuilder(512);
        _ = GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    // ===== Session State =====
    private sealed class SessionState
    {
        public long StartTs;
        public string App = "unknown";
        public int Pid;
        public string Title = "";
        public string? ExePath;
        public int IsIdle;

        public string Key() => $"{(IsIdle == 1 ? "Idle" : App)}|{(IsIdle == 1 ? 0 : Pid)}|{IsIdle}";
    }

    private sealed class PendingSwitch
    {
        public long FirstSeenTs;
        public string NewKey = "";
        public string NewApp = "unknown";
        public int NewPid;
        public string NewTitle = "";
        public string? NewExePath;
        public int NewIsIdle;
    }

    private static void InsertSession(SqliteConnection conn, long startTs, long endTs, SessionState s)
    {
        long dur = Math.Max(0, endTs - startTs);
        if (dur < MinSessionWriteMs) return;

        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            @"INSERT INTO sessions(start_ts, end_ts, duration_ms, app, pid, window_title, exe_path, is_idle)
              VALUES($st, $et, $dur, $app, $pid, $title, $path, $idle);";
        cmd.Parameters.AddWithValue("$st", startTs);
        cmd.Parameters.AddWithValue("$et", endTs);
        cmd.Parameters.AddWithValue("$dur", dur);
        cmd.Parameters.AddWithValue("$app", s.IsIdle == 1 ? "Idle" : s.App);
        cmd.Parameters.AddWithValue("$pid", s.IsIdle == 1 ? 0 : s.Pid);
        cmd.Parameters.AddWithValue("$title", s.Title ?? "");
        cmd.Parameters.AddWithValue("$path", (object?)s.ExePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$idle", s.IsIdle);
        cmd.ExecuteNonQuery();
    }

    // ===== GitHub =====
    private static string? TryGetEnvToken()
    {
        var t = Environment.GetEnvironmentVariable("TIMEMACHINE_GITHUB_TOKEN");
        return string.IsNullOrWhiteSpace(t) ? null : t.Trim();
    }

    private static HttpClient CreateGitHubClient(string token)
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TimeMachine/1.0");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    private static string? EnsureGitHubLogin(HttpClient http, ref string? cachedLogin)
    {
        if (!string.IsNullOrWhiteSpace(cachedLogin)) return cachedLogin;

        try
        {
            var json = http.GetStringAsync("https://api.github.com/user").GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("login", out var loginEl))
            {
                cachedLogin = loginEl.GetString();
                return cachedLogin;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GitHub] Failed to read /user: {ex.Message}");
        }

        return null;
    }

    private static int PullGitHubEventsIntoDb(SqliteConnection conn, HttpClient http, string login)
    {
        string url = $"https://api.github.com/users/{login}/events/public?per_page={GitEventsPerPage}";
        string json;

        try
        {
            json = http.GetStringAsync(url).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GitHub] Pull failed: {ex.Message}");
            return 0;
        }

        int inserted = 0;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return 0;

            using var tx = conn.BeginTransaction();

            foreach (var ev in doc.RootElement.EnumerateArray())
            {
                if (!ev.TryGetProperty("id", out var idEl)) continue;
                string? ghId = idEl.GetString();
                if (string.IsNullOrWhiteSpace(ghId)) continue;

                string ghType = ev.TryGetProperty("type", out var typeEl) ? (typeEl.GetString() ?? "unknown") : "unknown";

                string? repo = null;
                if (ev.TryGetProperty("repo", out var repoEl) && repoEl.ValueKind == JsonValueKind.Object)
                    if (repoEl.TryGetProperty("name", out var nameEl)) repo = nameEl.GetString();

                long createdMs = NowUnixMs();
                if (ev.TryGetProperty("created_at", out var createdEl))
                {
                    var s = createdEl.GetString();
                    if (!string.IsNullOrWhiteSpace(s) && DateTimeOffset.TryParse(s, out var dto))
                        createdMs = dto.ToUnixTimeMilliseconds();
                }

                string payload = ev.GetRawText();

                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText =
                    @"INSERT OR IGNORE INTO github_events(ts, gh_event_id, gh_type, repo, created_ts, payload_json)
                      VALUES($ts, $id, $type, $repo, $created, $payload);";
                cmd.Parameters.AddWithValue("$ts", NowUnixMs());
                cmd.Parameters.AddWithValue("$id", ghId);
                cmd.Parameters.AddWithValue("$type", ghType);
                cmd.Parameters.AddWithValue("$repo", (object?)repo ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$created", createdMs);
                cmd.Parameters.AddWithValue("$payload", payload);

                int n = cmd.ExecuteNonQuery();
                if (n > 0) inserted += n;
            }

            tx.Commit();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GitHub] Parse/DB error: {ex.Message}");
            return 0;
        }

        return inserted;
    }

    // ===== Reports over sessions =====
    private sealed record Sess(long StartTs, long EndTs, long DurMs, string App, int IsIdle);

    private static List<Sess> LoadTodaySessions(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            @"SELECT start_ts, end_ts, duration_ms, app, is_idle
              FROM sessions
              WHERE start_ts >= strftime('%s','now','start of day') * 1000
              ORDER BY start_ts;";

        var list = new List<Sess>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            long st = r.GetInt64(0);
            long et = r.GetInt64(1);
            long dur = r.GetInt64(2);
            string app = r.GetString(3);
            int idle = r.GetInt32(4);
            list.Add(new Sess(st, et, dur, app, idle));
        }
        return list;
    }

    private static string HmsFromMs(long ms)
    {
        if (ms < 0) ms = 0;
        var t = TimeSpan.FromMilliseconds(ms);
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
    }

    private static List<Sess> WithLive(List<Sess> list, SessionState? live)
    {
        if (live == null) return list;

        long now = NowUnixMs();
        long dur = Math.Max(0, now - live.StartTs);
        if (dur < MinSessionWriteMs) return list;

        var copy = new List<Sess>(list)
        {
            new Sess(live.StartTs, now, dur, live.IsIdle == 1 ? "Idle" : live.App, live.IsIdle)
        };
        return copy;
    }

    private static void ShowGeneral(SqliteConnection conn, SessionState? live)
    {
        var list = WithLive(LoadTodaySessions(conn), live);

        long activeMs = list.Where(x => x.IsIdle == 0).Sum(x => x.DurMs);
        long idleMs = list.Where(x => x.IsIdle == 1).Sum(x => x.DurMs);

        int sessionsCount = list.Count(x => x.IsIdle == 0);
        int idleSessions = list.Count(x => x.IsIdle == 1);

        double avgMs = sessionsCount > 0 ? list.Where(x => x.IsIdle == 0).Average(x => x.DurMs) : 0;

        var top = list.Where(x => x.IsIdle == 0)
                      .GroupBy(x => x.App)
                      .Select(g => new { App = g.Key, Ms = g.Sum(z => z.DurMs) })
                      .OrderByDescending(x => x.Ms)
                      .Take(10)
                      .ToList();

        Console.WriteLine("\n--- GENERAL (today) ---");
        Console.WriteLine($"Active: {HmsFromMs(activeMs)}   Idle: {HmsFromMs(idleMs)}");
        Console.WriteLine($"Sessions: {sessionsCount}   Idle sessions: {idleSessions}   Avg session: {HmsFromMs((long)avgMs)}");
        Console.WriteLine("\nTop apps:");
        foreach (var x in top)
            Console.WriteLine($"{x.App} : {HmsFromMs(x.Ms)}");
        Console.WriteLine("------------------------\n");
    }

    private static void ShowFocus(SqliteConnection conn, SessionState? live)
    {
        var list = WithLive(LoadTodaySessions(conn), live);
        var active = list.Where(x => x.IsIdle == 0).OrderBy(x => x.StartTs).ToList();

        long totalActiveMs = active.Sum(x => x.DurMs);

        long deepMsThreshold = DeepThresholdMin * 60_000L;
        var deep = active.Where(x => x.DurMs >= deepMsThreshold).ToList();
        long deepMs = deep.Sum(x => x.DurMs);

        int switches = 0;
        for (int i = 1; i < active.Count; i++)
            if (!string.Equals(active[i - 1].App, active[i].App, StringComparison.OrdinalIgnoreCase))
                switches++;

        double ratio = totalActiveMs > 0 ? (double)deepMs / totalActiveMs : 0.0;
        double score = totalActiveMs == 0 ? 0.0 : (ratio * 100.0) - (switches * SwitchPenalty);
        score = Math.Clamp(score, 0.0, 100.0);

        var longest = active.OrderByDescending(x => x.DurMs).FirstOrDefault();

        Console.WriteLine("\n--- FOCUS (today) ---");
        Console.WriteLine($"Deep threshold: {DeepThresholdMin} min");
        Console.WriteLine($"Deep sessions: {deep.Count}   Deep time: {HmsFromMs(deepMs)}");
        Console.WriteLine($"Switches: {switches}");
        Console.WriteLine($"Focus score: {score:0.0}%");
        if (longest != null)
            Console.WriteLine($"Longest session: {longest.App}  {HmsFromMs(longest.DurMs)}");
        Console.WriteLine("----------------------\n");
    }

    private static void ShowPattern(SqliteConnection conn, SessionState? live)
    {
        var list = WithLive(LoadTodaySessions(conn), live);
        var active = list.Where(x => x.IsIdle == 0).OrderBy(x => x.StartTs).ToList();

        Console.WriteLine("\n--- PATTERN (today) ---");

        if (active.Count == 0)
        {
            Console.WriteLine("No active sessions yet.");
            Console.WriteLine("-----------------------\n");
            return;
        }

        int switches = 0;
        var switchTo = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int i = 1; i < active.Count; i++)
        {
            if (!string.Equals(active[i - 1].App, active[i].App, StringComparison.OrdinalIgnoreCase))
            {
                switches++;
                string toApp = active[i].App;
                switchTo[toApp] = switchTo.TryGetValue(toApp, out var c) ? c + 1 : 1;
            }
        }

        long totalActiveMs = active.Sum(x => x.DurMs);
        double avgMs = active.Average(x => x.DurMs);

        var byHour = active
            .GroupBy(s => DateTimeOffset.FromUnixTimeMilliseconds(s.StartTs).ToLocalTime().Hour)
            .Select(g => new { Hour = g.Key, Ms = g.Sum(x => x.DurMs) })
            .OrderByDescending(x => x.Ms)
            .FirstOrDefault();

        Console.WriteLine($"Total active: {HmsFromMs(totalActiveMs)}");
        Console.WriteLine($"Switches: {switches}   Avg session: {HmsFromMs((long)avgMs)}");

        if (byHour != null)
            Console.WriteLine($"Peak hour: {byHour.Hour:00}:00  ({HmsFromMs(byHour.Ms)})");

        if (switchTo.Count > 0)
        {
            var top = switchTo.OrderByDescending(kv => kv.Value).First();
            Console.WriteLine($"Most switched-to app: {top.Key} ({top.Value})");
        }

        Console.WriteLine("------------------------\n");
    }

    private static void ShowDistractions(SqliteConnection conn, SessionState? live)
    {
        var list = WithLive(LoadTodaySessions(conn), live);
        var active = list.Where(x => x.IsIdle == 0).OrderBy(x => x.StartTs).ToList();

        Console.WriteLine("\n--- DISTRACTIONS (today) ---");

        if (active.Count == 0)
        {
            Console.WriteLine("No active sessions yet.");
            Console.WriteLine("----------------------------\n");
            return;
        }

        long shortMs = DistractionShortSec * 1000L;

        // 1) Short sessions per app
        var shortByApp = active
            .Where(x => x.DurMs <= shortMs)
            .GroupBy(x => x.App)
            .Select(g => new
            {
                App = g.Key,
                Count = g.Count(),
                TotalMs = g.Sum(z => z.DurMs),
                AvgMs = g.Average(z => z.DurMs)
            })
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.AvgMs)
            .Take(10)
            .ToList();

        // 2) Switch culprits (who we switch TO)
        var switchTo = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < active.Count; i++)
        {
            if (!string.Equals(active[i - 1].App, active[i].App, StringComparison.OrdinalIgnoreCase))
            {
                string toApp = active[i].App;
                switchTo[toApp] = switchTo.TryGetValue(toApp, out var c) ? c + 1 : 1;
            }
        }

        // 3) Time to first distraction (first short session after first "work" session)
        long? firstWorkTs = active.FirstOrDefault()?.StartTs;
        long? firstDistrTs = active.FirstOrDefault(x => x.DurMs <= shortMs)?.StartTs;

        if (firstWorkTs != null && firstDistrTs != null)
        {
            long delta = Math.Max(0, firstDistrTs.Value - firstWorkTs.Value);
            Console.WriteLine($"Time to first distraction: {HmsFromMs(delta)}  (short <= {DistractionShortSec}s)");
        }
        else
        {
            Console.WriteLine($"Time to first distraction: n/a  (short <= {DistractionShortSec}s)");
        }

        if (switchTo.Count > 0)
        {
            var top = switchTo.OrderByDescending(kv => kv.Value).First();
            Console.WriteLine($"Main switch target: {top.Key} ({top.Value})");
        }

        Console.WriteLine($"\nTop short sessions (<= {DistractionShortSec}s):");
        if (shortByApp.Count == 0) Console.WriteLine("(none)");
        foreach (var x in shortByApp)
            Console.WriteLine($"{x.App}  hits:{x.Count}  avg:{HmsFromMs((long)x.AvgMs)}  total:{HmsFromMs(x.TotalMs)}");

        Console.WriteLine("----------------------------\n");
    }

    private static void ShowGitHubToday(SqliteConnection conn)
    {
        Console.WriteLine("\n--- TODAY: GITHUB EVENTS ---");

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                @"SELECT gh_type, COUNT(*)
                  FROM github_events
                  WHERE created_ts >= strftime('%s','now','start of day') * 1000
                  GROUP BY gh_type
                  ORDER BY COUNT(*) DESC;";
            using var r = cmd.ExecuteReader();
            bool any = false;
            while (r.Read())
            {
                any = true;
                Console.WriteLine($"{r.GetString(0)} : {r.GetInt32(1)}");
            }
            if (!any) Console.WriteLine("(no events today)");
        }

        Console.WriteLine("\nLast 10:");
        using (var cmd2 = conn.CreateCommand())
        {
            cmd2.CommandText =
                @"SELECT created_ts, gh_type, COALESCE(repo,'')
                  FROM github_events
                  WHERE created_ts >= strftime('%s','now','start of day') * 1000
                  ORDER BY created_ts DESC
                  LIMIT 10;";
            using var r2 = cmd2.ExecuteReader();
            while (r2.Read())
            {
                long created = r2.GetInt64(0);
                string type = r2.GetString(1);
                string repo = r2.GetString(2);

                var dt = DateTimeOffset.FromUnixTimeMilliseconds(created).ToLocalTime();
                Console.WriteLine($"{dt:HH:mm:ss}  {type}  {repo}");
            }
        }

        Console.WriteLine("----------------------------\n");
    }

    private static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        string dbPath = "timemachine.db";
        string cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        using var conn = new SqliteConnection(cs);
        conn.Open();

        // sessions
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                @"CREATE TABLE IF NOT EXISTS sessions(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    start_ts INTEGER NOT NULL,
                    end_ts INTEGER NOT NULL,
                    duration_ms INTEGER NOT NULL,
                    app TEXT NOT NULL,
                    pid INTEGER NOT NULL,
                    window_title TEXT NOT NULL,
                    exe_path TEXT,
                    is_idle INTEGER NOT NULL
                  );
                  CREATE INDEX IF NOT EXISTS idx_sessions_start ON sessions(start_ts);
                  CREATE INDEX IF NOT EXISTS idx_sessions_app   ON sessions(app);";
            cmd.ExecuteNonQuery();
        }

        // github_events
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                @"CREATE TABLE IF NOT EXISTS github_events(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ts INTEGER NOT NULL,
                    gh_event_id TEXT NOT NULL,
                    gh_type TEXT NOT NULL,
                    repo TEXT,
                    created_ts INTEGER NOT NULL,
                    payload_json TEXT NOT NULL
                  );
                  CREATE UNIQUE INDEX IF NOT EXISTS idx_gh_event_id ON github_events(gh_event_id);
                  CREATE INDEX IF NOT EXISTS idx_gh_created_ts ON github_events(created_ts);";
            cmd.ExecuteNonQuery();
        }

        // GitHub init
        string? ghToken = TryGetEnvToken();
        HttpClient? ghHttp = null;
        string? ghLogin = null;

        if (ghToken != null)
        {
            ghHttp = CreateGitHubClient(ghToken);
            ghLogin = EnsureGitHubLogin(ghHttp, ref ghLogin);

            if (!string.IsNullOrWhiteSpace(ghLogin))
                Console.WriteLine($"[GitHub] Enabled for user: {ghLogin}");
            else
            {
                Console.WriteLine("[GitHub] Token found, but can't read login. GitHub disabled.");
                ghHttp.Dispose();
                ghHttp = null;
            }
        }
        else
        {
            Console.WriteLine("[GitHub] Disabled (set TIMEMACHINE_GITHUB_TOKEN).");
        }

        Console.WriteLine("\nTimeMachine.Agent started (PRODUCT MODE v4).");
        Console.WriteLine("- Sessions key: app+pid+idle (title doesn't split)");
        Console.WriteLine($"- Grace merge: {GraceSwitchMs}ms");
        Console.WriteLine("Keys:");
        Console.WriteLine("  S = General (today overview)");
        Console.WriteLine("  F = Focus (deep work)");
        Console.WriteLine("  P = Pattern (behaviour)");
        Console.WriteLine("  D = Distractions (who breaks focus)");
        Console.WriteLine("  G = GitHub (today events)");
        Console.WriteLine("  Ctrl+C = stop\n");

        var cur = new SessionState();
        bool hasSession = false;
        PendingSwitch? pending = null;

        long lastGitPoll = 0;

        void CloseCurrent()
        {
            if (!hasSession) return;
            InsertSession(conn, cur.StartTs, NowUnixMs(), cur);
            hasSession = false;
        }

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            CloseCurrent();
            ghHttp?.Dispose();
            Environment.Exit(0);
        };

        while (true)
        {
            long nowTs = NowUnixMs();

            // GitHub polling
            if (ghHttp != null && ghLogin != null && (nowTs - lastGitPoll) >= GitPollIntervalMs)
            {
                lastGitPoll = nowTs;
                int n = PullGitHubEventsIntoDb(conn, ghHttp, ghLogin);
                if (n > 0) Console.WriteLine($"[GitHub] +{n} new events saved.");
            }


            int idleSec = GetIdleSeconds();
            int isIdle = idleSec >= IdleThresholdSec ? 1 : 0;

            uint pidRaw;
            string title = GetActiveWindowTitle(out pidRaw).Trim();

            string app;
            int pid;
            string? exePath;

            if (isIdle == 1)
            {
                app = "Idle";
                pid = 0;
                title = "";
                exePath = null;
            }
            else
            {
                var info = TryGetProcessInfo(pidRaw);
                app = info.app;
                pid = info.pid;
                exePath = info.exePath;
            }

            string newKey = $"{(isIdle == 1 ? "Idle" : app)}|{(isIdle == 1 ? 0 : pid)}|{isIdle}";

            if (!hasSession)
            {
                cur.StartTs = nowTs;
                cur.App = app;
                cur.Pid = pid;
                cur.Title = title;
                cur.ExePath = exePath;
                cur.IsIdle = isIdle;
                hasSession = true;

                Console.WriteLine($"{DateTime.Now:HH:mm:ss} START {(isIdle == 1 ? "[IDLE]" : "      ")} {app} | {title}");
            }
            else
            {
                string curKey = cur.Key();

                if (newKey == curKey)
                {
                    cur.Title = title;
                    pending = null;
                }
                else
                {
                    if (pending == null || pending.NewKey != newKey)
                    {
                        pending = new PendingSwitch
                        {
                            FirstSeenTs = nowTs,
                            NewKey = newKey,
                            NewApp = app,
                            NewPid = pid,
                            NewTitle = title,
                            NewExePath = exePath,
                            NewIsIdle = isIdle
                        };
                    }

                    if (nowTs - pending.FirstSeenTs >= GraceSwitchMs)
                    {
                        InsertSession(conn, cur.StartTs, pending.FirstSeenTs, cur);

                        cur.StartTs = pending.FirstSeenTs;
                        cur.App = pending.NewApp;
                        cur.Pid = pending.NewPid;
                        cur.Title = pending.NewTitle;
                        cur.ExePath = pending.NewExePath;
                        cur.IsIdle = pending.NewIsIdle;

                        Console.WriteLine($"{DateTime.Now:HH:mm:ss} START {(cur.IsIdle == 1 ? "[IDLE]" : "      ")} {cur.App} | {cur.Title}");
                        pending = null;
                    }
                }
            }

            // Keys
            if (Console.KeyAvailable)
            {
                var k = Console.ReadKey(true).Key;

                if (k == ConsoleKey.S) ShowGeneral(conn, hasSession ? cur : null);
                else if (k == ConsoleKey.F) ShowFocus(conn, hasSession ? cur : null);
                else if (k == ConsoleKey.P) ShowPattern(conn, hasSession ? cur : null);
                else if (k == ConsoleKey.D) ShowDistractions(conn, hasSession ? cur : null);
                else if (k == ConsoleKey.G) ShowGitHubToday(conn);
            }

            Thread.Sleep(PollIntervalMs);
        }
    }
}
