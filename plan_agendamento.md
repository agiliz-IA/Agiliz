# Implementação do Runtime (Fase 2): Banco de Dados, Anti-Fraude e Agendamento

## Resumo
Este plano aborda a implementação da integração do **Entity Framework Core (EF Core)** no `Agiliz.Runtime`, a criação das entidades para persistir o estado do usuário/agendamentos, e o middleware de Anti-Fraude que será acionado através das Tools.

---

## User Review Required

> [!IMPORTANT]
> **Sobre o Entity Framework e Migrations:**
> 
> Usaremos o EF Core (`Npgsql.EntityFrameworkCore.PostgreSQL`). 
> Como o sistema lida com vários bots (`TenantId`), a abordagem mais comum e barata é termos **1 único Banco de Dados (Shared Database)** e filtrarmos as tabelas usando a coluna `TenantId`. Nesse caso, a migration atualiza o banco de dados inteiro para todos os bots de uma vez.
> 
> Se a intenção for **1 Banco de Dados por Bot** (ex: criar um banco novo para cada cliente isoladamente), a connection string precisará ser gerada dinamicamente no Runtime/CLI de acordo com o `TenantId`.
> 
> **Decisão a confirmar:** Assumirei o modelo de **Banco Único com coluna TenantId** por ser o mais simples de dar manutenção via Entity Framework. Criarei um comando na CLI (`agiliz db migrate`) que aplicará as migrations globais. Você concorda com essa modelagem de banco único com tenantId?

---

## Proposed Changes

### 1. Pacotes e Contexto de Banco (Entity Framework)

**Pacotes no `Agiliz.Runtime`:**
- `Microsoft.EntityFrameworkCore.Design`
- `Npgsql.EntityFrameworkCore.PostgreSQL`

#### [NEW] [AgilizDbContext.cs](file:///c:/Users/Jo%C3%A3o%20Pedro/Documents/Agiliz/Agiliz.Runtime/Data/AgilizDbContext.cs)
- Configuração do DbContext do Entity Framework.
- **Entidades:**
  - `SchedulingUser`: `Phone` (PK), `TenantId` (PK), `Name`, `PaymentMethod`, `LgpdConsentDate`.
  - `Appointment`: `Id` (PK), `Phone`, `TenantId`, `ScheduledTime`, `Status` (Pending, Confirmed, Cancelled, NoShow).

---

### 2. Comando de Migration na CLI (`Agiliz.CLI`)

#### [MODIFY] [Program.cs (Agiliz.CLI)](file:///c:/Users/Jo%C3%A3o%20Pedro/Documents/Agiliz/Agiliz.CLI/Program.cs)
- Adição do comando `agiliz db migrate`.

#### [NEW] [DbMigrateCommand.cs](file:///c:/Users/Jo%C3%A3o%20Pedro/Documents/Agiliz/Agiliz.CLI/Commands/DbMigrateCommand.cs)
- Comando responsável por invocar `context.Database.MigrateAsync()` programaticamente para aplicar as migrations pendentes do EF no banco de dados.

---

### 3. Middleware / Serviço de Anti-Fraude

#### [NEW] [AntiFraudService.cs](file:///c:/Users/Jo%C3%A3o%20Pedro/Documents/Agiliz/Agiliz.Runtime/Services/AntiFraudService.cs)
- Recebe o `AgilizDbContext` via Injeção de Dependência.
- Implementa `Task<bool> CheckEligibilityAsync(string phone, string tenantId)`.
- **Regras Executadas no EF Core:**
  1. `db.Appointments.Count(a => a.Phone == phone && a.Status == Pending)` -> Se >= 2, bloqueia.
  2. `db.Appointments.Count(a => a.Phone == phone && a.Status == NoShow)` -> Se >= 3, bloqueia permanentemente.

---

### 4. Integrações de Agendamento (Tools)

#### [NEW] [VerificarAgendaTool.cs](file:///c:/Users/Jo%C3%A3o%20Pedro/Documents/Agiliz/Agiliz.Runtime/Tools/VerificarAgendaTool.cs)
- Tool exposta ao LLM que retorna horários disponíveis (Mocked).

#### [NEW] [MarcarAgendaTool.cs](file:///c:/Users/Jo%C3%A3o%20Pedro/Documents/Agiliz/Agiliz.Runtime/Tools/MarcarAgendaTool.cs)
- Tool que recebe: `Nome`, `DataHora`, `FormaPagamento`.
- **Fluxo Interno:**
  1. Chama `AntiFraudService.CheckEligibilityAsync()`. Se bloqueado, retorna mensagem de negação ao LLM.
  2. Insere/Atualiza `SchedulingUser` via Entity Framework.
  3. Insere `Appointment` via Entity Framework.
  4. Dá `await db.SaveChangesAsync()`.
  5. Retorna sucesso ao LLM.

#### [MODIFY] [Program.cs e BotRunner.cs (Agiliz.Runtime)](file:///c:/Users/Jo%C3%A3o%20Pedro/Documents/Agiliz/Agiliz.Runtime/Program.cs)
- Adicionar `services.AddDbContext<AgilizDbContext>(...)` no DI.
- Injetar o `AntiFraudService` e as novas `Tools`.
- Configurar o LLM no `BotRunner.cs` para apenas passar as `Tools` de agendamento caso a configuração (`BotType`) do tenant seja `Scheduling`.

---

## Verification Plan
1. Rodar `agiliz db migrate` na CLI para garantir que as tabelas sejam criadas no Postgres.
2. Iniciar o `Agiliz.Runtime` e fazer um teste de endpoint de webhook.
3. Testar os cenários de fraude forçando 3 "NoShows" direto pelo banco de dados para ver se a Tool barra o agendamento no LLM.
