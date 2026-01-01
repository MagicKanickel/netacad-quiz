// app.js – Navigation, Login (mit Passwort) & Quiz-Logik

// ---------- Helfer ----------

function $(sel) {
    return document.querySelector(sel);
}

function showError(el, msg) {
    if (!el) return;
    el.textContent = msg || "";
    el.style.display = msg ? "block" : "none";
}

// Clientseitige "Session"
const SESSION_KEY = "netacadQuizSession";

function saveSession(email, remember) {
    const session = {
        email,
        createdAt: Date.now()
    };
    localStorage.setItem(SESSION_KEY, JSON.stringify(session));

    // Cookie nur, um auf anderen Seiten schnell zu prüfen
    const maxAge = remember ? 60 * 60 * 24 * 30 : 60 * 60 * 4; // 30 Tage / 4 h
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
    const session = loadSession();
    if (!session || !session.email) {
        window.location.href = "login.html";
        return null;
    }
    return session;
}

// ---------- Menü ----------

function setupMenu() {
    const toggle = document.querySelector(".menu-toggle");
    const nav = document.querySelector("[data-nav]");
    if (!toggle || !nav) return;

    toggle.addEventListener("click", () => {
        nav.classList.toggle("nav-open");
    });
}

// ---------- Login-Seite ----------

function setupLoginPage() {
    const form = $("#login-form");
    if (!form) return; // wir sind nicht auf der Login-Seite

    const emailInput = $("#login-email");
    const passwordInput = $("#login-password");
    const rememberInput = $("#login-remember");
    const errorEl = $("#login-error");

    form.addEventListener("submit", async (ev) => {
        ev.preventDefault();
        showError(errorEl, "");

        const email = emailInput.value.trim();
        const password = passwordInput.value;

        // Schul-E-Mail: 6 Ziffern + @studierende.htl-donaustadt.at
        const emailPattern = /^\d{6}@studierende\.htl-donaustadt\.at$/i;
        if (!emailPattern.test(email)) {
            showError(
                errorEl,
                "Bitte gib deine Schul-E-Mail im Format 230050@studierende.htl-donaustadt.at ein."
            );
            return;
        }

        if (!password || password.length < 6) {
            showError(errorEl, "Das Passwort muss mindestens 6 Zeichen lang sein.");
            return;
        }

        // *** WICHTIG ***
        // Aktuell nur clientseitig: wir speichern die Session lokal
        // und rufen KEIN Backend auf. Für echte Accounts müssen wir
        // später ein API-Login mit Passwort-HASH bauen.
        saveSession(email, !!rememberInput.checked);

        // Weiter zum Quiz
        window.location.href = "quiz.html";
    });
}

// ---------- Quiz-Seite ----------

function setupQuizPage() {
    if (!location.pathname.endsWith("quiz.html")) return;

    const session = requireLogin();
    if (!session) return; // redirect

    const chapterListEl = $("#chapter-list");
    const quizPanel = $("#quiz-panel");
    const quizTitleEl = $("#quiz-title");
    const quizContentEl = $("#quiz-content");

    let questions = [];
    let currentIndex = 0;
    let correctCount = 0;

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
            chapters.forEach((name) => {
                const btn = document.createElement("button");
                btn.className = "chapter-item";
                btn.textContent = name;
                btn.addEventListener("click", () => startQuiz(name));
                chapterListEl.appendChild(btn);
            });
        } catch (err) {
            console.error(err);
            chapterListEl.innerHTML =
                "<p>Fehler beim Laden der Kapitel. Versuche es später erneut.</p>";
        }
    }

    async function startQuiz(chapterName) {
        quizTitleEl.textContent = chapterName;
        quizContentEl.classList.remove("muted");
        quizContentEl.innerHTML = "<p>Lade Fragen …</p>";

        try {
            const url = "/api/questions?chapter=" + encodeURIComponent(chapterName);
            const res = await fetch(url);
            if (!res.ok) throw new Error("HTTP " + res.status);
            const data = await res.json();

            // Erwartete Struktur:
            // [{ id, text, answers: [..], correctIndex, imageUrl? }, ...]
            questions = Array.isArray(data) ? data : [];
            currentIndex = 0;
            correctCount = 0;

            if (questions.length === 0) {
                quizContentEl.innerHTML = "<p>Für dieses Kapitel wurden noch keine Fragen importiert.</p>";
                return;
            }

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
                <p>Du hast <strong>${correctCount}</strong> von <strong>${questions.length}</strong> Fragen richtig beantwortet.</p>
                <button class="btn-primary" id="restart-btn">Kapitel neu starten</button>
            `;
            const restartBtn = $("#restart-btn");
            if (restartBtn) {
                restartBtn.addEventListener("click", () => {
                    currentIndex = 0;
                    correctCount = 0;
                    renderQuestion();
                });
            }
            return;
        }

        const progress = `${currentIndex + 1} / ${questions.length}`;
        let html = `
            <div class="quiz-question-header">
                <span class="quiz-progress">${progress}</span>
            </div>
            <h3 class="quiz-question-text">${q.text}</h3>
        `;

        if (q.imageUrl) {
            html += `<div class="quiz-image-wrapper">
                        <img src="${q.imageUrl}" alt="Fragebild">
                     </div>`;
        }

        html += `<ul class="quiz-answers">`;
        (q.answers || []).forEach((ans, idx) => {
            html += `
                <li>
                    <button class="answer-btn" data-index="${idx}">
                        ${ans}
                    </button>
                </li>`;
        });
        html += `</ul>`;

        quizContentEl.innerHTML = html;

        const buttons = quizContentEl.querySelectorAll(".answer-btn");
        buttons.forEach((btn) => {
            btn.addEventListener("click", () => {
                const idx = Number(btn.dataset.index);
                handleAnswer(idx);
            });
        });
    }

    function handleAnswer(selectedIndex) {
        const q = questions[currentIndex];
        const correctIndex = q.correctIndex;

        const btns = quizContentEl.querySelectorAll(".answer-btn");
        btns.forEach((b, idx) => {
            if (idx === correctIndex) {
                b.classList.add("answer-correct");
            }
            if (idx === selectedIndex && idx !== correctIndex) {
                b.classList.add("answer-wrong");
            }
            b.disabled = true;
        });

        if (selectedIndex === correctIndex) {
            correctCount++;
        }

        const nextBtn = document.createElement("button");
        nextBtn.textContent =
            currentIndex + 1 < questions.length ? "Nächste Frage" : "Ergebnis anzeigen";
        nextBtn.className = "btn-primary quiz-next-btn";
        nextBtn.addEventListener("click", () => {
            currentIndex++;
            renderQuestion();
        });
        quizContentEl.appendChild(nextBtn);
    }

    loadChapters();
}

// ---------- Initialisierung ----------

document.addEventListener("DOMContentLoaded", () => {
    setupMenu();
    setupLoginPage();
    setupQuizPage();
});
