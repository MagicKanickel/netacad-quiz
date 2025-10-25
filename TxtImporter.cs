// TxtImporter.cs
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace QuizWeb
{
    public static class TxtImporter
    {
        // ACHTUNG: Hier explizit der DbContext-Typ aus unserem Namespace
        public static void ImportQuestions(QuizDb db, string quizRoot, string webRootFolderName)
        {
            // --- Beispiel: deine bestehende Logik hier einfügen ---
            // Grundstruktur (nur als Platzhalter; benutze deine vorhandene Implementierung):

            if (!Directory.Exists(quizRoot)) return;

            // Optional: Kapitelweise neu importieren -> bestehendes Kapitel löschen, danach neu einlesen
            // foreach (var chapterDir in Directory.GetDirectories(quizRoot)) { ... }

            // Falls deine vorhandene Methode bereits korrekt ist:
            // --> Lass den bisherigen Code einfach drin.
        }
    }
}
