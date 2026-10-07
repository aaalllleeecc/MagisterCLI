using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Magister2
{
    // Local HTTP/JSON API around DataGetter, so any frontend can use the data:
    //
    //   GET  /health                                   no auth; {"status":"ok"}
    //   GET  /api/afspraken?days=7                     appointments from today (or ?van=2026-10-01&tot=2026-10-07)
    //   GET  /api/afspraken/{id}                       one appointment in full (?around=2026-10-01 widens the fallback search)
    //   GET  /api/berichten?top=40&skip=0              inbox
    //   GET  /api/berichten/{id}                       one message: {message: <full JSON incl. body>, bijlagen: <attachment list>}
    //   GET  /api/cijfers?top=25&skip=0                latest grades
    //   GET  /api/studiewijzers?datum=2026-10-01       study guides
    //   GET  /api/studiewijzers/{id}?datum=...         one study guide with all onderdelen and their files (bronnen)
    //   GET  /api/raw?path=/api/...                    any school API path, raw JSON passthrough
    //   GET  /api/download?path=/api/...&name=x.pdf&type=application/pdf   file bytes (study-guide files, message attachments)
    //   POST /api/logout                               delete the saved session file
    //   POST /api/berichten                            send a message
    // Downloading a file: take its path from the JSON and pass it to /api/download:
    //   study guide file    bronnen[].downloadPath                    (from /api/studiewijzers/{id})
    //   message attachment  bijlagen.items[].links.download.href      (from /api/berichten/{id})
    //
    // Everything except /health needs:   Authorization: Bearer <api key>
    //
    // Environment:
    //   MAGISTER_API_KEY       fixed API key (otherwise a random one is generated and printed at startup)
    //   MAGISTER_BIND          address to listen on (default 127.0.0.1, i.e. this machine only)
    //   MAGISTER_ALLOW_ORIGIN  comma-separated browser origins allowed by CORS
    //                          (default: http://localhost:3000,5173 and 127.0.0.1:3000,5173)
    public static class MagisterServer
    {
        public static async Task RunAsync(DataGetter mg, int port)
        {
            string bind = Environment.GetEnvironmentVariable("MAGISTER_BIND") ?? "127.0.0.1";
            bool loopbackOnly = bind == "127.0.0.1" || bind == "localhost";

            string? envKey = Environment.GetEnvironmentVariable("MAGISTER_API_KEY");
            string apiKey = !string.IsNullOrWhiteSpace(envKey)
                ? envKey
                : Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

            string[] origins = (
                Environment.GetEnvironmentVariable("MAGISTER_ALLOW_ORIGIN")
                ?? "http://localhost:3000,http://localhost:5173,http://127.0.0.1:3000,http://127.0.0.1:5173"
            ).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var builder = WebApplication.CreateBuilder();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.WebHost.UseUrls("http://" + bind + ":" + port);
            builder.Services.AddCors(o =>
                o.AddPolicy(
                    "frontend",
                    p =>
                    {
                        if (origins.Contains("*"))
                            p.AllowAnyOrigin();
                        else
                            p.WithOrigins(origins);
                        p.WithHeaders("Authorization", "Content-Type").WithMethods("GET", "POST");
                    }
                )
            );

            WebApplication app = builder.Build();
            app.UseCors("frontend");

            // DataGetter is not thread-safe: handle one request at a time
            var gate = new SemaphoreSlim(1, 1);
            byte[] keyBytes = Encoding.UTF8.GetBytes(apiKey);

            // Guard 1: Host header must be local (blocks DNS-rebinding attacks from web pages)
            // Guard 2: API key (constant-time compare), except /health
            app.Use(
                async (ctx, next) =>
                {
                    if (loopbackOnly)
                    {
                        string host = ctx.Request.Host.Host;
                        if (host != "localhost" && host != "127.0.0.1" && host != "[::1]")
                        {
                            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                            return;
                        }
                    }

                    if (ctx.Request.Path != "/health" && ctx.Request.Method != "OPTIONS")
                    {
                        string? auth = ctx.Request.Headers.Authorization;
                        string supplied =
                            auth != null
                            && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                                ? auth.Substring(7).Trim()
                                : "";
                        if (
                            !CryptographicOperations.FixedTimeEquals(
                                Encoding.UTF8.GetBytes(supplied),
                                keyBytes
                            )
                        )
                        {
                            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                            return;
                        }
                    }

                    await next();
                }
            );

            async Task<IResult> Guard(Func<Task<IResult>> action)
            {
                await gate.WaitAsync();
                try
                {
                    return await action();
                }
                catch (ArgumentException ex)
                {
                    return Results.Json(new { error = ex.Message }, statusCode: 400);
                }
                catch (HttpRequestException ex)
                {
                    // the school API answered with an error status (message only contains method, path, status)
                    return Results.Json(
                        new { error = ex.Message, upstreamStatus = (int?)ex.StatusCode },
                        statusCode: 502
                    );
                }
                catch (Exception ex)
                {
                    return Results.Json(new { error = ex.Message }, statusCode: 500);
                }
                finally
                {
                    gate.Release();
                }
            }

            Task<IResult> Run<T>(Func<Task<T>> fetch) =>
                Guard(async () => Results.Ok(await fetch()));

            app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

            // ---------- appointments ----------

            app.MapGet(
                "/api/afspraken",
                (int? days, DateTime? van, DateTime? tot) =>
                    Run(() =>
                    {
                        DateTime start = van?.Date ?? DateTime.Today;
                        DateTime end = tot?.Date ?? start.AddDays(Math.Max(1, days ?? 7) - 1);
                        return mg.GetAfsprakenAsync(start, end);
                    })
            );

            app.MapGet(
                "/api/afspraken/{id:int}",
                (int id, DateTime? around) =>
                    Guard(async () =>
                    {
                        Afspraak? a = await mg.GetAfspraakAsync(id, around);
                        return a == null
                            ? Results.NotFound(new { error = "Appointment not found." })
                            : Results.Ok(a);
                    })
            );

            // ---------- assignments ----------

            app.MapGet(
                "/api/opdrachten",
                () =>
                    Guard(async () =>
                    {
                        var opdrachten = await mg.GetOpdrachtenAsync();

                        return Results.Ok(
                            opdrachten.Select(o => new
                            {
                                o.Id,
                                o.Titel,
                                o.Vak,
                                o.InleverenVoor,
                                o.IngeleverdOp,
                                o.StatusLaatsteOpdrachtVersie,
                                o.LaatsteOpdrachtVersienummer,
                                o.Bijlagen,
                                o.Docenten,
                                o.VersieNavigatieItems,
                                o.Beoordeling,
                                o.BeoordeeldOp,
                                o.OpnieuwInleveren,
                                o.Afgesloten,
                                o.MagInleveren,
                                o.SelfHref
                            })
                        );
                    })
            );

            app.MapGet(
                "/api/opdrachten/{id:int}",
                (int id) =>
                    Guard(async () =>
                    {
                        Opdracht? o = await mg.GetOpdrachtAsync(id);

                        if (o == null)
                        {
                            return Results.NotFound(
                                new { error = "Assignment not found." }
                            );
                        }

                        return Results.Ok(
                            new
                            {
                                o.Id,
                                o.Titel,
                                o.Vak,
                                o.InleverenVoor,
                                o.IngeleverdOp,
                                o.StatusLaatsteOpdrachtVersie,
                                o.LaatsteOpdrachtVersienummer,
                                o.Bijlagen,
                                o.Docenten,
                                o.VersieNavigatieItems,
                                o.Omschrijving,
                                o.PlainOmschrijving,
                                o.Beoordeling,
                                o.BeoordeeldOp,
                                o.OpnieuwInleveren,
                                o.Afgesloten,
                                o.MagInleveren,
                                o.SelfHref
                            }
                        );
                    })
            );
            // ---------- messages ----------
            app.MapGet(
                "/api/personen",
                (HttpRequest request) =>
                    Guard(async () =>
                    {
                        string? query = request.Query["q"].FirstOrDefault();

                        if (string.IsNullOrWhiteSpace(query))
                        {
                            return Results.BadRequest(new { error = "Missing query parameter: q" });
                        }

                        PersonenResponse personen = await mg.GetPersonenWithNameAsync(query);

                        return Results.Ok(personen);
                    })
            );
            app.MapGet(
                "/api/berichten",
                (int? top, int? skip) => Run(() => mg.GetBerichtenAsync(top ?? 40, skip ?? 0))
            );
            app.MapPost(
                "/api/berichten",
                (BerichtOpstellen bericht) =>
                    Guard(async () =>
                    {
                        await mg.SendBericht(bericht);
                        return Results.Ok(new { sent = true });
                    })
            );
            app.MapGet(
                "/api/berichten/{id:int}",
                (int id) =>
                    Guard(async () =>
                    {
                        Bericht? b = await mg.FindBerichtAsync(id);
                        if (b == null)
                            return Results.NotFound(
                                new { error = "Message not found in the inbox." }
                            );

                        using JsonDocument msgDoc = JsonDocument.Parse(
                            await mg.GetBerichtJsonAsync(b)
                        );
                        JsonElement message = msgDoc.RootElement.Clone();

                        // Attachments are a separate endpoint; {"items":[{id,naam,contentType,grootte,links:{self,download}}]}
                        object bijlagen = new { items = Array.Empty<object>() };
                        if (b.HeeftBijlagen)
                        {
                            using JsonDocument attDoc = JsonDocument.Parse(
                                await mg.GetRawAsync("/api/berichten/berichten/" + id + "/bijlagen")
                            );
                            bijlagen = attDoc.RootElement.Clone();
                        }
                        return Results.Ok(new { message, bijlagen });
                    })
            );

            // ---------- grades ----------

            app.MapGet(
                "/api/cijfers",
                (int? top, int? skip) => Run(() => mg.GetCijfersAsync(top ?? 25, skip ?? 0))
            );

            // ---------- study guides ----------

            app.MapGet(
                "/api/studiewijzers",
                (DateTime? datum) => Run(() => mg.GetStudiewijzersAsync(datum))
            );

            app.MapGet(
                "/api/studiewijzers/{id:int}",
                (int id, DateTime? datum) =>
                    Guard(async () =>
                    {
                        Studiewijzer? sw = await mg.FindStudiewijzerAsync(id, datum);
                        if (sw == null)
                            return Results.NotFound(
                                new
                                {
                                    error = "Study guide not found (is it active around that date?).",
                                }
                            );

                        StudiewijzerDetail? detail = await mg.GetStudiewijzerAsync(sw);
                        var onderdelen = new List<object>();
                        foreach (Onderdeel o in detail?.Onderdelen?.Items ?? new List<Onderdeel>())
                        {
                            OnderdeelDetail? od = await mg.GetOnderdeelAsync(o.SelfHref);
                            onderdelen.Add(
                                new
                                {
                                    o.Id,
                                    o.Titel,
                                    o.Volgnummer,
                                    o.IsZichtbaar,
                                    omschrijving = od?.Omschrijving ?? o.Omschrijving,
                                    bronnen = od?.Bronnen?.ConvertAll(b => new
                                    {
                                        b.Id,
                                        b.Naam,
                                        b.ContentType,
                                        b.Grootte,
                                        // pass this to /api/download?path=...
                                        downloadPath = b
                                            .Links?.Find(l =>
                                                string.Equals(
                                                    l.Rel,
                                                    "Contents",
                                                    StringComparison.OrdinalIgnoreCase
                                                )
                                            )
                                            ?.Href,
                                    }),
                                }
                            );
                        }
                        return Results.Ok(
                            new
                            {
                                sw.Id,
                                sw.Titel,
                                sw.Van,
                                sw.TotEnMet,
                                sw.VakCodes,
                                onderdelen,
                            }
                        );
                    })
            );

            // ---------- passthrough ----------

            app.MapGet(
                "/api/raw",
                (string path) =>
                    Guard(async () =>
                        Results.Content(await mg.GetRawAsync(path), "application/json")
                    )
            );

            app.MapGet(
                "/api/download",
                (string path, string? name, string? type) =>
                    Guard(async () =>
                        Results.File(
                            await mg.DownloadAsync(path),
                            type ?? "application/octet-stream",
                            name
                        )
                    )
            );

            app.MapPost(
                "/api/logout",
                () => Results.Ok(new { deleted = DataGetter.DeleteCache() })
            );

            Console.Error.WriteLine("Magister API listening on http://" + bind + ":" + port);
            if (string.IsNullOrWhiteSpace(envKey))
                Console.Error.WriteLine(
                    "API key (send as 'Authorization: Bearer <key>'): " + apiKey
                );
            Console.Error.WriteLine("Allowed browser origins: " + string.Join(", ", origins));
            if (!loopbackOnly)
                Console.Error.WriteLine(
                    "WARNING: listening on "
                        + bind
                        + ", not just this machine. Anyone who can reach it and has the key can read this account."
                );
            Console.Error.WriteLine("Ctrl+C to stop.");

            await app.RunAsync();
        }
    }
}
