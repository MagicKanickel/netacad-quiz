using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace QuizWeb
{
    public static class TxtImporter
    {
        /// <summary>
        /// Importiert alle Kapitel in quizRoot. Pro Kapitel werden existierende Fragen gelöscht
        /// und aus den Textdateien neu aufgebaut. Bilder werden anhand der Nummer im Dateinamen
        /// mit der Frage verknüpft (Question 18.txt -> Images/question_18.jpg usw.).
        /// </summary>
        /// <param name="db">DbContext</param>
        /// <param name="quizRoot">Physischer Pfad zu wwwroot/Quiz</param>
        /// <param name="webRootFolderName">Meist "Quiz" (für die RelativePath-Bildpfade)</param>
        public static void ImportQuestions(QuizDb db, string quizRoot, string webRootFolderName)
        {
            if (!Directory.Exists(quizRoot)) return;

            // alle Kapitel = alle Unterordner in wwwroot/Quiz
            foreach (var chapterDir in Directory.GetDirectories(quizRoot))
            {
                var chapterName = Path.GetFileName(chapterDir).Trim();

                // 1) Kapitel komplett löschen (inkl. Choices/Assets via Cascade)
                var old = db.Questions.Where(q => q.Chapter == chapterName);
                if (old.Any())
                {
                    db.Questions.RemoveRange(old);
                    db.SaveChanges();
                }

                // 2) Bilderliste des Kapitels ermitteln (optional)
                var imagesDir = Path.Combine(chapterDir, "Images");
                var chapterImages = Directory.Exists(imagesDir)
                    ? Directory.GetFiles(imagesDir, "*.*", SearchOption.TopDirectoryOnly)
                        .Where(p => HasImageExtension(p))
                        .ToList()
                    : new List<string>();

                // 3) Alle geeigneten Frage-Dateien (alle .txt außer wrong.txt)
                var questionFiles = Directory.GetFiles(chapterDir, "*.txt", SearchOption.TopDirectoryOnly)
                    .Where(p => !string.Equals(Path.GetFileName(p), "wrong.txt", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => NumericKeyFromFileName(p))
                    .ToList();

                foreach (var file in questionFiles)
                {
                    var text = File.ReadAllText(file);
                    var parsed = ParseQuestionFile(text);

                    if (parsed is null || parsed.Choices.Count == 0)
                        continue; // überspringen, wenn nicht brauchbar

                    var q = new Question
                    {
                        Id = Guid.NewGuid(),
                        Text = parsed.QuestionText,
                        Chapter = chapterName,
                        TimeLimitSeconds = parsed.TimeLimitSeconds,
                        CorrectCount = 0,
                        Choices = new List<Choice>(),
                        Assets = new List<QuestionAsset>()
                    };

                    // Antworten
                    foreach (var c in parsed.Choices)
                    {
                        q.Choices.Add(new Choice
                        {
                            Id = Guid.NewGuid(),
                            Text = c.Text,
                            IsCorrect = c.IsCorrect
                        });
                    }

                    // Bilder via Nummer im Dateinamen: Question 18.txt -> *18* im Bildnamen
                    var num = NumericKeyFromFileName(file);
                    if (num.HasValue && chapterImages.Count > 0)
                    {
                        var hits = chapterImages
                            .Where(p => FileNameContainsNumber(p, num.Value))
                            .ToList();

                        foreach (var imgPath in hits)
                        {
                            // Relative Path für das Web, z.B. "Quiz/CCNA Chapter 1-3 Exam/Images/question_18.jpg"
                            var rel = Path.Combine(
                                webRootFolderName,
                                chapterName,
                                "Images",
                                Path.GetFileName(imgPath)
                            ).Replace('\\', '/');

                            q.Assets.Add(new QuestionAsset
                            {
                                Id = Guid.NewGuid(),
                                RelativePath = rel
                            });
                        }
                    }

                    db.Questions.Add(q);
                }

                db.SaveChanges();
            }
        }

        // ------------------- Parser -------------------

        private static ParsedQuestion? ParseQuestionFile(string content)
        {
            // Normalisiere Zeilen
            var lines = content
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToList();

            if (lines.Count == 0) return null;

            // Frage = erste nicht-leere Zeile, sofern keine Überschrift "Question:" o.ä.
            string questionText = lines[0];
            if (questionText.StartsWith("Q:", StringComparison.OrdinalIgnoreCase) ||
                questionText.StartsWith("Question:", StringComparison.OrdinalIgnoreCase))
            {
                questionText = questionText.Substring(questionText.IndexOf(':') + 1).Trim();
            }

            // restliche Zeilen = Antworten
            var choices = new List<ParsedChoice>();

            // Erlaubte Marker für Korrekt
            // 1) "+ " oder "* " als Prefix
            // 2) "[x] " (checked)
            // 3) "(correct)" am Ende
            // Alles andere ist falsch.
            for (int i = 1; i < lines.Count; i++)
            {
                var raw = lines[i];

                bool isCorrect = false;
                string text = raw;

                // [x] korrekte
                if (Regex.IsMatch(raw, @"^\[x\]\s*", RegexOptions.IgnoreCase))
                {
                    isCorrect = true;
                    text = Regex.Replace(raw, @"^\[x\]\s*", "", RegexOptions.IgnoreCase);
                }
                // "+ " oder "* " als korrekt
                else if (raw.StartsWith("+ ") || raw.StartsWith("* "))
                {
                    isCorrect = true;
                    text = raw.Substring(2).Trim();
                }
                // "- " oder "[ ] " eher falsch → ohne Spezialbehandlung, nur Prefix abwerfen
                else if (raw.StartsWith("- "))
                {
                    isCorrect = false;
                    text = raw.Substring(2).Trim();
                }
                else if (Regex.IsMatch(raw, @"^\[\s\]\s*"))
                {
                    isCorrect = false;
                    text = Regex.Replace(raw, @"^\[\s\]\s*", "");
                }

                // (correct) am Ende
                var correctSuffix = Regex.Match(text, @"\s*\((correct|richtig)\)\s*$", RegexOptions.IgnoreCase);
                if (correctSuffix.Success)
                {
                    isCorrect = true;
                    text = text.Substring(0, correctSuffix.Index).Trim();
                }

                // Leere Antwort ignorieren
                if (string.IsNullOrWhiteSpace(text)) continue;

                choices.Add(new ParsedChoice { Text = text, IsCorrect = isCorrect });
            }

            // Mindestens 2 Antworten, mind. 1 korrekt
            if (choices.Count < 2 || !choices.Any(c => c.IsCorrect))
                return null;

            return new ParsedQuestion
            {
                QuestionText = questionText,
                TimeLimitSeconds = 60,
                Choices = choices
            };
        }

        // ------------------- Helpers -------------------

        private static bool HasImageExtension(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";
        }

        private static int? NumericKeyFromFileName(string path)
        {
            // nimmt erste Zahl im Dateinamen, z.B. "Question 18.txt" -> 18
            var name = Path.GetFileNameWithoutExtension(path);
            var m = Regex.Match(name, @"(\d+)");
            if (!m.Success) return null;
            if (int.TryParse(m.Groups[1].Value, out var val)) return val;
            return null;
        }

        private static bool FileNameContainsNumber(string path, int number)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            return Regex.IsMatch(name, $@"(^|[^0-9]){number}([^0-9]|$)");
        }

        // ------------------- DTOs -------------------

        private class ParsedQuestion
        {
            public string QuestionText { get; set; } = "";
            public int TimeLimitSeconds { get; set; }
            public List<ParsedChoice> Choices { get; set; } = new();
        }

        private class ParsedChoice
        {
            public string Text { get; set; } = "";
            public bool IsCorrect { get; set; }
        }
    }
}
