using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Net;
using QuizWeb;

var builder = WebApplication.CreateBuilder(args);

// -------------------------------------------------------------
// Services
// -------------------------------------------------------------
builder.Services.AddDbContext<Db>(opts =>
{
    opts.UseSqlite(builder.Configuration.GetConnectionString("db") ??
                   "Data Source=quiz.db");
});

builder.Services.AddIdentity<AppUser, IdentityRole>(opts =>
{
    opts.User.RequireUniqueEmail = true;
})
.AddEntityFrameworkStores<Db>()
.AddDefaultTokenProviders();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddAuthentication();
builder.Services.AddAuthorization();
builder.Services.AddScoped<IEmailSender, BrevoEmailSender>();

var app = builder.Build();

// -------------------------------------------------------------
// STATIC FILES
// -------------------------------------------------------------
app.UseDefaultFiles();
app.UseStaticFiles();

// -------------------------------------------------------------
// DATABASE + SEEDING
// -------------------------------------------------------------
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<Db>();
    var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();

    env.WebRootPath ??= Path.Combine(env.ContentRootPath, "wwwroot");
    Directory.CreateDirectory(env.WebRootPath);

    db.Database.Migrate();

    // ---------------------------------------------------------
    // DEMO QUIZ — nur anlegen, wenn die DB noch leer ist
    // ---------------------------------------------------------
    if (!db.Questions.Any())
    {
        Console.WriteLine("[Seed] Creating demo quiz...");

        var chapterName = "Demo – CCNA Kapitel 1";

        var q1 = new Question
        {
            Id = Guid.NewGuid(),
            Chapter = chapterName,
            Text = "Wie viele Schichten hat das OSI-Modell?",
            TimeLimitSeconds = 60,
            Choices = new List<Choice>
            {
                new Choice { Id = Guid.NewGuid(), Text = "4", IsCorrect = false },
                new Choice { Id = Guid.NewGuid(), Text = "7", IsCorrect = true },
                new Choice { Id = Guid.NewGuid(), Text = "5", IsCorrect = false },
                new Choice { Id = Guid.NewGuid(), Text = "8", IsCorrect = false }
            }
        };

        var q2 = new Question
        {
            Id = Guid.NewGuid(),
            Chapter = chapterName,
            Text = "Welche Schicht ist für MAC-Adressen zuständig?",
            TimeLimitSeconds = 60,
            Choices = new List<Choice>
            {
                new Choice { Id = Guid.NewGuid(), Text = "Transportschicht", IsCorrect = false },
                new Choice { Id = Guid.NewGuid(), Text = "Sicherungsschicht", IsCorrect = true },
                new Choice { Id = Guid.NewGuid(), Text = "Anwendungsschicht", IsCorrect = false },
                new Choice { Id = Guid.NewGuid(), Text = "Netzwerkschicht", IsCorrect = false }
            }
        };

        db.Questions.AddRange(q1, q2);
        db.SaveChanges();

        Console.WriteLine("[Seed] Demo quiz created.");
    }

    // ---------------------------------------------------------
    // TXT Importer vorbereiten (läuft erst wieder aktiv später)
    // ---------------------------------------------------------
    var quizRoot = Path.Combine(env.WebRootPath, "Quiz");
    Directory.CreateDirectory(quizRoot);
    TxtImporter.ImportQuestions(db, quizRoot, "Quiz");
}

// -------------------------------------------------------------
// API ENDPOINTS — OHNE LOGIN
// -------------------------------------------------------------

// 🎯 1) Kapitel laden
app.MapGet("/api/chapters", async (Db db) =>
{
    var chapters = await db.Questions
        .Select(q => q.Chapter)
        .Distinct()
        .OrderBy(x => x)
        .ToListAsync();

    return Results.Ok(chapters);
});

// 🎯 2) Fragen eines Kapitels laden
app.MapGet("/api/quiz", async (Db db, string? chapter) =>
{
    var rng = new Random();

    var q = db.Questions
        .Include(x => x.Choices)
        .Include(x => x.Assets)
        .AsQueryable();

    if (!string.IsNullOrWhiteSpace(chapter))
        q = q.Where(x => x.Chapter == chapter);

    var list = await q.OrderBy(_ => EF.Functions.Random()).ToListAsync();

    foreach (var item in list)
        item.Choices = item.Choices.OrderBy(_ => rng.Next()).ToList();

    var dto = list.Select(item => new
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

// 🎯 3) Quiz auswerten (ohne Benutzer)
app.MapPost("/api/submit", async (Db db, SubmitDTO payload) =>
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

// -------------------------------------------------------------
app.Run();
