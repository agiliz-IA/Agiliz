# Agiliz

Meta-agente de criação e operação de bots de atendimento via WhatsApp.

Built with **.NET 10 / C#**, [Groq](https://groq.com), [Anthropic Claude](https://anthropic.com) e [Evolution API](https://github.com/EvolutionAPI/evolution-api).

---

## Projetos

| Projeto | Descrição |
|---|---|
| `Agiliz.Core` | Modelos, clientes LLM (Groq e Claude), cliente Evolution API e leitor de configs |
| `Agiliz.CLI` | CLI interativo para criar, editar, listar e testar bots |
| `Agiliz.Runtime` | Servidor webhook ASP.NET Core que opera os bots em produção |

---

## Pré-requisitos

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Docker e Docker Compose](https://www.docker.com/products/docker-desktop)
- Conta no [Groq](https://console.groq.com) com API key
- Conta na [Anthropic](https://console.anthropic.com) com API key (produção)
- [ngrok](https://ngrok.com) para expor o webhook localmente (opcional em local)

---

## Configuração

1. Clone o repositório e copie o template de variáveis de ambiente:

```bash
git clone https://github.com/sua-conta/agiliz.git
cd agiliz
cp .env.example .env
```

2. Edite o `.env` com suas credenciais reais:

```
GROQ_API_KEY=gsk_...
ANTHROPIC_API_KEY=sk-ant-...
EVOLUTION_API_TOKEN=seu-token-aqui
```

3. Abra a pasta no VS Code — o plugin C# detecta o `Agiliz.sln` automaticamente.

---

## Criando um bot

```bash
# Inicia a entrevista com o meta-agente e gera o BotConfig
dotnet run --project Agiliz.CLI -- create <tenant-id>

# Testa o bot no terminal antes de subir (sem Evolution API)
dotnet run --project Agiliz.CLI -- test <tenant-id>

# Edita um bot existente
dotnet run --project Agiliz.CLI -- edit <tenant-id>

# Lista todos os bots configurados
dotnet run --project Agiliz.CLI -- list
```

---

## Subindo em produção

### Local (com Docker)

```bash
# Inicia Evolution API + Runtime + Wizard
docker-compose up -d

# Verifica logs
docker-compose logs -f runtime
```

### Produção (com ngrok)

```bash
# Terminal 1 — inicia Evolution API
docker-compose up evolution-api postgres

# Terminal 2 — inicia Runtime
dotnet run --project Agiliz.Runtime

# Terminal 3 — expõe para webhooks
ngrok http 5000
```

Verifique a saúde do servidor:

```
GET http://localhost:5000/health
```

Configure o webhook na Evolution API:

```
https://<sua-url-ngrok>/webhook   [HTTP POST]
```

---

## Estrutura

```
Agiliz/
├── Agiliz.sln
├── README.md
├── .gitignore
├── .env.example
├── .env                  ← credenciais reais (gitignored)
├── configs/              ← um .json por cliente (gitignored)
├── Agiliz.Core/
│   ├── Models/           BotConfig, ConversationMessage
│   ├── LLM/              ILlmClient, GroqClient, ClaudeClient, Factory
│   ├── Messaging/        IMessageProvider, EvolutionClient
│   └── Config/           BotConfigLoader
├── Agiliz.CLI/
│   ├── Agent/            MetaAgentSession
│   ├── Commands/         create, edit, list, test
│   └── UI/               ConsoleRenderer
└── Agiliz.Runtime/
    ├── Services/         TenantRegistry, SessionStore, BotRunner, SessionPurgeService
    └── Endpoints/        WhatsAppWebhook
```

---

## Fluxo de uma mensagem

```
Usuário (WhatsApp)
    ↓
Evolution API → POST /webhook (JSON)
    ↓
TenantRegistry.Resolve(remoteJid)   →  resolve o bot pelo número WhatsApp
    ↓
BotRunner.ProcessAsync()
    ├─ Flow match?  →  retorna resposta direta (sem LLM)
    └─ Sem match    →  chama LlmClient com histórico da sessão
    ↓
EvolutionClient.SendAsync()     →  envia resposta ao usuário
```

---

## Variáveis de ambiente

| Variável | Uso |
|---|---|
| `GROQ_API_KEY` | CLI (meta-agente) e bots com provider Groq |
| `ANTHROPIC_API_KEY` | Bots com provider Claude (produção) |
| `EVOLUTION_API_URL` | URL da Evolution API (padrão: http://localhost:8080/api) |
| `EVOLUTION_API_TOKEN` | Token de autenticação para Evolution API |

---

## Licença

MIT
