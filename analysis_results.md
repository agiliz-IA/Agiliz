# Análise de Produto e Infraestrutura: Agiliz

Considerando a sua persona inicial (**Clínicas e Estabelecimentos de Pequeno e Médio Porte**), a arquitetura atual do Agiliz já possui diferenciais excelentes: o uso de um Meta-Agente para onboarding (reduz fricção comercial), controle de custos (Guardrails de tokens) e infraestrutura própria de mensageria (Evolution API).

No entanto, para o produto ser comercialmente **competitivo** (bater de frente com soluções de prateleira baseadas em Typebot/Dialogflow), algumas peças-chave da operação de uma clínica ainda precisam ser desenvolvidas.

Aqui estão os 5 grandes pilares faltantes, ordenados por prioridade estratégica:

---

## 1. Integração Real de Calendário (Google Calendar / Outlook)
**Status Atual:** A ferramenta `VerificarAgendaTool` está com um retorno de horários mockados (falsos) e o sistema salva apenas no PostgreSQL local.
**O Problema:** Clínicas têm secretárias que marcam consultas manualmente pelo telefone. Se o bot não ler a agenda real, haverá *overbooking* (duas pessoas no mesmo horário).
**O que precisa ser feito:**
- Implementar integração via **Google Calendar API** (OAuth2).
- A `VerificarAgendaTool` deve consultar slots livres reais no calendário da clínica.
- A `MarcarAgendaTool` deve criar o evento no Google Calendar e inserir o link da videochamada (se for telemedicina) ou o endereço físico.

## 2. Transbordo Humano (Human Handoff)
**Status Atual:** O bot tenta responder tudo. Se estourar os *guardrails*, ele envia uma mensagem de erro genérica.
**O Problema:** Em ambientes de saúde, pacientes frequentemente têm dúvidas complexas (ex: "Esse plano cobre anestesia local?"). Se o bot travar, o paciente desiste. A secretária precisa conseguir intervir no WhatsApp.
**O que precisa ser feito:**
- Criar um comando/ferramenta de *Pause Bot*, parando a execução do `BotRunner` para aquele número.
- Ter uma aba simples (pode ser dentro do Dashboard do Blazor) onde a secretária vê as mensagens chegando e pode assumir o controle da conversa através da Evolution API.

## 3. Disparos Ativos e Lembretes (Proactive Messaging)
**Status Atual:** O sistema é puramente reativo. Ele apenas responde quando o cliente manda mensagem.
**O Problema:** Clínicas sofrem com uma taxa enorme de *No-Shows* (faltas). Sistemas competitivos mandam mensagens 24 horas antes confirmando a consulta.
**O que precisa ser feito:**
- Implementar um *Background Service* (ex: usando Hangfire ou Quartz.NET no Runtime).
- Esse serviço varre o banco PostgreSQL todos os dias de manhã, encontra as consultas do dia seguinte e usa a Evolution API para enviar: *"Olá [Nome], confirmando sua consulta amanhã às 14h. Responda 1 para Confirmar e 2 para Remarcar."*

## 4. Tratamento de Cancelamento e Reagendamento
**Status Atual:** O LLM sabe que deve agendar e as Tools de segurança (Anti-Fraude) bloqueiam excessos, mas ainda não programamos as ferramentas inversas.
**O Problema:** O usuário não consegue desmarcar pelo WhatsApp de forma autônoma sem depender do atendente.
**O que precisa ser feito:**
- Criar as tools: `DesmarcarAgendaTool` (que libera o slot no Google Calendar e atualiza o PostgreSQL) e `ReagendarAgendaTool`.

## 5. Fuso Horário e Horário Comercial (Business Rules)
**Status Atual:** A validação de Data/Hora nas ferramentas usa `DateTimeOffset` cru.
**O Problema:** O LLM muitas vezes não sabe que dia é hoje ou em qual fuso horário está. Além disso, não deve permitir agendamentos às 03:00 da manhã.
**O que precisa ser feito:**
- Injetar a data e hora atual do servidor (no fuso horário de Brasília `America/Sao_Paulo`) no `System Prompt` base do BotRunner para ancorar o LLM no tempo real.
- As Tools devem restringir os agendamentos respeitando o horário de funcionamento daquela clínica (informação que deverá ser extraída no Wizard e salva no `BotConfig.cs`).

---

### Conclusão e Recomendação
A infraestrutura backend (CLI, EF Core, Runtime, LLM genérico) está sólida. **O próximo passo natural e mais urgente é a Integração com o Google Calendar (Pilar 1)**, pois sem ela o produto não pode ser colocado em produção nem para testes com clínicas reais, dado o risco de conflito de horários. Em seguida, o **Transbordo Humano (Pilar 2)** garantiria que os testes com os primeiros clientes pudessem ser supervisionados e corrigidos manualmente pela secretária.
