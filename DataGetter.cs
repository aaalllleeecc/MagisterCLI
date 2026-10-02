using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace MagisterLoginDemo
{
    // =====================================================================
    // MODELS (property names are matched case-insensitively against the API JSON)
    // =====================================================================

    public class Link
    {
        public string? Rel { get; set; }
        public string? Href { get; set; }
    }

    public class Vak
    {
        public int Id { get; set; }
        public string? Naam { get; set; }
    }

    public class Docent
    {
        public int Id { get; set; }
        public string? Naam { get; set; }
        public string? Docentcode { get; set; }
    }

    public class Lokaal
    {
        public string? Naam { get; set; }
    }

    public class Afspraak
    {
        public int Id { get; set; }
        public DateTime Start { get; set; } // UTC as sent by the API
        public DateTime Einde { get; set; } // UTC as sent by the API
        public int? LesuurVan { get; set; }
        public int? LesuurTotMet { get; set; }
        public bool DuurtHeleDag { get; set; }
        public string? Omschrijving { get; set; }
        public string? Lokatie { get; set; }
        public string? Opmerking { get; set; }
        public string? Inhoud { get; set; } // homework / lesson content, HTML
        public int Status { get; set; }
        public int Type { get; set; }
        public bool HeeftBijlagen { get; set; }
        public List<Vak>? Vakken { get; set; }
        public List<Docent>? Docenten { get; set; }
        public List<Lokaal>? Lokalen { get; set; }
        public List<Link>? Links { get; set; }

        public DateTime StartLocal => Start.ToLocalTime();
        public DateTime EindeLocal => Einde.ToLocalTime();
    }

    public class Afzender
    {
        public string? Naam { get; set; }
    }

    public class BerichtLinks
    {
        public Link? Self { get; set; }
    }

    public class Bericht
    {
        public int Id { get; set; }
        public string? Onderwerp { get; set; }
        public Afzender? Afzender { get; set; }
        public DateTime VerzondenOp { get; set; }
        public bool HeeftBijlagen { get; set; }
        public BerichtLinks? Links { get; set; }

        public string SelfHref => Links?.Self?.Href ?? "";
    }

    public class VakInfo
    {
        public string? Omschrijving { get; set; }
    }

    public class Cijfer
    {
        public string? Omschrijving { get; set; }
        public string? Waarde { get; set; }
        public VakInfo? Vak { get; set; }
    }

    public class Studiewijzer
    {
        public int Id { get; set; }
        public string? Titel { get; set; }
        public DateTime Van { get; set; }
        public DateTime TotEnMet { get; set; }
        public List<string>? VakCodes { get; set; }
        public bool InLeerlingArchief { get; set; }
        public List<Link>? Links { get; set; }

        public string SelfHref => Links?.Find(l => l.Rel == "Self")?.Href ?? "";
    }

    public class Onderdeel
    {
        public int Id { get; set; }
        public string? Titel { get; set; }
        public string? Omschrijving { get; set; }
        public bool IsZichtbaar { get; set; }
        public int Volgnummer { get; set; }
        public List<Link>? Links { get; set; }
        public string SelfHref => Links?.Find(l => l.Rel == "Self")?.Href ?? "";
    }

    public class OnderdelenResponse
    {
        public List<Onderdeel>? Items { get; set; }
        public int TotalCount { get; set; }
    }

    public class StudiewijzerDetail
    {
        public int Id { get; set; }
        public string? Titel { get; set; }
        public bool IsZichtbaar { get; set; }
        public bool InLeerlingArchief { get; set; }
        public List<string>? VakCodes { get; set; }
        public OnderdelenResponse? Onderdelen { get; set; }
    }

    public class Bron
    {
        public int Id { get; set; }
        public string? Naam { get; set; }
        public string? ContentType { get; set; }
        public long Grootte { get; set; }
        public string? Uri { get; set; }
        public List<Link>? Links { get; set; }
    }

    public class OnderdeelDetail
    {
        public int Id { get; set; }
        public string? Titel { get; set; }
        public string? Omschrijving { get; set; }
        public List<Bron>? Bronnen { get; set; }
    }

    // What is written to disk between runs. Deliberately no password and no cookies.
    internal sealed class SessionCache
    {
        public string? Host { get; set; }
        public string? Username { get; set; }
        public DateTime LastLoginUtc { get; set; }
        public string? SchoolToken { get; set; }
        public string? LeerlingId { get; set; }
    }

    // =====================================================================
    // DataGetter: the class to build a CLI / app around
    //
    //   var mg = new DataGetter("RSG Pantarijn", "622318", password);
    //   foreach (var a in await mg.GetAfsprakenAsync(DateTime.Today, DateTime.Today.AddDays(6)))
    //       Console.WriteLine($"{a.StartLocal:ddd HH:mm} {a.Omschrijving}");
    //
    // It logs in lazily on first use, saves the session (token + time of login) to a small
    // JSON file, and re-uses it on later runs while it is younger than MaxTokenAge. When it is
    // too old, or the API answers 401, it logs in again (needs the password) and rewrites the file.
    // Not thread-safe: use one call at a time.
    // =====================================================================
    public sealed class DataGetter
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };

        // Token lifetime was ~1 hour in the captured flow; refresh a bit earlier
        private static readonly TimeSpan MaxTokenAge = TimeSpan.FromMinutes(50);

        private readonly string _school;
        private readonly string _username;
        private readonly Func<string> _passwordProvider;
        private MagisterLogin? _login;
        private DateTime _connectedAtUtc;
        private Action<string>? _log;

        /// <summary>School web host, e.g. pantarijn.magister.net</summary>
        public string Host { get; }

        /// <summary>Student id used in /api/personen/{id}/... (available after the first call or ConnectAsync).</summary>
        public string? LeerlingId { get; private set; }

        /// <summary>Optional diagnostics (e.g. Console.WriteLine). Never receives tokens or passwords.</summary>
        public Action<string>? Log
        {
            get => _log;
            set
            {
                _log = value;
                if (_login != null)
                    _login.Log = value;
            }
        }

        /// <summary>Save/load the session file between runs. Default: true.</summary>
        public bool UseCache { get; set; } = true;

        /// <summary>
        /// Where the session is stored: $MAGISTER_SESSION_FILE, otherwise a "magister/session.json" in the
        /// user's config folder (~/.config on Linux). Never inside the project directory.
        /// </summary>
        public static string CachePath =>
            Environment.GetEnvironmentVariable("MAGISTER_SESSION_FILE")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "magister",
                "session.json"
            );

        public DataGetter(
            string school,
            string username,
            string password,
            string host = "pantarijn.magister.net"
        )
            : this(school, username, () => password, host) { }

        /// <summary>The password is only requested when a real login is needed, not when a cached session is used.</summary>
        public DataGetter(
            string school,
            string username,
            Func<string> passwordProvider,
            string host = "pantarijn.magister.net"
        )
        {
            _school = school;
            _username = username;
            _passwordProvider = passwordProvider;
            Host = host;
        }

        /// <summary>Deletes the saved session. Returns true if a file was removed.</summary>
        public static bool DeleteCache()
        {
            if (!File.Exists(CachePath))
                return false;
            File.Delete(CachePath);
            return true;
        }

        // -----------------------------------------------------------------
        // Connection
        // -----------------------------------------------------------------

        /// <summary>Logs in from scratch (fresh cookies, fresh token). Called automatically when needed.</summary>
        public async Task ConnectAsync()
        {
            var login = new MagisterLogin { Log = _log };
            await login.LoginAsync(_school, _username, _passwordProvider());
            await login.GetSchoolTokenAsync(Host);

            using JsonDocument cur = JsonDocument.Parse(
                await login.GetSchoolStringAsync(Host, "/api/sessions/current")
            );
            string accountId = LastSegment(
                cur.RootElement.GetProperty("links")
                    .GetProperty("account")
                    .GetProperty("href")
                    .GetString()
            );

            using JsonDocument acc = JsonDocument.Parse(
                await login.GetSchoolStringAsync(Host, "/api/accounts/" + accountId)
            );
            LeerlingId = LastSegment(
                acc.RootElement.GetProperty("links")
                    .GetProperty("leerling")
                    .GetProperty("href")
                    .GetString()
            );

            _login = login;
            _connectedAtUtc = DateTime.UtcNow;
            SaveCache(login.SchoolToken!);
        }

        /// <summary>Makes sure a valid session exists (loads the saved one or logs in). Called automatically by every data method.</summary>
        public async Task EnsureConnectedAsync()
        {
            if (_login != null && DateTime.UtcNow - _connectedAtUtc <= MaxTokenAge)
                return;
            if (_login == null && UseCache && TryLoadCache())
                return;
            await ConnectAsync();
        }

        // -----------------------------------------------------------------
        // Session file
        // -----------------------------------------------------------------

        private bool TryLoadCache()
        {
            try
            {
                if (!File.Exists(CachePath))
                    return false;
                var c = JsonSerializer.Deserialize<SessionCache>(File.ReadAllText(CachePath), Json);
                if (c?.SchoolToken == null || c.LeerlingId == null)
                    return false;

                // A session belongs to one school host + user
                if (
                    !string.Equals(c.Host, Host, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(c.Username, _username, StringComparison.OrdinalIgnoreCase)
                )
                {
                    _log?.Invoke("cached session is for another user/host, ignoring it");
                    return false;
                }

                TimeSpan age = DateTime.UtcNow - c.LastLoginUtc;
                if (age < TimeSpan.Zero || age > MaxTokenAge)
                {
                    _log?.Invoke(
                        "cached session is " + (int)age.TotalMinutes + " min old, logging in again"
                    );
                    return false;
                }

                var login = new MagisterLogin { Log = _log };
                login.UseSchoolToken(c.SchoolToken);
                _login = login;
                LeerlingId = c.LeerlingId;
                _connectedAtUtc = c.LastLoginUtc;
                _log?.Invoke("using cached session (" + (int)age.TotalMinutes + " min old)");
                return true;
            }
            catch (Exception ex)
            {
                _log?.Invoke(
                    "session file unreadable (" + ex.GetType().Name + "), logging in again"
                );
                return false;
            }
        }

        private void SaveCache(string schoolToken)
        {
            if (!UseCache)
                return;
            try
            {
                string path = CachePath;
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    if (OperatingSystem.IsWindows())
                        Directory.CreateDirectory(dir);
                    else
                        Directory.CreateDirectory(
                            dir,
                            UnixFileMode.UserRead
                                | UnixFileMode.UserWrite
                                | UnixFileMode.UserExecute
                        );
                }

                var cache = new SessionCache
                {
                    Host = Host,
                    Username = _username,
                    LastLoginUtc = _connectedAtUtc,
                    SchoolToken = schoolToken,
                    LeerlingId = LeerlingId,
                };
                string json = JsonSerializer.Serialize(
                    cache,
                    new JsonSerializerOptions { WriteIndented = true }
                );

                // Created owner-only (0600) from the start on Linux/macOS, so the token is never briefly world-readable
                var opts = new FileStreamOptions
                {
                    Mode = FileMode.Create,
                    Access = FileAccess.Write,
                };
                if (!OperatingSystem.IsWindows())
                    opts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var fs = new FileStream(path, opts))
                using (var w = new StreamWriter(fs))
                    w.Write(json);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

                _log?.Invoke("session saved");
            }
            catch (Exception ex)
            {
                // Failing to cache must never break the actual request
                _log?.Invoke("could not save session file (" + ex.GetType().Name + ")");
            }
        }

        private static string LastSegment(string? href)
        {
            if (string.IsNullOrEmpty(href))
                throw new InvalidOperationException("Expected a link in the API response.");
            return href.TrimEnd('/').Split('/')[^1];
        }

        // Accepts "/api/..." or a full https://<host>/api/... URL
        private string ToPath(string pathOrUrl)
        {
            if (pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                var u = new Uri(pathOrUrl);
                if (!string.Equals(u.Host, Host, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException(
                        "Refusing to send the token to another host: " + u.Host
                    );
                return u.PathAndQuery;
            }
            return pathOrUrl.StartsWith("/") ? pathOrUrl : "/" + pathOrUrl;
        }

        private async Task<T> WithRetryAsync<T>(Func<MagisterLogin, Task<T>> call)
        {
            await EnsureConnectedAsync();
            try
            {
                return await call(_login!);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                _log?.Invoke("401 from API, logging in again");
                await ConnectAsync();
                return await call(_login!);
            }
        }

        // -----------------------------------------------------------------
        // Raw access (useful for endpoints that don't have a typed method yet)
        // -----------------------------------------------------------------

        public Task<string> GetRawAsync(string pathOrUrl)
        {
            string path = ToPath(pathOrUrl);
            return WithRetryAsync(l => l.GetSchoolStringAsync(Host, path));
        }

        public Task<byte[]> DownloadAsync(string pathOrUrl)
        {
            string path = ToPath(pathOrUrl);
            return WithRetryAsync(l => l.GetSchoolBytesAsync(Host, path));
        }

        private async Task<List<T>> GetListAsync<T>(string path)
        {
            string json = await GetRawAsync(path);
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            // Endpoints answer with {"Items":[...]} or {"items":[...]}; be tolerant about casing
            foreach (JsonProperty p in root.EnumerateObject())
                if (string.Equals(p.Name, "items", StringComparison.OrdinalIgnoreCase))
                    return JsonSerializer.Deserialize<List<T>>(p.Value.GetRawText(), Json)
                        ?? new List<T>();

            return new List<T>();
        }

        private async Task<string> LeerlingAsync()
        {
            await EnsureConnectedAsync();
            return LeerlingId!;
        }

        // -----------------------------------------------------------------
        // Typed data
        // -----------------------------------------------------------------

        /// <summary>Appointments (lessons etc.) between two dates, inclusive of both days.</summary>
        public async Task<List<Afspraak>> GetAfsprakenAsync(DateTime from, DateTime to)
        {
            string id = await LeerlingAsync();
            string path =
                "/api/personen/"
                + id
                + "/afspraken?status=1&van="
                + from.ToString("yyyy-MM-dd")
                + "&tot="
                + to.ToString("yyyy-MM-dd");
            var list = await GetListAsync<Afspraak>(path);
            list.Sort((a, b) => a.Start.CompareTo(b.Start));
            return list;
        }

        /// <summary>Messages in the inbox, newest first as the server returns them.</summary>
        public Task<List<Bericht>> GetBerichtenAsync(int top = 40, int skip = 0) =>
            GetListAsync<Bericht>(
                "/api/berichten/postvakin/berichten?top=" + top + "&skip=" + skip
            );

        /// <summary>Full message as raw JSON (the body lives inside it).</summary>
        public Task<string> GetBerichtJsonAsync(Bericht b) => GetRawAsync(b.SelfHref);

        /// <summary>Most recent grades.</summary>
        public async Task<List<Cijfer>> GetCijfersAsync(int top = 25, int skip = 0)
        {
            string id = await LeerlingAsync();
            return await GetListAsync<Cijfer>(
                "/api/personen/" + id + "/cijfers/laatste?top=" + top + "&skip=" + skip
            );
        }

        /// <summary>Study guides active around a date (default: today).</summary>
        public async Task<List<Studiewijzer>> GetStudiewijzersAsync(DateTime? pijldatum = null)
        {
            string id = await LeerlingAsync();
            string date = (pijldatum ?? DateTime.Today).ToString("yyyy-MM-dd"); // note: MM = month
            return await GetListAsync<Studiewijzer>(
                "/api/leerlingen/" + id + "/studiewijzers?pijldatum=" + date
            );
        }

        public async Task<StudiewijzerDetail?> GetStudiewijzerAsync(Studiewijzer s)
        {
            string json = await GetRawAsync(s.SelfHref);
            return JsonSerializer.Deserialize<StudiewijzerDetail>(json, Json);
        }

        /// <summary>One appointment by id. Tries the single-item endpoint, falls back to searching a date window.</summary>
        public async Task<Afspraak?> GetAfspraakAsync(int afspraakId, DateTime? around = null)
        {
            string id = await LeerlingAsync();
            try
            {
                string json = await GetRawAsync("/api/personen/" + id + "/afspraken/" + afspraakId);
                return JsonSerializer.Deserialize<Afspraak>(json, Json);
            }
            catch (HttpRequestException)
            {
                DateTime d = around ?? DateTime.Today;
                var list = await GetAfsprakenAsync(d.AddDays(-30), d.AddDays(60));
                return list.Find(a => a.Id == afspraakId);
            }
        }

        /// <summary>One study guide by id, from the list of guides around a date.</summary>
        public async Task<Studiewijzer?> FindStudiewijzerAsync(
            int studiewijzerId,
            DateTime? pijldatum = null
        )
        {
            var list = await GetStudiewijzersAsync(pijldatum);
            return list.Find(s => s.Id == studiewijzerId);
        }

        /// <summary>One inbox message by id, paging through the inbox until it is found.</summary>
        public async Task<Bericht?> FindBerichtAsync(int berichtId, int maxMessages = 500)
        {
            const int page = 100;
            for (int skip = 0; skip < maxMessages; skip += page)
            {
                var list = await GetBerichtenAsync(page, skip);
                if (list.Count == 0)
                    break;
                var hit = list.Find(b => b.Id == berichtId);
                if (hit != null)
                    return hit;
                if (list.Count < page)
                    break;
            }
            return null;
        }

        /// <summary>
        /// Downloads the file of a study-guide resource. Tries its links (Contents/Download/Self), then Uri.
        /// If a link returns JSON metadata instead of the file, follows a link or uri inside that JSON once.
        /// Returns null when the resource has no link at all; throws the last HTTP error when every link failed.
        /// </summary>
        public async Task<byte[]?> DownloadBronAsync(Bron b)
        {
            var candidates = new List<string>();
            foreach (string rel in new[] { "Contents", "Content", "Download", "Self" })
            {
                var link = b.Links?.Find(l =>
                    string.Equals(l.Rel, rel, StringComparison.OrdinalIgnoreCase)
                );
                if (!string.IsNullOrEmpty(link?.Href) && !candidates.Contains(link!.Href!))
                    candidates.Add(link.Href!);
            }
            if (!string.IsNullOrEmpty(b.Uri) && !candidates.Contains(b.Uri!))
                candidates.Add(b.Uri!);
            if (b.Links != null)
                foreach (var l in b.Links)
                    if (!string.IsNullOrEmpty(l.Href) && !candidates.Contains(l.Href!))
                        candidates.Add(l.Href!);

            bool wantsJson = (b.ContentType ?? "").Contains(
                "json",
                StringComparison.OrdinalIgnoreCase
            );
            Exception? lastError = null;

            foreach (string href in candidates)
            {
                byte[] data;
                try
                {
                    data = await DownloadAsync(href);
                }
                catch (HttpRequestException ex)
                {
                    _log?.Invoke("download failed for " + href + ": " + ex.Message);
                    lastError = ex;
                    continue;
                }

                // A '{' at the start of a non-JSON file means we got metadata about the file
                if (!wantsJson && data.Length > 0 && data[0] == (byte)'{')
                {
                    string? next = null;
                    try
                    {
                        using JsonDocument doc = JsonDocument.Parse(data);
                        foreach (JsonProperty p in doc.RootElement.EnumerateObject())
                        {
                            if (
                                string.Equals(p.Name, "uri", StringComparison.OrdinalIgnoreCase)
                                && p.Value.ValueKind == JsonValueKind.String
                                && !string.IsNullOrEmpty(p.Value.GetString())
                            )
                                next = p.Value.GetString();

                            if (
                                string.Equals(p.Name, "links", StringComparison.OrdinalIgnoreCase)
                                && p.Value.ValueKind == JsonValueKind.Array
                            )
                            {
                                foreach (JsonElement le in p.Value.EnumerateArray())
                                {
                                    string? rel = null,
                                        h = null;
                                    foreach (JsonProperty lp in le.EnumerateObject())
                                    {
                                        if (
                                            string.Equals(
                                                lp.Name,
                                                "rel",
                                                StringComparison.OrdinalIgnoreCase
                                            )
                                        )
                                            rel = lp.Value.GetString();
                                        if (
                                            string.Equals(
                                                lp.Name,
                                                "href",
                                                StringComparison.OrdinalIgnoreCase
                                            )
                                        )
                                            h = lp.Value.GetString();
                                    }
                                    if (
                                        h != null
                                        && h != href
                                        && rel != null
                                        && (
                                            rel.Equals(
                                                "Contents",
                                                StringComparison.OrdinalIgnoreCase
                                            )
                                            || rel.Equals(
                                                "Download",
                                                StringComparison.OrdinalIgnoreCase
                                            )
                                        )
                                    )
                                        next = h;
                                }
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        next = null; // not JSON after all: treat the bytes as the file
                    }

                    if (next != null)
                    {
                        _log?.Invoke("following " + next);
                        try
                        {
                            return await DownloadAsync(next);
                        }
                        catch (HttpRequestException ex)
                        {
                            _log?.Invoke("download failed for " + next + ": " + ex.Message);
                            lastError = ex;
                            continue;
                        }
                    }
                }

                return data;
            }

            if (lastError != null)
                throw lastError;
            return null;
        }

        public async Task<OnderdeelDetail?> GetOnderdeelAsync(string href)
        {
            string json = await GetRawAsync(href);
            return JsonSerializer.Deserialize<OnderdeelDetail>(json, Json);
        }
    }
}
