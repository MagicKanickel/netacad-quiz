using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace QuizWeb
{
    public static class TxtImporter
    {
        /// <summary>
        /// Importiert alle Kapitel und Fragen aus wwwroot/Quiz/...
        /// Erwartete Struktur:
        /// wwwroot/
        ///   Quiz/
        ///     CCNA Chapter 1-3 Exam/
        ///       Question 0.txt
        ///       Question 1.txt
        ///       ...
        ///       Images/
        ///         question_18.jpg
        ///         question_19.png
        ///         ...
        /// </summary>
        public static void ImportQuestions(QuizDb db, string quizRoot, string webRootFolderName)
        {
            if (!Directory.Exists(quizRoot))
            {
                Console.WriteLine($"[Import] Quiz root '{quizRoot}' nicht gefunden.");
                return;
            }

            Console.WriteLine($"[Import] Starte Import aus: {quizRoot}");

            // Alte Daten entfernen, damit keine Duplikate entstehen
            if (db.Questions.Any())
            {
                Console.WriteLine($"[Import] Entferne alte Fragen: {db.Questions.Count()}");

                db.Assets.RemoveRange(db.Assets);
                db.Choices.RemoveRange(db.Choices);
                db.Questions.RemoveRange(db.Questions);
                db.SaveChanges();
            }

            var chapterDirs = Directory.GetDirectories(quizRoot);
            if (chapterDirs.Length == 0)
            {
                Console.WriteLine("[Import] Keine Kapitelordner gefunden.");
                return;
            }

            foreach (var chapterDir in chapterDirs)
            {
                var chapterName = Path.GetFileName(chapterDir);
                Console.WriteLine($"[Import] Kapitel: {chapterName}");

                var imagesDir = Path.Combine(chapterDir, "Images");
                var imageFiles = Directory.Exists(imagesDir)
                    ? Directory.GetFiles(imagesDir)
                    : Array.Empty<string>();

                var questionFiles = Directory.GetFiles(chapterDir, "Question *.txt", SearchOption.TopDirectoryOnly)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (questionFiles.Count == 0)
                {
                    Console.WriteLine($"[Import]   Keine Question-*.txt in '{chapterName}' gefunden.");
                    continue;
                }

                foreach (var file in questionFiles)
                {
                    var fileName = Path.GetFileName(file);
                    Console.WriteLine($"[Import]   Frage: {fileName}");

                    var rawLines = File.ReadAllLines(file);
                    var lines = rawLines
                        .Select(l => l.Trim())
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .ToList();

                    if (lines.Count == 0)
                    {
                        Console.WriteLine($"[Import]   -> Datei leer, übersprungen.");
                        continue;
                    }

                    int index = 0;

                    // 1) Frage-Text
                    string questionText = lines[index++];
                    int timeLimit = 30;       // Default
                    int choiceCount = 0;
                    int correctCount = 0;

                    // 2) Zeitlimit (zweite Zeile, Zahl)
                    if (index < lines.Count && int.TryParse(lines[index], out var t))
                    {
                        timeLimit = t;
                        index++;
                    }

                    // 3) Anzahl Antworten
                    if (index < lines.Count && int.TryParse(lines[index], out var c))
                    {
                        choiceCount = c;
                        index++;
                    }

                    // 4) Anzahl korrekter Antworten (optional, wir berechnen später zur Sicherheit nach)
                    if (index < lines.Count && int.TryParse(lines[index], out var cc))
                    {
                        correctCount = cc;
                        index++;
                    }

                    var questionId = Guid.NewGuid();
                    var question = new Question
                    {
                        Id = questionId,
                        Text = questionText,
                        Chapter = chapterName,
                        TimeLimitSeconds = timeLimit,
                        CorrectCount = correctCount,
                        Choices = new List<Choice>(),
                        Assets = new List<QuestionAsset>()
                    };

                    var choices = new List<Choice>();

                    // 5) Antworttexte + true/false
                    while (index < lines.Count)
                    {
                        string choiceText = lines[index++];
                        bool isCorrect = false;

                        if (index < lines.Count && bool.TryParse(lines[index], out var flag))
                        {
                            isCorrect = flag;
                            index++;
                        }

                        var choice = new Choice
                        {
                            Id = Guid.NewGuid(),
                            QuestionId = questionId,
                            Question = question,
                            Text = choiceText,
                            IsCorrect = isCorrect
                        };

                        choices.Add(choice);
                    }

                    // Falls correctCount 0 ist, aus den Choices ableiten
                    if (correctCount <= 0)
                    {
                        correctCount = choices.Count(x => x.IsCorrect);
                    }
                    question.CorrectCount = correctCount;
                    question.Choices = choices;

                    db.Questions.Add(question);
                    db.Choices.AddRange(choices);

                    // 6) Bilder zuordnen: question_XYZ.* -> Question XYZ.txt
                    var match = Regex.Match(fileName, @"Question\s+(\d+)\.txt", RegexOptions.IgnoreCase);
                    if (match.Success && imageFiles.Length > 0)
                    {
                        var num = match.Groups[1].Value; // z.B. "18"
                        var relatedImages = imageFiles.Where(p =>
                        {
                            var fn = Path.GetFileNameWithoutExtension(p).ToLowerInvariant();
                            return fn.Contains($"question_{num}".ToLowerInvariant());
                        });

                        foreach (var img in relatedImages)
                        {
                            // Relativer Pfad aus Sicht von wwwroot, z.B.:
                            // Quiz/CCNA Chapter 1-3 Exam/Images/question_18.jpg
                            var relPath = Path.Combine(
                                    webRootFolderName,
                                    chapterName,
                                    "Images",
                                    Path.GetFileName(img))
                                .Replace("\\", "/");

                            var asset = new QuestionAsset
                            {
                                Id = Guid.NewGuid(),
                                QuestionId = questionId,
                                Question = question,
                                RelativePath = relPath
                            };

                            question.Assets.Add(asset);
                            db.Assets.Add(asset);
                        }
                    }
                }
            }

            db.SaveChanges();
            Console.WriteLine($"[Import] Fertig. Importierte Fragen: {db.Questions.Count()}");
        }
    }
}
