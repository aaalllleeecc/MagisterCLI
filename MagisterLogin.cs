using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;

namespace MagisterLoginDemo
{
    internal sealed class MagisterLogin
    {
        private const string Host = "https://accounts.magister.net";
        private const string Entra = "https://login.microsoftonline.com";

        /// <summary>Optional diagnostics sink (for example Console.WriteLine). Never receives tokens or passwords.</summary>
        public Action<string>? Log { get; set; }

        private readonly CookieContainer _jar = new CookieContainer();
        private readonly HttpClient _http;

        public MagisterLogin()
        {
            _http = new HttpClient(
                new HttpClientHandler
                {
                    CookieContainer = _jar,
                    UseCookies = true,
                    AllowAutoRedirect = false, // every Location header must be inspected manually
                }
            );
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (X11; Linux x86_64; rv:143.0) Gecko/20100101 Firefox/143.0"
            );
        }

        public async Task<string> LoginAsync(string schoolQuery, string username, string password)
        {
            // 0) Start the OIDC request (client iam-profile). This part was NOT in the HAR:
            //    the redirect to the login page is assumed to carry sessionId/returnUrl/authCode as query params.
            string state = Guid.NewGuid().ToString("N");
            string nonce = Guid.NewGuid().ToString("N");
            string authUrl =
                Host
                + "/connect/authorize?client_id=iam-profile"
                + "&redirect_uri="
                + Uri.EscapeDataString(Host + "/profile/oidc/redirect_callback.html")
                + "&response_type="
                + Uri.EscapeDataString("id_token token")
                + "&scope="
                + Uri.EscapeDataString("openid profile email magister.iam.profile")
                + "&state="
                + state
                + "&nonce="
                + nonce;

            Uri? loginLoc = await FollowUntilLoginPage(new Uri(authUrl));
            if (loginLoc == null)
                throw new InvalidOperationException("Did not get redirected to a login page.");

            var q = HttpUtility.ParseQueryString(loginLoc.Query);
            string sessionId =
                q["sessionId"]
                ?? throw new InvalidOperationException("No sessionId in " + loginLoc);
            string returnUrl = DecodeUri(
                q["returnUrl"] ?? throw new InvalidOperationException("No returnUrl in " + loginLoc)
            );
            string authCode = q["authCode"] ?? await FindAuthCode(loginLoc);

            // 1) tenant search + select
            string search = await _http.GetStringAsync(
                Host
                    + "/challenges/tenant/search?sessionId="
                    + sessionId
                    + "&key="
                    + Uri.EscapeDataString(schoolQuery)
            );
            using var searchDoc = JsonDocument.Parse(search);
            var tenants = new List<(string name, string id)>();
            foreach (JsonElement t in searchDoc.RootElement.EnumerateArray())
                tenants.Add(
                    (
                        t.GetProperty("displayName").GetString() ?? "",
                        t.GetProperty("id").GetString()!
                    )
                );

            // Prefer an exact (case-insensitive) display-name match, otherwise accept a single result
            var exact = tenants.FindAll(t =>
                string.Equals(t.name, schoolQuery, StringComparison.OrdinalIgnoreCase)
            );
            if (exact.Count == 1)
                tenants = exact;
            if (tenants.Count != 1)
                throw new InvalidOperationException(
                    "School query '"
                        + schoolQuery
                        + "' matched "
                        + tenants.Count
                        + " tenants: "
                        + string.Join(", ", tenants.ConvertAll(t => t.name))
                        + ". Pass a more specific name, e.g. \"RSG Pantarijn\"."
                );
            string tenantId = tenants[0].id;

            await PostJson(
                "/challenges/tenant",
                new
                {
                    sessionId,
                    returnUrl,
                    authCode,
                    tenant = tenantId,
                    m_ChT = 8000,
                    m_ChF = 0,
                }
            );

            // 2) username -> externalidp redirect (school is federated to Microsoft Entra)
            JsonElement u = await PostJson(
                "/challenges/username",
                new
                {
                    sessionId,
                    returnUrl,
                    authCode,
                    username,
                    m_ChT = 1200,
                    m_ChF = 0,
                }
            );
            if (u.GetProperty("action").GetString() != "externalidp")
                throw new NotSupportedException(
                    "Tenant is not federated, different flow needed: " + u
                );

            var redirectUrl = new Uri(u.GetProperty("redirectURL").GetString()!);
            string upn = HttpUtility.ParseQueryString(redirectUrl.Query)["login_hint"]!;

            // 3) Entra sign-in page (follow redirects, then parse $Config)
            (HttpResponseMessage page, Uri pageUrl) = await GetFollowing(redirectUrl);
            JsonElement cfg = ParseConfig(await page.Content.ReadAsStringAsync());

            // 4) password post
            var loginPost = new HttpRequestMessage(HttpMethod.Post, S(cfg, "urlPost"))
            {
                Content = new FormUrlEncodedContent(
                    new Dictionary<string, string>
                    {
                        ["i13"] = "0",
                        ["login"] = upn,
                        ["loginfmt"] = upn,
                        ["type"] = "11",
                        ["LoginOptions"] = "3",
                        ["passwd"] = password,
                        ["ps"] = "2",
                        ["canary"] = S(cfg, "canary"),
                        ["ctx"] = S(cfg, "sCtx"),
                        ["hpgrequestid"] = S(cfg, "sessionId"),
                        ["flowToken"] = S(cfg, "sFT"),
                        ["NewUser"] = "1",
                        ["fspost"] = "0",
                        ["i21"] = "0",
                        ["CookieDisclosure"] = "0",
                        ["IsFidoSupported"] = "1",
                        ["isSignupPost"] = "0",
                        ["i19"] = "5000",
                    }
                ),
            };
            loginPost.Headers.Referrer = pageUrl;
            loginPost.Headers.Add("Origin", Entra);
            HttpResponseMessage r2 = await _http.SendAsync(loginPost);
            cfg = ParseConfig(await r2.Content.ReadAsStringAsync());

            if (S(cfg, "pgid") != "KmsiInterrupt")
                throw new UnauthorizedAccessException(
                    "Entra did not reach the KMSI step (pgid="
                        + S(cfg, "pgid")
                        + ", error="
                        + S(cfg, "sErrorCode")
                        + "). Wrong password, MFA or conditional access."
                );

            // 5) "Stay signed in?"
            var kmsi = new HttpRequestMessage(HttpMethod.Post, Entra + "/kmsi")
            {
                Content = new FormUrlEncodedContent(
                    new Dictionary<string, string>
                    {
                        ["LoginOptions"] = "1",
                        ["type"] = "28",
                        ["DontShowAgain"] = "true",
                        ["ctx"] = S(cfg, "sCtx"),
                        ["hpgrequestid"] = S(cfg, "sessionId"),
                        ["flowToken"] = S(cfg, "sFT"),
                        ["canary"] = S(cfg, "canary"),
                    }
                ),
            };
            kmsi.Headers.Referrer = new Uri(Entra + "/common/login");
            kmsi.Headers.Add("Origin", Entra);
            HttpResponseMessage r3 = await _http.SendAsync(kmsi);
            string html = await r3.Content.ReadAsStringAsync();

            // 6) Auto-submitting form: hand the code back to Magister
            var back = new HttpRequestMessage(HttpMethod.Post, Host + "/ExternalLogin/OidcSignin")
            {
                Content = new FormUrlEncodedContent(
                    new Dictionary<string, string>
                    {
                        ["code"] = HtmlField(html, "code"),
                        ["state"] = HtmlField(html, "state"),
                        ["session_state"] = HtmlField(html, "session_state"),
                    }
                ),
            };
            back.Headers.Referrer = new Uri(Entra + "/");
            back.Headers.Add("Origin", Entra);
            HttpResponseMessage r4 = await _http.SendAsync(back); // 302 -> /connect/authorize/callback?...
            if (r4.Headers.Location == null)
                throw new InvalidOperationException(
                    "OidcSignin returned " + (int)r4.StatusCode + " without Location."
                );

            // 7) Follow until the Location contains #id_token=...&access_token=...
            Uri? final = await FollowUntilToken(new Uri(new Uri(Host), r4.Headers.Location));
            if (final == null)
                throw new InvalidOperationException(
                    "No access_token fragment found at end of redirect chain."
                );

            var frag = HttpUtility.ParseQueryString(final.Fragment.TrimStart('#'));
            return frag["access_token"]
                ?? throw new InvalidOperationException("access_token missing.");
        }

        // ---------- school API (SPA login: own OIDC client + bearer token + SESSION_ID cookie) ----------

        private string? _schoolToken;

        /// <summary>Bearer token for the school API (set by GetSchoolTokenAsync or UseSchoolToken).</summary>
        public string? SchoolToken => _schoolToken;

        /// <summary>Re-use a token from an earlier login instead of logging in again.</summary>
        public void UseSchoolToken(string token) => _schoolToken = token;

        // The school web app (/magister/) is a single-page app that logs in by itself with the OIDC client
        // "M6-<host>" and then calls /api with a bearer token (and a SESSION_ID cookie). Here the OIDC part is done
        // by hand, re-using the accounts.magister.net cookies we got from LoginAsync.
        public async Task<string> GetSchoolTokenAsync(string schoolHost)
        {
            string clientId = "M6-" + schoolHost;
            string redirectUri = "https://" + schoolHost + "/magister/oidc/redirect_callback.html"; // fallback guess
            string scope = Environment.GetEnvironmentVariable("MAGISTER_SCOPE") ?? "openid profile";

            // Try to read the real redirect_uri / scope out of the SPA's own scripts
            var pageUrl = new Uri("https://" + schoolHost + "/magister/");
            var sources = await FetchPageWithScripts(pageUrl);
            string? foundRedirect = null,
                foundScope = null;
            foreach ((string name, string text) in sources)
            {
                if (foundRedirect == null)
                {
                    Match r = Regex.Match(text, "[\"']([^\"']*redirect_callback[^\"']*)[\"']");
                    if (r.Success)
                        foundRedirect = new Uri(pageUrl, r.Groups[1].Value).ToString();
                }
                if (foundScope == null)
                {
                    foreach (Match s in Regex.Matches(text, "scope\\s*:\\s*[\"']([^\"']+)[\"']"))
                        if (s.Groups[1].Value.Contains("openid"))
                        {
                            foundScope = s.Groups[1].Value;
                            break;
                        }
                }
            }
            if (foundRedirect != null)
                redirectUri = foundRedirect;
            if (foundScope != null && Environment.GetEnvironmentVariable("MAGISTER_SCOPE") == null)
                scope = foundScope;
            Log?.Invoke("  client_id=" + clientId);
            Log?.Invoke(
                "  redirect_uri="
                    + redirectUri
                    + (foundRedirect == null ? "  (guess)" : "  (from page)")
            );
            Log?.Invoke("  scope=" + scope + (foundScope == null ? "  (guess)" : "  (from page)"));
            if (foundRedirect == null || foundScope == null)
                foreach ((string name, string text) in sources)
                    System.IO.File.WriteAllText("dump_school_" + name, text);

            string authUrl =
                Host
                + "/connect/authorize?client_id="
                + Uri.EscapeDataString(clientId)
                + "&redirect_uri="
                + Uri.EscapeDataString(redirectUri)
                + "&response_type="
                + Uri.EscapeDataString("id_token token")
                + "&scope="
                + Uri.EscapeDataString(scope)
                + "&state="
                + Guid.NewGuid().ToString("N")
                + "&nonce="
                + Guid.NewGuid().ToString("N");

            Uri url = new Uri(authUrl);
            for (int hop = 0; hop < 15; hop++)
            {
                HttpResponseMessage res = await _http.GetAsync(url);
                Log?.Invoke(
                    "  hop " + hop + ": " + (int)res.StatusCode + " " + url.Host + url.AbsolutePath
                );
                if (!(IsRedirect(res) && res.Headers.Location != null))
                    throw new InvalidOperationException(
                        "Authorize did not end in a token redirect (last: "
                            + (int)res.StatusCode
                            + " "
                            + url.AbsolutePath
                            + "). Check client_id / redirect_uri / scope above."
                    );
                url = new Uri(url, res.Headers.Location);
                if (url.Fragment.Contains("access_token="))
                {
                    var frag = HttpUtility.ParseQueryString(url.Fragment.TrimStart('#'));
                    _schoolToken = frag["access_token"];
                    return _schoolToken!;
                }
            }
            throw new InvalidOperationException(
                "Too many redirects while requesting the school token."
            );
        }

        public bool HasSchoolSessionCookie(string schoolHost) =>
            _jar.GetCookies(new Uri("https://" + schoolHost))["SESSION_ID"] != null;

        // GET on the school API. The HttpClient has AllowAutoRedirect = false, so redirects (e.g. the 307 that
        // file downloads answer with) are followed here by hand. The bearer token and Referer are only sent to the
        // school host itself, never to another host the file may be redirected to.
        private async Task<HttpResponseMessage> SchoolGetAsync(
            string schoolHost,
            string pathAndQuery
        )
        {
            Uri url = new Uri("https://" + schoolHost + pathAndQuery);

            for (int hop = 0; hop < 6; hop++)
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.ParseAdd("application/json, text/plain, */*");

                bool sameHost = string.Equals(
                    url.Host,
                    schoolHost,
                    StringComparison.OrdinalIgnoreCase
                );
                if (sameHost)
                {
                    req.Headers.Referrer = new Uri("https://" + schoolHost + "/magister/");
                    if (_schoolToken != null)
                        req.Headers.Authorization =
                            new System.Net.Http.Headers.AuthenticationHeaderValue(
                                "Bearer",
                                _schoolToken
                            );
                }

                HttpResponseMessage res = await _http.SendAsync(req);

                if (IsRedirect(res) && res.Headers.Location != null)
                {
                    Uri next = new Uri(url, res.Headers.Location);
                    res.Dispose();
                    if (next.Scheme != Uri.UriSchemeHttps)
                        throw new HttpRequestException(
                            "Refusing to follow a redirect to " + next.Scheme + "."
                        );
                    Log?.Invoke(
                        "  redirect -> "
                            + next.Host
                            + next.AbsolutePath
                            + (sameHost ? "" : "  (other host, no token)")
                    );
                    url = next;
                    continue;
                }

                if (!res.IsSuccessStatusCode)
                {
                    int code = (int)res.StatusCode;
                    res.Dispose();
                    throw new HttpRequestException(
                        "GET " + pathAndQuery.Split('?')[0] + " returned " + code,
                        null,
                        (HttpStatusCode)code
                    );
                }
                return res;
            }

            throw new HttpRequestException("Too many redirects for " + pathAndQuery.Split('?')[0]);
        }

        public async Task<string> GetSchoolStringAsync(string schoolHost, string pathAndQuery)
        {
            using HttpResponseMessage res = await SchoolGetAsync(schoolHost, pathAndQuery);
            return await res.Content.ReadAsStringAsync();
        }

        public async Task<byte[]> GetSchoolBytesAsync(string schoolHost, string pathAndQuery)
        {
            using HttpResponseMessage res = await SchoolGetAsync(schoolHost, pathAndQuery);
            return await res.Content.ReadAsByteArrayAsync();
        }

        private async Task<List<(string name, string text)>> FetchPageWithScripts(Uri pageUrl)
        {
            var list = new List<(string name, string text)>();
            HttpResponseMessage first = await _http.GetAsync(pageUrl);
            string html = await first.Content.ReadAsStringAsync();
            list.Add(("page.html", html));

            Uri baseUri = pageUrl;
            Match bt = Regex.Match(html, "<base[^>]+href=\"([^\"]+)\"", RegexOptions.IgnoreCase);
            if (bt.Success)
                baseUri = new Uri(pageUrl, HttpUtility.HtmlDecode(bt.Groups[1].Value));

            foreach (
                Match m in Regex.Matches(
                    html,
                    "<script[^>]+src=\"([^\"]+)\"",
                    RegexOptions.IgnoreCase
                )
            )
            {
                Uri js = new Uri(baseUri, HttpUtility.HtmlDecode(m.Groups[1].Value));
                if (js.Host != pageUrl.Host)
                    continue;
                HttpResponseMessage r = await _http.GetAsync(js);
                if (r.IsSuccessStatusCode)
                    list.Add(
                        (
                            System.IO.Path.GetFileName(js.LocalPath),
                            await r.Content.ReadAsStringAsync()
                        )
                    );
            }
            return list;
        }

        // ---------- helpers ----------

        // Mimics JavaScript decodeURI(): the login page decodes returnUrl a second time, but leaves
        // reserved characters (; / ? : @ & = + $ , #) encoded. In the HAR "%20" became a space while
        // redirect_uri kept its %3A / %2F.
        private static string DecodeUri(string s) =>
            Regex.Replace(
                s,
                "%([0-9a-fA-F]{2})",
                m =>
                {
                    int b = Convert.ToInt32(m.Groups[1].Value, 16);
                    return b < 0x80 && ";/?:@&=+$,#".IndexOf((char)b) < 0
                        ? ((char)b).ToString()
                        : m.Value;
                }
            );

        // authCode is not in the redirect URL. In the HAR it only appears in the POST bodies, so it must come
        // from the login page HTML or its scripts. Look for a 12-hex-char value tied to the word "authCode".
        // If nothing is found, everything fetched is dumped to dump_* files so the real location can be found.
        private async Task<string> FindAuthCode(Uri loginUrl)
        {
            string html = await _http.GetStringAsync(loginUrl);
            var sources = new List<(string name, string text)> { ("login.html", html) };

            // Respect <base href="..."> if the page has one; browsers resolve relative script paths against it
            Uri baseUri = loginUrl;
            Match baseTag = Regex.Match(
                html,
                "<base[^>]+href=\"([^\"]+)\"",
                RegexOptions.IgnoreCase
            );
            if (baseTag.Success)
                baseUri = new Uri(loginUrl, HttpUtility.HtmlDecode(baseTag.Groups[1].Value));

            foreach (
                Match m in Regex.Matches(
                    html,
                    "<script[^>]+src=\"([^\"]+)\"",
                    RegexOptions.IgnoreCase
                )
            )
            {
                string src = HttpUtility.HtmlDecode(m.Groups[1].Value);
                var candidates = new List<Uri>
                {
                    new Uri(baseUri, src),
                    new Uri(loginUrl, src),
                    new Uri(new Uri(Host), src.TrimStart('.', '/')), // host-root relative
                    new Uri(new Uri(Host + "/js/"), System.IO.Path.GetFileName(src)), // /js/<file>
                };

                foreach (Uri js in candidates)
                {
                    if (js.Host != "accounts.magister.net")
                        continue;
                    HttpResponseMessage r = await _http.GetAsync(js);
                    if (!r.IsSuccessStatusCode)
                        continue;
                    sources.Add(
                        (
                            System.IO.Path.GetFileName(js.LocalPath),
                            await r.Content.ReadAsStringAsync()
                        )
                    );
                    break;
                }
            }

            foreach ((string name, string text) in sources)
            {
                // The bundle builds it as: (a=["0a41","c24a1f","87662f8b8c","585c74"],["3","1"].map(i=>a[parseInt(i)||0]).join(""))
                Match ob = Regex.Match(
                    text,
                    "\\(a=\\[((?:\"[0-9a-fA-F]+\",?)+)\\],\\[((?:\"\\d+\",?)+)\\]\\.map"
                );
                if (ob.Success)
                {
                    var parts = Regex.Matches(ob.Groups[1].Value, "\"([^\"]+)\"");
                    var idx2 = Regex.Matches(ob.Groups[2].Value, "\"(\\d+)\"");
                    var sb = new StringBuilder();
                    foreach (Match i in idx2)
                    {
                        int k = int.Parse(i.Groups[1].Value);
                        sb.Append(
                            k < parts.Count ? parts[k].Groups[1].Value : parts[0].Groups[1].Value
                        );
                    }
                    return sb.ToString();
                }

                Match hit = Regex.Match(
                    text,
                    "authCode[\"']?\\s*[:=]\\s*[\"']([0-9a-f]{12})[\"']",
                    RegexOptions.IgnoreCase
                );
                if (hit.Success)
                    return hit.Groups[1].Value;

                int idx = text.IndexOf("authCode", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    int start = Math.Max(0, idx - 200);
                    string near = text.Substring(start, Math.Min(600, text.Length - start));
                    Match h2 = Regex.Match(
                        near,
                        "[\"']([0-9a-f]{12})[\"']",
                        RegexOptions.IgnoreCase
                    );
                    if (h2.Success)
                        return h2.Groups[1].Value;
                }
            }

            foreach ((string name, string text) in sources)
                System.IO.File.WriteAllText("dump_" + name, text);
            throw new InvalidOperationException(
                "authCode not found in login page or scripts. Dumped "
                    + sources.Count
                    + " files as dump_* in the working directory."
            );
        }

        private string Xsrf() => _jar.GetCookies(new Uri(Host))["XSRF-TOKEN"]?.Value ?? "";

        private async Task<JsonElement> PostJson(string path, object body)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, Host + path)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(body),
                    Encoding.UTF8,
                    "application/json"
                ),
            };
            req.Headers.Add("x-xsrf-token", Xsrf()); // rotates on every response, so re-read each time
            req.Headers.Add("Origin", Host);
            req.Headers.Referrer = new Uri(Host + "/");
            req.Headers.Accept.ParseAdd("application/json");

            HttpResponseMessage res = await _http.SendAsync(req);
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            return doc.RootElement.Clone();
        }

        // Follows redirects and returns the first Location that looks like the interactive login page
        private async Task<Uri?> FollowUntilLoginPage(Uri url)
        {
            for (int i = 0; i < 15; i++)
            {
                HttpResponseMessage res = await _http.GetAsync(url);
                if (IsRedirect(res) && res.Headers.Location != null)
                {
                    url = new Uri(url, res.Headers.Location);
                    if (url.Query.Contains("sessionId="))
                        return url;
                    continue;
                }
                return url.Query.Contains("sessionId=") ? url : null;
            }
            return null;
        }

        private async Task<Uri?> FollowUntilToken(Uri url)
        {
            for (int i = 0; i < 15; i++)
            {
                HttpResponseMessage res = await _http.GetAsync(url);
                if (IsRedirect(res) && res.Headers.Location != null)
                {
                    url = new Uri(url, res.Headers.Location);
                    if (url.Fragment.Contains("access_token="))
                        return url;
                    continue;
                }
                return null;
            }
            return null;
        }

        private async Task<(HttpResponseMessage, Uri)> GetFollowing(Uri url)
        {
            for (int i = 0; i < 15; i++)
            {
                HttpResponseMessage res = await _http.GetAsync(url);
                if (IsRedirect(res) && res.Headers.Location != null)
                {
                    url = new Uri(url, res.Headers.Location);
                    continue;
                }
                return (res, url);
            }
            throw new InvalidOperationException("Too many redirects.");
        }

        private static bool IsRedirect(HttpResponseMessage r) =>
            (int)r.StatusCode >= 300 && (int)r.StatusCode < 400;

        // Microsoft embeds its page state as: $Config={...};//]]>
        private static JsonElement ParseConfig(string html)
        {
            Match m = Regex.Match(html, @"\$Config=(\{.*?\});\s*//\]\]>", RegexOptions.Singleline);
            if (!m.Success)
                m = Regex.Match(html, @"\$Config=(\{.*?\});", RegexOptions.Singleline);
            if (!m.Success)
                throw new InvalidOperationException("No $Config found in Microsoft page.");
            using var doc = JsonDocument.Parse(m.Groups[1].Value);
            return doc.RootElement.Clone();
        }

        private static string S(JsonElement cfg, string key) =>
            cfg.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? ""
                : "";

        // Reads <input ... name="X" ... value="Y"> regardless of attribute order
        private static string HtmlField(string html, string name)
        {
            foreach (Match tag in Regex.Matches(html, @"<input\b[^>]*>", RegexOptions.IgnoreCase))
            {
                string t = tag.Value;
                if (
                    !Regex.IsMatch(
                        t,
                        "name=\"" + Regex.Escape(name) + "\"",
                        RegexOptions.IgnoreCase
                    )
                )
                    continue;
                Match v = Regex.Match(t, "value=\"([^\"]*)\"", RegexOptions.IgnoreCase);
                if (v.Success)
                    return HttpUtility.HtmlDecode(v.Groups[1].Value);
            }
            throw new InvalidOperationException("Form field '" + name + "' not found.");
        }
    }
}
