# Implementação: Integração com Google Calendar

Neste plano, focaremos no Pilar 1: Conectar os bots de agendamento ao Google Agenda real de cada clínica/profissional, permitindo a sincronização em tempo real das consultas.

## 1. Abordagem: Service Account vs OAuth2

Existem duas formas de acessar a API do Google:
1. **OAuth2 (Tela de Consentimento):** O usuário clica em "Logar com o Google" e nós guardamos um token de acesso dele. Mais complexo de implementar (requer fluxos de redirecionamento, validação do app pelo Google para não dar tela de "App não seguro").
2. **Service Account (Conta de Serviço):** Nós criamos um "e-mail de robô" invisível no Google Cloud. Tudo o que o seu cliente (a clínica) precisa fazer é entrar na agenda pessoal dele, clicar em "Compartilhar Agenda" e dar permissão de escrita para esse e-mail de robô.
   - **Vantagem:** É infinitamente mais fácil, rápido para criar um MVP, e não exige aprovação burocrática do Google App!

> [!IMPORTANT]
> **Decisão Proposta:** Vamos usar **Service Account**. Como você quer uma solução robusta e voltada para empresas (B2B), você, como dono do Agiliz, manterá a chave mestra (Service Account) e orientará seus clientes a apenas darem permissão para o e-mail do bot. Você está de acordo?

---

## 2. Passo a Passo: Como Obter suas Credenciais do Google Cloud (Tutorial)

Como solicitado, aqui está o guia de como criaremos a nossa Service Account:

1. Acesse o [Google Cloud Console](https://console.cloud.google.com/).
2. Crie um **Novo Projeto** (ex: `Agiliz-Scheduling`).
3. Vá em "APIs e Serviços" > "Biblioteca" e pesquise por **Google Calendar API**. Clique em Ativar.
4. Vá em "APIs e Serviços" > "Credenciais".
5. Clique em **Criar Credenciais** > **Conta de Serviço (Service Account)**.
6. Dê um nome (ex: `bot-agendamento`). O Google vai gerar um e-mail longo para você (ex: `bot-agendamento@agiliz-scheduling.iam.gserviceaccount.com`). *Atenção: É para esse e-mail que as clínicas deverão compartilhar suas agendas.*
7. Clique na Service Account criada > Aba **Chaves (Keys)** > **Adicionar Chave** > **Criar Nova Chave** > Formato **JSON**.
8. O download de um arquivo `.json` será feito. Esse arquivo é sua "Chave Mestra". Iremos salvá-lo na pasta local `configs` do Agiliz com o nome `google_credentials.json`.

---

## 3. Proposed Changes (Arquitetura do Código)

### Atualização do Wizard (Meta-Agente)
- **`BotConfig.cs`:** Adicionar o campo `GoogleCalendarId` (que normalmente é o e-mail da clínica associado àquela agenda).
- **`WizardSessionStore.cs`:** Modificar o `SchedulingMetaSystemPrompt`.
  - O meta-agente perguntará: *"Qual é o e-mail associado à sua agenda do Google?"*
  - O meta-agente instruirá: *"Para que eu possa marcar horários, lembre-se de ir nas configurações da sua agenda do Google e compartilhá-la com o e-mail do nosso bot: [EMAIL-DA-SERVICE-ACCOUNT], dando permissão de edição."*

### Atualização do Runtime (Ferramentas de Integração)
- Instalar bibliotecas `Google.Apis.Calendar.v3` no `Agiliz.Runtime`.
- **`GoogleCalendarService.cs`:** Novo serviço injetado no Runtime que usará o arquivo `google_credentials.json` para autenticar o bot. Terá os métodos:
  - `GetAvailableSlotsAsync(string calendarId, DateTime date)`
  - `CreateEventAsync(string calendarId, Appointment appointment)`
- **`VerificarAgendaTool.cs`:** Trocar os horários mockados por uma chamada ao `GoogleCalendarService`.
- **`MarcarAgendaTool.cs`:** Após passar pelo `AntiFraudService` e gravar no nosso Postgres, chamará o `GoogleCalendarService` para inserir o evento oficial no Google Agenda da clínica.

---

## 4. User Review Required

> [!WARNING]
> Leia o tutorial do passo a passo e gere a sua Service Account no Google Cloud. 
> Salve o arquivo JSON na pasta do seu projeto em `configs/google_credentials.json`.
> Quando tiver feito isso, ou se estiver apenas de acordo com o plano para eu escrever os códigos C#, me dê a confirmação!
