// Portal do cliente Agiliz — sem framework e sem innerHTML: todo texto vindo da API entra como
// textContent (mensagens de pacientes não viram HTML). Rotas por hash: #/inicio, #/conversas, ...
(() => {
  "use strict";

  const app = document.getElementById("app");
  const toastEl = document.getElementById("toast");
  const STORAGE_BOT = "agiliz.portal.bot";
  const state = { me: null, tenantId: null, timer: null };

  // ─── Utilidades ─────────────────────────────────────────────────────────

  /** el("div", { class: "x", onclick }, "texto", filho) — strings viram nós de texto. */
  function el(tag, attrs, ...children) {
    const node = document.createElement(tag);
    for (const [key, value] of Object.entries(attrs || {})) {
      if (value === null || value === undefined || value === false) continue;
      if (key.startsWith("on") && typeof value === "function") node.addEventListener(key.slice(2), value);
      else if (key === "class") node.className = value;
      else node.setAttribute(key, value === true ? "" : String(value));
    }
    for (const child of children.flat(Infinity)) {
      if (child === null || child === undefined || child === false) continue;
      node.append(child instanceof Node ? child : document.createTextNode(String(child)));
    }
    return node;
  }

  const SVG_NS = "http://www.w3.org/2000/svg";
  const ICONS = {
    home: "M3 11l9-7 9 7v9a1 1 0 0 1-1 1h-5v-6H9v6H4a1 1 0 0 1-1-1z",
    chat: "M4 5h16v11H8l-4 4z",
    calendar: "M4 6h16v14H4zM4 10h16M8 3v4M16 3v4",
    plug: "M9 3v5M15 3v5M6 8h12v3a6 6 0 0 1-12 0zM12 17v4"
  };
  function icon(name) {
    const svg = document.createElementNS(SVG_NS, "svg");
    svg.setAttribute("viewBox", "0 0 24 24");
    svg.setAttribute("fill", "none");
    svg.setAttribute("stroke", "currentColor");
    svg.setAttribute("stroke-width", "1.8");
    svg.setAttribute("stroke-linejoin", "round");
    svg.setAttribute("stroke-linecap", "round");
    svg.setAttribute("aria-hidden", "true");
    const path = document.createElementNS(SVG_NS, "path");
    path.setAttribute("d", ICONS[name]);
    svg.append(path);
    return svg;
  }

  let toastTimer;
  function toast(message) {
    toastEl.textContent = message;
    toastEl.classList.add("toast-show");
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => toastEl.classList.remove("toast-show"), 3200);
  }

  class ApiError extends Error {
    constructor(status, message) { super(message); this.status = status; }
  }

  async function api(path, options = {}) {
    const response = await fetch(`/portal/api${path}`, {
      method: options.method || "GET",
      credentials: "same-origin",
      headers: { "Content-Type": "application/json", "X-Agiliz-Portal": "1" },
      body: options.body ? JSON.stringify(options.body) : undefined
    });
    if (response.status === 401) { state.me = null; showLogin(); throw new ApiError(401, "Sessão expirada."); }
    const text = await response.text();
    const data = text ? JSON.parse(text) : null;
    if (!response.ok) throw new ApiError(response.status, (data && (data.error || data.detail)) || "Algo deu errado. Tente de novo.");
    return data;
  }

  const timeFmt = new Intl.DateTimeFormat("pt-BR", { hour: "2-digit", minute: "2-digit" });
  const dayFmt = new Intl.DateTimeFormat("pt-BR", { weekday: "long", day: "2-digit", month: "long" });

  function ago(iso) {
    if (!iso) return "";
    const minutes = Math.round((Date.now() - new Date(iso).getTime()) / 60000);
    if (minutes < 1) return "agora";
    if (minutes < 60) return `há ${minutes} min`;
    const hours = Math.round(minutes / 60);
    if (hours < 24) return `há ${hours} h`;
    return new Date(iso).toLocaleDateString("pt-BR");
  }

  function initials(name) {
    const letters = String(name || "?").replace(/[^\p{L}\s]/gu, "").trim().split(/\s+/).map(w => w[0]).join("");
    return (letters || "#").slice(0, 2).toUpperCase();
  }

  function setBusy(button, busy, label) {
    button.disabled = busy;
    if (label) button.textContent = label;
  }

  function stopPolling() { clearInterval(state.timer); state.timer = null; }

  const bot = () => state.me.bots.find(b => b.tenantId === state.tenantId);
  const botPath = (path) => `/bots/${encodeURIComponent(state.tenantId)}${path}`;

  // ─── Login ──────────────────────────────────────────────────────────────

  function brand() {
    return el("div", { class: "brand" }, el("span", { class: "brand-mark", "aria-hidden": "true" }, "A"), "Agiliz");
  }

  function showLogin() {
    stopPolling();
    let phone = "";
    const error = el("p", { class: "error", role: "alert" });
    const card = el("div", { class: "login-card" });
    app.replaceChildren(el("main", { class: "login" }, card));
    stepPhone();

    function stepPhone() {
      const input = el("input", { class: "input", id: "phone", type: "tel", inputmode: "tel", autocomplete: "tel", placeholder: "(21) 99999-9999", required: true, value: phone });
      const button = el("button", { class: "btn btn-primary btn-block", type: "submit" }, "Receber código no WhatsApp");
      const form = el("form", {
        onsubmit: async (e) => {
          e.preventDefault();
          phone = input.value.trim();
          error.textContent = "";
          setBusy(button, true, "Enviando...");
          try {
            await api("/login/start", { method: "POST", body: { phone } });
            stepCode();
          } catch (err) {
            error.textContent = err.message;
            setBusy(button, false, "Receber código no WhatsApp");
          }
        }
      },
        el("div", { class: "field" }, el("label", { for: "phone" }, "Seu WhatsApp"), input),
        error,
        button);
      card.replaceChildren(brand(), el("h1", {}, "Portal do cliente"),
        el("p", { class: "muted" }, "Acompanhe o atendimento do seu bot pelo celular ou pelo computador. Enviamos um código para o seu WhatsApp."),
        form);
      input.focus();
    }

    function stepCode() {
      const input = el("input", { class: "input input-code", id: "code", inputmode: "numeric", autocomplete: "one-time-code", maxlength: "6", pattern: "[0-9]{6}", required: true, placeholder: "••••••" });
      const button = el("button", { class: "btn btn-primary btn-block", type: "submit" }, "Entrar");
      const resend = el("button", { class: "btn-link", type: "button", disabled: true }, "Reenviar código (60 s)");
      let seconds = 60;
      const countdown = setInterval(() => {
        seconds -= 1;
        if (seconds <= 0) { clearInterval(countdown); resend.disabled = false; resend.textContent = "Reenviar código"; }
        else resend.textContent = `Reenviar código (${seconds} s)`;
      }, 1000);
      resend.addEventListener("click", async () => {
        try { await api("/login/start", { method: "POST", body: { phone } }); toast("Código reenviado."); clearInterval(countdown); stepCode(); }
        catch (err) { error.textContent = err.message; }
      });
      const form = el("form", {
        onsubmit: async (e) => {
          e.preventDefault();
          error.textContent = "";
          setBusy(button, true, "Entrando...");
          try {
            await api("/login/verify", { method: "POST", body: { phone, code: input.value.trim() } });
            clearInterval(countdown);
            await start();
          } catch (err) {
            error.textContent = err.message;
            setBusy(button, false, "Entrar");
            input.select();
          }
        }
      },
        el("div", { class: "field" }, el("label", { for: "code" }, "Código de 6 dígitos"), input),
        error,
        button);
      card.replaceChildren(brand(), el("h1", {}, "Digite o código"),
        el("p", { class: "muted" }, "Se este número tiver acesso, o código chega no WhatsApp em instantes. Ele vale por 10 minutos."),
        form,
        el("div", { class: "row-between" },
          el("button", { class: "btn-link", type: "button", onclick: () => { clearInterval(countdown); stepPhone(); } }, "Trocar número"),
          resend));
      input.focus();
    }
  }

  // ─── Estrutura ──────────────────────────────────────────────────────────

  const ROUTES = [
    { hash: "#/inicio", label: "Início", icon: "home", render: renderHome },
    { hash: "#/conversas", label: "Conversas", icon: "chat", render: renderConversations },
    { hash: "#/agenda", label: "Agenda", icon: "calendar", render: renderAgenda, schedulingOnly: true },
    { hash: "#/integracoes", label: "Integrações", icon: "plug", render: renderIntegrations }
  ];

  async function start() {
    try { state.me = await api("/me"); } catch { return; }
    if (!state.me.bots.length) {
      app.replaceChildren(el("main", { class: "login" }, el("div", { class: "login-card" }, brand(),
        el("h1", {}, "Nenhum bot liberado"),
        el("p", { class: "muted" }, "Este número ainda não tem acesso a nenhum bot. Fale com a equipe Agiliz."),
        el("button", { class: "btn btn-block", onclick: logout }, "Sair"))));
      return;
    }
    const saved = localStorageGet(STORAGE_BOT);
    state.tenantId = state.me.bots.some(b => b.tenantId === saved) ? saved : state.me.bots[0].tenantId;
    if (!location.hash || location.hash === "#") location.hash = "#/inicio";
    else render();
  }

  function localStorageGet(key) { try { return localStorage.getItem(key); } catch { return null; } }
  function localStorageSet(key, value) { try { localStorage.setItem(key, value); } catch { /* modo privado */ } }

  async function logout() {
    try { await api("/logout", { method: "POST" }); } catch { /* sessão já expirada */ }
    state.me = null;
    location.hash = "";
    showLogin();
  }

  function shell(content) {
    const current = bot();
    const routes = ROUTES.filter(r => !r.schedulingOnly || current.type === "Scheduling");
    const active = location.hash.split("?")[0].split("/").slice(0, 2).join("/");

    const nav = el("nav", { class: "nav", "aria-label": "Seções" },
      el("div", { class: "nav-brand" }, brand()),
      routes.map(r => el("a", { href: r.hash, "aria-current": active === r.hash ? "page" : null }, icon(r.icon), el("span", {}, r.label))),
      el("div", { class: "nav-footer" }, el("button", { class: "btn btn-small btn-block", onclick: logout }, "Sair")));
    if (routes.length === 3) nav.classList.add("nav-3");

    const switcher = state.me.bots.length > 1
      ? el("select", {
          class: "bot-switch", "aria-label": "Trocar de bot",
          onchange: (e) => { state.tenantId = e.target.value; localStorageSet(STORAGE_BOT, state.tenantId); location.hash = "#/inicio"; render(); }
        }, state.me.bots.map(b => el("option", { value: b.tenantId, selected: b.tenantId === state.tenantId }, b.name)))
      : null;

    const status = current.paused
      ? el("span", { class: "chip chip-warn" }, el("span", { class: "dot" }), "Pausado")
      : el("span", { class: "chip chip-ok" }, el("span", { class: "dot" }), "Atendendo");

    const header = el("header", { class: "topbar" },
      el("div", { class: "topbar-title" }, el("strong", {}, current.name), el("span", {}, current.isDemo ? "Demonstração" : "Portal do cliente")),
      status,
      switcher,
      el("button", { class: "btn btn-small mobile-only", onclick: logout, "aria-label": "Sair" }, "Sair"));

    app.replaceChildren(el("div", { class: "shell" }, header, nav, el("main", { class: "main", id: "main" }, content)));
  }

  function render() {
    stopPolling();
    if (!state.me) return showLogin();
    const parts = location.hash.split("?")[0].split("/");
    const route = ROUTES.find(r => r.hash === `${parts[0]}/${parts[1]}`) || ROUTES[0];
    if (route.schedulingOnly && bot().type !== "Scheduling") { location.hash = "#/inicio"; return; }
    if (parts[1] === "conversas" && parts[2]) return renderConversation(decodeURIComponent(parts.slice(2).join("/")));
    if (parts[1] === "conectar") return renderConnect();
    route.render();
  }

  function loadingView(title) {
    const main = el("div", {}, el("h2", { class: "page" }, title), el("p", { class: "loading" }, "Carregando..."));
    shell(main);
    return main;
  }

  function errorView(main, title, err) {
    main.replaceChildren(el("h2", { class: "page" }, title), el("div", { class: "banner banner-warn" }, err.message),
      el("button", { class: "btn", onclick: render }, "Tentar de novo"));
  }

  // ─── Início ─────────────────────────────────────────────────────────────

  async function renderHome() {
    const main = loadingView("Início");
    let data;
    try { data = await api(botPath("/overview")); } catch (err) { if (err.status !== 401) errorView(main, "Início", err); return; }

    const current = bot();
    current.paused = data.paused;
    const blocks = [el("h2", { class: "page" }, "Início")];

    if (data.isDemo) {
      blocks.push(el("div", { class: "banner banner-demo" },
        el("span", {}, "Esta é uma demonstração. Mande uma mensagem para o número da clínica e acompanhe aqui."),
        el("button", { class: "btn btn-small", onclick: resetDemo }, "Limpar demonstração")));
    }
    if (data.error) blocks.push(el("div", { class: "banner banner-warn" }, data.error));

    const usage = data.usage;
    const usageCard = el("div", { class: "card" },
      el("div", { class: "stat-label" }, "Contatos atendidos no mês"),
      el("div", { class: "stat" }, usage ? String(usage.contacts) : "—", usage && usage.allowance ? el("span", { class: "muted small" }, ` de ${usage.allowance}`) : null),
      usage && usage.allowance ? el("meter", { min: "0", max: String(usage.allowance), low: String(usage.allowance * 0.8), high: String(usage.allowance), optimum: "0", value: String(Math.min(usage.contacts, usage.allowance)) }) : null,
      el("div", { class: "small muted" }, usageNote(usage, data.plan)));

    const waitingCard = el("a", { class: "card stat-link", href: "#/conversas?filtro=atendente" },
      el("div", { class: "stat-label" }, "Aguardando atendente"),
      el("div", { class: "stat" }, String(data.waiting)),
      el("div", { class: "small muted" }, data.waiting ? "Toque para responder" : "Nenhuma conversa com a equipe"));

    const stats = [usageCard, waitingCard];
    if (current.type === "Scheduling") {
      stats.push(el("a", { class: "card stat-link", href: "#/agenda" },
        el("div", { class: "stat-label" }, "Consultas hoje"),
        el("div", { class: "stat" }, String(data.appointmentsToday)),
        el("div", { class: "small muted" }, "Ver agenda")));
    }
    blocks.push(el("div", { class: `grid ${stats.length === 3 ? "grid-3" : "grid-2"}` }, stats));

    const pauseButton = el("button", { class: data.paused ? "btn btn-primary" : "btn btn-danger" }, data.paused ? "Voltar a atender" : "Pausar o bot");
    pauseButton.addEventListener("click", async () => {
      const pausing = !data.paused;
      if (pausing && !confirm("Pausar o bot? As mensagens continuam chegando aqui, mas ninguém responde automaticamente.")) return;
      setBusy(pauseButton, true);
      try {
        await api(botPath("/pause"), { method: "POST", body: { paused: pausing } });
        current.paused = pausing;
        toast(pausing ? "Bot pausado." : "Bot atendendo de novo.");
        render();
      } catch (err) { toast(err.message); setBusy(pauseButton, false); }
    });
    blocks.push(el("div", { class: "card row-between" },
      el("div", {}, el("h3", {}, data.paused ? "O bot está pausado" : "O bot está atendendo"),
        el("div", { class: "small muted" }, data.paused ? "As mensagens chegam, mas não são respondidas." : "Respondendo seus clientes 24 horas por dia.")),
      pauseButton));

    blocks.push(el("div", { class: "row-between card-gap" }, el("h3", {}, "Conversas recentes"), el("a", { href: "#/conversas" }, "Ver todas")));
    blocks.push(conversationList(data.recent, "Nenhuma conversa ainda."));
    main.replaceChildren(...blocks);
  }

  function usageNote(usage, plan) {
    if (!usage) return "Uso indisponível agora.";
    if (!usage.allowance) return plan ? "" : "Sem franquia definida.";
    if (usage.level === "Exceeded") return "Franquia do plano atingida. O atendimento continua normalmente.";
    if (usage.level === "Warning") return "Perto do limite da franquia do plano.";
    return `Plano ${plan ? plan.name : ""}`.trim();
  }

  async function resetDemo() {
    if (!confirm("Apagar as conversas, contatos e consultas da demonstração?")) return;
    try { await api(botPath("/demo/reset"), { method: "POST" }); toast("Demonstração limpa."); render(); }
    catch (err) { toast(err.message); }
  }

  // ─── Conversas ──────────────────────────────────────────────────────────

  function conversationList(items, emptyText) {
    if (!items || !items.length) return el("div", { class: "card empty" }, emptyText);
    return el("ul", { class: "list" }, items.map(c => el("li", {},
      el("a", { href: `#/conversas/${encodeURIComponent(c.id)}` },
        el("span", { class: "avatar", "aria-hidden": "true" }, initials(c.contact)),
        el("span", { class: "item-main" }, el("strong", {}, c.contact), el("span", {}, c.preview || "")),
        el("span", { class: "item-side" }, ago(c.lastActivityAt), c.withHuman ? el("span", { class: "chip chip-warn" }, "com atendente") : null)))));
  }

  async function renderConversations() {
    const main = loadingView("Conversas");
    const onlyHuman = location.hash.includes("filtro=atendente");
    const load = async () => {
      let items;
      try { items = await api(botPath("/conversations")); } catch (err) { if (err.status !== 401) errorView(main, "Conversas", err); return; }
      const shown = onlyHuman ? items.filter(c => c.withHuman) : items;
      main.replaceChildren(
        el("h2", { class: "page" }, "Conversas"),
        el("div", { class: "tabs", role: "group", "aria-label": "Filtro" },
          el("button", { class: "tab", "aria-pressed": String(!onlyHuman), onclick: () => { location.hash = "#/conversas"; } }, `Todas (${items.length})`),
          el("button", { class: "tab", "aria-pressed": String(onlyHuman), onclick: () => { location.hash = "#/conversas?filtro=atendente"; } }, `Com atendente (${items.filter(c => c.withHuman).length})`)),
        conversationList(shown, onlyHuman ? "Nenhuma conversa aguardando a equipe." : "Nenhuma conversa ainda."));
    };
    await load();
    state.timer = setInterval(load, 10000);
  }

  async function renderConversation(id) {
    const main = loadingView("Conversa");
    let draft = "";
    const load = async () => {
      let c;
      try { c = await api(botPath(`/conversations/${encodeURIComponent(id)}`)); }
      catch (err) { if (err.status !== 401) errorView(main, "Conversa", err); return; }

      const messages = el("div", { class: "messages", "aria-live": "polite" },
        c.messages.length
          ? c.messages.map(m => el("div", { class: `bubble ${m.role === "cliente" ? "bubble-in" : "bubble-out"}` },
              el("span", { class: "bubble-who" }, m.role === "cliente" ? c.contact : "Bot / equipe"), m.text))
          : el("p", { class: "empty" }, c.lastMessagePreview ? `Última mensagem: "${c.lastMessagePreview}"` : "Sem mensagens recentes."));

      const textarea = el("textarea", { class: "input", rows: "2", placeholder: "Escreva sua resposta...", "aria-label": "Resposta" });
      textarea.value = draft;
      textarea.addEventListener("input", () => { draft = textarea.value; });
      const send = el("button", { class: "btn btn-primary", type: "submit" }, "Enviar");
      const composer = el("form", {
        class: "composer",
        onsubmit: async (e) => {
          e.preventDefault();
          if (!textarea.value.trim()) return;
          setBusy(send, true);
          try {
            await api(botPath(`/conversations/${encodeURIComponent(id)}/reply`), { method: "POST", body: { body: textarea.value } });
            draft = "";
            toast("Mensagem enviada. A conversa está com você.");
            await load();
          } catch (err) { toast(err.message); setBusy(send, false); }
        }
      }, textarea, send);

      const action = async (path, message) => {
        try { await api(botPath(`/conversations/${encodeURIComponent(id)}/${path}`), { method: "POST" }); toast(message); await load(); }
        catch (err) { toast(err.message); }
      };

      main.replaceChildren(
        el("div", { class: "chat-head" },
          el("a", { class: "btn btn-small", href: "#/conversas", "aria-label": "Voltar" }, "←"),
          el("span", { class: "avatar", "aria-hidden": "true" }, initials(c.contact)),
          el("span", { class: "item-main" }, el("strong", {}, c.contact), el("span", {}, c.phone)),
          c.withHuman ? el("span", { class: "chip chip-warn" }, "com atendente") : el("span", { class: "chip chip-ok" }, "com o bot")),
        messages,
        el("p", { class: "small muted" }, "Mostramos as mensagens mais recentes desta conversa."),
        composer,
        el("div", { class: "handoff" },
          c.withHuman
            ? el("button", { class: "btn", onclick: () => action("release", "O bot voltou a atender esta conversa.") }, "Devolver ao bot")
            : el("button", { class: "btn", onclick: () => action("takeover", "Conversa com você: o bot não responde até você devolver.") }, "Assumir conversa")));
      messages.scrollTop = messages.scrollHeight;
      if (document.activeElement === document.body) window.scrollTo(0, document.body.scrollHeight);
    };
    await load();
    state.timer = setInterval(() => { if (!document.activeElement || document.activeElement.tagName !== "TEXTAREA") load(); }, 8000);
  }

  // ─── Agenda ─────────────────────────────────────────────────────────────

  async function renderAgenda() {
    const main = loadingView("Agenda");
    const params = new URLSearchParams(location.hash.split("?")[1] || "");
    const day = params.get("dia") || isoDay(new Date());
    let data;
    try { data = await api(botPath(`/appointments?day=${day}`)); } catch (err) { if (err.status !== 401) errorView(main, "Agenda", err); return; }

    const date = new Date(`${data.day}T12:00:00`);
    const shift = (days) => { const d = new Date(date); d.setDate(d.getDate() + days); location.hash = `#/agenda?dia=${isoDay(d)}`; };
    main.replaceChildren(...[
      el("h2", { class: "page" }, "Agenda"),
      el("div", { class: "row-between card-gap" },
        el("button", { class: "btn btn-small", onclick: () => shift(-1), "aria-label": "Dia anterior" }, "‹"),
        el("strong", { class: "capitalize" }, dayFmt.format(date)),
        el("button", { class: "btn btn-small", onclick: () => shift(1), "aria-label": "Próximo dia" }, "›")),
      data.appointments.length
        ? el("ul", { class: "list" }, data.appointments.map(a => el("li", {},
            el("div", { class: "item" },
              el("span", { class: "time" }, a.time),
              el("span", { class: "item-main" }, el("strong", {}, a.contact), el("span", {}, `${a.durationMinutes} min`)),
              el("span", { class: `chip ${a.status === "confirmada" ? "chip-ok" : a.status === "cancelada" || a.status === "faltou" ? "chip-danger" : "chip-brand"}` }, a.status)))))
        : el("div", { class: "card empty" }, "Nenhuma consulta marcada neste dia."),
      isoDay(new Date()) !== data.day ? el("p", {}, el("a", { href: "#/agenda" }, "Voltar para hoje")) : null
    ].filter(Boolean));
  }

  function isoDay(d) {
    const pad = (n) => String(n).padStart(2, "0");
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
  }

  // ─── Integrações ────────────────────────────────────────────────────────

  async function renderIntegrations() {
    const main = loadingView("Integrações");
    let items;
    try { items = await api(botPath("/integrations")); } catch (err) { if (err.status !== 401) errorView(main, "Integrações", err); return; }

    main.replaceChildren(
      el("h2", { class: "page" }, "Integrações"),
      el("p", { class: "muted" }, "Tudo o que o seu bot usa, no mesmo lugar. O que está no seu plano você liga e desliga aqui; o resto a equipe Agiliz configura."),
      el("div", { class: "grid grid-2" }, items.map(integrationCard)));
  }

  function integrationCard(i) {
    const result = el("div", { class: "sr-only", role: "status" });
    const chipClass = i.status === "ligada" ? "chip-ok" : i.status === "sob consulta" ? "chip-warn" : "";
    const actions = el("div", { class: "row card-gap" });

    if (i.id === "whatsapp") actions.append(el("a", { class: "btn btn-small btn-primary", href: "#/conectar" }, "Conectar número"));
    if (i.canToggle) {
      const toggle = el("button", { class: "btn btn-small" }, i.enabled ? "Desligar" : "Ligar");
      toggle.addEventListener("click", async () => {
        setBusy(toggle, true);
        try { await api(botPath(`/integrations/${encodeURIComponent(i.id)}`), { method: "POST", body: { enabled: !i.enabled } }); toast(i.enabled ? `${i.name} desligada.` : `${i.name} ligada.`); render(); }
        catch (err) { toast(err.message); setBusy(toggle, false); }
      });
      actions.append(toggle);
    }
    if (i.testable && i.id !== "email") {
      const test = el("button", { class: "btn btn-small" }, "Testar conexão");
      test.addEventListener("click", async () => {
        setBusy(test, true, "Testando...");
        try {
          const r = await api(botPath(`/integrations/${encodeURIComponent(i.id)}/test`), { method: "POST" });
          result.className = `result ${r.ok ? "result-ok" : "result-fail"}`;
          result.textContent = `${r.ok ? "✓" : "✗"} ${r.message}`;
        } catch (err) { result.className = "result result-fail"; result.textContent = err.message; }
        setBusy(test, false, "Testar conexão");
      });
      actions.append(test);
    }

    return el("div", { class: "card" },
      el("div", { class: "row-between" }, el("h3", {}, i.name), el("span", { class: `chip ${chipClass}` }, i.alwaysOn ? "sempre ligada" : i.status)),
      el("p", { class: "small muted" }, i.description),
      i.offer ? el("div", { class: "small" }, i.offer) : null,
      i.steps.length ? el("ul", { class: "steps" }, i.steps.map(s => el("li", {},
        el("span", { class: `check ${s.done ? "check-done" : ""}`, "aria-label": s.done ? "feito" : "pendente" }, s.done ? "✓" : ""), el("span", {}, s.text)))) : null,
      actions.childNodes.length ? actions : null,
      result);
  }

  // ─── Conectar o WhatsApp pelo celular ───────────────────────────────────

  async function renderConnect() {
    const main = el("div", {});
    shell(main);
    const status = el("div", { role: "status" });
    const codeBox = el("div", {});
    const generate = el("button", { class: "btn btn-primary btn-block" }, "Gerar código de conexão");

    const check = async () => {
      try {
        const r = await api(botPath("/whatsapp/status"));
        status.className = `result ${r.ok ? "result-ok" : "result-fail"}`;
        status.textContent = r.ok ? "✓ WhatsApp conectado. O bot já pode atender." : r.message;
        if (r.ok) stopPolling();
        return r.ok;
      } catch (err) { status.className = "result result-fail"; status.textContent = err.message; return false; }
    };

    generate.addEventListener("click", async () => {
      setBusy(generate, true, "Gerando...");
      try {
        const r = await api(botPath("/whatsapp/pairing"), { method: "POST" });
        if (r.ok && r.pairingCode) {
          const code = r.pairingCode.length === 8 ? `${r.pairingCode.slice(0, 4)}-${r.pairingCode.slice(4)}` : r.pairingCode;
          codeBox.replaceChildren(el("div", { class: "pairing-code", "aria-label": "Código de conexão" }, code),
            el("p", { class: "small muted" }, "O código vale por poucos minutos. Assim que você digitar, esta tela confirma sozinha."));
          stopPolling();
          state.timer = setInterval(check, 5000);
        } else if (r.ok && r.qrCodeDataUrl) {
          // Sem código: QR para o celular do bot ler (abra esta tela em outro aparelho, como o computador).
          codeBox.replaceChildren(el("img", { class: "pairing-qr", src: r.qrCodeDataUrl, alt: "QR code para conectar o WhatsApp" }),
            el("p", { class: "small muted" }, "No celular do bot: WhatsApp › Aparelhos conectados › Conectar um aparelho, e aponte para este QR. Ele muda a cada ~40 s; se expirar, gere outro."));
          stopPolling();
          state.timer = setInterval(check, 5000);
        } else {
          codeBox.replaceChildren(el("div", { class: "result result-fail" }, r.message));
        }
      } catch (err) { codeBox.replaceChildren(el("div", { class: "result result-fail" }, err.message)); }
      setBusy(generate, false, "Gerar outro código");
    });

    main.append(
      el("h2", { class: "page" }, "Conectar o WhatsApp"),
      el("div", { class: "card" },
        el("p", {}, "Use o celular com o número do bot. Não precisa de QR code."),
        el("ol", { class: "howto" },
          el("li", {}, "Toque em “Gerar código de conexão”."),
          el("li", {}, "No WhatsApp do número do bot, abra Configurações › Aparelhos conectados › Conectar um aparelho."),
          el("li", {}, "Escolha “Conectar com número de telefone” e digite o código.")),
        el("div", { class: "card-gap" }, generate),
        codeBox,
        status),
      el("p", {}, el("a", { href: "#/integracoes" }, "← Voltar para Integrações")));
    check();
  }

  // ─── Início do app ──────────────────────────────────────────────────────

  window.addEventListener("hashchange", render);
  start();
})();
