// app.js – Auth (Cookie), Dashboard (overview), Quiz-UI + Practice Modes

function $(sel) { return document.querySelector(sel); }
function $all(sel) { return Array.from(document.querySelectorAll(sel)); }

function show(el, on) {
    if (!el) return;
    el.style.display = on ? "" : "none";
}

function setText(el, txt) {
    if (!el) return;
    el.textContent = txt ?? "";
}

function clamp(n, a, b) { return Math.max(a, Math.min(b, n)); }

async function apiGet(url) {
    const res = await fetch(url, { credentials: "include" });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    return res.json();
}

async function apiPost(url, body) {
    const res = await fetch(url, {
        method: "POST",
        credentials: "include",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body ?? {})
    });
    if (!res.ok) {
        let msg = "Request failed";
        try {
            const data = await res.json();
            if (data?.error) msg = data.error;
        } catch { }
        throw new Error(msg);
    }
    return res.json();
}

// ----------------- Global practice mode (Slider) -----------------
const PRACTICE_KEY = "netacad.practiceMode"; // 1..4
function getPracticeMode() {
    const raw = Number(localStorage.getItem(PRACTICE_KEY) || "1");
    return clamp(raw, 1, 4);
}
function setPracticeMode(v) {
    localStorage.setItem(PRACTICE_KEY, String(clamp(v, 1, 4)));
}

// ----------------- Menu -----------------
function setupMenu() {
    const toggle = $(".menu-toggle");
    const nav = document.querySelector("[data-nav]");
    if (!toggle || !nav) return;

    toggle.addEventListener("click", () => nav.classList.toggle("nav-open"));
}

// ----------------- Auth guard -----------------
async function requireAuth() {
    try {
        const st = await apiGet("/api/auth/status");
        if (!st?.isAuthenticated) {
            location.href = "/login.html";
            return null;
        }
        return st;
    } catch {
        location.href = "/login.html";
        return null;
    }
}

// ----------------- Login page -----------------
function setupLoginPage() {
    const form = $("#login-form");
    if (!form) return;

    const email = $("#login-email");
    const pass = $("#login-password");
    const remember = $("#login-remember");
    const err = $("#login-error");

    form.addEventListener("submit", async (e) => {
        e.preventDefault();
        setText(err, "");

        const mail = (email?.value || "").trim();
        const pw = (pass?.value || "");

        const emailPattern = /^\d{6}@studierende\.htl-donaustadt\.at$/i;
        if (!emailPattern.test(mail)) {
            setText(err, "Bitte Schul-E-Mail im Format 230050@studierende.htl-donaustadt.at eingeben.");
            return;
        }
        if (pw.length < 6) {
            setText(err, "Passwort muss mindestens 6 Zeichen lang sein.");
            return;
        }

        try {
            await apiPost("/api/auth/login", { email: mail, password: pw, rememberMe: !!remember?.checked });
            location.href = "/overview.html";
        } catch (ex) {
            setText(err, ex.message || "Login fehlgeschlagen.");
        }
    });
}

// ----------------- Register page -----------------
function setupRegisterPage() {
    const form = $("#register-form");
    if (!form) return;

    const email = $("#register-email");
    const pass = $("#register-password");
    const remember = $("#register-remember");
    const err = $("#register-error");

    form.addEventListener("submit", async (e) => {
        e.preventDefault();
        setText(err, "");

        const mail = (email?.value || "").trim();
        const pw = (pass?.value || "");

        const emailPattern = /^\d{6}@studierende\.htl-donaustadt\.at$/i;
        if (!emailPattern.test(mail)) {
            setText(err, "Nur Schul-E-Mail erlaubt (230050@studierende.htl-donaustadt.at).");
            return;
        }
        if (pw.length < 6) {
            setText(err, "Passwort muss mindestens 6 Zeichen lang sein.");
            return;
        }

        try {
            await apiPost("/api/auth/register", { email: mail, password: pw, rememberMe: !!remember?.checked });
            location.href = "/overview.html";
        } catch (ex) {
            setText(err, ex.message || "Registrierung fehlgeschlagen.");
        }
    });
}

// ----------------- Profile page (reset + logout) -----------------
function setupProfilePage() {
    const root = $("#profile-root");
    if (!root) return;

    (async () => {
        const st = await requireAuth();
        if (!st) return;

        setText($("#profile-email"), st.email || "");

        $("#btn-logout")?.addEventListener("click", async () => {
            try {
                await apiPost("/api/auth/logout", {});
            } finally {
                location.href = "/login.html";
            }
        });

        $("#btn-reset")?.addEventListener("click", async () => {
            if (!confirm("Wirklich alle Lern-Daten zurücksetzen?")) return;
            try {
                await apiPost("/api/progress/reset", {});
                alert("Zurückgesetzt ✅");
            } catch (ex) {
                alert(ex.message || "Fehler beim Reset.");
            }
        });
    })();
}

// ----------------- Overview (Dashboard) -----------------
function setupOverviewPage() {
    const root = $("#overview-root");
    if (!root) return;

    const slider = $("#practice-slider");
    const sliderLabel = $("#practice-label");
    const chaptersList = $("#chapters-list");
    const filterSel = $("#chapters-filter");
    const barsRoot = $("#overview-bars");

    function labelForMode(v) {
        if (v === 1) return "Off";
        if (v === 2) return "Easy Practice";
        if (v === 3) return "Medium Practice";
        return "Study";
    }

    function applySliderUI(v) {
        if (!slider) return;
        slider.value = String(v);
        if (sliderLabel) sliderLabel.textContent = labelForMode(v);
        setPracticeMode(v);
    }

    function ring(percent, completed) {
        // simple SVG ring
        const p = clamp(percent, 0, 100);
        const r = 18;
        const c = 2 * Math.PI * r;
        const dash = (p / 100) * c;
        const rest = c - dash;

        const centerText = completed ? "✓" : `${p}%`;

        return `
      <svg class="ring" viewBox="0 0 50 50" aria-hidden="true">
        <circle class="ring-bg" cx="25" cy="25" r="${r}" />
        <circle class="ring-fg" cx="25" cy="25" r="${r}"
                stroke-dasharray="${dash} ${rest}" />
        <text x="25" y="29" text-anchor="middle" class="ring-text">${centerText}</text>
      </svg>
    `;
    }

    function chapterCard(item) {
        // item: {chapter, questionCount, totalMinutes, correctEver, percent, completed, hasTimeouts}
        return `
      <div class="chapter-card" data-chapter="${item.chapter}">
        <div class="chapter-top">
          <div class="chapter-name">${item.chapter}</div>
          <div class="chapter-right">
            ${ring(item.percent, item.completed)}
          </div>
        </div>

        <button class="chapter-expand" type="button" aria-label="Details anzeigen">Details</button>

        <div class="chapter-details">
          <div class="chapter-meta">
            <div><span class="muted">Fragen:</span> <strong>${item.questionCount}</strong></div>
            <div><span class="muted">Dauer:</span> <strong>${item.totalMinutes} min</strong></div>
            <div><span class="muted">Jemals korrekt:</span> <strong>${item.correctEver}</strong></div>
            <div><span class="muted">Timeouts:</span> <strong>${item.hasTimeouts ? "Ja" : "Nein"}</strong></div>
          </div>
        </div>
      </div>
    `;
    }

    function matchesFilter(item, filter) {
        if (filter === "all") return true;
        if (filter === "completed") return item.completed;
        if (filter === "incomplete") return !item.completed;
        if (filter === "timeouts") return item.hasTimeouts;
        return true;
    }

    async function loadDashboard() {
        const st = await requireAuth();
        if (!st) return;

        // slider init
        const mode = getPracticeMode();
        applySliderUI(mode);

        slider?.addEventListener("input", () => {
            const v = Number(slider.value);
            applySliderUI(v);
        });

        // load chapter progress
        let items = [];
        try {
            items = await apiGet("/api/progress/chapters");
        } catch (ex) {
            chaptersList.innerHTML = `<p class="muted">Fehler beim Laden der Kapitel.</p>`;
            return;
        }

        // bars (overall progress)
        const totalQ = items.reduce((a, x) => a + (x.questionCount || 0), 0);
        const totalCorrect = items.reduce((a, x) => a + (x.correctEver || 0), 0);
        const overall = totalQ ? Math.round((totalCorrect / totalQ) * 100) : 0;

        barsRoot.innerHTML = `
      <div class="bar-row">
        <div class="bar-title">Gesamtfortschritt</div>
        <div class="bar">
          <div class="bar-fill" style="width:${overall}%"></div>
        </div>
        <div class="bar-val">${overall}%</div>
      </div>
    `;

        const renderList = () => {
            const filter = filterSel?.value || "all";
            const shown = items.filter(x => matchesFilter(x, filter));

            chaptersList.innerHTML = shown.map(chapterCard).join("");

            // expand logic + click to open quiz
            $all(".chapter-card").forEach(card => {
                const expBtn = card.querySelector(".chapter-expand");
                const details = card.querySelector(".chapter-details");

                expBtn?.addEventListener("click", (e) => {
                    e.stopPropagation();
                    details?.classList.toggle("open");
                });

                card.addEventListener("click", () => {
                    const chap = card.getAttribute("data-chapter");
                    if (!chap) return;
                    location.href = `/quiz.html?chapter=${encodeURIComponent(chap)}`;
                });
            });
        };

        filterSel?.addEventListener("change", renderList);
        renderList();
    }

    loadDashboard();
}

// ----------------- Quiz page -----------------
function setupQuizPage() {
    const root = $("#quiz-root");
    if (!root) return;

    const titleEl = $("#quiz-chapter");
    const qTextEl = $("#q-text");
    const answersEl = $("#answers");
    const timerEl = $("#timer");
    const timerIcon = $("#timer-icon");
    const overlayEl = $("#timeout-overlay");
    const navEl = $("#question-nav");

    const params = new URLSearchParams(location.search);
    const chapter = params.get("chapter");

    let mode = getPracticeMode(); // 1..4
    let questions = [];
    let idx = 0;

    // per question state
    let selected = new Set(); // choiceId(s)
    let locked = false;
    let remaining = 0;
    let timerHandle = null;
    let secondChance = false; // only in Easy when time runs out first time
    const results = new Map(); // questionId -> {state:'correct'|'wrong'|'timeout', chosen:[...]}

    function clearTimer() {
        if (timerHandle) clearInterval(timerHandle);
        timerHandle = null;
    }

    function renderTimer() {
        if (!timerEl) return;
        const m = Math.floor(remaining / 60);
        const s = remaining % 60;
        timerEl.textContent = `${m}:${String(s).padStart(2, "0")}`;

        // styling (yellow warning) when secondChance
        timerEl.classList.toggle("warn", !!secondChance);
        if (timerIcon) timerIcon.classList.toggle("warn", !!secondChance);
    }

    function startTimer(seconds) {
        clearTimer();
        remaining = Math.max(0, seconds | 0);
        renderTimer();

        timerHandle = setInterval(() => {
            remaining--;
            if (remaining <= 0) {
                clearTimer();
                onTimeout();
            } else {
                renderTimer();
            }
        }, 1000);
    }

    function showOverlay(text) {
        if (!overlayEl) return;
        overlayEl.querySelector(".overlay-text").textContent = text;
        overlayEl.classList.add("open");
        setTimeout(() => overlayEl.classList.remove("open"), 900);
    }

    function getCurrent() {
        return questions[idx] || null;
    }

    function setNavState() {
        navEl.innerHTML = questions.map((q, i) => {
            const r = results.get(q.id);
            const cls =
                i === idx ? "qdot active" :
                    r?.state === "correct" ? "qdot ok" :
                        r?.state === "wrong" ? "qdot bad" :
                            r?.state === "timeout" ? "qdot timeout" :
                                "qdot";
            return `<button class="${cls}" type="button" data-i="${i}">${i + 1}</button>`;
        }).join("");

        $all(".qdot").forEach(b => {
            b.addEventListener("click", () => {
                const i = Number(b.getAttribute("data-i"));
                if (Number.isFinite(i)) {
                    idx = i;
                    renderQuestion();
                }
            });
        });
    }

    function resetSelection() {
        selected = new Set();
        locked = false;
    }

    function choiceButton(choice) {
        // choice: {id,text}
        return `
      <button class="answer-card" type="button" data-id="${choice.id}">
        <span class="answer-text">${choice.text}</span>
        <span class="answer-mark"></span>
      </button>
    `;
    }

    function markAnswer(btn, state) {
        btn.classList.remove("ok", "bad", "picked");
        if (state === "picked") btn.classList.add("picked");
        if (state === "ok") btn.classList.add("ok");
        if (state === "bad") btn.classList.add("bad");
    }

    function applyPickedUI() {
        $all(".answer-card").forEach(btn => {
            const id = btn.getAttribute("data-id");
            if (!id) return;
            markAnswer(btn, selected.has(id) ? "picked" : "");
        });
    }

    function lockAll() {
        locked = true;
        $all(".answer-card").forEach(b => b.disabled = true);
    }

    // Evaluate server-side (authoritative) by calling /api/submit for single question
    async function submitOne(questionId, choiceIds, timedOut) {
        const payload = { answers: [{ questionId, choiceIds, timedOut: !!timedOut }] };
        return apiPost("/api/submit", payload);
    }

    async function onTimeout() {
        const q = getCurrent();
        if (!q) return;

        mode = getPracticeMode();

        // Easy Practice: first timeout -> second chance (yellow time)
        if (mode === 2 && !secondChance) {
            secondChance = true;
            showOverlay("Zeit abgelaufen – 2. Chance!");
            // nochmal die gleiche Zeit
            startTimer(q.timeLimitSeconds || 20);
            return;
        }

        // sonst: timed out -> mark state, move next
        showOverlay("Zeit abgelaufen");
        results.set(q.id, { state: "timeout", chosen: [] });
        setNavState();

        // speichern (timedOut = true)
        try { await submitOne(q.id, [], true); } catch { /* ignore */ }

        // next
        setTimeout(() => {
            idx = Math.min(idx + 1, questions.length); // may go to end
            renderQuestion();
        }, 500);
    }

    function computeIsMulti(q) {
        // correctRequired > 1 => multi select
        return (q.correctRequired || 1) > 1;
    }

    async function finalizeAnswer() {
        const q = getCurrent();
        if (!q || locked) return;

        const choiceIds = Array.from(selected).map(x => x);
        if (choiceIds.length === 0) return;

        lockAll();

        // In Medium/Study: submit now, but feedback differs in UI
        let result;
        try {
            // timedOut false
            result = await submitOne(q.id, choiceIds, false);
        } catch {
            // If submit fails, keep going but mark wrong
            result = { correct: 0, total: 1 };
        }

        // We can infer per-question correctness from server response only if we submit one question:
        // if correct == 1 => ok else wrong
        const ok = (result?.correct === 1);

        if (mode === 1) {
            // Off: no feedback colors, just next
            results.set(q.id, { state: ok ? "correct" : "wrong", chosen: choiceIds });
            setNavState();
            goNext();
            return;
        }

        if (mode === 2) {
            // Easy: immediate feedback, allow correction if wrong
            if (ok) {
                results.set(q.id, { state: "correct", chosen: choiceIds });
                setNavState();
                // mark green
                $all(".answer-card").forEach(btn => {
                    if (selected.has(btn.getAttribute("data-id"))) btn.classList.add("ok");
                });
                setTimeout(goNext, 450);
            } else {
                // mark selected red, unlock for correction (but keep timer running)
                results.set(q.id, { state: "wrong", chosen: choiceIds });
                setNavState();
                $all(".answer-card").forEach(btn => {
                    const id = btn.getAttribute("data-id");
                    if (id && selected.has(id)) btn.classList.add("bad");
                });
                // allow user to change selection -> re-enable
                locked = false;
                $all(".answer-card").forEach(b => b.disabled = false);
            }
            return;
        }

        if (mode === 3) {
            // Medium: show feedback via nav dots, not by marking answers
            results.set(q.id, { state: ok ? "correct" : "wrong", chosen: choiceIds });
            setNavState();
            setTimeout(goNext, 250);
            return;
        }

        // mode 4 Study: no immediate feedback; show at end
        results.set(q.id, { state: ok ? "correct" : "wrong", chosen: choiceIds });
        setNavState();
        setTimeout(goNext, 250);
    }

    function goNext() {
        secondChance = false;
        idx++;

        if (idx >= questions.length) {
            renderSummary();
            return;
        }
        renderQuestion();
    }

    function renderSummary() {
        clearTimer();
        const total = questions.length;
        const correct = Array.from(results.values()).filter(x => x.state === "correct").length;
        const wrong = Array.from(results.values()).filter(x => x.state === "wrong").length;
        const timeouts = Array.from(results.values()).filter(x => x.state === "timeout").length;

        titleEl.textContent = chapter || "Quiz";

        qTextEl.innerHTML = `
      <div class="summary">
        <h2>Ergebnis</h2>
        <p><strong>${correct}</strong> richtig • <strong>${wrong}</strong> falsch • <strong>${timeouts}</strong> Timeouts</p>
        <div class="summary-actions">
          <button class="btn-primary" id="btn-back">Zurück zum Dashboard</button>
          <button class="btn-secondary" id="btn-restart">Kapitel neu starten</button>
        </div>
        ${getPracticeMode() === 4 ? `<p class="muted">Study: Du siehst erst hier die Auswertung.</p>` : ``}
      </div>
    `;

        answersEl.innerHTML = "";
        navEl.innerHTML = "";

        $("#btn-back")?.addEventListener("click", () => location.href = "/overview.html");
        $("#btn-restart")?.addEventListener("click", () => {
            idx = 0;
            results.clear();
            renderQuestion();
        });
    }

    function renderQuestion() {
        mode = getPracticeMode();
        resetSelection();
        clearTimer();

        const q = getCurrent();
        if (!q) {
            renderSummary();
            return;
        }

        titleEl.textContent = chapter || q.chapter || "Quiz";
        qTextEl.textContent = q.text || "(keine Frage)";

        // answers
        const isMulti = computeIsMulti(q);
        $("#multi-hint").textContent = isMulti ? `Mehrfachauswahl: ${q.correctRequired} auswählen` : `Einzelauswahl`;

        answersEl.innerHTML = (q.choices || []).map(choiceButton).join("");

        // answer click behavior
        $all(".answer-card").forEach(btn => {
            btn.addEventListener("click", async () => {
                if (locked) return;

                const id = btn.getAttribute("data-id");
                if (!id) return;

                if (isMulti) {
                    // toggle
                    if (selected.has(id)) selected.delete(id);
                    else selected.add(id);
                    applyPickedUI();
                } else {
                    // single: pick and submit immediately
                    selected = new Set([id]);
                    applyPickedUI();
                    await finalizeAnswer();
                }
            });
        });

        // For multi: require confirm
        const confirmBtn = $("#btn-confirm");
        show(confirmBtn, isMulti);
        confirmBtn.onclick = async () => {
            if (selected.size !== (q.correctRequired || 2)) {
                showOverlay(`Bitte genau ${q.correctRequired} auswählen`);
                return;
            }
            await finalizeAnswer();
        };

        // start timer
        startTimer(q.timeLimitSeconds || 20);

        // nav dots
        setNavState();
    }

    async function loadQuiz() {
        const st = await requireAuth();
        if (!st) return;

        if (!chapter) {
            location.href = "/overview.html";
            return;
        }

        try {
            questions = await apiGet(`/api/quiz?chapter=${encodeURIComponent(chapter)}`);
        } catch (ex) {
            titleEl.textContent = "Fehler";
            qTextEl.textContent = "Fehler beim Laden der Fragen.";
            return;
        }

        idx = 0;
        results.clear();
        renderQuestion();
    }

    loadQuiz();
}

// ----------------- Init -----------------
document.addEventListener("DOMContentLoaded", () => {
    setupMenu();
    setupLoginPage();
    setupRegisterPage();
    setupOverviewPage();
    setupQuizPage();
    setupProfilePage();
});
