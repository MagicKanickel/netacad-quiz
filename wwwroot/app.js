function $(sel) { return document.querySelector(sel); }
function $all(sel) { return Array.from(document.querySelectorAll(sel)); }

function showError(el, msg) {
    if (!el) return;
    el.textContent = msg || "";
    el.style.display = msg ? "block" : "none";
}

function escapeHtml(s) {
    return String(s)
        .replaceAll("&", "&amp;")
        .replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;")
        .replaceAll('"', "&quot;")
        .replaceAll("'", "&#039;");
}

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

const EMAIL_PATTERN = /^\d{6}@studierende\.htl-donaustadt\.at$/i;

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

        if (!EMAIL_PATTERN.test(email)) {
            showError(errorEl, "Bitte Schul-E-Mail im Format 230050@studierende.htl-donaustadt.at eingeben.");
            return;
        }
        if (!password || password.length < 6) {
            showError(errorEl, "Passwort muss mindestens 6 Zeichen lang sein.");
            return;
        }

        try {
            const res = await fetch("/api/auth/login", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                credentials: "include",
                body: JSON.stringify({ email, password, remember: !!rememberInput?.checked })
            });

            if (!res.ok) {
                const data = await res.json().catch(() => null);
                showError(errorEl, data?.error || "Login fehlgeschlagen.");
                return;
            }

            window.location.href = "/overview.html";
        } catch (e) {
            console.error(e);
            showError(errorEl, "Netzwerkfehler. Bitte später erneut.");
        }
    });
}

// ---------------- Register ----------------
function setupRegisterPage() {
    const form = $("#register-form");
    if (!form) return;

    const emailInput = $("#reg-email");
    const pass1 = $("#reg-password");
    const pass2 = $("#reg-password2");
    const errorEl = $("#register-error");

    form.addEventListener("submit", async (ev) => {
        ev.preventDefault();
        showError(errorEl, "");

        const email = emailInput.value.trim();
        const p1 = pass1.value;
        const p2 = pass2.value;

        if (!EMAIL_PATTERN.test(email)) {
            showError(errorEl, "Bitte Schul-E-Mail im Format 230050@studierende.htl-donaustadt.at eingeben.");
            return;
        }
        if (!p1 || p1.length < 6) {
            showError(errorEl, "Passwort muss mindestens 6 Zeichen lang sein.");
            return;
        }
        if (p1 !== p2) {
            showError(errorEl, "Passwörter stimmen nicht überein.");
            return;
        }

        try {
            // 1) Register
            const res = await fetch("/api/auth/register", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                credentials: "include",
                body: JSON.stringify({ email, password: p1 })
            });

            if (!res.ok) {
                const data = await res.json().catch(() => null);
                showError(errorEl, data?.error || "Registrierung fehlgeschlagen.");
                return;
            }

            // 2) Auto-Login (damit “nach Registrierung login geht” garantiert ist)
            const loginRes = await fetch("/api/auth/login", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                credentials: "include",
                body: JSON.stringify({ email, password: p1, remember: true })
            });

            if (!loginRes.ok) {
                showError(errorEl, "Konto erstellt, aber Login fehlgeschlagen. Bitte gehe zum Login.");
                return;
            }

            window.location.href = "/overview.html";
        } catch (e) {
            console.error(e);
            showError(errorEl, "Netzwerkfehler. Bitte später erneut.");
        }
    });
}

// ---------------- Overview / Kapitel (wie gehabt) ----------------
async function setupOverviewPage() {
    const listEl = $("#chapter-list");
    if (!listEl) return;

    const auth = await requireAuthOrRedirect();
    if (!auth) return;

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

// ---------------- Quiz (wie gehabt – gekürzt hier) ----------------
async function setupQuizPage() {
    const contentEl = $("#quiz-content");
    if (!contentEl) return;

    const auth = await requireAuthOrRedirect();
    if (!auth) return;

    // ... dein Quiz-Lade-Code bleibt wie bei dir ...
}

document.addEventListener("DOMContentLoaded", () => {
    setupMenu();
    setupLoginPage();
    setupRegisterPage();
    setupOverviewPage();
    setupQuizPage();
});
