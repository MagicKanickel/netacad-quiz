// app.js – Auth (Server), Menü, Overview (Kapitel), Quiz (API)

function $(sel) { return document.querySelector(sel); }
function $all(sel) { return Array.from(document.querySelectorAll(sel)); }

function showError(el, msg) {
    if (!el) return;
    el.textContent = msg || "";
    el.style.display = msg ? "block" : "none";
}

function getModeName(v) {
    return ["Off", "Easy Practice", "Medium Practice", "Study"][v] ?? "Off";
}

const MODE_KEY = "netacad.mode";

// ---------------- Menü ----------------
function setupMenu() {
    const toggle = $(".menu-toggle");
    const nav = document.querySelector("[data-nav]");
    if (!toggle || !nav) return;

    toggle.addEventListener("click", () => {
        nav.classList.toggle("nav-open");
    });
}

// ---------------- Auth Helpers (Server) ----------------
async function apiAuthStatus() {
    const res = await fetch("/api/auth/status", { credentials: "include" });
    if (!res.ok) return { isAuthenticated: false, email: null };
    return res.json();
}

async function requireAuthOrRedirect() {
    const st = await apiAuthStatus();
    if (!st.isAuthenticated) {
        window.location.href = "/login.html";
        return null;
    }
    return st;
}

// ---------------- Login ----------------
function setupLoginPage() {
    const form = $("#login-form");
    if (!form) return;

    const emailInput = $("#login-email");
    const passwordInput = $("#login-password");
    const rememberInput = $("#login-remember");
    const errorEl = $("#login-error");

    form.addEventListener("submit", async (ev) => {
        ev.preventDefault();
        showError(errorEl, "");

        const email = emailInput.value.trim();
        const password = passwordInput.value;

        const emailPattern = /^\d{6}@studierende\.htl-donaustadt\.at$/i;
        if (!emailPattern.test(email)) {
            showError(errorEl, "Bitte Schul-E-Mail im Format 230050@studierende.htl-donaustadt.at eingeben.");
            return;
        }
        if (!password || password.length < 6) {
            showError(errorEl, "Passwort muss mindestens 6 Zeichen lang sein.");
            return;
        }

        try {
            // Login am Server
            const res = await fetch("/api/auth/login", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                credentials: "include",
                body: JSON.stringify({ email, password })
            });

            if (!res.ok) {
                const data = await res.json().catch(() => null);
                showError(errorEl, data?.error || "Login fehlgeschlagen.");
                return;
            }

            // optional: Remember-Flag könntest du serverseitig später auswerten
            localStorage.setItem("netacad.remember", rememberInput.checked ? "1" : "0");

            // WICHTIG: Nach Login auf overview!
            window.location.href = "/overview.html";
        } catch (e) {
            console.error(e);
            showError(errorEl, "Netzwerkfehler. Bitte später erneut.");
        }
    });
}

// ---------------- Overview / Kapitel ----------------
async function setupOverviewPage() {
    const listEl = $("#chapter-list");
    if (!listEl) return; // nicht auf overview

    // Login Pflicht
    const auth = await requireAuthOrRedirect();
    if (!auth) return;

    // Mode Slider global
    const slider = $("#mode-slider");
    const label = $("#mode-label");

    const savedMode = Number(localStorage.getItem(MODE_KEY) ?? "0");
    if (slider) slider.value = String(savedMode);
    if (label) label.textContent = getModeName(savedMode);

    slider?.addEventListener("input", () => {
        const v = Number(slider.value);
        localStorage.setItem(MODE_KEY, String(v));
        if (label) label.textContent = getModeName(v);
    });

    // Kapitel laden
    async function loadChapters() {
        listEl.innerHTML = `<p class="muted">Lade Kapitel …</p>`;
        try {
            const res = await fetch("/api/chapters", { credentials: "include" });
            if (!res.ok) throw new Error("HTTP " + res.status);
            const chapters = await res.json();

            if (!Array.isArray(chapters) || chapters.length === 0) {
                listEl.innerHTML = `<p class="muted">Keine Kapitel gefunden.</p>`;
                return;
            }

            // aktuell: nur Name anzeigen (später: Fragen/Dauer/Progress-Kreis)
            listEl.innerHTML = "";
            for (const ch of chapters) {
                const item = document.createElement("div");
                item.className = "chapter-item-card";
                item.innerHTML = `
          <div class="chapter-item-top">
            <div class="chapter-name">${escapeHtml(String(ch))}</div>
            <div class="chapter-progress">
              <div class="progress-pill"><span>0%</span></div>
            </div>
          </div>
          <div class="chapter-item-bottom muted">Klick zum Starten</div>
        `;

                item.addEventListener("click", () => {
                    window.location.href = "/quiz.html?chapter=" + encodeURIComponent(ch);
                });

                listEl.appendChild(item);
            }
        } catch (e) {
            console.error(e);
            listEl.innerHTML = `<p>Fehler beim Laden der Kapitel. Versuche es später erneut.</p>`;
        }
    }

    await loadChapters();
}

// ---------------- Quiz ----------------
async function setupQuizPage() {
    const contentEl = $("#quiz-content");
    if (!contentEl) return; // nicht quiz.html

    // Login Pflicht
    const auth = await requireAuthOrRedirect();
    if (!auth) return;

    const titleEl = $("#quiz-title");
    const subEl = $("#quiz-subtitle");
    const timerValueEl = $("#timer-value");
    const navEl = $("#question-nav");

    const params = new URLSearchParams(location.search);
    const chapter = params.get("chapter");
    if (!chapter) {
        titleEl.textContent = "Kein Kapitel gewählt";
        subEl.textContent = "Bitte zurück zum Dashboard und Kapitel auswählen.";
        contentEl.innerHTML = `<a class="btn-primary" href="/overview.html">➜ Zum Dashboard</a>`;
        return;
    }

    titleEl.textContent = chapter;
    subEl.textContent = "Lade Fragen …";
    contentEl.innerHTML = `<p class="muted">Lade Fragen …</p>`;

    // API: Program.cs hat /api/quiz?chapter=...
    let questions = [];
    try {
        const res = await fetch("/api/quiz?chapter=" + encodeURIComponent(chapter), { credentials: "include" });
        if (!res.ok) throw new Error("HTTP " + res.status);
        questions = await res.json();
        if (!Array.isArray(questions) || questions.length === 0) {
            subEl.textContent = "Keine Fragen";
            contentEl.innerHTML = `<p>Für dieses Kapitel wurden noch keine Fragen importiert.</p>`;
            return;
        }
    } catch (e) {
        console.error(e);
        subEl.textContent = "Fehler";
        contentEl.innerHTML = `<p>Fehler beim Laden der Fragen. Versuche es später erneut.</p>`;
        return;
    }

    subEl.textContent = `${questions.length} Fragen`;
    let idx = 0;
    let timeLeft = 0;
    let timerId = null;

    function buildNav() {
        if (!navEl) return;
        navEl.innerHTML = "";
        questions.forEach((_, i) => {
            const b = document.createElement("button");
            b.className = "qdot";
            b.type = "button";
            b.textContent = String(i + 1);
            if (i === idx) b.classList.add("active");
            b.addEventListener("click", () => {
                idx = i;
                render();
            });
            navEl.appendChild(b);
        });
    }

    function startTimer(seconds) {
        clearInterval(timerId);
        timeLeft = Math.max(0, Number(seconds || 0));
        timerValueEl.textContent = String(timeLeft);

        timerId = setInterval(() => {
            timeLeft--;
            if (timeLeft < 0) timeLeft = 0;
            timerValueEl.textContent = String(timeLeft);

            if (timeLeft === 0) {
                clearInterval(timerId);
                // TODO: Timeout-Overlay + Wertung (kommt als nächster Schritt)
                // Für jetzt: nächste Frage
                setTimeout(() => {
                    idx = Math.min(idx + 1, questions.length - 1);
                    render();
                }, 250);
            }
        }, 1000);
    }

    function render() {
        buildNav();

        const q = questions[idx];
        if (!q) return;

        // Frage
        const qText = q.text ?? "";
        const choices = Array.isArray(q.choices) ? q.choices : [];

        contentEl.innerHTML = `
      <div class="quiz-meta muted">${idx + 1} / ${questions.length}</div>
      <h2 class="quiz-question">${escapeHtml(qText)}</h2>
      <div class="answers" id="answers"></div>
    `;

        const answersEl = $("#answers");
        answersEl.innerHTML = "";

        // Antworten (nur Text anzeigen!)
        for (const c of choices) {
            const btn = document.createElement("button");
            btn.className = "answer-card";
            btn.type = "button";
            btn.innerHTML = `<span class="answer-text">${escapeHtml(c.text ?? "")}</span>`;

            btn.addEventListener("click", () => {
                // TODO: Multi-Select + Submit + Practice-Mode Verhalten (kommt als nächster Schritt)
                // Für jetzt: nächste Frage
                idx = Math.min(idx + 1, questions.length - 1);
                render();
            });

            answersEl.appendChild(btn);
        }

        // Timer
        startTimer(q.timeLimitSeconds ?? 0);

        // aktive Dot Markierung
        $all(".qdot").forEach((b, i) => {
            b.classList.toggle("active", i === idx);
        });
    }

    render();
}

function escapeHtml(s) {
    return String(s)
        .replaceAll("&", "&amp;")
        .replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;")
        .replaceAll('"', "&quot;")
        .replaceAll("'", "&#039;");
}

// ---------------- Init ----------------
document.addEventListener("DOMContentLoaded", () => {
    setupMenu();
    setupLoginPage();
    setupOverviewPage();
    setupQuizPage();
});
