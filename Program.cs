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
        opt.SignIn.RequireConfirmedAccount = true;
        opt.Password.RequiredLength = 8;
        opt.Password.RequireDigit = true;
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
        o.LoginPath = "/";
    });

builder.Services.AddAuthorization();

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

// -----------------------------
// E-Mail Service (Brevo API bevorzugt, sonst SMTP)
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
// DB-Migrate + Import + Seed
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
// AUTH Endpunkte (kannst du ignorieren, Quiz braucht sie nicht)
// =========================================================

app.MapGet("/api/auth/status", (HttpContext ctx) =>
{
    var ok = ctx.User.Identity?.IsAuthenticated ?? false;
    var email = ok ? ctx.User.Identity?.Name : null;
    return Results.Ok(new AuthStatusDto(ok, email));
});

app.MapPost("/api/auth/register",
async (RegisterDto dto, UserManager<AppUser> users, SignInManager<AppUser> signIn,
       Db db, IEmailSender mail, IConfiguration cfg, HttpContext ctx) =>
{
    if (!dto.AcceptTos) return Results.BadRequest(new { error = "Bitte AGB akzeptieren." });

    var key = await db.RegistrationKeys.FindAsync(dto.RegistrationKey);
    if (key is null || key.Used || (key.ExpiresUtc.HasValue && key.ExpiresUtc < DateTime.UtcNow))
        return Results.BadRequest(new { error = "Ungültiger oder bereits verwendeter Registrierungsschlüssel." });

    if (await users.FindByEmailAsync(dto.Email) is not null)
        return Results.BadRequest(new { error = "E-Mail ist bereits registriert." });

    var user = new AppUser { UserName = dto.Email, Email = dto.Email };
    var res = await users.CreateAsync(user, dto.Password);
    if (!res.Succeeded) return Results.BadRequest(new { error = string.Join("; ", res.Errors.Select(e => e.Description)) });

    // Bestätigungslink
    var token = await users.GenerateEmailConfirmationTokenAsync(user);
    var baseUrl = cfg["APP_BASEURL"]?.TrimEnd('/') ?? $"{ctx.Request.Scheme}://{ctx.Request.Host}";
    var url = $"{baseUrl}/api/auth/confirm?uid={Uri.EscapeDataString(user.Id)}&token={Uri.EscapeDataString(token)}&rk={Uri.EscapeDataString(dto.RegistrationKey)}";

    await mail.SendAsync(
        dto.Email,
        "Bitte E-Mail bestätigen",
        $@"
<div style=""font-family:system-ui,-apple-system,Segoe UI,Roboto,Arial,sans-serif;line-height:1.5;color:#111"">
  <h2 style=""margin:0 0 12px"">Willkommen beim NetAcad-Quiz!</h2>
  <p style=""margin:0 0 16px"">Klicke auf den Button, um deine Registrierung zu bestätigen und dich automatisch anzumelden:</p>

  <p style=""margin:24px 0"">
    <a href=""{WebUtility.HtmlEncode(url)}""
       style=""display:inline-block;background:#111;color:#fff;text-decoration:none;
              padding:12px 18px;border-radius:8px;font-weight:600"">
      E-Mail jetzt bestätigen
    </a>
  </p>

  <p style=""margin:16px 0"">Falls der Button nicht funktioniert, öffne diesen Link im Browser:<br>
    <span style=""font-size:13px;color:#555"">{WebUtility.HtmlEncode(url)}</span>
  </p>

  <hr style=""border:none;border-top:1px solid #eee;margin:24px 0"">
  <p style=""font-size:12px;color:#777;margin:0"">Diese E-Mail wurde automatisch versendet.</p>
</div>"
    );

    return Results.Ok(new { ok = true, info = "Bestätigungs-E-Mail gesendet." });
});

app.MapGet("/api/auth/confirm",
async (string uid, string token, string rk,
       UserManager<AppUser> users, SignInManager<AppUser> signIn, Db db) =>
{
    var user = await users.FindByIdAsync(uid);
    if (user == null) return Results.BadRequest("Ungültiger Benutzer.");

    var res = await users.ConfirmEmailAsync(user, token);
    if (!res.Succeeded) return Results.BadRequest("Bestätigung fehlgeschlagen.");

    var key = await db.RegistrationKeys.FindAsync(rk);
    if (key is not null && !key.Used)
    {
        key.Used = true;
        key.UsedByUserId = user.Id;
        key.UsedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    await signIn.SignInAsync(user, isPersistent: true);
    var html = "<html><head><meta http-equiv='refresh' content='0;url=/' /></head><body>Verifiziert. Weiterleitung …</body></html>";
    return Results.Content(html, "text/html");
});

app.MapPost("/api/auth/login",
async (LoginDto dto, SignInManager<AppUser> signIn, UserManager<AppUser> users) =>
{
    var user = await users.FindByEmailAsync(dto.Email);
    if (user == null) return Results.BadRequest(new { error = "Falsche E-Mail oder Passwort." });
    if (!await users.IsEmailConfirmedAsync(user)) return Results.BadRequest(new { error = "E-Mail noch nicht bestätigt." });

    var res = await signIn.PasswordSignInAsync(user, dto.Password, isPersistent: true, lockoutOnFailure: false);
    if (!res.Succeeded) return Results.BadRequest(new { error = "Falsche E-Mail oder Passwort." });

    return Results.Ok(new { ok = true });
});

app.MapPost("/api/auth/logout", async (SignInManager<AppUser> signIn) =>
{
    await signIn.SignOutAsync();
    return Results.Ok(new { ok = true });
});

// ---------------------------------------------------------
// Testmail
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
// QUIZ APIs (OHNE LOGIN!)
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
});

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
        choices = item.Choices.Select(c => new { id = c.Id, text = c.Text }),
        assets = item.Assets.Select(a => "/" + a.RelativePath)
    });

    return Results.Ok(dto);
});

// Auswertung
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
});

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
    public record RegisterDto(string Email, string Password, string RegistrationKey, bool AcceptTos);
    public record LoginDto(string Email, string Password);
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
