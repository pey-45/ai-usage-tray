using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace UsageTray;

internal sealed record UsageWindow(double Percent, DateTimeOffset? ResetAt);

internal sealed record UsageReading(
    UsageWindow? Session = null,
    UsageWindow? Week = null,
    DateTimeOffset? Timestamp = null,
    string? Error = null,
    DateTimeOffset? RetryAt = null);

internal static class UsageSources
{
    private static readonly string Profile = Environment.GetEnvironmentVariable("USERPROFILE")
        ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly HttpClient ClaudeClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static async Task<UsageReading> ReadClaudeAsync()
    {
        string path = Path.Combine(Profile, ".claude", ".credentials.json");

        try
        {
            var credentials = await ReadClaudeCredentialsAsync(path);
            bool refreshed = credentials.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (refreshed)
            {
                await RefreshClaudeCredentialsAsync();
                credentials = await ReadClaudeCredentialsAsync(path);
            }

            var usage = await RequestClaudeUsageAsync(credentials.Token);
            if (usage is not null)
                return usage;

            if (!refreshed)
            {
                await RefreshClaudeCredentialsAsync();
                credentials = await ReadClaudeCredentialsAsync(path);
                usage = await RequestClaudeUsageAsync(credentials.Token);
            }

            return usage ?? new(Error: "Sesión no autorizada");
        }
        catch (FileNotFoundException) { return new(Error: "Faltan las credenciales"); }
        catch (DirectoryNotFoundException) { return new(Error: "Faltan las credenciales"); }
        catch (UnauthorizedAccessException) { return new(Error: "Credenciales inaccesibles"); }
        catch (IOException) { return new(Error: "Error de lectura"); }
        catch (Win32Exception) { return new(Error: "No se pudo ejecutar Claude CLI"); }
        catch (TimeoutException) { return new(Error: "Claude CLI tardó demasiado"); }
        catch (OperationCanceledException) { return new(Error: "La petición agotó el tiempo"); }
        catch (HttpRequestException error)
        {
            return new(Error: error.StatusCode is { } status ? $"Error HTTP {(int)status}" : "Error de conexión");
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException
                                      or KeyNotFoundException or ArgumentException)
        {
            return new(Error: "Formato de datos inesperado");
        }
    }

    private static async Task<(string Token, long ExpiresAt)> ReadClaudeCredentialsAsync(string path)
    {
        using var stream = File.OpenRead(path);
        using var json = await JsonDocument.ParseAsync(stream);
        var oauth = json.RootElement.GetProperty("claudeAiOauth");
        var token = oauth.GetProperty("accessToken").GetString();
        if (string.IsNullOrWhiteSpace(token))
            throw new FormatException();

        return (token, oauth.GetProperty("expiresAt").GetInt64());
    }

    private static async Task RefreshClaudeCredentialsAsync()
    {
        var startInfo = new ProcessStartInfo("claude.exe", "auth status")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        Process process;
        try
        {
            process = Process.Start(startInfo)!;
        }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3)
        {
            startInfo.FileName = Path.Combine(Profile, ".local", "bin", "claude.exe");
            process = Process.Start(startInfo)!;
        }

        using (process)
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            try
            {
                await Task.WhenAll(
                    process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, timeout.Token),
                    process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token),
                    process.WaitForExitAsync(timeout.Token)).WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { } // Puede haber terminado al vencer el plazo.
                catch (Win32Exception) { }
                throw new TimeoutException();
            }
        }
    }

    private static async Task<UsageReading?> RequestClaudeUsageAsync(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        request.Headers.UserAgent.ParseAdd("usage-tray");

        using var response = await ClaudeClient.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return null;

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var now = DateTimeOffset.Now;
            var retryAfter = response.Headers.RetryAfter;
            var retryAt = retryAfter?.Delta is { } delay
                ? now + delay
                : retryAfter?.Date ?? now.AddMinutes(15);
            return new(Error: $"limitado hasta {retryAt.ToLocalTime():HH:mm}", RetryAt: retryAt);
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var json = await JsonDocument.ParseAsync(stream);
        return new(ReadClaudeWindow(json.RootElement, "five_hour"),
            ReadClaudeWindow(json.RootElement, "seven_day"));
    }

    private static UsageWindow? ReadClaudeWindow(JsonElement root, string name)
    {
        var window = root.GetProperty(name);
        if (window.ValueKind == JsonValueKind.Null)
            return null;

        var reset = window.GetProperty("resets_at");
        return new(window.GetProperty("utilization").GetDouble(),
            reset.ValueKind == JsonValueKind.Null ? null : reset.GetDateTimeOffset());
    }

    public static UsageReading ReadCodex()
    {
        try
        {
            string sessions = Path.Combine(Profile, ".codex", "sessions");
            string? latest = null;
            DateTime latestWrite = DateTime.MinValue;
            var today = DateTime.Today;

            for (int daysAgo = 0; daysAgo < 7; daysAgo++)
            {
                var day = today.AddDays(-daysAgo);
                string folder = Path.Combine(sessions, day.ToString(@"yyyy\\MM\\dd", CultureInfo.InvariantCulture));
                if (!Directory.Exists(folder))
                    continue;

                foreach (var file in new DirectoryInfo(folder).EnumerateFiles("rollout-*.jsonl"))
                {
                    if (file.LastWriteTimeUtc > latestWrite)
                    {
                        latest = file.FullName;
                        latestWrite = file.LastWriteTimeUtc;
                    }
                }
            }

            if (latest is null)
                return new(Error: "Sin sesiones recientes");

            using var reader = new StreamReader(new FileStream(latest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            string? last = null;
            while (reader.ReadLine() is { } line)
            {
                if (line.Contains("\"rate_limits\"", StringComparison.Ordinal))
                    last = line;
            }

            if (last is null)
                return new(Error: "Sin datos de uso en la sesión");

            using var json = JsonDocument.Parse(last);
            var root = json.RootElement;
            var limits = root.GetProperty("payload").GetProperty("rate_limits");
            if (limits.ValueKind == JsonValueKind.Null)
                return new(Error: "Sin datos de uso en la sesión");

            var now = DateTimeOffset.UtcNow;
            return new(ReadCodexWindow(limits, "primary", now), ReadCodexWindow(limits, "secondary", now),
                root.GetProperty("timestamp").GetDateTimeOffset());
        }
        catch (UnauthorizedAccessException) { return new(Error: "Sesiones inaccesibles"); }
        catch (IOException) { return new(Error: "Error al leer la sesión"); }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException
                                      or KeyNotFoundException or ArgumentOutOfRangeException)
        {
            return new(Error: "Formato de datos inesperado");
        }
    }

    private static UsageWindow? ReadCodexWindow(JsonElement limits, string name, DateTimeOffset now)
    {
        if (!limits.TryGetProperty(name, out var window) || window.ValueKind == JsonValueKind.Null)
            return null;

        var reset = window.GetProperty("resets_at");
        DateTimeOffset? resetAt = reset.ValueKind == JsonValueKind.Null
            ? null : DateTimeOffset.FromUnixTimeSeconds(reset.GetInt64());
        return new(resetAt <= now ? 0 : window.GetProperty("used_percent").GetDouble(), resetAt);
    }
}
