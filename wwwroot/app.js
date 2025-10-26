// Burger toggle
document.addEventListener('click', (e) => {
  const menu = document.querySelector('.menu');
  const isBurger = e.target.closest('.burger');
  if (isBurger) {
    menu.style.display = (menu.style.display === 'block') ? 'none' : 'block';
  } else if (!e.target.closest('.menu')) {
    if (menu) menu.style.display = 'none';
  }
});

// API helper
async function api(path, opts = {}) {
  const res = await fetch(path, { credentials: 'include', ...opts });
  if (!res.ok) throw new Error(await res.text());
  return res.json();
}

// Guard for pages that need auth
async function requireAuthOrRedirect() {
  try {
    const s = await api('/api/auth/status');
    if (!s.isAuthenticated) location.href = '/login.html';
    return s;
  } catch {
    location.href = '/login.html';
  }
}

// Get auth status (for nav, etc.)
async function getAuthStatus() {
  try { return await api('/api/auth/status'); } catch { return { isAuthenticated: false }; }
}

window.NetAcad = { api, requireAuthOrRedirect, getAuthStatus };
