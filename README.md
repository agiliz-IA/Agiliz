# Agiliz

Plataforma para criar e operar **bots de WhatsApp com IA** para pequenos negócios (o foco hoje são clínicas: o bot agenda, confirma, remarca e cancela consultas na Google Agenda e passa a conversa para um humano quando precisa).

São quatro partes que sobem juntas no Docker:

| Parte | Para que serve | Endereço local |
|---|---|---|
| **Painel (Wizard)** | Onde a equipe Agiliz cria bots, vende (quiz, planos, propostas) e acompanha custos. | http://localhost:5001 |
| **Portal do cliente** | Página simples (PC e celular) onde o dono do negócio acompanha o bot dele. | http://localhost:5001/portal |
| **Motor (Runtime)** | Recebe as mensagens do WhatsApp e responde com a IA. Não tem tela. | http://localhost:5000/health |
| **Evolution API** | Conecta os números de WhatsApp (via WhatsApp Web). | http://localhost:8080 |

> 📘 **Como usar o painel e o portal:** veja o [Guia do painel e do portal](docs/GUIA-DO-PAINEL.md).

---

## 🚀 Subir o projeto no seu computador (passo a passo)

### Passo 0 — Instalar o que precisa (uma vez só)

1. **Docker Desktop**: https://www.docker.com/products/docker-desktop/ — instale, abra e espere ficar "Engine running" (no Windows ele pode pedir para instalar o WSL; aceite e reinicie).
2. **Git**: https://git-scm.com/downloads
3. Baixe o código, na branch certa (todo o trabalho recente está em `feat/retomada-piloto`):
   ```bash
   git clone https://github.com/agiliz-IA/Agiliz.git
   cd Agiliz
   git checkout feat/retomada-piloto
   ```

Não precisa instalar .NET para rodar no Docker.

### Passo 1 — Criar as chaves

| O quê | Obrigatório? | Onde conseguir |
|---|---|---|
| Chave do **Groq** (`GROQ_API_KEY`) | Sim | https://console.groq.com/keys → *Create API Key*. Tem plano gratuito. |
| Chave da **Anthropic** (`ANTHROPIC_API_KEY`) | Não | https://console.anthropic.com/settings/keys. Só se algum bot usar Claude. |
| Senha da **Evolution** (`AUTHENTICATION_API_KEY` = `EVOLUTION_API_TOKEN`) | Sim | Você inventa (valor aleatório). Os dois têm que ser iguais. |
| Senha **painel ↔ motor** (`RUNTIME_ADMIN_TOKEN`) | Sim | Você inventa (valor aleatório). |
| Login do **painel** (`WIZARD_ADMIN_USER` / `WIZARD_ADMIN_PASSWORD`) | Sim | Você inventa. |
| Credencial da **Google Agenda** (arquivo) | Sim no Docker | Veja "Google Agenda" logo abaixo. |

Para gerar um valor aleatório:
- Windows (PowerShell): `-join ((48..57)+(65..90) | Get-Random -Count 32 | % {[char]$_})`
- Mac/Linux: `openssl rand -hex 16`

### Passo 2 — Criar o arquivo `.env`

Na pasta do projeto:
```bash
cp .env.example .env
```
Abra o `.env` num editor de texto (Bloco de Notas serve) e preencha. Cada linha do `.env.example` explica o que é e onde pegar. **Nunca** mande o `.env` por chat nem coloque no git.

### Passo 3 — Google Agenda (arquivo de credencial)

O bot de agendamento lê e grava na Google Agenda usando uma **conta de serviço** do Google (é gratuito).

> ⚠️ **No Docker o motor não sobe sem esse arquivo** (ele roda em modo produção e exige a credencial). Se você ainda não for usar agenda, crie a credencial mesmo assim; ela só não será usada.

1. Entre em https://console.cloud.google.com/ e crie um projeto (ex.: "agiliz").
2. Em **APIs e serviços → Biblioteca**, ative a **Google Calendar API**.
3. Em **IAM e administrador → Contas de serviço**, crie uma conta (ex.: "agiliz-bot"). Não precisa dar papel nenhum.
4. Abra a conta criada → aba **Chaves** → **Adicionar chave → Criar nova chave → JSON**. Um arquivo `.json` será baixado.
5. Na pasta do projeto, crie a pasta `configs` e salve o arquivo como **`configs/google_credentials.json`**.
6. Para cada agenda que o bot vai usar: no Google Agenda da clínica, **Configurações da agenda → Compartilhar com pessoas específicas** → adicione o e-mail da conta de serviço (está no JSON, campo `client_email`, termina em `iam.gserviceaccount.com`) com permissão **"Fazer alterações nos eventos"**. O **ID da agenda** (em "Integrar agenda") é o que você cola no painel, na aba Integrações do bot.

### Passo 4 — Subir tudo

```bash
docker compose up -d --build
```
A primeira vez demora alguns minutos (baixa imagens e compila). Para conferir:
```bash
docker compose ps
```
Todos devem aparecer como `running`/`healthy`. Depois abra:
- Painel: http://localhost:5001 (usuário e senha do `.env`)
- Motor: http://localhost:5000/health deve mostrar `"status":"ok"`

Se algo não subir, veja o log do serviço, por exemplo: `docker compose logs runtime --tail 50`.

### Passo 5 — Conectar um número de WhatsApp

Cada número de WhatsApp é uma **instância** na Evolution, e o nome da instância tem que ser **o próprio número** (só dígitos, com 55 e DDD, ex.: `5511999999999`). O mesmo número vai no bot, no painel.

Rode os dois comandos abaixo trocando `SUA_SENHA_EVOLUTION` (o valor de `AUTHENTICATION_API_KEY`) e o número. No Windows use o **Git Bash** (vem com o Git) para colar como está.

1. Criar a instância:
   ```bash
   curl -X POST http://localhost:8080/instance/create \
     -H "apikey: SUA_SENHA_EVOLUTION" -H "Content-Type: application/json" \
     -d '{"instanceName":"5511999999999","qrcode":true,"integration":"WHATSAPP-BAILEYS"}'
   ```
2. Mandar as mensagens desse número para o motor (webhook):
   ```bash
   curl -X POST http://localhost:8080/webhook/set/5511999999999 \
     -H "apikey: SUA_SENHA_EVOLUTION" -H "Content-Type: application/json" \
     -d '{"webhook":{"enabled":true,"url":"http://runtime:8080/webhook","byEvents":false,"events":["MESSAGES_UPSERT"]}}'
   ```
   A URL é `http://runtime:8080/webhook` mesmo: é o endereço do motor **dentro** do Docker. Esse webhook não precisa ficar público.
3. Crie o bot no painel com esse mesmo número (veja o [guia](docs/GUIA-DO-PAINEL.md)), vá em **Integrações do bot → WhatsApp → Gerar código de conexão** e leia o QR com o celular do número (WhatsApp → Aparelhos conectados → Conectar um aparelho).
4. Clique em **Testar conexão**. Deve aparecer "WhatsApp conectado".
5. Mande uma mensagem de **outro celular** para o número. O bot responde.

> *Não verificado:* a Evolution v2 costuma ter uma tela própria em http://localhost:8080/manager para criar instâncias sem `curl`. Se existir na versão do projeto, também serve (use a mesma senha).

### Parar, reiniciar e atualizar

```bash
docker compose stop              # para tudo (os dados ficam)
docker compose up -d             # sobe de novo
git pull && docker compose up -d --build   # atualizar para a versão nova do código
```
Os bots, planos e propostas ficam na pasta `configs/`; conversas, agenda e custos ficam no Postgres (volume do Docker). `docker compose down -v` **apaga o banco**: não use sem querer.

---

## 🌐 Opcional — deixar o painel acessível pela internet (grátis)

Serve para mostrar o painel e o **portal do cliente** para alguém de fora (ex.: uma clínica no celular), com o projeto rodando no seu PC. O PC precisa ficar ligado.

> ⚠️ Antes de expor, **troque `WIZARD_ADMIN_PASSWORD`** no `.env` e rode `docker compose up -d` de novo. Quem tiver o link vê a tela de login do painel.

Só a porta do painel (5001) precisa ser exposta. O webhook do WhatsApp já funciona dentro do Docker e **não** precisa de túnel.

**Opção A — ngrok** (precisa de conta gratuita, endereço fixo):
1. Crie a conta em https://dashboard.ngrok.com/signup e instale: https://ngrok.com/download
2. Conecte sua conta (o token aparece no painel do ngrok, em *Your Authtoken*):
   ```bash
   ngrok config add-authtoken SEU_TOKEN_NGROK
   ```
3. No painel do ngrok, em **Domains**, pegue seu domínio grátis (algo como `nome-aleatorio.ngrok-free.app`) e rode:
   ```bash
   ngrok http --url=nome-aleatorio.ngrok-free.app 5001
   ```
4. Acesse `https://nome-aleatorio.ngrok-free.app` (painel) e `https://nome-aleatorio.ngrok-free.app/portal` (portal). Na primeira visita o ngrok mostra um aviso; é só clicar em *Visit Site*.

**Opção B — Cloudflare Tunnel rápido** (sem conta, mas o endereço muda toda vez):
1. Instale o `cloudflared`: https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/downloads/
2. Rode:
   ```bash
   cloudflared tunnel --url http://localhost:5001
   ```
3. Ele mostra um endereço `https://...trycloudflare.com`. Use igual ao do ngrok.

Os dois dão HTTPS, então deixe `PORTAL_SECURE_COOKIES=true`.

---

## 🧑‍💻 Para desenvolvedores: rodar sem Docker

Precisa do **.NET 10 SDK**, do Postgres/Evolution do Docker (`docker compose up -d postgres redis evolution-api`) e das variáveis do `.env` exportadas no terminal.
```bash
cd Agiliz.Runtime && dotnet run     # motor
cd Agiliz.Wizard && dotnet run      # painel (o endereço aparece no terminal)
dotnet test                         # testes
```
Em `dotnet run` (modo Development) o motor sobe sem a credencial do Google e, sem `RUNTIME_ADMIN_TOKEN`, as rotas do painel ficam abertas.

---

## 🏗 Arquitetura

- **`Agiliz.Core`**: modelos, clientes de IA (Groq e Claude), catálogo de integrações, planos e quiz.
- **`Agiliz.Runtime`**: webhook da Evolution, `BotRunner` (IA + ferramentas de agenda), Postgres, Google Agenda, lembretes, transbordo para humano, limpeza da demonstração.
- **`Agiliz.Wizard`**: painel em Blazor Server e o portal do cliente (`/portal`, página estática + API).
- **`Agiliz.CLI`**: linha de comando para criar e testar bots sem subir o servidor.
- **`Agiliz.Tests`**: testes automatizados.

## 📚 Referência técnica

### Pasta `configs/` (não versionada)
```
configs/
  bots/{tenantId}.json      ← um arquivo por bot (criado pelo painel ou pelo CLI)
  google_credentials.json   ← credenciais do Google Calendar
  telemetry.json
```
Bots que ainda estiverem soltos na raiz (`configs/*.json`, layout antigo) são movidos para `configs/bots/` na primeira vez que o Runtime, o painel ou o CLI iniciam.

Os custos (LLM, ferramentas, transcrição) ficam na tabela `CostRecords` do Postgres, gravada pelo Runtime. O `configs/billing.json` antigo não é mais lido.

### Painel ↔ Runtime
O painel chama o Runtime para ativar bots sem reiniciar (`POST /api/admin/reload`), ler o financeiro (`GET /api/admin/billing`) e operar o handoff. Essas rotas exigem o header `X-Admin-Token` com o valor de `RUNTIME_ADMIN_TOKEN` (no `.env`, usado pelos dois containers). Em `dotnet run` local (Development) e sem token, as rotas ficam abertas.

### Integrações por cliente
O catálogo fica em `Agiliz.Core/Integrations/IntegrationCatalog.cs` (Google Agenda, áudio, e-mail, transbordo, lembretes; WhatsApp oficial sob consulta; Pix e sistemas de clínica em breve). No painel:
- **Integrações**: quem usa cada uma e quanto custou no mês.
- **Bot → Integrações**: ligar/desligar, campos (ex.: ID da agenda), passos do cliente, "Testar conexão" e preço mensal cobrado.

O agente só recebe as ferramentas das integrações ligadas. Bots criados antes do catálogo continuam como estavam até alguém salvar a aba. Cada custo é gravado com a integração que o gerou; o Financeiro mostra uso, custos fixos (`Billing:FixedMonthlyBrl:<id>`) e margem em R$ (`Billing:UsdToBrl`).

### Planos, franquia e quiz de venda
- **Planos** (`configs/plans/`): preço, franquia de contatos por mês, integrações incluídas e adicionais. Na primeira vez o painel cria três planos provisórios (Essencial, Profissional, Clínica) para editar.
- **Franquia**: o Runtime conta contatos diferentes por cliente no mês (tabela `ContactActivities`, fuso de Brasília). O painel avisa a 80% e 100% no Dashboard e no Financeiro; o atendimento nunca é bloqueado.
- **Quiz de venda**: 10 perguntas com regras fixas (sem IA) recomendam tipo de bot, plano, integrações, custo estimado e margem. Regras em `Agiliz.Core/Plans/Quiz.cs`.
- **Propostas** (`configs/proposals/`): quiz salvo para retomar e medir conversão. "Criar bot" leva as respostas ao meta-agente e, ao salvar, aplica plano, integrações e LGPD ao bot. Guardam nome e telefone do contato do lead.

### Portal do cliente e demonstração
- **Portal** (`/portal` no painel): página para o cliente acompanhar o bot no PC ou no celular — início com uso da franquia, conversas (assumir, responder, devolver ao bot), agenda do dia, pausar o bot, integrações do plano e conexão do número por QR code (a Evolution v2.1.1 não devolve o código de pareamento).
- **Acesso**: o admin cadastra os WhatsApp com acesso na aba Integrações do bot (card "Portal do cliente"). O cliente entra com um código de 6 dígitos enviado pelo WhatsApp; a sessão é um cookie HttpOnly de 30 dias. O portal é uma página estática + API própria, fora do Basic Auth e sem circuito Blazor, então não alcança as telas do admin.
- **Demonstração**: no Dashboard, "Criar bot de demonstração" cria o `clinica-demo` (Clínica Sorriso). O vendedor entra no portal com o próprio WhatsApp; os telefones dos leads aparecem mascarados e o Runtime limpa a demo toda madrugada (3h) ou pelo botão "Limpar demonstração". Passo a passo da demonstração no [guia](docs/GUIA-DO-PAINEL.md#demonstração-para-vendas).
- **Produção**: sirva o painel atrás de HTTPS (`PORTAL_SECURE_COOKIES=true`). As chaves da sessão ficam em `configs/keys`.

