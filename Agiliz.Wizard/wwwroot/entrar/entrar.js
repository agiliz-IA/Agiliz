// Entrada única da Agiliz (/entrar): a mesma tela para funcionários (painel) e clientes (portal).
// Sem framework e sem innerHTML: todo texto entra como textContent. Telas por caminho:
// /entrar, /entrar/proteger, /entrar/escolher, /entrar/conta, /entrar/sair.
(() => {
  "use strict";

  const app = document.getElementById("app");
  const toastEl = document.getElementById("toast");
  const params = new URLSearchParams(location.search);
  const RESEND_SECONDS = 60;

  // Fontes carregadas depois da tela: se o Google Fonts demorar ou estiver bloqueado, a entrada não trava.
  document.head.append(Object.assign(document.createElement("link"), {
    rel: "stylesheet",
    href: "https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600&family=Space+Grotesk:wght@500;600;700&display=swap"
  }));

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
    bolt: ["M13 2 4 14h7l-1 8 9-12h-7z"],
    whatsapp: ["M3 21l1.6-4.6A8.5 8.5 0 1 1 8 20z", "M9 9.5c0 3 2.5 5.5 5.5 5.5l1-1.5-2-1-1 .8a4 4 0 0 1-1.8-1.8l.8-1-1-2z"],
    mail: ["M3 5h18v14H3z", "m3 7 9 6 9-6"],
    back: ["M15 18l-6-6 6-6"],
    chevron: ["m9 6 6 6-6 6"],
    plus: ["M12 5v14M5 12h14"],
    check: ["m5 12 5 5L20 7"],
    help: ["M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18z", "M9.5 9.5a2.5 2.5 0 1 1 3.5 2.3c-.6.3-1 .9-1 1.6v.6", "M12 17h.01"],
    alert: ["M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18z", "M12 8v5", "M12 16h.01"],
    edit: ["M12 20h9", "M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4z"],
    shield: ["M12 3 4 6v6c0 4.5 3.4 8.3 8 9 4.6-.7 8-4.5 8-9V6z", "m9 12 2 2 4-4"],
    grid: ["M3 3h7v9H3z", "M14 3h7v5h-7z", "M14 12h7v9h-7z", "M3 16h7v5H3z"],
    chat: ["M21 12a8 8 0 0 1-11.8 7L4 20l1-4.8A8 8 0 1 1 21 12z", "M8.5 11h7M8.5 14h4"],
    logout: ["M15 4h4v16h-4", "M10 8l-4 4 4 4", "M6 12h10"]
  };

  function icon(name) {
    const svg = document.createElementNS(SVG_NS, "svg");
    svg.setAttribute("viewBox", "0 0 24 24");
    svg.setAttribute("fill", "none");
    svg.setAttribute("stroke", "currentColor");
    svg.setAttribute("stroke-width", "1.9");
    svg.setAttribute("stroke-linejoin", "round");
    svg.setAttribute("stroke-linecap", "round");
    svg.setAttribute("aria-hidden", "true");
    for (const d of ICONS[name]) {
      const path = document.createElementNS(SVG_NS, "path");
      path.setAttribute("d", d);
      svg.append(path);
    }
    return svg;
  }

  function googleIcon() {
    const svg = document.createElementNS(SVG_NS, "svg");
    svg.setAttribute("viewBox", "0 0 48 48");
    svg.setAttribute("aria-hidden", "true");
    const parts = [
      ["#EA4335", "M24 9.5c3.5 0 6.6 1.2 9 3.5l6.7-6.7C35.6 2.4 30.2 0 24 0 14.6 0 6.6 5.4 2.6 13.3l7.8 6C12.3 13.6 17.7 9.5 24 9.5z"],
      ["#4285F4", "M46.5 24.5c0-1.6-.1-3.1-.4-4.5H24v9h12.7c-.6 3-2.3 5.5-4.8 7.2l7.6 5.9c4.4-4.1 7-10.1 7-17.6z"],
      ["#FBBC05", "M10.4 28.7c-.5-1.4-.8-3-.8-4.7s.3-3.3.8-4.7l-7.8-6C.9 16.6 0 20.2 0 24s.9 7.4 2.6 10.7l7.8-6z"],
      ["#34A853", "M24 48c6.5 0 11.9-2.1 15.9-5.8l-7.6-5.9c-2.1 1.4-4.9 2.3-8.3 2.3-6.3 0-11.7-4.1-13.6-9.8l-7.8 6C6.6 42.6 14.6 48 24 48z"]
    ];
    for (const [fill, d] of parts) {
      const path = document.createElementNS(SVG_NS, "path");
      path.setAttribute("fill", fill);
      path.setAttribute("d", d);
      svg.append(path);
    }
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
    constructor(status, message, data) { super(message); this.status = status; this.data = data; }
  }

  async function api(path, body) {
    const response = await fetch(`/entrar/api${path}`, {
      method: body === undefined ? "GET" : "POST",
      credentials: "same-origin",
      headers: { "Content-Type": "application/json", "X-Agiliz-Portal": "1" },
      body: body === undefined ? undefined : JSON.stringify(body)
    });
    const text = await response.text();
    let data = null;
    try { data = text ? JSON.parse(text) : null; } catch { data = null; }
    if (!response.ok) {
      const message = response.status === 429 && !(data && data.error)
        ? "Muitas tentativas. Espere um minuto e tente de novo."
        : (data && (data.error || data.detail)) || "Algo deu errado. Tente de novo.";
      throw new ApiError(response.status, message, data);
    }
    return data;
  }

  async function me() {
    try { return await api("/me"); } catch (err) { if (err.status === 401) return null; throw err; }
  }

  /** Só caminhos deste site ("/portal"), nunca outro endereço. */
  function safeReturn(value) {
    return value && value.startsWith("/") && !value.startsWith("//") && !value.startsWith("/\\") ? value : null;
  }

  function go(path) { location.assign(path); }

  function setBusy(button, busy, label) {
    button.disabled = busy;
    if (label) button.textContent = busy ? "Aguarde…" : label;
  }

  function screen(...children) {
    const main = el("main", { class: "screen" }, ...children);
    app.replaceChildren(main);
    const heading = main.querySelector("h1");
    if (heading) { heading.setAttribute("tabindex", "-1"); heading.focus({ preventScroll: true }); }
    return main;
  }

  function brand() {
    return el("div", { class: "brand" },
      el("div", { class: "brand-mark" }, icon("bolt")),
      el("span", { class: "brand-name" }, "agiliz", el("span", {}, ".ia")));
  }

  function backButton(onclick, label = "Voltar") {
    return el("button", { class: "back", type: "button", "aria-label": label, onclick }, icon("back"));
  }

  function alertBox(kind, title, text) {
    return el("div", { class: `alert alert-${kind}`, role: "alert" },
      icon(kind === "ok" ? "check" : "alert"),
      el("span", {}, title ? el("span", { class: "alert-title" }, title) : null, text));
  }

  function initials(name) {
    return (name || "?").trim().slice(0, 1).toUpperCase() || "?";
  }

  /** Telefone ou e-mail? Só para escolher o ícone enquanto a pessoa digita. */
  function looksLikeEmail(value) { return value.includes("@") || /[a-z]/i.test(value); }

  function identifierField(id, value, label) {
    const iconSlot = el("span", { class: "input-icon" }, icon(looksLikeEmail(value || "") ? "mail" : "whatsapp"));
    const input = el("input", {
      id, type: "text", inputmode: "email", autocomplete: "username", autocapitalize: "none", spellcheck: "false",
      placeholder: "(21) 98888-7777 ou voce@email.com", value: value || ""
    });
    const wrap = el("div", { class: "input-wrap" }, iconSlot, input);
    input.addEventListener("input", () => iconSlot.replaceChildren(icon(looksLikeEmail(input.value) ? "mail" : "whatsapp")));
    const field = el("div", { class: "field" },
      el("label", { for: id }, label || "WhatsApp ou e-mail"),
      wrap,
      el("p", { class: "hint" }, "Reconhecemos sozinhos se é número ou e-mail."));
    return { field, input, wrap };
  }

  /** Seis caixinhas: colar preenche tudo; ao completar, chama onComplete(código). */
  function codeInput(onComplete) {
    const boxes = [];
    const grid = el("div", { class: "code-grid", role: "group", "aria-label": "Código de 6 dígitos" });
    const value = () => boxes.map(b => b.value).join("");
    const fill = (digits, start) => {
      let i = start;
      for (const d of digits) { if (i > 5) break; boxes[i].value = d; i++; }
      (boxes[Math.min(i, 5)]).focus();
      if (value().length === 6) onComplete(value());
    };
    for (let i = 0; i < 6; i++) {
      const box = el("input", {
        type: "text", inputmode: "numeric", maxlength: "1", "aria-label": `Dígito ${i + 1}`,
        autocomplete: i === 0 ? "one-time-code" : "off"
      });
      box.addEventListener("input", () => {
        grid.classList.remove("error");
        const digits = box.value.replace(/\D/g, "");
        box.value = "";
        if (digits) fill(digits, i);
      });
      box.addEventListener("keydown", event => {
        if (event.key === "Backspace" && !box.value && i > 0) { boxes[i - 1].value = ""; boxes[i - 1].focus(); event.preventDefault(); }
        if (event.key === "ArrowLeft" && i > 0) boxes[i - 1].focus();
        if (event.key === "ArrowRight" && i < 5) boxes[i + 1].focus();
      });
      box.addEventListener("paste", event => {
        const digits = (event.clipboardData || window.clipboardData).getData("text").replace(/\D/g, "");
        if (!digits) return;
        event.preventDefault();
        fill(digits, i);
      });
      boxes.push(box);
      grid.append(box);
    }
    return {
      grid,
      focus: () => boxes[0].focus(),
      reset: () => { boxes.forEach(b => { b.value = ""; }); grid.classList.add("error"); boxes[0].focus(); }
    };
  }

  /** Botão "Reenviar código em 0:42" que se libera sozinho. */
  function resendButton(onResend) {
    const label = el("span", {}, "Reenviar código");
    const timer = el("strong", {});
    const button = el("button", { class: "resend", type: "button", disabled: true }, label, timer);
    let left = RESEND_SECONDS;
    let handle;
    const tick = () => {
      if (!document.body.contains(button)) { clearInterval(handle); return; }
      left--;
      if (left <= 0) {
        clearInterval(handle);
        button.disabled = false;
        timer.textContent = "agora";
      } else {
        timer.textContent = clock(left);
      }
    };
    const clock = seconds => `em ${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, "0")}`;
    timer.textContent = clock(left);
    handle = setInterval(tick, 1000);
    button.addEventListener("click", async () => {
      button.disabled = true;
      try { await onResend(); } catch (err) { toast(err.message); button.disabled = false; }
    });
    return button;
  }

  // ─── Entrar ─────────────────────────────────────────────────────────────

  const ERRORS = {
    convite: ["Link de convite inválido", "Esse link venceu ou já foi usado. Peça outro ao seu contato na Agiliz, ou entre com seu WhatsApp ou e-mail."],
    google: ["Não deu para entrar com o Google", "Tente de novo, ou entre com seu WhatsApp ou e-mail."],
    "google-sem-acesso": ["Essa conta Google ainda não tem acesso", "Entre com seu WhatsApp ou e-mail. Depois, em Minha conta, você pode adicionar o Google."],
    "google-desligado": ["Entrar com Google ainda não está ligado", "Entre com seu WhatsApp ou e-mail."]
  };

  const login = { config: { google: false, email: false }, identifier: "", channel: null, sentTo: "", error: null, noAccess: false };

  async function showLoginPage() {
    app.replaceChildren(el("div", { class: "loading" }, "Carregando…"));
    const [account, config] = await Promise.all([me().catch(() => null), api("/config").catch(() => login.config)]);
    if (account) { go(safeReturn(params.get("volta")) || account.destination); return; }
    login.config = config;
    const preset = ERRORS[params.get("erro")];
    if (preset) login.error = preset;
    stepIdentifier();
  }

  function googleUrl(mode) {
    const back = mode === "vincular" ? location.pathname + location.search : params.get("volta");
    const query = new URLSearchParams({ modo: mode });
    if (back) query.set("volta", back);
    return `/entrar/google?${query}`;
  }

  function stepIdentifier() {
    const { field, input, wrap } = identifierField("ident", login.identifier);
    if (login.noAccess) wrap.classList.add("warn");
    const submit = el("button", { class: "btn btn-primary", type: "submit" }, "Receber código");

    const form = el("form", { class: "stack", novalidate: true },
      login.config.google ? el("a", { class: "btn btn-google", href: googleUrl("entrar") }, googleIcon(), "Continuar com Google") : null,
      login.config.google ? el("div", { class: "divider" }, "ou") : null,
      field,
      login.noAccess
        ? alertBox("warn", "Este número ou e-mail ainda não tem acesso", "Confira se digitou certo ou entre de outro jeito. Se é cliente novo, peça o convite ao seu contato na Agiliz.")
        : login.error ? alertBox("error", login.error[0], login.error[1]) : null,
      submit);

    form.addEventListener("submit", async event => {
      event.preventDefault();
      login.identifier = input.value.trim();
      if (!login.identifier) { input.focus(); return; }
      setBusy(submit, true, "Receber código");
      await requestCode();
      setBusy(submit, false, "Receber código");
    });

    screen(
      brand(),
      el("div", { class: "heading" },
        el("h1", {}, "Entrar"),
        el("p", {}, "Painel da equipe e portal do cliente, no mesmo lugar.")),
      form,
      el("div", { class: "grow" }),
      el("p", { class: "footer-note" }, "Sem senha. O acesso é liberado pela equipe Agiliz."));
    if (!login.config.google || login.identifier) input.focus();
  }

  async function requestCode(via) {
    login.error = null;
    login.noAccess = false;
    try {
      const result = await api("/start", { identifier: login.identifier, via });
      login.channel = result.channel;
      login.sentTo = result.sentTo;
      stepCode();
      return true;
    } catch (err) {
      if (err.data && err.data.code === "sem-acesso") login.noAccess = true;
      else if (err.status === 429 && err.data && err.data.sentTo) { login.channel = err.data.channel; login.sentTo = err.data.sentTo; stepCode(); toast(err.message); return true; }
      else login.error = ["Não foi agora", err.message];
      stepIdentifier();
      return false;
    }
  }

  function stepCode() {
    let verifying = false;
    const where = login.channel === "Email" ? "para o e-mail" : "para o WhatsApp";
    const status = el("div", { "aria-live": "polite" });
    const code = codeInput(async value => {
      if (verifying) return;
      verifying = true;
      status.replaceChildren();
      try {
        const result = await api("/verify", { identifier: login.identifier, code: value, return: safeReturn(params.get("volta")) });
        go(result.next);
      } catch (err) {
        status.replaceChildren(alertBox("error", null, err.message));
        code.reset();
        verifying = false;
      }
    });

    screen(
      backButton(() => stepIdentifier(), "Voltar e corrigir"),
      el("div", { class: "icon-tile" }, icon(login.channel === "Email" ? "mail" : "whatsapp")),
      el("div", { class: "heading" },
        el("h1", {}, "Digite o código"),
        el("p", {}, `Mandamos 6 números ${where} `, el("strong", {}, login.sentTo || ""), ". Vale por 10 minutos.")),
      el("div", { class: "stack-sm" },
        el("span", { class: "sr-only" }, "Código de 6 dígitos"),
        code.grid,
        el("p", { class: "hint" }, "Colou o código? Preenchemos tudo e entramos sozinhos.")),
      status,
      resendButton(async () => { if (await requestCode(login.channel === "Email" ? "email" : undefined)) toast("Código reenviado."); }),
      el("div", { class: "grow" }),
      el("button", { class: "btn btn-outline", type: "button", onclick: showNotArrived }, icon("help"), "Não chegou?"));
    code.focus();
  }

  function showNotArrived() {
    const close = () => backdrop.remove();
    const options = [];
    if (login.channel !== "Email") {
      options.push(el("button", {
        class: "option", type: "button", onclick: async () => {
          close();
          if (await requestCode("email")) toast("Mandamos o código para o seu e-mail.");
        }
      },
      el("span", { class: "option-icon" }, icon("mail")),
      el("span", { class: "option-text" },
        el("span", { class: "option-title" }, "Mandar por e-mail"),
        el("span", { class: "option-sub" }, "Para o e-mail cadastrado na sua conta")),
      el("span", { class: "option-end" }, icon("chevron"))));
    }
    if (login.config.google) {
      options.push(el("a", { class: "option", href: googleUrl("entrar") },
        el("span", { class: "option-icon google" }, googleIcon()),
        el("span", { class: "option-text" },
          el("span", { class: "option-title" }, "Entrar com Google"),
          el("span", { class: "option-sub" }, "Sem código, um toque")),
        el("span", { class: "option-end" }, icon("chevron"))));
    }
    options.push(el("button", { class: "option", type: "button", onclick: () => { close(); stepIdentifier(); } },
      el("span", { class: "option-icon" }, icon("edit")),
      el("span", { class: "option-text" },
        el("span", { class: "option-title" }, login.channel === "Email" ? "Corrigir o e-mail" : "Corrigir o número"),
        el("span", { class: "option-sub" }, login.sentTo || "")),
      el("span", { class: "option-end" }, icon("chevron"))));

    const sheet = el("div", { class: "sheet", role: "dialog", "aria-modal": "true", "aria-labelledby": "sheet-title" },
      el("div", { class: "sheet-handle" }),
      el("div", {},
        el("h2", { id: "sheet-title" }, "Não chegou o código?"),
        el("p", {}, "Às vezes demora um pouco. Você também pode:")),
      el("div", { class: "stack-sm" }, options),
      el("div", { class: "tip" },
        el("strong", {}, "Dica"),
        login.channel === "Email"
          ? "Confira a caixa de spam e a aba Promoções."
          : "O código vem do número oficial da Agiliz, numa conversa nova. Se você está testando no mesmo celular do bot, ele também aparece lá."),
      el("button", { class: "btn btn-text", type: "button", onclick: close }, "Fechar"));
    const backdrop = el("div", { class: "sheet-backdrop", onclick: event => { if (event.target === backdrop) close(); } }, sheet);
    backdrop.addEventListener("keydown", event => { if (event.key === "Escape") close(); });
    document.body.append(backdrop);
    sheet.querySelector("button, a").focus();
  }

  // ─── Adicionar forma de entrar (proteger e Minha conta) ───────────────────

  /** Formulário em dois passos: WhatsApp/e-mail → código. onDone(me) quando confirmar. */
  function addIdentityForm(onDone, onCancel) {
    const box = el("div", { class: "stack" });
    let identifier = "";

    const stepOne = (error) => {
      const { field, input } = identifierField("novo", identifier, "Novo WhatsApp ou e-mail");
      const submit = el("button", { class: "btn btn-primary", type: "submit" }, "Mandar código");
      const form = el("form", { class: "stack", novalidate: true }, field, error ? alertBox("error", null, error) : null,
        submit, el("button", { class: "btn btn-text", type: "button", onclick: onCancel }, "Cancelar"));
      form.addEventListener("submit", async event => {
        event.preventDefault();
        identifier = input.value.trim();
        if (!identifier) { input.focus(); return; }
        setBusy(submit, true, "Mandar código");
        try {
          const result = await api("/identities/start", { identifier });
          stepTwo(result);
        } catch (err) {
          stepOne(err.message);
        }
      });
      box.replaceChildren(form);
      input.focus();
    };

    const stepTwo = (sent) => {
      const status = el("div", { "aria-live": "polite" });
      let verifying = false;
      const code = codeInput(async value => {
        if (verifying) return;
        verifying = true;
        try {
          onDone(await api("/identities/verify", { code: value }));
        } catch (err) {
          status.replaceChildren(alertBox("error", null, err.message));
          code.reset();
          verifying = false;
        }
      });
      box.replaceChildren(
        el("p", { class: "hint" }, "Digite o código que mandamos para ", el("strong", {}, sent.sentTo || identifier), "."),
        code.grid, status,
        el("button", { class: "btn btn-text", type: "button", onclick: () => stepOne() }, "Corrigir"));
      code.focus();
    };

    stepOne();
    return box;
  }

  function identityRow(identity, endNode) {
    const kindIcon = identity.kind === "Google" ? googleIcon() : icon(identity.kind === "Email" ? "mail" : "whatsapp");
    const title = identity.kind === "Google" ? "Google" : identity.kind === "Email" ? "E-mail" : "WhatsApp";
    return el("div", { class: "list-row" }, kindIcon,
      el("span", { class: "option-text" },
        el("span", { class: "option-title" }, title),
        el("span", { class: "option-sub" }, identity.label)),
      endNode);
  }

  // ─── Proteja sua conta (primeiro acesso) ─────────────────────────────────

  async function showProtect() {
    const [account, config] = await Promise.all([me(), api("/config").catch(() => ({ google: false }))]);
    if (!account) { go("/entrar"); return; }
    const next = safeReturn(params.get("volta")) || account.destination;
    const finish = async () => { try { await api("/protect-seen", {}); } catch { /* segue mesmo assim */ } go(next); };

    const hasGoogle = account.identities.some(i => i.kind === "Google");
    const hasEmail = account.identities.some(i => i.kind === "Email");
    const hasPhone = account.identities.some(i => i.kind === "WhatsApp");
    const area = el("div", { class: "stack-sm" });

    const render = (acc) => {
      const rows = acc.identities.map(i => el("div", { class: "option" },
        el("span", { class: "option-icon ok" }, icon("check")),
        el("span", { class: "option-text" },
          el("span", { class: "option-title" }, i.kind === "Google" ? "Google" : i.kind === "Email" ? "E-mail" : "WhatsApp"),
          el("span", { class: "option-sub" }, i.label)),
        el("span", { class: "option-badge" }, "Ativo")));
      const add = [];
      if (config.google && !hasGoogle) {
        add.push(el("a", { class: "option featured", href: googleUrl("vincular") },
          el("span", { class: "option-icon google" }, googleIcon()),
          el("span", { class: "option-text" },
            el("span", { class: "option-title" }, "Adicionar Google"),
            el("span", { class: "option-sub" }, "Recomendado: entra com um toque")),
          el("span", { class: "option-end" }, icon("plus"))));
      }
      add.push(el("button", {
        class: "option" + (config.google && !hasGoogle ? "" : " featured"), type: "button",
        onclick: () => area.replaceChildren(addIdentityForm(async () => finish(), () => render(acc)))
      },
      el("span", { class: "option-icon" }, icon(hasEmail ? "whatsapp" : "mail")),
      el("span", { class: "option-text" },
        el("span", { class: "option-title" }, hasEmail ? "Adicionar WhatsApp" : hasPhone ? "Adicionar e-mail" : "Adicionar WhatsApp ou e-mail"),
        el("span", { class: "option-sub" }, "Confirmamos com um código")),
      el("span", { class: "option-end" }, icon("plus"))));
      area.replaceChildren(...rows, ...add);
    };
    render(account);

    screen(
      el("div", { class: "icon-tile strong" }, icon("shield")),
      el("div", { class: "heading" },
        el("p", { class: "eyebrow" }, `Boas-vindas, ${account.name}`),
        el("h1", {}, "Adicione outra forma de entrar"),
        el("p", {}, "Se você trocar de celular ou o WhatsApp falhar, ainda consegue entrar. Tudo o que está na sua conta continua lá.")),
      params.get("erro") === "google-outra-conta" ? alertBox("warn", "Essa conta Google já é de outra pessoa", "Use outra conta Google ou adicione um e-mail.") : null,
      area,
      el("div", { class: "grow" }),
      el("button", { class: "btn btn-text", type: "button", onclick: finish }, "Agora não"));
  }

  // ─── Painel ou portal ───────────────────────────────────────────────────

  async function showChoose() {
    const account = await me();
    if (!account) { go("/entrar?volta=" + encodeURIComponent(location.pathname)); return; }
    if (!(account.staff && account.client)) { go(account.destination); return; }

    screen(
      el("div", { class: "person" },
        el("div", { class: "avatar", "aria-hidden": "true" }, initials(account.name)),
        el("div", { class: "person-text" }, el("span", { class: "person-name" }, `Olá, ${account.name}`))),
      el("div", { class: "heading" }, el("h1", {}, "Para onde você quer ir?")),
      el("div", { class: "stack" },
        el("a", { class: "choice primary", href: "/" },
          el("span", { class: "choice-top" },
            el("span", { class: "option-icon" }, icon("grid")),
            el("span", { class: "pill pill-admin" }, account.role === "Admin" ? "Admin" : "Vendedor")),
          el("span", {},
            el("span", { class: "choice-title" }, "Painel da equipe"), el("br"),
            el("span", { class: "choice-sub" }, "Criar bots, demonstrações, propostas e convidar clientes."))),
        el("a", { class: "choice", href: "/portal" },
          el("span", { class: "choice-top" },
            el("span", { class: "option-icon" }, icon("chat")),
            el("span", { class: "pill" }, account.bots === 1 ? "1 bot" : `${account.bots} bots`)),
          el("span", {},
            el("span", { class: "choice-title" }, "Portal do cliente"), el("br"),
            el("span", { class: "choice-sub" }, "Conversas, agenda e configurações do bot da clínica.")))),
      el("div", { class: "grow" }),
      el("p", { class: "footer-note" }, "Dá para trocar depois pelo menu."));
  }

  // ─── Minha conta ────────────────────────────────────────────────────────

  async function showAccount() {
    const [account, config] = await Promise.all([me(), api("/config").catch(() => ({ google: false }))]);
    if (!account) { go("/entrar?volta=" + encodeURIComponent(location.pathname)); return; }
    renderAccount(account, config, params.get("erro") === "google-outra-conta"
      ? alertBox("warn", "Essa conta Google já é de outra pessoa", "Use outra conta Google.") : null);
  }

  function renderAccount(account, config, notice) {
    const canRemove = account.identities.length > 1;
    const list = el("div", { class: "list" }, account.identities.map(identity =>
      identityRow(identity, canRemove
        ? el("button", {
          class: "small-btn", type: "button", "aria-label": `Remover ${identity.label}`, onclick: async (event) => {
            const button = event.currentTarget;
            button.disabled = true;
            try { renderAccount(await api("/identities/remove", { kind: identity.kind, value: identity.value }), config); toast("Removido."); }
            catch (err) { toast(err.message); button.disabled = false; }
          }
        }, "Remover")
        : null)));

    const addArea = el("div", {});
    const addButtons = el("div", { class: "list" });
    if (config.google && !account.identities.some(i => i.kind === "Google")) {
      addButtons.append(el("a", { class: "list-action accent", href: googleUrl("vincular") }, icon("plus"), "Adicionar Google"));
    }
    addButtons.append(el("button", {
      class: "list-action accent", type: "button",
      onclick: () => addArea.replaceChildren(addIdentityForm(updated => { toast("Forma de entrar adicionada."); renderAccount(updated, config); }, () => addArea.replaceChildren()))
    }, icon("plus"), "Adicionar WhatsApp ou e-mail"));

    const pills = [];
    if (account.role) pills.push(el("span", { class: `pill ${account.role === "Admin" ? "pill-admin" : "pill-seller"}` }, account.role === "Admin" ? "Admin" : "Vendedor"));
    if (account.client) pills.push(el("span", { class: "pill" }, account.bots === 1 ? "Cliente · 1 bot" : `Cliente · ${account.bots} bots`));

    const places = [];
    if (account.staff) places.push(el("a", { class: "list-action", href: "/" }, icon("grid"), "Ir para o painel"));
    if (account.client) places.push(el("a", { class: "list-action", href: "/portal" }, icon("chat"), "Ir para o portal"));

    screen(
      el("div", { class: "brand" }, el("h1", { class: "brand-name" }, "Minha conta")),
      notice,
      el("div", { class: "person" },
        el("div", { class: "avatar", "aria-hidden": "true" }, initials(account.name)),
        el("div", { class: "person-text" }, el("span", { class: "person-name" }, account.name), el("div", { class: "pills" }, pills))),
      places.length ? el("div", { class: "list" }, places) : null,
      el("section", { class: "stack-sm", "aria-labelledby": "formas" },
        el("h2", { class: "section-title", id: "formas" }, "Formas de entrar"),
        list, addButtons, addArea,
        el("p", { class: "hint" }, "Qualquer uma abre a mesma conta. A última não pode ser removida.")),
      el("section", { class: "stack-sm", "aria-labelledby": "sessao" },
        el("h2", { class: "section-title", id: "sessao" }, "Sessão"),
        el("div", { class: "list" },
          el("button", { class: "list-action", type: "button", onclick: logout }, icon("logout"), "Sair deste aparelho"),
          el("button", {
            class: "list-action danger", type: "button", onclick: async () => {
              try { await api("/logout-all", {}); } catch { /* já saiu */ }
              go("/entrar");
            }
          }, icon("logout"), "Sair de todos os aparelhos"))));
  }

  async function logout() {
    try { await api("/logout", {}); } catch { /* sessão já tinha acabado */ }
    go("/entrar");
  }

  // ─── Rotas ──────────────────────────────────────────────────────────────

  const path = location.pathname.replace(/\/+$/, "");
  const routes = {
    "/entrar/proteger": showProtect,
    "/entrar/escolher": showChoose,
    "/entrar/conta": showAccount,
    "/entrar/sair": logout
  };
  (routes[path] || showLoginPage)().catch(err => {
    screen(brand(), alertBox("error", "Não conseguimos carregar", err.message || "Tente de novo em instantes."),
      el("button", { class: "btn btn-primary", type: "button", onclick: () => location.reload() }, "Tentar de novo"));
  });
})();
