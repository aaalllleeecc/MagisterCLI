using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MagisterLoginDemo
{
    // Example CLI on top of DataGetter.
    //
    //   dotnet run -- <school> <username> [command] [n]
    //
    //   commands:  afspraken [days=7]   appointments from today (first column is the id)
    //              afspraak <id>        one appointment in full
    //              berichten [count=20] inbox (first column is the id)
    //              bericht <id>         one message in full (body, recipients, attachments)
    //              bijlage <id> [nr]    download the attachments of a message (all, or one by its [number])
    //              cijfers   [count=25] latest grades
    //              studiewijzers        study guides around today (first column is the id)
    //              studiewijzer <id> [onderdeel]   one study guide (or just one onderdeel, by its [number])
    //              download <id> <onderdeel> [bron]    download the files of an onderdeel (all, or one by its [number])
    //              raw </api/path>      print the raw JSON of any endpoint
    //              logout               delete the saved session file
    //              serve [port=5075]    local HTTP/JSON API for frontends (see MagisterServer.cs)
    //              help                 show all commands (also -h, --help)
    //
    //   MAGISTER_PASSWORD=...  (otherwise you are asked for it)
    //   MAGISTER_HOST=...      (default pantarijn.magister.net)
    //   MAGISTER_VERBOSE=1     print login diagnostics (incl. whether a saved session was used)
    //   MAGISTER_SESSION_FILE=...  where the session is saved (default ~/.config/magister/session.json)
    //   MAGISTER_DOWNLOAD_DIR=...  where downloads are saved (default ./downloads)
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            if ((args.Length >= 1 && IsHelp(args[0])) || (args.Length > 2 && IsHelp(args[2])))
            {
                Console.WriteLine(HelpText);
                return 0;
            }

            // "serve help" without school/username
            if (args.Length >= 2 && args[0].Equals("serve", StringComparison.OrdinalIgnoreCase) && IsHelp(args[1]))
            {
                Console.WriteLine(ServeHelpText);
                return 0;
            }

            if (args.Length < 2)
            {
                Console.Error.WriteLine(HelpText);
                return 1;
            }

            string school = args[0];
            string username = args[1];
            string command = args.Length > 2 ? args[2].ToLowerInvariant() : "afspraken";
            string? arg3 = args.Length > 3 ? args[3] : null;
            int n = arg3 != null && int.TryParse(arg3, out int parsed) ? parsed : -1;
            int n2 = args.Length > 4 && int.TryParse(args[4], out int p2) ? p2 : -1;   // onderdeel number
            int n3 = args.Length > 5 && int.TryParse(args[5], out int p3) ? p3 : -1;   // bron number
            bool verbose = Environment.GetEnvironmentVariable("MAGISTER_VERBOSE") == "1";

            string host = Environment.GetEnvironmentVariable("MAGISTER_HOST") ?? "pantarijn.magister.net";

            // The password is only asked for when no valid saved session exists
            // Remembered in memory only, so a long-running `serve` can log in again without prompting
            string? password = null;
            var mg = new DataGetter(school, username,
                () => password ??= Environment.GetEnvironmentVariable("MAGISTER_PASSWORD") ?? ReadPassword(), host);
            if (Environment.GetEnvironmentVariable("MAGISTER_VERBOSE") == "1")
                mg.Log = Console.Error.WriteLine;

            try
            {
                switch (command)
                {
                    case "serve":
                        if (arg3 != null && IsHelp(arg3))
                        {
                            Console.WriteLine(ServeHelpText);
                            return 0;
                        }
                        // Log in first, so a password prompt or login error shows up here, before listening
                        await mg.EnsureConnectedAsync();
                        await MagisterServer.RunAsync(mg, n > 0 ? n : 5075);
                        break;

                    case "logout":
                        Console.WriteLine(DataGetter.DeleteCache() ? "Saved session deleted." : "No saved session.");
                        break;

                    case "afspraken":
                        int days = n > 0 ? n : 7;
                        foreach (var a in await mg.GetAfsprakenAsync(DateTime.Today, DateTime.Today.AddDays(days - 1)))
                            Console.WriteLine($"{a.Id,-9} {a.StartLocal:ddd dd-MM HH:mm}  {a.Omschrijving,-28} {a.Lokatie}");
                        break;

                    case "afspraak":
                    {
                        if (n <= 0) { Console.Error.WriteLine("Usage: ... afspraak <id>"); return 1; }
                        var a = await mg.GetAfspraakAsync(n);
                        if (a == null) { Console.Error.WriteLine("Appointment not found."); return 1; }

                        Console.WriteLine($"Id:           {a.Id}");
                        Console.WriteLine($"Omschrijving: {a.Omschrijving}");
                        Console.WriteLine($"Tijd:         {a.StartLocal:ddd dd-MM-yyyy HH:mm} - {a.EindeLocal:HH:mm}" +
                                          (a.DuurtHeleDag ? " (hele dag)" : ""));
                        Console.WriteLine($"Lesuur:       {a.LesuurVan}-{a.LesuurTotMet}");
                        Console.WriteLine($"Lokatie:      {a.Lokatie}");
                        Console.WriteLine($"Vakken:       {string.Join(", ", a.Vakken?.ConvertAll(v => v.Naam) ?? new List<string?>())}");
                        Console.WriteLine($"Docenten:     {string.Join(", ", a.Docenten?.ConvertAll(d => d.Naam) ?? new List<string?>())}");
                        Console.WriteLine($"Status/Type:  {a.Status}/{a.Type}   Bijlagen: {a.HeeftBijlagen}");
                        if (!string.IsNullOrWhiteSpace(a.Opmerking))
                            Console.WriteLine("\nOpmerking:\n" + StripHtml(a.Opmerking));
                        if (!string.IsNullOrWhiteSpace(a.Inhoud))
                            Console.WriteLine("\nInhoud:\n" + StripHtml(a.Inhoud));
                        if (a.Links != null)
                            foreach (var l in a.Links)
                                Console.WriteLine($"  link {l.Rel}: {l.Href}");
                        break;
                    }

                    case "berichten":
                        foreach (var b in await mg.GetBerichtenAsync(n > 0 ? n : 20))
                            Console.WriteLine($"{b.Id,-9} {b.VerzondenOp.ToLocalTime():dd-MM HH:mm}  {b.Afzender?.Naam,-24} {b.Onderwerp}");
                        break;

                    case "bericht":
                    {
                        if (n <= 0) { Console.Error.WriteLine("Usage: ... bericht <id>"); return 1; }
                        var bm = await mg.FindBerichtAsync(n);
                        if (bm == null) { Console.Error.WriteLine("Message not found in the inbox."); return 1; }

                        using JsonDocument doc = JsonDocument.Parse(await mg.GetBerichtJsonAsync(bm));
                        JsonElement root = doc.RootElement;

                        Console.WriteLine($"Id:        {bm.Id}");
                        Console.WriteLine($"Onderwerp: {bm.Onderwerp}");
                        Console.WriteLine($"Van:       {bm.Afzender?.Naam}");
                        Console.WriteLine($"Verzonden: {bm.VerzondenOp.ToLocalTime():ddd dd-MM-yyyy HH:mm}");
                        string aan = NamesOf(Prop(root, "ontvangers"));
                        string cc = NamesOf(Prop(root, "kopieOntvangers"));
                        if (aan != "") Console.WriteLine($"Aan:       {aan}");
                        if (cc != "") Console.WriteLine($"Cc:        {cc}");

                        if (bm.HeeftBijlagen)
                        {
                            var atts = await GetBijlagenAsync(mg, bm.Id);
                            if (atts.Count > 0)
                            {
                                Console.WriteLine("Bijlagen:");
                                int ai = 0;
                                foreach (JsonElement att in atts)
                                {
                                    long size = Prop(att, "grootte")?.GetInt64() ?? 0;
                                    Console.WriteLine($"  - [{ai++}] {Prop(att, "naam")?.GetString()} ({Prop(att, "contentType")?.GetString()}, {size / 1024} KB)");
                                    if (verbose) Console.WriteLine("        raw: " + att.GetRawText());
                                }
                            }
                        }

                        string? body = Prop(root, "inhoud")?.GetString();
                        if (!string.IsNullOrWhiteSpace(body))
                            Console.WriteLine("\n" + StripHtml(body));
                        else
                            Console.WriteLine("\n(no 'inhoud' field found, raw JSON follows)\n" + root.GetRawText());
                        break;
                    }

                    case "bijlage":
                    {
                        if (n <= 0) { Console.Error.WriteLine("Usage: ... bijlage <berichtId> [nr]"); return 1; }
                        var bm = await mg.FindBerichtAsync(n);
                        if (bm == null) { Console.Error.WriteLine("Message not found in the inbox."); return 1; }

                        var atts = await GetBijlagenAsync(mg, bm.Id);
                        if (atts.Count == 0) { Console.Error.WriteLine("This message has no attachments."); return 1; }

                        string dir = Environment.GetEnvironmentVariable("MAGISTER_DOWNLOAD_DIR") ?? "downloads";
                        Directory.CreateDirectory(dir);

                        int i = -1;
                        bool any = false;
                        foreach (JsonElement item in atts)
                        {
                            i++;
                            if (n2 >= 0 && i != n2) continue;
                            any = true;

                            string name = Prop(item, "naam")?.GetString() ?? Prop(item, "name")?.GetString() ?? ("bijlage-" + i);
                            byte[]? data = null;
                            Exception? last = null;
                            foreach (string href in HrefsOf(item))
                            {
                                try
                                {
                                    byte[] d = await mg.DownloadAsync(href);
                                    // JSON metadata instead of the file: try the next link
                                    if (d.Length > 0 && d[0] == (byte)'{' && !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                                    data = d;
                                    break;
                                }
                                catch (HttpRequestException ex) { last = ex; }
                            }

                            if (data == null)
                            {
                                Console.Error.WriteLine($"Could not download {name}: {last?.Message ?? "no usable link"}");
                                Console.Error.WriteLine("  raw: " + item.GetRawText());
                                continue;
                            }
                            string file = Path.Combine(dir, SafeFileName(name));
                            await File.WriteAllBytesAsync(file, data);
                            Console.WriteLine($"Saved {file} ({data.Length / 1024} KB)");
                        }
                        if (!any) { Console.Error.WriteLine("No attachment with that number."); return 1; }
                        break;
                    }

                    case "cijfers":
                        foreach (var c in await mg.GetCijfersAsync(n > 0 ? n : 25))
                            Console.WriteLine($"{c.Vak?.Omschrijving,-28} {c.Omschrijving,-24} {c.Waarde}");
                        break;

                    case "studiewijzers":
                        foreach (var s in await mg.GetStudiewijzersAsync())
                            Console.WriteLine($"{s.Id,-9} {s.Titel}  ({string.Join(",", s.VakCodes ?? new List<string>())})");
                        break;

                    case "studiewijzer":
                    {
                        if (n <= 0) { Console.Error.WriteLine("Usage: ... studiewijzer <id> [onderdeel]"); return 1; }
                        var sw = await mg.FindStudiewijzerAsync(n);
                        if (sw == null) { Console.Error.WriteLine("Study guide not found (is it active around today?)."); return 1; }

                        var detail = await mg.GetStudiewijzerAsync(sw);
                        Console.WriteLine($"{sw.Titel}  ({sw.Van:dd-MM-yyyy} - {sw.TotEnMet:dd-MM-yyyy})");
                        bool shown = false;
                        foreach (var o in detail?.Onderdelen?.Items ?? new List<Onderdeel>())
                        {
                            if (n2 >= 0 && o.Volgnummer != n2) continue;
                            shown = true;
                            Console.WriteLine($"\n[{o.Volgnummer}] {o.Titel}{(o.IsZichtbaar ? "" : "  (verborgen)")}");
                            var od = await mg.GetOnderdeelAsync(o.SelfHref);
                            if (verbose)
                            {
                                Console.WriteLine($"    onderdeel href: {o.SelfHref}");
                                Console.WriteLine("    raw: " + await mg.GetRawAsync(o.SelfHref));
                            }
                            string text = od?.Omschrijving ?? o.Omschrijving ?? "";
                            if (!string.IsNullOrWhiteSpace(text))
                                Console.WriteLine("    " + StripHtml(text).Replace("\n", "\n    "));
                            int bi = 0;
                            foreach (var bron in od?.Bronnen ?? new List<Bron>())
                            {
                                Console.WriteLine($"    - [{bi++}] {bron.Naam} ({bron.ContentType}, {bron.Grootte / 1024} KB)");
                                if (verbose && bron.Links != null)
                                    foreach (var l in bron.Links) Console.WriteLine($"        link {l.Rel}: {l.Href}");
                            }
                        }
                        if (n2 >= 0 && !shown) { Console.Error.WriteLine("No onderdeel with that number."); return 1; }
                        break;
                    }

                    case "download":
                    {
                        if (n <= 0 || n2 < 0) { Console.Error.WriteLine("Usage: ... download <studiewijzerId> <onderdeel> [bron]"); return 1; }
                        var sw = await mg.FindStudiewijzerAsync(n);
                        if (sw == null) { Console.Error.WriteLine("Study guide not found (is it active around today?)."); return 1; }

                        var detail = await mg.GetStudiewijzerAsync(sw);
                        var onderdeel = detail?.Onderdelen?.Items?.Find(x => x.Volgnummer == n2);
                        if (onderdeel == null) { Console.Error.WriteLine("No onderdeel with that number."); return 1; }

                        var od = await mg.GetOnderdeelAsync(onderdeel.SelfHref);
                        var bronnen = od?.Bronnen ?? new List<Bron>();
                        var chosen = new List<Bron>();
                        if (n3 >= 0) { if (n3 < bronnen.Count) chosen.Add(bronnen[n3]); }
                        else chosen = bronnen;
                        if (chosen.Count == 0) { Console.Error.WriteLine("No files to download."); return 1; }

                        string dir = Environment.GetEnvironmentVariable("MAGISTER_DOWNLOAD_DIR") ?? "downloads";
                        Directory.CreateDirectory(dir);
                        foreach (var bron in chosen)
                        {
                            byte[]? data;
                            try { data = await mg.DownloadBronAsync(bron); }
                            catch (Exception ex)
                            {
                                Console.Error.WriteLine($"Could not download {bron.Naam}: {ex.Message}");
                                continue;
                            }
                            if (data == null)
                            {
                                Console.Error.WriteLine($"Could not download {bron.Naam}: the resource has no link.");
                                continue;
                            }
                            string file = Path.Combine(dir, SafeFileName(bron.Naam));
                            await File.WriteAllBytesAsync(file, data);
                            Console.WriteLine($"Saved {file} ({data.Length / 1024} KB)");
                        }
                        break;
                    }

                    case "raw":
                        if (arg3 == null) { Console.Error.WriteLine("Usage: ... raw </api/path>"); return 1; }
                        Console.WriteLine(await mg.GetRawAsync(arg3));
                        break;

                    default:
                        Console.Error.WriteLine("Unknown command: " + command + "  (run with 'help' to list the commands)");
                        return 1;
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Failed: " + ex.Message);
                return 2;
            }
        }

        // Case-insensitive property lookup; null if absent or not an object
        private static JsonElement? Prop(JsonElement? e, string name)
        {
            if (e is not { ValueKind: JsonValueKind.Object }) return null;
            foreach (JsonProperty p in e.Value.EnumerateObject())
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
            return null;
        }

        // Joins the "naam" of every entry in a recipient array
        private static string NamesOf(JsonElement? arr)
        {
            if (arr is not { ValueKind: JsonValueKind.Array }) return "";
            var names = new List<string>();
            foreach (JsonElement r in arr.Value.EnumerateArray())
            {
                string? nm = Prop(r, "weergavenaam")?.GetString() ?? Prop(r, "naam")?.GetString();
                if (!string.IsNullOrEmpty(nm)) names.Add(nm);
            }
            return string.Join(", ", names);
        }

        // Attachments of a message live on their own endpoint: /api/berichten/berichten/{id}/bijlagen
        // -> {"items":[{"id":..,"naam":..,"contentType":..,"grootte":..,"links":{"self":{..},"download":{"href":..}}}]}
        private static async Task<List<JsonElement>> GetBijlagenAsync(DataGetter mg, int berichtId)
        {
            string json = await mg.GetRawAsync("/api/berichten/berichten/" + berichtId + "/bijlagen");
            using JsonDocument doc = JsonDocument.Parse(json);
            var list = new List<JsonElement>();
            JsonElement? items = Prop(doc.RootElement, "items");
            if (items is { ValueKind: JsonValueKind.Array })
                foreach (JsonElement i in items.Value.EnumerateArray()) list.Add(i.Clone());
            return list;
        }

        // Every href inside a JSON element (links may be an array of {rel,href} or an object of {self:{href}}),
        // ordered so that Contents/Download links come before Self, then anything else
        private static List<string> HrefsOf(JsonElement root)
        {
            var found = new List<(int rank, string href)>();

            void Walk(JsonElement x, string? parentName)
            {
                if (x.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement i in x.EnumerateArray()) Walk(i, parentName);
                }
                else if (x.ValueKind == JsonValueKind.Object)
                {
                    string? rel = Prop(x, "rel")?.GetString() ?? parentName;
                    foreach (JsonProperty p in x.EnumerateObject())
                    {
                        if (string.Equals(p.Name, "href", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                        {
                            string r = (rel ?? "").ToLowerInvariant();
                            int rank = r is "contents" or "content" or "download" ? 0 : r == "self" ? 1 : 2;
                            found.Add((rank, p.Value.GetString()!));
                        }
                        else Walk(p.Value, p.Name);
                    }
                }
            }

            Walk(root, null);
            found.Sort((a, b) => a.rank.CompareTo(b.rank));
            var result = new List<string>();
            foreach (var f in found) if (!result.Contains(f.href)) result.Add(f.href);
            return result;
        }

        private static string SafeFileName(string? name)
        {
            string f = Path.GetFileName(name ?? "download");
            foreach (char c in Path.GetInvalidFileNameChars()) f = f.Replace(c, '_');
            return string.IsNullOrWhiteSpace(f) ? "download" : f;
        }

        private static bool IsHelp(string a) =>
            a.Equals("help", StringComparison.OrdinalIgnoreCase) || a == "-h" || a == "--help" || a == "/?";

        private const string HelpText = @"Magister CLI

Usage:
  dotnet run -- <school> <username> [command] [arguments]
  dotnet run -- help

  <school>    school name as shown on the login page, e.g. ""RSG Pantarijn""
  <username>  your Magister username

Commands (default: afspraken):
  afspraken [days=7]                 appointments from today. First column is the id.
  afspraak <id>                      one appointment in full (time, teachers, homework, remarks, links)

  berichten [count=20]               inbox. First column is the id.
  bericht <id>                       one message in full (sender, recipients, body, attachments)
  bijlage <id> [nr]                  download the attachments of a message: all, or only attachment [nr]

  cijfers [count=25]                 latest grades

  studiewijzers                      study guides active around today. First column is the id.
  studiewijzer <id> [onderdeel]      one study guide with its onderdelen and files, or only onderdeel [number]
  download <id> <onderdeel> [bron]   download the files of an onderdeel: all, or only file [bron]

  raw </api/path>                    print the raw JSON of any school API path
  logout                             delete the saved session file
  serve [port=5075]                  start the local HTTP/JSON API for frontends (see serve help)
  help                               show this text (also: -h, --help)

Numbers: onderdelen and files are numbered in the output of 'studiewijzer', attachments in the output of 'bericht'.
Downloads are saved in ./downloads (or $MAGISTER_DOWNLOAD_DIR).

Examples:
  dotnet run -- ""RSG Pantarijn""(of jouw school) <leerling nummer> afspraken 14
  dotnet run -- ""RSG Pantarijn""(of jouw school) <leerling nummer> studiewijzer 8890 2
  dotnet run -- ""RSG Pantarijn""(of jouw school) <leerling nummer> download 8890 1 0
  dotnet run -- ""RSG Pantarijn""(of jouw school) <leerling nummer> bijlage 6366174
  dotnet run -- ""RSG Pantarijn""(of jouw school) <leerling nummer> raw /api/personen/<id>/afspraken/123456

Environment variables:
  MAGISTER_PASSWORD      password (otherwise you are asked for it, only when no saved session is valid)
  MAGISTER_HOST          school web host (default pantarijn.magister.net)
  MAGISTER_VERBOSE=1     print login diagnostics, raw JSON of onderdelen/attachments, and redirects
  MAGISTER_SESSION_FILE  where the session is saved (default ~/.config/magister/session.json)
  MAGISTER_DOWNLOAD_DIR  where downloads are saved (default ./downloads)
  MAGISTER_SCOPE         override the OIDC scope used for the school token
  serve only:  MAGISTER_API_KEY, MAGISTER_BIND, MAGISTER_ALLOW_ORIGIN";

        private const string ServeHelpText = @"Magister serve: local HTTP/JSON API for frontends

Usage:
  dotnet run -- <school> <username> serve [port=5075]
  dotnet run -- <school> <username> serve help

serve logs in first (asking for the password if no saved session is valid), then listens on
http://127.0.0.1:<port>. Press Ctrl+C to stop. It handles one request at a time.

Authentication:
  Every endpoint except /health needs the header   Authorization: Bearer <api key>
  The key is $MAGISTER_API_KEY, or a random one that is printed at startup when that is not set.

Endpoints (all GET unless noted):
  /health                                  no key needed; {""status"":""ok""}

  /api/afspraken?days=7                    appointments from today
  /api/afspraken?van=2026-10-01&tot=2026-10-07   ... or between two dates
  /api/afspraken/{id}                      one appointment in full (?around=2026-10-01 widens the fallback search)

  /api/berichten?top=40&skip=0             inbox
  /api/berichten/{id}                      one message: { message: <full JSON incl. body>, bijlagen: <attachment list> }

  /api/cijfers?top=25&skip=0               latest grades

  /api/studiewijzers?datum=2026-10-01      study guides around a date (default today)
  /api/studiewijzers/{id}?datum=...        one study guide with all onderdelen and their files (bronnen)

  /api/raw?path=/api/...                   any school API path, raw JSON passthrough
  /api/download?path=/api/...&name=x.pdf&type=application/pdf
                                           file bytes (name and type are optional)
  POST /api/logout                         delete the saved session file

Downloading a file: take its path from the JSON and pass it to /api/download.
  study guide file     bronnen[].downloadPath               from /api/studiewijzers/{id}
  message attachment   bijlagen.items[].links.download.href  from /api/berichten/{id}
  Use the naam and contentType of the same entry for name= and type=.

Errors: 400 bad request, 401 missing or wrong key, 404 not found, 502 the school API answered with an error
(the body has upstreamStatus), 500 anything else. Error bodies look like {""error"":""...""}.

Examples:
  KEY=<your key>
  curl -H ""Authorization: Bearer $KEY"" localhost:5075/api/afspraken?days=3
  curl -H ""Authorization: Bearer $KEY"" localhost:5075/api/studiewijzers/8890
  curl -H ""Authorization: Bearer $KEY"" -o file.pdf 'localhost:5075/api/download?path=/api/berichten/bijlagen/436406/download'

Environment variables:
  MAGISTER_API_KEY       fixed API key (otherwise random, printed at startup)
  MAGISTER_BIND          address to listen on (default 127.0.0.1, this machine only)
  MAGISTER_ALLOW_ORIGIN  comma-separated browser origins allowed by CORS
                         (default: http://localhost:3000 and :5173, http://127.0.0.1:3000 and :5173; * allows any)

Security:
  By default only this machine can connect, and requests whose Host header is not local are rejected.
  If you set MAGISTER_BIND to another address, anyone who can reach the port and has the key can read this account.
  /api/raw and /api/download only talk to your school host. Do not share the API key.";

        private static string StripHtml(string html)
        {
            string s = Regex.Replace(html, @"<(br|/p|/li|/div)\s*/?>", "\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, "<[^>]+>", "");
            return WebUtility.HtmlDecode(s).Trim();
        }

        private static string ReadPassword()
        {
            Console.Error.Write("Password: ");
            var sb = new StringBuilder();
            while (true)
            {
                ConsoleKeyInfo k = Console.ReadKey(intercept: true);
                if (k.Key == ConsoleKey.Enter) break;
                if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; }
                else if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
            }
            Console.Error.WriteLine();
            return sb.ToString();
        }
    }
}