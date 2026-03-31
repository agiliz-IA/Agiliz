# 🚀 Agiliz 
**Solução modular para automação e orquestração de Agentes IA no WhatsApp.**

Agiliz é um ecossistema C# (.NET 10) projetado para criar, gerenciar e executar Múltiplos Bots Inteligentes através do WhatsApp, conectando provedores de LLMs (como Claude e Llama/Groq) à infraestrutura de comunicação (Evolution API).

---

## 🏗 Arquitetura do Projeto

O Agiliz foi desenhado separando preocupações em múltiplos projetos. Isso garante performance, coesão e independência para implantar apenas as partes necessárias em diferentes ambientes.

### 1. `Agiliz.Core` 🧠
**A Espinha Dorsal do Sistema.**
Esta biblioteca contém todos os domínios, interfaces, modelos de dados e clientes base do sistema.
- **`Models`**: Configurações de Bots (TenantId, Webhooks), Modelos de Estado de Conversação (`ConversationMessage`).
- **`LLM`**: Clientes polimórficos (`GroqClient`, `ClaudeClient`), Factory Pattern (`LlmClientFactory`) e o Sistema de **Function Calling** (`ToolCallExecutor`), permitindo que as IAs usem ferramentas estruturadas em tempo real.
- **`Tools`**: Ferramentas nativas (ex: `SendEmailTool` via SMTP).

### 2. `Agiliz.Runtime` ⚙️
**O Coração da Execução (Backend de Tráfego).**
Uma API focada puramente em receber Webhooks e transacionar mensagens. Feito para rodar em produção silenciosamente.
- Comunica-se diretamente com a **Evolution API v2**.
- Recebe mensagens por Post (`WhatsAppWebhook`), resgata o histórico do contato armazenado localmente (`ChatHistoryStore`) ou em banco, e dispara o `BotRunner` para acionar a Inferência da LLM pertinente ao tenant (loja/empresa).

### 3. `Agiliz.Wizard` 🧙‍♂️
**O Painel de Controle (Admin Control Panel).**
Aplicação Frontend **Blazor Server** construída através do *Atomic Design*.
- Interface rica com *Dashboards*, *Editor de Bots*, *Simulador de Conversas* (TestPage), *Históricos de Telemetria*, e envio manual de E-mails (EmailJS).
- Utiliza um poderoso fluxo de "Entrevista" (`WizardSessionStore`): um meta-agente conversa com você para configurar um novo bot automaticamente via JSON Extraction.
- Mantém isolamento CSS limpo (`.razor.css`) e UI Modular com componentes puros (`Atoms` e `Organisms`).

### 4. `Agiliz.CLI` 💻
**A Ferramenta de Linha de Comando.**
Utilitário de linha de comando para simular conversas rapidamente sem levantar o servidor completo.
- Útil para debugar Prompts de sistema via prompt interativo no próprio terminal!

---

## 🛠 Features e Funcionalidades Modernas

- 📡 **Multi-Tenant Nativo:** A arquitetura aceita `N` lojas funcionando em um único servidor, separando credenciais pelo número do WhatsApp ou Token de Instância no webhook.
- ⚡ **Function Calling Engine:** A engine de I.A consegue discernir quando deve "Falar" com o usuário e quando deve "Executar uma Ação" autônoma no sistema (como invocar a `SendEmailTool` para validar uma compra).
- 🧩 **UI em Atomic Design:** O Painel Wizard reaproveita perfeitamente componentes modulares com layouts unificados, garantindo a solidez e clareza do design por todo o sistema.
- 📦 **Injeção de Dependências Dinâmicas:** Todo cliente, de APIs ao Cache de sessões, funciona sem overhead global.

---

## ⚙️ Setup e Instalação

### Pré-Requisitos
1. **.NET 10 SDK** instalado.
2. Contas provisionadas nos LLMs configurados (**Groq** ou **Anthropic/Claude**).
3. (Opcional) **Evolution API** rodando via *Docker* para habilitar os testes do Webhook.

### 1. Configurando Variáveis de Ambiente (`.env`)
No raiz do projeto global, faça uma cópia de `.env.example` para `.env`:
```bash
cp .env.example .env
```
Abra `.env` e defina suas chaves de API:
```env
# Provedores de LLM
GROQ_API_KEY="gsk_SuaChaveAqui"
CLAUDE_API_KEY="sk-ant-SuaChaveAqui"

# Evolution API
EVOLUTION_BASE_URL="http://localhost:8080"
EVOLUTION_GLOBAL_API_KEY="admin_apikey"

# Credenciais Email (opcional, para a Tool)
SMTP_HOST="smtp.hostinger.com"
SMTP_PORT="465"
SMTP_USER="seu-email@dominio.com"
SMTP_PASS="suaSenha"
```

### 2. Rodando o Painel de Controle (Wizard)
Siga os guias de configuração do seu primeiro Bot diretamente do seu navegador:
```bash
cd Agiliz.Wizard
dotnet run
```
Acesse `http://localhost:62853`!

### 3. Rodando o Motor de Produção (Runtime) 
Uma vez configurado e testado seus bots, libere o Runtime para acoplar no Webhook da Evolution API:
```bash
cd Agiliz.Runtime
dotnet run
```

---

_Construído com obsessão em performance e modularização pela equipe Agiliz._ 💜
