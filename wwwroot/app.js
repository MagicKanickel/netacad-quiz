// Sehr simples Quiz-Frontend OHNE Login-Zwang.
// Nutzt die Backend-APIs:
//   GET  /api/chapters
//   GET  /api/quiz?chapter=…
//   POST /api/submit  (nutzen wir hier noch nicht für Mistakes, nur lokal)

let chapters = [];
let questions = [];
let currentChapter = null;
let currentIndex = 0;
let answers = new Map(); // QuestionId -> Set(ChoiceId)

const statusText = document.getElementById("statusText");
const chapterContainer = document.getElementById("chapterContainer");

const quizCard = document.getElementById("quizCard");
const questionMeta = document.getElementById("questionMeta");
const questionText = document.getElementById("questionText");
const answersContainer = document.getElementById("answersContainer");
const progressText = document.getElementById("progressText");
const btnPrev = document.getElementById("btnPrev");
const btnNext = document.getElementById("btnNext");

const resultCard = document.getElementById("resultCard");
const resultSummary = document.getElementById("resultSummary");
const btnRestart = document.getElementById("btnRestart");

async function loadChapters() {
    try {
        const resp = await fetch("/api/chapters");
        if (!resp.ok) throw new Error("Fehler beim Laden der Kapitel");
        chapters = await resp.json();

        chapterContainer.innerHTML = "";
        if (!chapters || chapters.length === 0) {
            statusText.textContent = "Keine Kapitel gefunden.";
            return;
        }

        statusText.textContent = "Wähle ein Kapitel aus:";
        chapters.forEach(ch => {
            const btn = document.createElement("button");
            btn.className = "chapter-btn";
            btn.textContent = ch;
            btn.addEventListener("click", () => startChapter(ch, btn));
            chapterContainer.appendChild(btn);
        });
    } catch (err) {
        console.error(err);
        statusText.textContent = "Fehler beim Laden der Kapitel.";
    }
}

async function startChapter(chapter, btnElement) {
    currentChapter = chapter;
    currentIndex = 0;
    answers.clear();

    // Buttons visuell updaten
    document.querySelectorAll(".chapter-btn").forEach(b => b.classList.remove("active"));
    if (btnElement) btnElement.classList.add("active");

    statusText.textContent = `Lade Fragen für: ${chapter} …`;
    quizCard.style.display = "none";
    resultCard.style.display = "none";

    try {
        const resp = await fetch(`/api/quiz?chapter=${encodeURIComponent(chapter)}`);
        if (!resp.ok) throw new Error("Fehler beim Laden der Fragen");
        questions = await resp.json();

        if (!questions || questions.length === 0) {
            statusText.textContent = "Für dieses Kapitel sind noch keine Fragen vorhanden.";
            return;
        }

        statusText.textContent = "";
        quizCard.style.display = "block";
        showQuestion(0);
    } catch (err) {
        console.error(err);
        statusText.textContent = "Fehler beim Laden der Fragen.";
    }
}

function showQuestion(index) {
    if (!questions || questions.length === 0) return;

    currentIndex = Math.max(0, Math.min(index, questions.length - 1));
    const q = questions[currentIndex];

    questionMeta.textContent = `${currentChapter} – Frage ${currentIndex + 1} von ${questions.length}`;
    questionText.textContent = q.text;

    const selectedIds = answers.get(q.id) || new Set();
    answersContainer.innerHTML = "";

    q.choices.forEach(choice => {
        const btn = document.createElement("button");
        btn.className = "answer-btn";
        btn.textContent = choice.text;
        if (selectedIds.has(choice.id)) {
            btn.classList.add("selected");
        }
        btn.addEventListener("click", () => {
            // Single Choice (für Multiple Choice: Set toggeln)
            const set = new Set([choice.id]);
            answers.set(q.id, set);
            showQuestion(currentIndex); // neu rendern
        });
        answersContainer.appendChild(btn);
    });

    progressText.textContent = `Beantwortet: ${answers.size} / ${questions.length}`;

    btnPrev.disabled = currentIndex === 0;
    btnNext.textContent = currentIndex === questions.length - 1 ? "Auswerten" : "Weiter";
}

// Buttons Vor/Zurück
btnPrev.addEventListener("click", () => {
    if (currentIndex > 0) showQuestion(currentIndex - 1);
});

btnNext.addEventListener("click", () => {
    if (currentIndex < questions.length - 1) {
        showQuestion(currentIndex + 1);
    } else {
        evaluate();
    }
});

btnRestart.addEventListener("click", () => {
    if (currentChapter) {
        const activeBtn = Array.from(document.querySelectorAll(".chapter-btn"))
            .find(b => b.classList.contains("active"));
        startChapter(currentChapter, activeBtn || null);
    }
});

function evaluate() {
    if (!questions || questions.length === 0) return;

    let correct = 0;

    // Wir werten lokal aus (Backend /api/submit brauchen wir hier nicht zwingend)
    questions.forEach(q => {
        const selected = answers.get(q.id) || new Set();
        const correctChoices = q.choices.filter(c => c.isCorrect).map(c => c.id);
        const selectedArr = Array.from(selected);

        const ok =
            selectedArr.length === correctChoices.length &&
            selectedArr.every(id => correctChoices.includes(id));

        if (ok) correct++;
    });

    const total = questions.length;
    const percent = total > 0 ? Math.round((correct / total) * 100) : 0;

    resultSummary.textContent = `Du hast ${correct} von ${total} Fragen richtig beantwortet (${percent} %).`;
    resultCard.style.display = "block";
}

// Start
loadChapters();
