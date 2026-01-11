// =========================================================
// USINGs
// =========================================================
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using QuizWeb;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

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
    var cs = builder.Configuration.GetConnectionString("db") ?? "Data Source=quiz.db";
    opts.UseSqlite(cs);
});

builder.Services
    .AddIdentityCore<AppUser>(opt =>
    {
        opt.User.RequireUniqueEmail = true;

        // WICHTIG: du wolltest "ohne E-Mail-Verifizierung" (vorerst)
        opt.SignIn.RequireConfirmedEmail = false;
        opt.SignIn.RequireConfirmedAccount = false;

        opt.Password.RequiredLength = 6;
        opt.Password.RequireDigit = false; // wenn du willst -> true
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
        o.LoginPath = "/login.html";
        o.AccessDeniedPath = "/login.html";
        o.SlidingExpiration = true;
    });

builder.Services.AddAuthorization();

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

var app = builder.Build();

// -----------------------------
// Pipeline
// -----------------------------
app.UseDefaultFiles();   // index.html etc.
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

    env.WebRootPath ??= Path.Combine(env.ContentRootPath, "wwwroot");
    Directory.CreateDirectory(env.WebRootPath);

    var quizRoot = Path.Combine(env.WebRootPath, "Quiz");
    Directory.CreateDirectory(quizRoot);

    db.Database.Migrate();

    // Wenn noch keine Fragen in der DB sind -> einmalig importieren
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
// AUTH Endpunkte
// =========================================================

static bool IsSchoolMail(string email)
{
    // 6 Ziffern + @studierende.htl-donaustadt.at
    return System.Text.RegularExpressions.Regex.IsMatch(
        email ?? "",
        @"^\d{6}@studierende\.htl-donaustadt\.at$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
    );
}

app.MapGet("/api/auth/status", (HttpContext ctx) =>
{
    var ok = ctx.User.Identity?.IsAuthenticated ?? false;
    var email = ok ? ctx.User.Identity?.Name : null;
    return Results.Ok(new AuthStatusDto(ok, email));
});

app.MapPost("/api/auth/register",
async (RegisterDto dto, UserManager<AppUser> users, SignInManager<AppUser> signIn) =>
{
    if (string.IsNullOrWhiteSpace(dto.Email) || !IsSchoolMail(dto.Email))
        return Results.BadRequest(new { error = "Nur Schul-E-Mail erlaubt (z.B. 230050@studierende.htl-donaustadt.at)." });

    if (string.IsNullOrWhiteSpace(dto.Password) || dto.Password.Length < 6)
        return Results.BadRequest(new { error = "Passwort muss mindestens 6 Zeichen lang sein." });

    if (await users.FindByEmailAsync(dto.Email) is not null)
        return Results.BadRequest(new { error = "E-Mail ist bereits registriert." });

    var user = new AppUser { UserName = dto.Email, Email = dto.Email };
    var res = await users.CreateAsync(user, dto.Password);
    if (!res.Succeeded)
        return Results.BadRequest(new { error = string.Join("; ", res.Errors.Select(e => e.Description)) });

    // automatisch einloggen (optional)
    await signIn.SignInAsync(user, isPersistent: dto.RememberMe);

    return Results.Ok(new { ok = true });
});

app.MapPost("/api/auth/login",
async (LoginDto dto, SignInManager<AppUser> signIn, UserManager<AppUser> users) =>
{
    if (string.IsNullOrWhiteSpace(dto.Email) || !IsSchoolMail(dto.Email))
        return Results.BadRequest(new { error = "Nur Schul-E-Mail erlaubt." });

    var user = await users.FindByEmailAsync(dto.Email);
    if (user == null) return Results.BadRequest(new { error = "Falsche E-Mail oder Passwort." });

    var res = await signIn.PasswordSignInAsync(user, dto.Password, isPersistent: dto.RememberMe, lockoutOnFailure: false);
    if (!res.Succeeded) return Results.BadRequest(new { error = "Falsche E-Mail oder Passwort." });

    return Results.Ok(new { ok = true });
});

app.MapPost("/api/auth/logout", async (SignInManager<AppUser> signIn) =>
{
    await signIn.SignOutAsync();
    return Results.Ok(new { ok = true });
}).RequireAuthorization();

// =========================================================
// QUIZ APIs (JETZT MIT LOGIN!)
// =========================================================

// Kapitel-Liste + Metadaten
app.MapGet("/api/chapters", async (Db db) =>
{
    // Nur Namen
    var list = await db.Questions
        .Select(q => q.Chapter)
        .Distinct()
        .OrderBy(x => x)
        .ToListAsync();

    return Results.Ok(list);
}).RequireAuthorization();

// Fragen für ein Kapitel
app.MapGet("/api/quiz", async (Db db, string chapter) =>
{
    if (string.IsNullOrWhiteSpace(chapter))
        return Results.BadRequest(new { error = "chapter fehlt." });

    var rng = new Random();

    var questions = await db.Questions
        .Include(x => x.Choices)
        .Include(x => x.Assets)
        .Where(x => x.Chapter == chapter)
        .OrderBy(_ => EF.Functions.Random())
        .ToListAsync();

    foreach (var item in questions)
        item.Choices = item.Choices.OrderBy(_ => rng.Next()).ToList();

    // DTO: choices = [{id,text}] und assets = ["/..."]
    var dto = questions.Select(item => new
    {
        id = item.Id,
        text = item.Text,
        chapter = item.Chapter,
        timeLimitSeconds = item.TimeLimitSeconds,
        // MULTI: wie viele richtige Antworten sind dabei?
        correctRequired = item.Choices.Count(c => c.IsCorrect),
        choices = item.Choices.Select(c => new { id = c.Id, text = c.Text }),
        assets = item.Assets.Select(a => "/" + a.RelativePath)
    });

    return Results.Ok(dto);
}).RequireAuthorization();

// Progress-Übersicht pro User (Dashboard)
app.MapGet("/api/progress/chapters",
async (HttpContext ctx, Db db) =>
{
    var userId = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

    // alle chapters mit counts
    var chapters = await db.Questions
        .GroupBy(q => q.Chapter)
        .Select(g => new
        {
            chapter = g.Key,
            questionCount = g.Count(),
            totalTimeSeconds = g.Sum(x => x.TimeLimitSeconds)
        })
        .OrderBy(x => x.chapter)
        .ToListAsync();

    // stats pro question
    var stats = await db.UserQuestionStats
        .Where(s => s.UserId == userId)
        .ToListAsync();

    // Map QuestionId -> Stat
    var statByQ = stats.ToDictionary(s => s.QuestionId, s => s);

    // Für jedes Kapitel: wie viele Fragen wurden jemals korrekt beantwortet?
    var questions = await db.Questions
        .Select(q => new { q.Id, q.Chapter })
        .ToListAsync();

    var correctEverByChapter = questions
        .GroupBy(q => q.Chapter)
        .ToDictionary(
            g => g.Key,
            g => g.Count(q => statByQ.TryGetValue(q.Id, out var st) && st.CorrectEver)
        );

    var timeoutEverByChapter = questions
        .GroupBy(q => q.Chapter)
        .ToDictionary(
            g => g.Key,
            g => g.Count(q => statByQ.TryGetValue(q.Id, out var st) && st.TimeoutCount > 0)
        );

    var dto = chapters.Select(ch =>
    {
        var correctEver = correctEverByChapter.TryGetValue(ch.chapter, out var c) ? c : 0;
        var timeouts = timeoutEverByChapter.TryGetValue(ch.chapter, out var t) ? t : 0;

        var percent = ch.questionCount == 0 ? 0 : (int)Math.Round(100.0 * correctEver / ch.questionCount);
        var completed = correctEver >= ch.questionCount && ch.questionCount > 0;

        return new
        {
            ch.chapter,
            ch.questionCount,
            totalMinutes = (int)Math.Ceiling(ch.totalTimeSeconds / 60.0),
            correctEver,
            percent,
            completed,
            hasTimeouts = timeouts > 0
        };
    });

    return Results.Ok(dto);
}).RequireAuthorization();

// Antwort-Submission + Speichern pro User
app.MapPost("/api/submit",
async (HttpContext ctx, Db db, SubmitDTO payload) =>
{
    var userId = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

    int correct = 0;
    int timedOut = 0;
    var wrongs = new List<object>();

    foreach (var ans in payload.Answers)
    {
        var q = await db.Questions.Include(x => x.Choices)
                                  .FirstOrDefaultAsync(x => x.Id == ans.QuestionId);
        if (q == null) continue;

        // Timeout?
        if (ans.TimedOut)
        {
            timedOut++;
            await UpsertStat(db, userId, q.Id, correctEver: false, isTimeout: true);
            continue;
        }

        var chosen = (ans.ChoiceIds ?? new List<Guid>()).ToHashSet();
        var correctSet = q.Choices.Where(c => c.IsCorrect).Select(c => c.Id).ToHashSet();

        bool ok = chosen.SetEquals(correctSet);
        if (ok) correct++;

        await UpsertStat(db, userId, q.Id, correctEver: ok, isTimeout: false, wasWrong: !ok);

        if (!ok)
        {
            wrongs.Add(new
            {
                question = q.Text,
                your = string.Join(" | ", q.Choices.Where(c => chosen.Contains(c.Id)).Select(c => c.Text)),
                correct = string.Join(" | ", q.Choices.Where(c => c.IsCorrect).Select(c => c.Text))
            });
        }
    }

    await db.SaveChangesAsync();

    return Results.Ok(new
    {
        total = payload.Answers.Count,
        correct,
        timedOut,
        wrongs
    });
}).RequireAuthorization();

// Reset (Profil -> Einstellungen)
app.MapPost("/api/progress/reset", async (HttpContext ctx, Db db) =>
{
    var userId = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

    var rows = await db.UserQuestionStats.Where(x => x.UserId == userId).ToListAsync();
    db.UserQuestionStats.RemoveRange(rows);
    await db.SaveChangesAsync();

    return Results.Ok(new { ok = true });
}).RequireAuthorization();

app.Run();

// =========================================================
// Helpers
// =========================================================
static async Task UpsertStat(Db db, string userId, Guid qid, bool correctEver, bool isTimeout, bool wasWrong = false)
{
    var st = await db.UserQuestionStats.FirstOrDefaultAsync(x => x.UserId == userId && x.QuestionId == qid);
    if (st == null)
    {
        st = new UserQuestionStat
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            QuestionId = qid,
            CorrectEver = false,
            WrongCount = 0,
            TimeoutCount = 0,
            LastAnsweredAtUtc = DateTime.UtcNow
        };
        await db.UserQuestionStats.AddAsync(st);
    }

    st.LastAnsweredAtUtc = DateTime.UtcNow;

    if (isTimeout) st.TimeoutCount++;
    if (wasWrong) st.WrongCount++;

    if (correctEver) st.CorrectEver = true;
}

// =========================================================
// NAMESPACE + MODELLE
// =========================================================
namespace QuizWeb
{
    public class AppUser : IdentityUser { }

    public class QuizDb : IdentityDbContext<AppUser>
    {
        public QuizDb(DbContextOptions<QuizDb> opt) : base(opt) { }

        public DbSet<Question> Questions => Set<Question>();
        public DbSet<Choice> Choices => Set<Choice>();
        public DbSet<QuestionAsset> Assets => Set<QuestionAsset>();

        public DbSet<UserQuestionStat> UserQuestionStats => Set<UserQuestionStat>();

        protected override void OnModelCreating(ModelBuilder b)
        {
            base.OnModelCreating(b);

            b.Entity<Question>().HasKey(x => x.Id);
            b.Entity<Choice>().HasKey(x => x.Id);
            b.Entity<QuestionAsset>().HasKey(x => x.Id);

            b.Entity<UserQuestionStat>().HasKey(x => x.Id);
            b.Entity<UserQuestionStat>()
                .HasIndex(x => new { x.UserId, x.QuestionId })
                .IsUnique();

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

    // Entities
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

    // Progress pro User / Frage
    public class UserQuestionStat
    {
        public Guid Id { get; set; }
        public string UserId { get; set; } = "";
        public Guid QuestionId { get; set; }

        public bool CorrectEver { get; set; }
        public int WrongCount { get; set; }
        public int TimeoutCount { get; set; }
        public DateTime LastAnsweredAtUtc { get; set; }
    }

    // DTOs
    public record RegisterDto(string Email, string Password, bool RememberMe);
    public record LoginDto(string Email, string Password, bool RememberMe);
    public record AuthStatusDto(bool IsAuthenticated, string? Email);

    public class SubmitDTO
    {
        public List<SubmitAnswer> Answers { get; set; } = new();
    }

    public class SubmitAnswer
    {
        public Guid QuestionId { get; set; }
        public List<Guid> ChoiceIds { get; set; } = new();

        // NEU: Timeout-Flag
        public bool TimedOut { get; set; }
    }
}
