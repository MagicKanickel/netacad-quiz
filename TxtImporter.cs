using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QuizWeb
{
    /// <summary>
    /// Sehr einfacher Importer:
    /// - durchsucht wwwroot/Quiz nach Kapitelordnern
    /// - liest alle QuestionX.txt Dateien
    /// - erste nichtleere Zeile = Frage
    /// - alle folgenden nichtleeren Zeilen = Antworten
    /// - erste Antwort wird als korrekt markiert
    /// - zugehöriges Bild: Images/question_X.* (falls vorhanden)
    /// </summary>
    /// 

    // Wo muss der Quiz-Ordner liegen?
    // Z.B. wwwroot/Quiz
    // Was ist der aktuelle Pfad?
    // Directory.GetCurrentDirectory()

    // Was ist genau wwwroot?
    // In ASP.NET Core ist wwwroot der Standardordner für statische Dateien.
    // Liegt wwwroot neben der .exe Datei.
    // Z.B. bei Debug-Ausführung: bin/Debug/net9.0/wwwroot
    public static class TxtImporter
    {
        public static int ImportAll(QuizDb db, string quizRoot, Action<string>? log = null)
        {
            log ??= _ => { };

            if (!Directory.Exists(quizRoot))
            {
                log($"[Import] Quiz-Root '{quizRoot}' nicht gefunden.");
                return 0;
            }

            var chapterDirs = Directory.GetDirectories(quizRoot);
            if (chapterDirs.Length == 0)
            {
                log($"[Import] Keine Kapitelordner in '{quizRoot}' gefunden.");
                return 0;
            }

            int added = 0;

            foreach (var chapterDir in chapterDirs.OrderBy(p => p))
            {
                var chapterName = Path.GetFileName(chapterDir);
                if (string.IsNullOrWhiteSpace(chapterName))
                    continue;

                log($"[Import] Kapitel: {chapterName}");

                // Alle Question*.txt außer wrong.txt
                var questionFiles = Directory
                    .EnumerateFiles(chapterDir, "*.txt", SearchOption.TopDirectoryOnly)
                    .Where(p =>
                    {
                        var name = Path.GetFileName(p);
                        return name.StartsWith("Question", StringComparison.OrdinalIgnoreCase)
                               && !name.Equals("wrong.txt", StringComparison.OrdinalIgnoreCase);
                    })
                    .OrderBy(p => p)
                    .ToList();

                foreach (var file in questionFiles)
                {
                    try
                    {
                        added += ImportSingleFile(db, chapterName, chapterDir, file, log);
                    }
                    catch (Exception ex)
                    {
                        log($"[Import] Fehler bei Datei '{file}': {ex.Message}");
                    }
                }
            }

            if (added > 0)
            {
                db.SaveChanges();
            }

            log($"[Import] Fertig. Neu hinzugefügt: {added} Fragen.");
            return added;
        }

        private static int ImportSingleFile(
            QuizDb db,
            string chapterName,
            string chapterDir,
            string filePath,
            Action<string> log)
        {
            var lines = File.ReadAllLines(filePath)
                            .Select(l => l.Trim())
                            .Where(l => !string.IsNullOrWhiteSpace(l))
                            .ToList();

            if (lines.Count == 0)
            {
                log($"[Import] Datei '{filePath}' leer, übersprungen.");
                return 0;
            }

            // Frage = erste Zeile
            var questionText = lines[0];
            var choiceLines = lines.Skip(1).ToList();

            if (choiceLines.Count == 0)
            {
                // Wenn keine Antworten da sind, machen wir eine Dummy-Antwort
                choiceLines.Add("OK");
            }

            // Prüfen, ob eine ähnliche Frage schon existiert (grobe Dublettenvermeidung)
            bool exists = db.Questions.Any(q =>
                q.Chapter == chapterName &&
                q.Text == questionText);

            if (exists)
            {
                log($"[Import] Frage bereits vorhanden: '{questionText}'");
                return 0;
            }

            var q = new Question
            {
                Id = Guid.NewGuid(),
                Chapter = chapterName,
                Text = questionText,
                TimeLimitSeconds = 60,   // später fein einstellbar
                CorrectCount = 1,
                Choices = new List<Choice>(),
                Assets = new List<QuestionAsset>()
            };

            // Antworten anlegen – erste Antwort als korrekt markieren
            bool first = true;
            foreach (var raw in choiceLines)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                var text = CleanupChoiceText(raw);

                q.Choices.Add(new Choice
                {
                    Id = Guid.NewGuid(),
                    QuestionId = q.Id,
                    Question = q,
                    Text = text,
                    IsCorrect = first  // erste = korrekt
                });

                first = false;
            }

            // Versuchen, ein Bild zuzuordnen
            TryAttachImage(q, chapterDir, filePath);

            db.Questions.Add(q);
            return 1;
        }

        private static string CleanupChoiceText(string line)
        {
            // Häufige Präfixe wie "A) ", "1. ", "- " entfernen
            var trimmed = line.TrimStart('-', '•', '–', '*', '\t', ' ');

            // Dinge wie "A) ", "a) ", "1) ", "1." entfernen
            int idx = trimmed.IndexOf(')');
            if (idx == 1 || idx == 2)
            {
                return trimmed[(idx + 1)..].Trim();
            }

            idx = trimmed.IndexOf('.');
            if (idx == 1 || idx == 2)
            {
                return trimmed[(idx + 1)..].Trim();
            }

            return trimmed;
        }

        private static void TryAttachImage(Question q, string chapterDir, string questionFilePath)
        {
            try
            {
                var imagesDir = Path.Combine(chapterDir, "Images");
                if (!Directory.Exists(imagesDir))
                    return;

                var fileName = Path.GetFileNameWithoutExtension(questionFilePath); // z.B. "Question 0"
                // Nummer extrahieren
                var numPart = new string(fileName.Where(char.IsDigit).ToArray());

                if (string.IsNullOrEmpty(numPart))
                    return;

                var baseName = $"question_{numPart}";
                var exts = new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp" };

                foreach (var ext in exts)
                {
                    var imgPath = Path.Combine(imagesDir, baseName + ext);
                    if (File.Exists(imgPath))
                    {
                        // Relative Pfad, wie er später vom Browser geladen wird
                        // Beispiel: "Quiz/CCNA Chapter 1-3 Exam/Images/question_0.png"
                        var relative = Path.Combine(
                            "Quiz",
                            Path.GetFileName(chapterDir),
                            "Images",
                            Path.GetFileName(imgPath)
                        ).Replace("\\", "/");

                        q.Assets.Add(new QuestionAsset
                        {
                            Id = Guid.NewGuid(),
                            QuestionId = q.Id,
                            Question = q,
                            RelativePath = relative
                        });

                        break;
                    }
                }
            }
            catch
            {
                // Bilder sind "nice to have" – Fehler hier ignorieren
            }
        }
    }
}
