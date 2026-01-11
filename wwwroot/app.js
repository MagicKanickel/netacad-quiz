// app.js – Navigation, Login (mit Passwort) & Quiz-Logik

// ----------------- Helpers -----------------

function $(sel) {
    return document.querySelector(sel);
}

function showError(el, msg) {
    if (!el) return;
    el.textContent = msg || "";
    el.style.display = msg ? "block" : "none";
}

// Clientseitige Session (nur Browser, kein echtes Konto)
const SESSION_KEY = "netacadQuizSession";

function saveSession(email, remember) {
    const session = { email, createdAt: Date.now() };
    try {
        localStorage.setItem(SESSION_KEY, JSON.stringify(session));
    } catch { /* ignore */ }

    const maxAge = remember ? 60 * 60 * 24 * 30 : 60 * 60 * 4; // 30d / 4h
    document.cookie = `netacadQuizAuth=1; path=/; max-age=${maxAge}`;
}

function loadSession() {
    try {
        const raw = localStorage.getItem(SESSION_KEY);
        if (!raw) return null;
        return JSON.parse(raw);
    } catch {
        return null;
    }
}

function requireLogin() {
    const s = loadSession();
    if (!s || !s.email) {
        window.location.href = "login.html";
        return null;
    }
    return s;
}

// ----------------- Menü -----------------

function setupMenu() {
    const toggle = $(".menu-toggle");
    const nav = document.querySelector("[data-nav]");
    if (!toggle || !nav) return;

    toggle.addEventListener("click", () => {
        nav.classList.toggle("nav-open");
    });
}

// ----------------- Login-Seite -----------------

function setupLoginPage() {
    const form = $("#login-form");
    if (!form) return; // wir sind nicht auf login.html

    const emailInput = $("#login-email");
    const passwordInput = $("#login-password");
    const rememberInput = $("#login-remember");
    const errorEl = $("#login-error");

    form.addEventListener("submit", (ev) => {
        ev.preventDefault();
        showError(errorEl, "");

        const email = emailInput.value.trim();
        const password = passwordInput.value;

        // Schul-E-Mail: 6 Ziffern + @studierende.htl-donaustadt.at
        const emailPattern = /^\d{6}@studierende\.htl-donaustadt\.at$/i;
        if (!emailPattern.test(email)) {
            showError(
                errorEl,
                "Bitte deine Schul-E-Mail im Format 230050@studierende.htl-donaustadt.at eingeben."
            );
            return;
        }

        if (!password || password.length < 6) {
            showError(errorEl, "Das Passwort muss mindestens 6 Zeichen lang sein.");
            return;
        }

        // (aktuell nur clientseitige Session)
        saveSession(email, !!rememberInput.checked);

        // Weiter zum Quiz
        window.location.href = "quiz.html";
    });
}

// ----------------- Quiz-Seite -----------------

function setupQuizPage() {
    if (!location.pathname.endsWith("quiz.html")) return;

    const session = requireLogin();
    if (!session) return;

    const chapterListEl = document.getElementById("chapter-list");
    const quizTitleEl = document.getElementById("quiz-title");
    const quizContentEl = document.getElementById("quiz-content");

    let questions = [];
    let currentIndex = 0;

    async function loadChapters() {
        try {
            const res = await fetch("/api/chapters");
            if (!res.ok) throw new Error("HTTP " + res.status);
            const chapters = await res.json();

            if (!Array.isArray(chapters) || chapters.length === 0) {
                chapterListEl.innerHTML = "<p>Keine Kapitel gefunden.</p>";
                return;
            }

            chapterListEl.innerHTML = "";
            for (const name of chapters) {
                const btn = document.createElement("button");
                btn.className = "chapter-item";
                btn.type = "button";
                btn.textContent = name;
                btn.addEventListener("click", () => startQuiz(name));
                chapterListEl.appendChild(btn);
            }
        } catch (err) {
            console.error(err);
            chapterListEl.innerHTML =
                "<p>Fehler beim Laden der Kapitel. Versuche es später erneut.</p>";
        }
    }

    async function startQuiz(chapterName) {
        quizTitleEl.textContent = chapterName;
        quizContentEl.innerHTML = "<p>Lade Fragen …</p>";

        try {
            // WICHTIG: Backend heißt /api/quiz (nicht /api/questions)
            const res = await fetch("/api/quiz?chapter=" + encodeURIComponent(chapterName));
            if (!res.ok) throw new Error("HTTP " + res.status);

            const data = await res.json();
            if (!Array.isArray(data) || data.length === 0) {
                quizContentEl.innerHTML =
                    "<p>Für dieses Kapitel wurden noch keine Fragen importiert.</p>";
                return;
            }

            questions = data;
            currentIndex = 0;
            renderQuestion();
        } catch (err) {
            console.error(err);
            quizContentEl.innerHTML =
                "<p>Fehler beim Laden der Fragen. Versuche es später erneut.</p>";
        }
    }

    function renderQuestion() {
        const q = questions[currentIndex];
        if (!q) {
            quizContentEl.innerHTML = `
        <h3>Fertig!</h3>
        <p>Kapitel abgeschlossen.</p>
      `;
            return;
        }

        const progress = `${currentIndex + 1} / ${questions.length}`;

        const assetsHtml = (q.assets || [])
            .map((src) => `
        <div class="quiz-image-wrapper">
          <img src="${src}" alt="Fragebild">
        </div>
      `)
            .join("");

        const choices = Array.isArray(q.choices) ? q.choices : [];
        const choiceItems = choices
            .map((c) => `
        <li>
          <button class="answer-btn" type="button" data-choice-id="${c.id}">
            ${escapeHtml(c.text)}
          </button>
        </li>
      `)
            .join("");

        quizContentEl.innerHTML = `
      <div class="quiz-question-header">
        <span class="quiz-progress">${progress}</span>
        ${q.timeLimitSeconds ? `<span class="quiz-timer">⏱ ${q.timeLimitSeconds}s</span>` : ""}
      </div>

      <h3 class="quiz-question-text">${escapeHtml(q.text || "")}</h3>
      ${assetsHtml}

      <p class="muted">Wähle deine Antwort(en) und klicke dann auf „Antwort prüfen“.</p>

      <ul class="quiz-answers">
        ${choiceItems}
      </ul>

      <div class="quiz-actions">
        <button class="btn-primary" id="submit-answer">Antwort prüfen</button>
        <span id="quiz-msg" class="muted"></span>
      </div>
    `;

        // Multi-select: Buttons togglen
        const selected = new Set();
        quizContentEl.querySelectorAll(".answer-btn").forEach((btn) => {
            btn.addEventListener("click", () => {
                const id = btn.dataset.choiceId;
                if (!id) return;

                if (selected.has(id)) {
                    selected.delete(id);
                    btn.classList.remove("answer-selected");
                } else {
                    selected.add(id);
                    btn.classList.add("answer-selected");
                }
            });
        });

        // Prüfen über Backend (korrekt für "Choose two")
        const submitBtn = document.getElementById("submit-answer");
        const msgEl = document.getElementById("quiz-msg");

        submitBtn.addEventListener("click", async () => {
            msgEl.textContent = "";

            if (selected.size === 0) {
                msgEl.textContent = "Bitte mindestens eine Antwort auswählen.";
                return;
            }

            submitBtn.disabled = true;

            try {
                const payload = {
                    answers: [
                        {
                            questionId: q.id,
                            choiceIds: Array.from(selected)
                        }
                    ]
                };

                const res = await fetch("/api/submit", {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify(payload)
                });

                if (!res.ok) throw new Error("HTTP " + res.status);
                const result = await res.json();

                const ok = result && result.correct === 1;
                if (ok) {
                    msgEl.textContent = "✅ Richtig!";
                    msgEl.classList.remove("error");
                } else {
                    msgEl.textContent =
                        "❌ Falsch. Richtige Antwort(en): " + (result.wrongs?.[0]?.correct || "");
                    msgEl.classList.add("error");
                }

                // Buttons sperren
                quizContentEl.querySelectorAll(".answer-btn").forEach((b) => (b.disabled = true));

                // Next
                const nextBtn = document.createElement("button");
                nextBtn.className = "btn-primary quiz-next-btn";
                nextBtn.textContent =
                    currentIndex + 1 < questions.length ? "Nächste Frage" : "Fertig";
                nextBtn.addEventListener("click", () => {
                    currentIndex++;
                    renderQuestion();
                });
                submitBtn.parentElement.appendChild(nextBtn);
            } catch (err) {
                console.error(err);
                msgEl.textContent = "Fehler beim Prüfen. Bitte später erneut.";
                msgEl.classList.add("error");
                submitBtn.disabled = false;
            }
        });
    }

    function escapeHtml(s) {
        return String(s)
            .replaceAll("&", "&amp;")
            .replaceAll("<", "&lt;")
            .replaceAll(">", "&gt;")
            .replaceAll('"', "&quot;")
            .replaceAll("'", "&#039;");
    }

    loadChapters();
}


// ----------------- Init -----------------

document.addEventListener("DOMContentLoaded", () => {
    setupMenu();
    setupLoginPage();
    setupQuizPage();
});
