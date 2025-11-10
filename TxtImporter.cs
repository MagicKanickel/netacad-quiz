using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace QuizWeb
{
    public static class TxtImporter
    {
        /// <summary>
        /// Liest alle Kapitel aus wwwroot/Quiz und legt Fragen + Antworten in der DB an.
        /// Passt zum Aufbau deines ZIP (Ordner = Kapitel, darin Question X.txt, optional Images/).
        /// </summary>
        public static int ImportQuestions(QuizDb db, string webRoot, string quizFolder = "Quiz")
        {
            var quizRoot = Path.Combine(webRoot, quizFolder);
            if (!Directory.Exists(quizRoot))
                return 0;

            int added = 0;

            // alle Kapitelordner: /wwwroot/Quiz/<Kapitel>
            foreach (var chapterDir in Directory.GetDirectories(quizRoot))
            {
                var chapterName = Path.GetFileName(chapterDir);

                // nur echte Question-*.txt, keine .txt.br / .txt.gz
                var questionFiles = Directory
                    .GetFiles(chapterDir, "*.txt", SearchOption.TopDirectoryOnly)
                    .Where(p =>
                    {
                        var f = Path.GetFileName(p);
                        if (f.EndsWith(".txt.br", StringComparison.OrdinalIgnoreCase)) return false;
                        if (f.EndsWith(".txt.gz", StringComparison.OrdinalIgnoreCase)) return false;
                        return f.StartsWith("Question", StringComparison.OrdinalIgnoreCase);
                    })
                    .OrderBy(p => p)
                    .ToList();

                foreach (var file in questionFiles)
                {
                    var textKey = $"{chapterName}/{Path.GetFileName(file)}";

                    // schon vorhanden? -> überspringen
                    var existing = db.Questions
                        .Include(q => q.Choices)
                        .FirstOrDefault(q => q.TextKey == textKey);

                    if (existing != null)
                        continue;

                    var parsed = ParseQuestionFile(file);
                    if (parsed == null)
                        continue;

                    var (questionText, timeLimit, choices) = parsed.Value;

                    var q = new Question
                    {
                        Id = Guid.NewGuid(),
                        Chapter = chapterName,
                        Text = questionText,
                        TimeLimitSeconds = timeLimit,
                        TextKey = textKey,
                        Choices = choices.Select(c => new Choice
                        {
                            Id = Guid.NewGuid(),
                            Text = c.text,
                            IsCorrect = c.isCorrect
                        }).ToList()
                    };

                    db.Questions.Add(q);
                    added++;
                }

                // Bilder zuordnen (wenn vorhanden)
                var imagesDir = Path.Combine(chapterDir, "Images");
                if (Directory.Exists(imagesDir))
                {
                    foreach (var imgPath in Directory.GetFiles(imagesDir))
                    {
                        var imgFile = Path.GetFileNameWithoutExtension(imgPath);

                        // Nummer aus dem Dateinamen holen (question_18, Question 28, …)
                        var m = Regex.Match(imgFile, @"(\d+)");
                        if (!m.Success) continue;

                        var number = m.Groups[1].Value; // "18"
                        var questionFileName = $"Question {number}.txt";
                        var textKey = $"{chapterName}/{questionFileName}";

                        var q = db.Questions.FirstOrDefault(x => x.TextKey == textKey);
                        if (q == null) continue;

                        var relPath = Path.GetRelativePath(webRoot, imgPath).Replace("\\", "/");
                        var already = db.Assets.Any(a => a.QuestionId == q.Id && a.RelativePath == relPath);
                        if (!already)
                        {
                            db.Assets.Add(new QuestionAsset
                            {
                                Id = Guid.NewGuid(),
                                QuestionId = q.Id,
                                RelativePath = relPath
                            });
                        }
                    }
                }
            }

            db.SaveChanges();
            return added;
        }

        /// <summary>
        /// Erwartetes Format:
        /// Zeile 1: Frage
        /// Zeile 2: Zeit (z.B. 20)
        /// Zeile 3: Anzahl Antworten (z.B. 4)
        /// optional Zeile 4: eine weitere Zahl -> überspringen
        /// danach Paare: Antworttext + "true"/"false"
        /// </summary>
        private static (string question, int time, List<(string text, bool isCorrect)> choices)? ParseQuestionFile(string path)
        {
            var allLines = File.ReadAllLines(path)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToList();

            if (allLines.Count < 3)
                return null;

            string question = allLines[0];
            int time = int.TryParse(allLines[1], out var t) ? t : 0;
            int choiceCount = int.TryParse(allLines[2], out var c) ? c : 0;

            int idx = 3;

            // evtl. zusätzliche Zahl überspringen
            if (idx < allLines.Count && int.TryParse(allLines[idx], out _)
                && (allLines.Count - (idx + 1)) >= choiceCount * 2)
            {
                idx++;
            }

            var choices = new List<(string text, bool isCorrect)>();

            for (int i = 0; i < choiceCount && idx < allLines.Count; i++)
            {
                var answerText = allLines[idx++];
                bool isCorrect = false;
                if (idx < allLines.Count)
                {
                    var flag = allLines[idx++];
                    isCorrect = flag.Equals("true", StringComparison.OrdinalIgnoreCase)
                                || flag.Equals("1")
                                || flag.Equals("yes", StringComparison.OrdinalIgnoreCase);
                }

                choices.Add((answerText, isCorrect));
            }

            if (choices.Count == 0)
                return null;

            return (question, time, choices);
        }
    }

    /// <summary>
    /// Erweiterung der vorhandenen Question-Klasse um TextKey.
    /// Weil deine eigentlichen Entities in Program.cs stehen,
    /// machen wir sie hier einfach partial.
    /// </summary>
    public partial class Question
    {
        public string? TextKey { get; set; }
    }
}
