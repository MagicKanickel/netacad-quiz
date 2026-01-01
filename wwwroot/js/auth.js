// auth.js – Simple client-side login handling for NetAcad-Quiz

const AUTH_KEY = "netacad_quiz_email";
const AUTH_COOKIE = "netacad_quiz_email";
const SCHOOL_PATTERN = /^\d{6}@studierende\.htl-donaustadt\.at$/i;

// --- helpers ---

function setCookie(name, value, maxAgeSeconds) {
    let cookie = `${name}=${encodeURIComponent(value)}; path=/`;
    if (maxAgeSeconds) cookie += `; max-age=${maxAgeSeconds}`;
    document.cookie = cookie;
}

function getCookie(name) {
    const cookies = document.cookie.split(";").map(c => c.trim());
    for (const c of cookies) {
        if (c.startsWith(name + "=")) {
            return decodeURIComponent(c.substring(name.length + 1));
        }
    }
    return null;
}

function clearCookie(name) {
    document.cookie = `${name}=; path=/; max-age=0`;
}

function setCurrentUser(email, remember) {
    if (remember) {
        localStorage.setItem(AUTH_KEY, email);
        setCookie(AUTH_COOKIE, email, 60 * 60 * 24 * 30); // 30 Tage
    } else {
        sessionStorage.setItem(AUTH_KEY, email);
        clearCookie(AUTH_COOKIE);
    }
}

function getCurrentUser() {
    return (
        sessionStorage.getItem(AUTH_KEY) ||
        localStorage.getItem(AUTH_KEY) ||
        getCookie(AUTH_COOKIE)
    );
}

function clearCurrentUser() {
    sessionStorage.removeItem(AUTH_KEY);
    localStorage.removeItem(AUTH_KEY);
    clearCookie(AUTH_COOKIE);
}

// --- login form handling ---

async function handleLoginForm(evt) {
    evt.preventDefault();

    const emailInput = document.getElementById("login-email");
    const rememberInput = document.getElementById("login-remember");
    const msgEl = document.getElementById("login-message");

    const email = (emailInput.value || "").trim();

    // Validate pattern: 6 digits + @studierende.htl-donaustadt.at
    if (!SCHOOL_PATTERN.test(email)) {
        msgEl.textContent =
            "Bitte gib deine Schuladresse im Format 123456@studierende.htl-donaustadt.at ein.";
        msgEl.className = "msg msg-error";
        return;
    }

    // Später: hier echten API-Call einbauen (z.B. POST /api/auth/login)
    // Für jetzt: lokal "einloggen"
    setCurrentUser(email, rememberInput.checked);

    msgEl.textContent = "Login erfolgreich – du wirst weitergeleitet …";
    msgEl.className = "msg msg-success";

    const params = new URLSearchParams(window.location.search);
    const returnUrl = params.get("returnUrl") || "/quiz.html";

    setTimeout(() => {
        window.location.href = returnUrl;
    }, 600);
}

// --- auth guard für geschützte Seiten (z.B. quiz.html) ---

function requireAuth() {
    const user = getCurrentUser();
    if (!user) {
        const returnUrl = encodeURIComponent(window.location.pathname + window.location.search);
        window.location.href = `/login.html?returnUrl=${returnUrl}`;
    } else {
        // Optional: Name/Email im Header anzeigen
        const userBadge = document.getElementById("user-badge");
        if (userBadge) {
            userBadge.textContent = user;
            userBadge.style.display = "inline-flex";
        }
    }
}

// --- Logout-Handler ---

function handleLogout(e) {
    e.preventDefault();
    clearCurrentUser();
    window.location.href = "/";
}

document.addEventListener("DOMContentLoaded", () => {
    const loginForm = document.getElementById("login-form");
    if (loginForm) {
        loginForm.addEventListener("submit", handleLoginForm);
    }

    const logoutLinks = document.querySelectorAll("[data-logout-link]");
    logoutLinks.forEach(a => a.addEventListener("click", handleLogout));
});

// Expose requireAuth global für quiz.html etc.
window.requireAuth = requireAuth;
