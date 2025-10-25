// =========================================================
// USINGs
// =========================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using MailKit.Net.Smtp;
using MimeKit;

// Modelle/DbContext liegen im Namespace QuizWeb:
using QuizWeb;
// Alias, damit es im Top-Level keine Mehrdeutigkeit gibt:
using Db = QuizWeb.QuizDb;


// =========================================================
// TOP-LEVEL APP CODE (hier KEINE Klassen/Records/Namespaces deklarieren)
// =========================================================

var builder = WebApplication.CreateBuilder(args);

// --- Services ---
builder.Services.AddDbContext<Db>(o => o.UseSqlite("Data Source=quiz.db"));

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

// E-Mail Service (SMTP-Daten via ENV-Variablen)
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();

var app = builder.Build();

// --- Pipeline ---
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

// --- DB-Migrate + Import + Seed ---
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<Db>();
    var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();

    env.WebRootPath ??= Path.Combine(env.ContentRootPath, "wwwroot");
    Directory.CreateDirectory(env.WebRootPath);

    db.Database.Migrate();

    // Import aus wwwroot/Quiz
    var quizRoot = Path.Combine(env.WebRootPath, "Quiz");
    Directory.CreateDirectory(quizRoot);
    TxtImporter.ImportQuestions(db, quizRoot, "Quiz"); // erwartet deine vorhandene TxtImporter.cs

    // Beispiel-Registrierungsschlüssel
    if (!db.RegistrationKeys.Any())
    {
        db.RegistrationKeys.Add(new RegistrationKey { Key = Guid.NewGuid().ToString("N") });
        db.SaveChanges();
        Console.WriteLine($"[Seed] Registrierungsschlüssel: {db.RegistrationKeys.Select(k => k.Key).First()}");
    }
}


// -----------------------------
// AUTH Endpunkte
// -----------------------------
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

    await mail.SendAsync(dto.Email, "Bitte E-Mail bestätigen", $@"
        <p>Hallo,</p>
        <p>Klicke auf den Link, um deine Registrierung zu bestätigen und dich automatisch anzumelden:</p>
        <p><a href=""{WebUtility.HtmlEncode(url)}"">E-Mail jetzt bestätigen</a></p>");

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


// -----------------------------
// QUIZ APIs (geschützt)
// -----------------------------
app.MapGet("/api/chapters", async (Db db) =>
{
    var list = await db.Questions
        .Select(q => q.Chapter)
        .Distinct()
        .OrderBy(x => x)
        .ToListAsync();

    return Results.Ok(list);
}).RequireAuthorization();

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
}).RequireAuthorization();

app.MapPost("/api/submit",
async (Db db, HttpContext ctx, SubmitDTO payload, UserManager<AppUser> users) =>
{
    var uid = ctx.User.Identity?.IsAuthenticated == true
              ? (await users.FindByEmailAsync(ctx.User.Identity!.Name!))?.Id
              : null;

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
            if (!string.IsNullOrEmpty(uid))
            {
                db.Mistakes.Add(new Mistake
                {
                    Id = Guid.NewGuid(),
                    UserId = uid!,
                    QuestionId = q.Id,
                    ChosenChoiceIdsCsv = string.Join(",", chosen),
                    CreatedAt = DateTime.UtcNow
                });
            }

            wrongs.Add(new
            {
                Question = q.Text,
                Your = string.Join(" | ", q.Choices.Where(c => chosen.Contains(c.Id)).Select(c => c.Text)),
                Correct = string.Join(" | ", q.Choices.Where(c => c.IsCorrect).Select(c => c.Text))
            });
        }
    }

    await db.SaveChangesAsync();
    return Results.Ok(new { total = payload.Answers.Count, correct, wrongs });
}).RequireAuthorization();

app.Run();


// =========================================================
// TYPEN / MODELLE (einmalig, sauber, im Namespace)
// =========================================================
namespace QuizWeb
{
    // --- E-Mail ---
    public interface IEmailSender { Task SendAsync(string to, string subject, string html); }

    public class SmtpEmailSender : IEmailSender
    {
        private readonly IConfiguration _cfg;
        public SmtpEmailSender(IConfiguration cfg) => _cfg = cfg;

        public async Task SendAsync(string to, string subject, string html)
        {
            var host = _cfg["SMTP_HOST"];
            var port = int.Parse(_cfg["SMTP_PORT"] ?? "587");
            var user = _cfg["SMTP_USER"];
            var pass = _cfg["SMTP_PASS"];
            var from = _cfg["SMTP_FROM"] ?? user ?? "no-reply@example.com";

            var msg = new MimeMessage();
            msg.From.Add(new MailboxAddress("Quiz", from));
            msg.To.Add(MailboxAddress.Parse(to));
            msg.Subject = subject;
            msg.Body = new BodyBuilder { HtmlBody = html }.ToMessageBody();

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(host, port, MailKit.Security.SecureSocketOptions.StartTls);
            if (!string.IsNullOrWhiteSpace(user)) await smtp.AuthenticateAsync(user, pass);
            await smtp.SendAsync(msg);
            await smtp.DisconnectAsync(true);
        }
    }

    // --- Identity User ---
    public class AppUser : IdentityUser { }

    // --- DbContext ---
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

    // --- Entities ---
    public class Question
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

    // --- DTOs ---
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
