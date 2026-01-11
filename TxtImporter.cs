using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QuizWeb
{
    public static class TxtImporter
    {
        // Importiert alle Kapitel-Unterordner in wwwroot/Quiz
        // Erwartet Struktur:
        //  wwwroot/Quiz/<Kapitelname>/Question1.txt, Question2.txt, ... (+ evtl. Bilder im selben Ordner)
        public static int ImportAll(QuizDb db, string quizRoot, Action<string>? log = null)
        {
            if (!Directory.Exists(quizRoot))
            {
                log?.Invoke($"[Import] Quiz-Root existiert nicht: {quizRoot}");
                return 0;
            }

            int added = 0;

            foreach (var chapterDir in Directory.GetDirectories(quizRoot))
            {
                var chapterName = Path.GetFileName(chapterDir);
                if (string.IsNullOrWhiteSpace(chapterName)) continue;

                log?.Invoke($"[Import] Kapitel: {chapterName}");

                // alle Question*.txt (egal ob Question1.txt oder Question001.txt)
                var files = Directory.GetFiles(chapterDir, "Question*.txt", SearchOption.TopDirectoryOnly)
                                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                                     .ToList();

                foreach (var file in files)
                {
                    try
                    {
                        var parsed = ParseQuestionFile(file, chapterName, log);
                        if (parsed == null) continue;

                        // Duplikate vermeiden: gleiche Frage + Kapitel
                        // (optional kannst du auch Hash/Guid pro File machen – so ist es simpel)
                        bool exists = db.Questions.Any(q => q.Chapter == chapterName && q.Text == parsed.Text);
                        if (exists)
                        {
                            log?.Invoke($"[Import] Skip (existiert): {Path.GetFileName(file)}");
                            continue;
                        }

                        db.Questions.Add(parsed);
                        added++;
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke($"[Import] Fehler in {Path.GetFileName(file)}: {ex.Message}");
                    }
                }

                // pro Kapitel speichern (schneller als pro Frage)
                db.SaveChanges();
            }

            return added;
        }

        private static Question? ParseQuestionFile(string filePath, string chapterName, Action<string>? log)
        {
            // Wichtig: LEERE Zeilen ignorieren, aber Fragezeile beibehalten
            var lines = File.ReadAllLines(filePath)
                            .Select(l => (l ?? "").TrimEnd('\r', '\n'))
                            .ToList();

            // entferne komplett leere Zeilen (die stören sonst das Indexing)
            lines = lines.Select(l => l.Trim()).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

            if (lines.Count < 6)
            {
                log?.Invoke($"[Parse] Zu wenig Zeilen ({lines.Count}) in {Path.GetFileName(filePath)}");
                return null;
            }

            string questionText = lines[0].Trim();

            if (!int.TryParse(lines[1].Trim(), out int timeLimitSeconds))
                timeLimitSeconds = 0;

            if (!int.TryParse(lines[2].Trim(), out int answerCount))
            {
                log?.Invoke($"[Parse] Ungültiger AnswerCount in {Path.GetFileName(filePath)}: '{lines[2]}'");
                return null;
            }

            if (!int.TryParse(lines[3].Trim(), out int correctCount))
                correctCount = 1;

            int expectedMin = 4 + (answerCount * 2);
            if (lines.Count < expectedMin)
            {
                log?.Invoke($"[Parse] Datei hat zu wenig Daten für {answerCount} Antworten: {Path.GetFileName(filePath)}");
                return null;
            }

            var q = new Question
            {
                Id = Guid.NewGuid(),
                Chapter = chapterName,
                Text = questionText,
                TimeLimitSeconds = timeLimitSeconds,
                CorrectCount = correctCount,
                Choices = new List<Choice>(),
                Assets = new List<QuestionAsset>()
            };

            // Antworten lesen: (Text, true/false) * answerCount
            int idx = 4;
            for (int i = 0; i < answerCount; i++)
            {
                string ansText = lines[idx++].Trim();
                string flag = lines[idx++].Trim();

                bool isCorrect = false;
                if (!bool.TryParse(flag, out isCorrect))
                {
                    // falls mal "TRUE"/"FALSE"/"True"/etc → TryParse deckt das ab,
                    // aber falls Sonderfälle, kannst du hier erweitern.
                    isCorrect = string.Equals(flag, "1", StringComparison.OrdinalIgnoreCase);
                }

                q.Choices.Add(new Choice
                {
                    Id = Guid.NewGuid(),
                    QuestionId = q.Id,
                    Text = ansText,
                    IsCorrect = isCorrect
                });
            }

            // Restliche Zeilen: optional Assets
            // Variante A: erste Restzeile ist Zahl = assetCount, danach assetCount Dateinamen
            // Variante B: Restzeilen enthalten direkt Dateinamen (png/jpg/…)
            var rest = lines.Skip(idx).Select(x => x.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            if (rest.Count > 0)
            {
                int assetCount;
                int start = 0;

                if (int.TryParse(rest[0], out assetCount) && assetCount > 0 && rest.Count >= 1 + assetCount)
                {
                    start = 1;
                    for (int i = 0; i < assetCount; i++)
                    {
                        TryAddAsset(q, Path.GetDirectoryName(filePath)!, rest[start + i], log);
                    }
                }
                else
                {
                    foreach (var token in rest)
                        TryAddAsset(q, Path.GetDirectoryName(filePath)!, token, log);
                }
            }

            // Sanity: wenn correctCount nicht passt, trotzdem nicht crashen – nur loggen
            int realCorrect = q.Choices.Count(c => c.IsCorrect);
            if (correctCount > 0 && realCorrect != correctCount)
            {
                log?.Invoke($"[Parse] Warnung: CorrectCount={correctCount}, aber gefunden={realCorrect} in {Path.GetFileName(filePath)}");
                // Du kannst hier auch q.CorrectCount = realCorrect setzen, wenn du willst.
            }

            return q;
        }

        private static void TryAddAsset(Question q, string baseDir, string assetLine, Action<string>? log)
        {
            // Nur Bilddateien akzeptieren (falls später mehr, erweitern)
            string lower = assetLine.ToLowerInvariant();
            bool looksLikeImage =
                lower.EndsWith(".png") || lower.EndsWith(".jpg") || lower.EndsWith(".jpeg") ||
                lower.EndsWith(".gif") || lower.EndsWith(".webp");

            if (!looksLikeImage) return;

            // assetLine kann Dateiname oder relativer Pfad sein
            string abs = Path.IsPathRooted(assetLine) ? assetLine : Path.Combine(baseDir, assetLine);

            if (!File.Exists(abs))
            {
                log?.Invoke($"[Asset] Datei nicht gefunden: {assetLine}");
                return;
            }

            // Wir speichern den Pfad relativ zu wwwroot (dein Import-Root ist wwwroot/Quiz/...)
            // Also: ".../wwwroot/Quiz/<Kapitel>/<bild>" => "Quiz/<Kapitel>/<bild>"
            // Heuristik: finde "wwwroot" im Pfad
            string rel = MakeRelativeToWwwRoot(abs);
            if (string.IsNullOrWhiteSpace(rel))
            {
                // fallback: nur Dateiname in Kapitel-Ordner
                rel = $"Quiz/{q.Chapter}/{Path.GetFileName(abs)}";
            }

            // Duplikate verhindern
            if (q.Assets.Any(a => string.Equals(a.RelativePath, rel, StringComparison.OrdinalIgnoreCase)))
                return;

            q.Assets.Add(new QuestionAsset
            {
                Id = Guid.NewGuid(),
                QuestionId = q.Id,
                RelativePath = rel.Replace("\\", "/")
            });
        }

        private static string MakeRelativeToWwwRoot(string absolutePath)
        {
            // versucht ".../wwwroot/<...>" zu extrahieren
            var norm = absolutePath.Replace('\\', '/');
            var marker = "/wwwroot/";
            var idx = norm.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return "";

            return norm.Substring(idx + marker.Length);
        }
    }
}
