// =========================================================
// USINGs
// =========================================================
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MimeKit;
using QuizWeb;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;

using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

// Alias, damit es im Top-Level keine Mehrdeutigkeit gibt:
using Db = QuizWeb.QuizDb;

// =========================================================
// TOP-LEVEL APP CODE
// =========================================================

var builder = WebApplication.CreateBuilder(args);

// -----------------------------
// DB + Identity
// -----------------------------
builder.Services.AddDbContext<Db>(opts =>
{
    // ConnectionString "db" aus appsettings.json oder Fallback
    var cs = builder.Configuration.GetConnectionString("db")
             ?? "Data Source=quiz.db";
    opts.UseSqlite(cs);
});

builder.Services
    .AddIdentityCore<AppUser>(opt =>
    {
        opt.User.RequireUniqueEmail = true;

        // ✅ WICHTIG: Damit Registrierung -> Login ohne Mail-Confirm funktioniert:
        opt.SignIn.RequireConfirmedAccount = false;
        opt.SignIn.RequireConfirmedEmail = false;

        opt.Password.RequiredLength = 6;      // passend zu deinem Frontend
        opt.Password.RequireDigit = false;
        opt.Password.RequireUppercase = false;
        opt.Password.RequireNonAlphanumeric = false;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<Db>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme)
    .AddCookie(IdentityConstants.ApplicationScheme, o =>
    {
        o.Cookie.Name = "quiz.auth";

        // ✅ Login-Seite
        o.LoginPath = "/login.html";

        // (Optional) wenn du später AccessDenied brauchst:
        // o.AccessDeniedPath = "/login.html";
    });

builder.Services.AddAuthorization();

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

// -----------------------------
// E-Mail Service (optional; bleibt drin)
// -----------------------------
if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BREVO_API_KEY")))
    builder.Services.AddSingleton<IEmailSender, BrevoApiEmailSender>();
else
    builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();

var app = builder.Build();

// -----------------------------
// Pipeline
// -----------------------------
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

// -----------------------------
// DB-Migrate + Import
// -----------------------------
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<QuizDb>();
    var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();

    // wwwroot setzen und Quiz-Ordner sicherstellen
    env.WebRootPath ??= Path.Combine(env.ContentRootPath, "wwwroot");
    Directory.CreateDirectory(env.WebRootPath);

    var quizRoot = Path.Combine(env.WebRootPath, "Quiz");
    Directory.CreateDirectory(quizRoot);

    // Migrationen ausführen
    db.Database.Migrate();

    // Wenn noch keine Fragen in der DB sind -> einmalig alles importieren
    if (!db.Questions.Any())
    {
        Console.WriteLine("[Startup] Keine Fragen gefunden – starte Import aus wwwroot/Quiz …");
        var added = TxtImporter.ImportAll(db, quizRoot, msg => Console.WriteLine(msg));
        Console.WriteLine($"[Startup] Import fertig. Neu hinzugefügt: {added} Fragen.");
    }
    else
    {
        Console.WriteLine($"[Startup] DB enthält bereits {db.Questions.Count()} Fragen – kein Import nötig.");
    }
}

// =========================================================
// AUTH APIs (Register/Login/Logout/Status)
// =========================================================

// Status: eingeloggt?
app.MapGet("/api/auth/status", (HttpContext ctx) =>
{
    var ok = ctx.User.Identity?.IsAuthenticated ?? false;
    var email = ok ? (ctx.User.Identity?.Name ?? ctx.User.FindFirst("email")?.Value) : null;
    return Results.Ok(new AuthStatusDto(ok, email));
});

// Registrierung: erstellt User, loggt ihn direkt ein
app.MapPost("/api/auth/register",
async (RegisterDtoSimple dto,
       UserManager<AppUser> users,
       SignInManager<AppUser> signIn) =>
{
    var email = (dto.Email ?? "").Trim();

    // Schul-E-Mail: 6 Ziffern + @studierende.htl-donaustadt.at
    var emailPattern = new System.Text.RegularExpressions.Regex(@"^\d{6}@studierende\.htl-donaustadt\.at$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    if (!emailPattern.IsMatch(email))
        return Results.BadRequest(new { error = "Bitte Schul-E-Mail im Format 230050@studierende.htl-donaustadt.at eingeben." });

    if (string.IsNullOrWhiteSpace(dto.Password) || dto.Password.Length < 6)
        return Results.BadRequest(new { error = "Das Passwort muss mindestens 6 Zeichen lang sein." });

    if (await users.FindByEmailAsync(email) is not null)
        return Results.BadRequest(new { error = "E-Mail ist bereits registriert." });

    var user = new AppUser { UserName = email, Email = email };

    var res = await users.CreateAsync(user, dto.Password);
    if (!res.Succeeded)
        return Results.BadRequest(new { error = string.Join("; ", res.Errors.Select(e => e.Description)) });

    // ✅ sofort einloggen
    await signIn.SignInAsync(user, isPersistent: true);

    return Results.Ok(new { ok = true });
});

// Login: setzt Cookie (Remember steuert Persistenz)
app.MapPost("/api/auth/login",
async (LoginDto dto, SignInManager<AppUser> signIn, UserManager<AppUser> users) =>
{
    var email = (dto.Email ?? "").Trim();
    var user = await users.FindByEmailAsync(email);

    if (user == null)
        return Results.BadRequest(new { error = "Falsche E-Mail oder Passwort." });

    // (Bestätigung ist AUS, siehe Identity-Optionen oben)
    var res = await signIn.PasswordSignInAsync(user, dto.Password, isPersistent: dto.Remember, lockoutOnFailure: false);
    if (!res.Succeeded)
        return Results.BadRequest(new { error = "Falsche E-Mail oder Passwort." });

    return Results.Ok(new { ok = true });
});

app.MapPost("/api/auth/logout", async (SignInManager<AppUser> signIn) =>
{
    await signIn.SignOutAsync();
    return Results.Ok(new { ok = true });
});

// ---------------------------------------------------------
// Testmail (optional)
// ---------------------------------------------------------
app.MapGet("/api/testmail", async (IEmailSender mail, string to) =>
{
    try
    {
        await mail.SendAsync(
            to,
            "NetAcad-Quiz – Testmail",
            "<h1>Glückwunsch 🎉</h1><p>Dein SMTP/Brevo-Setup funktioniert!</p>"
        );
        return Results.Ok(new { ok = true });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex);
        return Results.Problem(title: "SMTP/API error", detail: ex.Message);
    }
});

// =========================================================
// QUIZ APIs (JETZT NUR MIT LOGIN!)
// =========================================================

// Kapitel-Liste
app.MapGet("/api/chapters", async (Db db) =>
{
    var list = await db.Questions
        .Select(q => q.Chapter)
        .Distinct()
        .OrderBy(x => x)
        .ToListAsync();

    return Results.Ok(list);
}).RequireAuthorization();

// Fragen für ein Kapitel
app.MapGet("/api/quiz", async (Db db, string? chapter) =>
{
    var rng = new Random();

    var q = db.Questions
        .Include(x => x.Choices)
        .Include(x => x.Assets)
        .AsQueryable();

    if (!string.IsNullOrWhiteSpace(chapter))
        q = q.Where(x => x.Chapter == chapter);

    var questions = await q
        .OrderBy(_ => EF.Functions.Random())
        .ToListAsync();

    foreach (var item in questions)
        item.Choices = item.Choices.OrderBy(_ => rng.Next()).ToList();

    var dto = questions.Select(item => new
    {
        id = item.Id,
        text = item.Text,
        chapter = item.Chapter,
        timeLimitSeconds = item.TimeLimitSeconds,
        // ✅ nur die Antworttexte und IDs an den Client, NICHT IsCorrect!
        choices = item.Choices.Select(c => new { id = c.Id, text = c.Text }),
        assets = item.Assets.Select(a => "/" + a.RelativePath)
    });

    return Results.Ok(dto);
}).RequireAuthorization();

// Auswertung (Server prüft korrekt/falsch)
app.MapPost("/api/submit",
async (Db db, SubmitDTO payload) =>
{
    int correct = 0;
    var wrongs = new List<object>();

    foreach (var ans in payload.Answers)
    {
        var q = await db.Questions.Include(x => x.Choices)
                                  .FirstOrDefaultAsync(x => x.Id == ans.QuestionId);
        if (q == null) continue;

        var chosen = (ans.ChoiceIds ?? new List<Guid>()).ToHashSet();
        var correctSet = q.Choices.Where(c => c.IsCorrect).Select(c => c.Id).ToHashSet();

        bool ok = chosen.SetEquals(correctSet);
        if (ok) correct++;
        else
        {
            wrongs.Add(new
            {
                Question = q.Text,
                Your = string.Join(" | ", q.Choices.Where(c => chosen.Contains(c.Id)).Select(c => c.Text)),
                Correct = string.Join(" | ", q.Choices.Where(c => c.IsCorrect).Select(c => c.Text))
            });
        }
    }

    return Results.Ok(new { total = payload.Answers.Count, correct, wrongs });
}).RequireAuthorization();

app.Run();

// =========================================================
// NAMESPACE + MODELLE
// =========================================================
namespace QuizWeb
{
    // -------------------------
    // E-Mail
    // -------------------------
    public interface IEmailSender { Task SendAsync(string to, string subject, string html); }

    public class SmtpEmailSender : IEmailSender
    {
        private readonly IConfiguration _cfg;
        public SmtpEmailSender(IConfiguration cfg) => _cfg = cfg;

        public async Task SendAsync(string to, string subject, string html)
        {
            string Get(string env, string jsonPath, string? def = null) =>
                Environment.GetEnvironmentVariable(env) ?? _cfg[jsonPath] ?? def;

            var host = Get("SMTP_HOST", "EmailSettings:Host") ?? throw new InvalidOperationException("SMTP host missing");
            var portStr = Get("SMTP_PORT", "EmailSettings:Port", "587");
            var user = Get("SMTP_USER", "EmailSettings:UserName");
            var pass = Get("SMTP_PASS", "EmailSettings:Password");
            var from = Get("SMTP_FROM", "EmailSettings:SenderEmail", user ?? "no-reply@example.com")!;
            var fromNm = Get("SMTP_FROM_NAME", "EmailSettings:SenderName", "NetAcad-Quiz")!;
            if (!int.TryParse(portStr, out var port)) port = 587;

            var msg = new MimeMessage();
            msg.From.Add(new MailboxAddress(fromNm, from));
            msg.To.Add(MailboxAddress.Parse(to));
            msg.Subject = subject;
            msg.Body = new BodyBuilder { HtmlBody = html }.ToMessageBody();

            using var client = new MailKit.Net.Smtp.SmtpClient();
            client.Timeout = 15000;
            var ssl = port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;

            try
            {
                await client.ConnectAsync(host, port, ssl);

                if (!string.IsNullOrWhiteSpace(user))
                {
                    if (string.IsNullOrWhiteSpace(pass))
                        throw new InvalidOperationException("SMTP password missing (SMTP_PASS / EmailSettings:Password).");

                    await client.AuthenticateAsync(user, pass);
                }

                await client.SendAsync(msg);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SMTP] Host={host}:{port}, SSL={ssl}, User={(string.IsNullOrEmpty(user) ? "<none>" : "<set>")}");
                Console.Error.WriteLine(ex);
                throw;
            }
            finally
            {
                try { await client.DisconnectAsync(true); } catch { }
            }
        }
    }

    public class BrevoApiEmailSender : IEmailSender
    {
        private readonly HttpClient _http;
        private readonly string _apiKey;
        private readonly string _fromEmail;
        private readonly string _fromName;

        public BrevoApiEmailSender(IConfiguration cfg)
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

            _apiKey = Environment.GetEnvironmentVariable("BREVO_API_KEY")
                         ?? cfg["EmailSettings:BrevoApiKey"]
                         ?? throw new InvalidOperationException("BREVO_API_KEY fehlt.");
            _fromEmail = Environment.GetEnvironmentVariable("BREVO_FROM_EMAIL")
                         ?? cfg["EmailSettings:SenderEmail"]
                         ?? throw new InvalidOperationException("BREVO_FROM_EMAIL/EmailSettings:SenderEmail fehlt.");
            _fromName = Environment.GetEnvironmentVariable("BREVO_FROM_NAME")
                         ?? cfg["EmailSettings:SenderName"]
                         ?? "NetAcad-Quiz";

            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            _http.DefaultRequestHeaders.Add("api-key", _apiKey);
        }

        public async Task SendAsync(string to, string subject, string html)
        {
            var payload = new
            {
                sender = new { email = _fromEmail, name = _fromName },
                to = new[] { new { email = to } },
                subject,
                htmlContent = html
            };

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            var resp = await _http.PostAsync("https://api.brevo.com/v3/smtp/email", content);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"Brevo API error {(int)resp.StatusCode}: {body}");
            }
        }
    }

    // -------------------------
    // Identity User
    // -------------------------
    public class AppUser : IdentityUser { }

    // -------------------------
    // DbContext
    // -------------------------
    public class QuizDb : IdentityDbContext<AppUser>
    {
        public QuizDb(DbContextOptions<QuizDb> opt) : base(opt) { }

        public DbSet<Question> Questions => Set<Question>();
        public DbSet<Choice> Choices => Set<Choice>();
        public DbSet<QuestionAsset> Assets => Set<QuestionAsset>();
        public DbSet<Mistake> Mistakes => Set<Mistake>();
        public DbSet<RegistrationKey> RegistrationKeys => Set<RegistrationKey>();

        protected override void OnModelCreating(ModelBuilder b)
        {
            base.OnModelCreating(b);

            b.Entity<Question>().HasKey(x => x.Id);
            b.Entity<Choice>().HasKey(x => x.Id);
            b.Entity<QuestionAsset>().HasKey(x => x.Id);
            b.Entity<Mistake>().HasKey(x => x.Id);
            b.Entity<RegistrationKey>().HasKey(x => x.Key);

            b.Entity<Question>()
                .HasMany(x => x.Choices)
                .WithOne(x => x.Question!)
                .HasForeignKey(x => x.QuestionId)
                .OnDelete(DeleteBehavior.Cascade);

            b.Entity<Question>()
                .HasMany(x => x.Assets)
                .WithOne(x => x.Question!)
                .HasForeignKey(x => x.QuestionId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }

    // -------------------------
    // Entities
    // -------------------------
    public partial class Question
    {
        public Guid Id { get; set; }
        public string Text { get; set; } = "";
        public string Chapter { get; set; } = "";
        public int TimeLimitSeconds { get; set; }
        public int CorrectCount { get; set; }
        public List<Choice> Choices { get; set; } = new();
        public List<QuestionAsset> Assets { get; set; } = new();
    }

    public class Choice
    {
        public Guid Id { get; set; }
        public Guid QuestionId { get; set; }
        public Question? Question { get; set; }
        public string Text { get; set; } = "";
        public bool IsCorrect { get; set; }
    }

    public class QuestionAsset
    {
        public Guid Id { get; set; }
        public Guid QuestionId { get; set; }
        public Question? Question { get; set; }
        public string RelativePath { get; set; } = "";
    }

    public class Mistake
    {
        public Guid Id { get; set; }
        public string UserId { get; set; } = "";
        public Guid QuestionId { get; set; }
        public string? ChosenChoiceIdsCsv { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class RegistrationKey
    {
        public string Key { get; set; } = "";
        public bool Used { get; set; }
        public string? UsedByUserId { get; set; }
        public DateTime? UsedAtUtc { get; set; }
        public DateTime? ExpiresUtc { get; set; }
    }

    // -------------------------
    // DTOs
    // -------------------------
    public record RegisterDtoSimple(string Email, string Password);
    public record LoginDto(string Email, string Password, bool Remember);
    public record AuthStatusDto(bool IsAuthenticated, string? Email);

    public class SubmitDTO
    {
        public List<SubmitAnswer> Answers { get; set; } = new();
    }

    public class SubmitAnswer
    {
        public Guid QuestionId { get; set; }
        public List<Guid> ChoiceIds { get; set; } = new();
    }
}
