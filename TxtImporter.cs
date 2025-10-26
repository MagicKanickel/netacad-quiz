// TxtImporter.cs
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;
using System.ComponentModel.DataAnnotations.Schema;


namespace QuizWeb
{
    /// <summary>
    /// Liest Fragen aus wwwroot/Quiz/<Kapitel>/<Datei>.txt
    /// Format:
    ///   # Frage
    ///   time=30           (optional, Sekunden)
    ///   [x] richtige Antwort
    ///   [ ] falsche Antwort
    ///   [x] richtige Antwort
    ///   ...
    ///   assets=bild1.png;bilder/abb2.jpg  (optional, Semikolon-getrennt; Pfade relativ zum Kapitelordner)
    ///
    /// Jede .txt-Datei erzeugt genau 1 Frage.
    /// </summary>
    public static class TxtImporter
    {
        // Doppelte Einträge vermeiden: wir verwenden einen stabilen "NaturalKey" (Kapitel + Dateiname)
        private static string BuildNaturalKey(string chapter, string fileNameWithoutExt)
            => $"{chapter.Trim()}::{fileNameWithoutExt.Trim()}".ToLowerInvariant();

        public static void ImportQuestions(QuizDb db, string quizRoot, string webRootFolderName)
        {
            if (!Directory.Exists(quizRoot))
                return;

            // Bestehendes Kapitel/Fragen-Cache (für Idempotenz)
            var existing = db.Questions
                .Include(q => q.Choices)
                .Include(q => q.Assets)
                .AsNoTracking()
                .ToList()
                .ToDictionary(q => q.TextKey ?? "", q => q);

            foreach (var chapterDir in Directory.GetDirectories(quizRoot))
            {
                var chapterName = Path.GetFileName(chapterDir) ?? "General";

                foreach (var txtPath in Directory.GetFiles(chapterDir, "*.txt", SearchOption.TopDirectoryOnly))
                {
                    var fileName = Path.GetFileNameWithoutExtension(txtPath);
                    var textKey = BuildNaturalKey(chapterName, fileName);

                    // Datei parsen
                    var raw = File.ReadAllLines(txtPath)
                                  .Where(l => !string.IsNullOrWhiteSpace(l))
                                  .Select(l => l.Trim())
                                  .ToList();

                    if (raw.Count == 0) continue;

                    // 1. Frage (erste Nicht-leere Zeile, die nicht mit "time=" oder "assets=" beginnt)
                    string? question = raw.FirstOrDefault(l => !(l.StartsWith("time=", StringComparison.OrdinalIgnoreCase) ||
                                                                 l.StartsWith("assets=", StringComparison.OrdinalIgnoreCase)));
                    if (question == null) continue;

                    int time = 30;
                    var timeLine = raw.FirstOrDefault(l => l.StartsWith("time=", StringComparison.OrdinalIgnoreCase));
                    if (timeLine != null && int.TryParse(timeLine.Substring(5).Trim(), out var t))
                        time = Math.Clamp(t, 5, 300);

                    // 2. Antworten: Zeilen mit [x] / [ ] am Anfang
                    var choiceRegex = new Regex(@"^\[(x|\s)\]\s*(.+)$", RegexOptions.IgnoreCase);
                    var choices = raw.Select(l => choiceRegex.Match(l))
                                     .Where(m => m.Success)
                                     .Select(m => new
                                     {
                                         IsCorrect = string.Equals(m.Groups[1].Value, "x", StringComparison.OrdinalIgnoreCase),
                                         Text = m.Groups[2].Value.Trim()
                                     })
                                     .ToList();

                    if (choices.Count == 0) continue;

                    // 3. Assets (optional)
                    var assetsLine = raw.FirstOrDefault(l => l.StartsWith("assets=", StringComparison.OrdinalIgnoreCase));
                    var assets = new List<string>();
                    if (assetsLine != null)
                    {
                        var payload = assetsLine.Substring("assets=".Length).Trim();
                        if (!string.IsNullOrWhiteSpace(payload))
                        {
                            assets = payload.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                            .ToList();
                        }
                    }

                    // Relative Pfade für Assets (damit sie unter /Quiz/... erreichbar sind)
                    var relAssets = assets.Select(a =>
                    {
                        // Beispiel: "bild.png" -> "Quiz/<Kapitel>/bild.png"
                        var norm = a.Replace("\\", "/").TrimStart('/');
                        return Path.Combine(webRootFolderName, chapterName, norm).Replace("\\", "/");
                    }).ToList();

                    // Entweder upsert (überschreiben) oder neu einfügen
                    if (existing.TryGetValue(textKey, out var old))
                    {
                        // Upsert: vorhandene Frage ersetzen
                        var q = db.Questions
                                  .Include(q => q.Choices)
                                  .Include(q => q.Assets)
                                  .First(x => x.Id == old.Id);

                        q.Text = question;
                        q.Chapter = chapterName;
                        q.TimeLimitSeconds = time;
                        q.CorrectCount = 0;

                        // Choices ersetzen
                        db.Choices.RemoveRange(q.Choices);
                        q.Choices.Clear();
                        foreach (var c in choices)
                        {
                            q.Choices.Add(new Choice
                            {
                                Id = Guid.NewGuid(),
                                QuestionId = q.Id,
                                Text = c.Text,
                                IsCorrect = c.IsCorrect
                            });
                        }

                        // Assets ersetzen
                        db.Assets.RemoveRange(q.Assets);
                        q.Assets.Clear();
                        foreach (var ra in relAssets)
                        {
                            q.Assets.Add(new QuestionAsset
                            {
                                Id = Guid.NewGuid(),
                                QuestionId = q.Id,
                                RelativePath = ra
                            });
                        }
                        // done
                    }
                    else
                    {
                        var newQ = new Question
                        {
                            Id = Guid.NewGuid(),
                            Text = question,
                            Chapter = chapterName,
                            TimeLimitSeconds = time,
                            CorrectCount = 0,
                            TextKey = textKey,
                            Choices = choices.Select(c => new Choice
                            {
                                Id = Guid.NewGuid(),
                                Text = c.Text,
                                IsCorrect = c.IsCorrect
                            }).ToList(),
                            Assets = relAssets.Select(a => new QuestionAsset
                            {
                                Id = Guid.NewGuid(),
                                RelativePath = a
                            }).ToList()
                        };

                        db.Questions.Add(newQ);
                    }
                }
            }

            db.SaveChanges();
        }
    }

    // Ergänzung im Model für Upsert-Key
    public partial class Question
    {
        [NotMapped]
        // Stabiler Schlüssel (Kapitel + Dateiname); in Migration nicht zwingend erforderlich
        public string? TextKey { get; set; }
    }
}
