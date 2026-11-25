# Guia do painel e do portal

Este guia explica, sem termos técnicos, o que cada tela do Agiliz faz e quando usar. Para instalar e subir o projeto, veja o [README](../README.md).

- **Painel (admin):** http://localhost:5001 — uso interno da Agiliz (criar bots, vender, acompanhar custos).
- **Portal do cliente:** http://localhost:5001/portal — o que o dono da clínica usa.

> Os itens marcados com *(não verificado)* foram descritos a partir do código, sem abrir a tela funcionando.

---

## O fluxo típico de um cliente novo

1. **Quiz de venda** com o cliente, na reunião → gera uma **Proposta** com plano e preço.
2. Na proposta, **Criar bot** → a entrevista com a IA monta o bot.
3. **Testar** o bot no simulador.
4. **Integrações do bot**: plano, Google Agenda, conectar o WhatsApp, liberar o portal.
5. O cliente entra no **Portal** e acompanha tudo pelo celular.
6. No dia a dia: **Atendimentos** (quando o bot passa para humano) e **Financeiro** (custo × preço).

---

## Painel (admin)

O navegador pede usuário e senha: são `WIZARD_ADMIN_USER` e `WIZARD_ADMIN_PASSWORD` do `.env`.

### Dashboard (bots ativos)
A tela inicial. Lista todos os bots com o número de WhatsApp e a IA que usam, e avisa quando um cliente passou de 80% ou 100% da franquia do mês (o atendimento **nunca** é bloqueado, é só um aviso para cobrar o excedente ou subir de plano).

Em cada bot:
- **▶ Testar**: abre o simulador.
- **Editar**: refaz a entrevista com a IA para mudar o comportamento.
- **Integrações**: configurações do cliente (a tela mais importante, veja abaixo).
- **Deletar**: apaga o bot e o motor para de responder aquele número na hora. Não tem como desfazer.

Botões do topo: **Novo bot** e **Criar bot de demonstração** (veja [Demonstração](#demonstração-para-vendas)).

### Novo bot (entrevista com a IA)
Em vez de escrever configurações, você conversa com uma IA que monta o bot. São três passos:
1. **Identificação**: um nome curto e único (ex.: `clinica-santos`) e o número de WhatsApp do bot, só dígitos com 55 e DDD.
2. **Entrevista**: conte como é o negócio (horários, serviços, preços, tom de voz, o que o bot não pode fazer). A IA pergunta o que faltar.
3. **Revisão**: confira as respostas automáticas e a IA escolhida, e clique em **Salvar Bot e Lançar**. O bot já começa a atender, sem reiniciar nada.

Bots de **agendamento** usam sempre o Groq (Claude só vale para bots genéricos).

### Testar (simulador)
Converse com o bot como se fosse um paciente, **sem** usar o WhatsApp de ninguém. Use antes de conectar o número: tente perguntas difíceis, pedidos de remarcação, pedido para falar com humano, e veja se o bot se comporta bem. Se não gostar, volte em **Editar**.

### Integrações do bot
Abre pelo botão **Integrações** do bot. É onde se liga o que o cliente contratou. A mudança vale na próxima mensagem do cliente.

- **Plano** e **Preço negociado (R$/mês)**: qual plano o cliente comprou e, se teve desconto, o preço real. Usado no Financeiro para calcular a margem.
- **Portal do cliente**:
  - *Nome no portal*: como o bot aparece para o cliente.
  - *WhatsApp com acesso*: os números que podem entrar no portal (um por linha). Tirar um número corta o acesso na hora.
  - *Responder só a estes números*: deixe **vazio** para atender todo mundo. Preencha só quando o bot estiver num número pessoal (ex.: demonstração), para ele não responder seus contatos.
  - **Abrir portal ↗**: abre o portal para conferir.
- **Cada integração** tem um interruptor, campos, os "passos do cliente" (o que pedir para ele), **Testar conexão** e o preço mensal cobrado. As principais:

| Integração | Para que serve | O que configurar |
|---|---|---|
| Atendimento com IA | O bot em si. Sempre ligada. | — |
| WhatsApp (não oficial) | O número do bot, via Evolution. | **Gerar código de conexão** prepara o número na Evolution sozinho e mostra um QR: leia no celular do número (WhatsApp → Aparelhos conectados → Conectar um aparelho). O QR muda a cada ~40 s. Depois, **Testar conexão**. |
| Google Agenda | O bot consulta horários livres, marca, remarca e cancela. | O **ID da agenda** da clínica, compartilhada com o e-mail da conta de serviço (ver README, passo 3). |
| Lembretes na véspera | O bot manda lembrete da consulta no dia anterior. | Precisa da Google Agenda. |
| Transcrição de áudio | O bot entende áudios do paciente. | Nada além da chave do Groq. |
| Envio de e-mail | O bot manda e-mails (ex.: confirmação). | Os `SMTP_*` do `.env`. Sem eles os e-mails só aparecem no log. |
| Transbordo para humano | O bot pausa e chama alguém da clínica quando não sabe responder ou o paciente pede. | Aparece em **Atendimentos** e no portal. |
| WhatsApp oficial, Pix, Sistemas de clínica | Sob consulta / em breve. | Ainda não funcionam. |

### Atendimentos
Lista as conversas em que o bot **pausou e passou para humano**. Daqui a equipe Agiliz pode responder pelo número do bot ou devolver a conversa ao bot. O cliente faz o mesmo pelo portal.

### Integrações (menu lateral)
Visão geral: quais clientes usam cada integração e quanto ela custou no mês. Bom para saber o que mais vende e o que mais pesa no custo.

### Planos
Os pacotes que a Agiliz vende: preço mensal, **franquia** (quantos contatos diferentes por mês), preço por contato excedente, máximo de profissionais, e quais integrações vêm incluídas ou são vendidas à parte. Na primeira vez aparecem três planos provisórios (Essencial, Profissional, Clínica) para ajustar. Só planos marcados como **Ativo** aparecem no quiz.

### Quiz de venda
Dez perguntas para responder **junto com o cliente** na reunião. Ele recomenda tipo de bot, plano, integrações, custo estimado por mês e margem, usando regras fixas (sem IA, então o resultado é previsível). No fim, **Salvar proposta**.

### Propostas
Os quizzes salvos. Daqui você retoma uma conversa de venda, marca como perdida, ou clica em **Criar bot**: a entrevista já começa com as respostas do quiz e, ao salvar, o bot já vem com plano, integrações, LGPD e acesso ao portal para o contato da proposta.

### Financeiro
Quanto cada cliente custa (IA, transcrição, ferramentas e custos fixos) em R$, comparado com o preço que ele paga, e o uso da franquia. Filtre por um mês para ver a margem (preço e custos fixos são mensais). Se o painel não conseguir falar com o motor, a margem não aparece.

### Telemetria e Disparar e-mail
Telas antigas de teste de e-mail: a Telemetria lista os e-mails enviados pelos bots e o Disparar e-mail envia um manualmente. Pouco usadas hoje.

---

## Portal do cliente

Endereço: `/portal` (ex.: http://localhost:5001/portal ou o endereço do ngrok + `/portal`). Funciona no PC e no celular.

**Login:** o cliente digita o WhatsApp dele, recebe um **código de 6 dígitos** no WhatsApp e digita no portal. Fica logado por 30 dias. Só entra quem está em *WhatsApp com acesso* nas Integrações do bot. O código sai pelo número do bot; se ele ainda não estiver conectado, sai pelo `PORTAL_SENDER_NUMBER` do `.env`.

O portal **não** dá acesso ao painel admin nem aos custos internos.

- **Início**: contatos atendidos no mês × franquia do plano, conversas aguardando atendente, consultas de hoje e o botão **Pausar o bot** (as mensagens continuam chegando, mas o bot não responde até **Voltar a atender**).
- **Conversas**: as conversas recentes. Em cada uma: **Assumir conversa** (o bot para de responder aquele paciente e o cliente responde pelo portal), e **Devolver ao bot**.
- **Agenda**: as consultas do dia, com setas para outros dias.
- **Integrações**: liga e desliga só o que está no plano dele, e **Conectar o WhatsApp** (gera o QR para ler no celular do número do bot).

---

## Demonstração para vendas

Serve para mostrar o bot funcionando numa reunião, com uma clínica fictícia ("Clínica Sorriso").

1. Tenha um número (chip) para a demonstração.
2. No Dashboard, **Criar bot de demonstração**: informe o número do chip e os WhatsApp dos vendedores (separados por vírgula).
3. O painel abre as Integrações do `clinica-demo`. Conecte o WhatsApp pelo QR e informe o **ID de uma Google Agenda de exemplo** (crie uma agenda só para isso e compartilhe com a conta de serviço).
4. Na reunião, o lead manda mensagem para o chip e marca uma consulta. O vendedor mostra tudo acontecendo no **portal**, entrando com o próprio WhatsApp. Os telefones dos leads aparecem mascarados.
5. Tudo da demo (conversas, contatos, consultas e eventos na agenda) é apagado toda madrugada às 3h, ou na hora pelo botão **Limpar demonstração** no portal.

Se o chip for um número pessoal, preencha *Responder só a estes números* para o bot não responder seus contatos.

---

## Problemas comuns

| Sintoma | Causa provável | O que fazer |
|---|---|---|
| `agiliz-runtime` reinicia sem parar | `AUTHENTICATION_API_KEY` vazio ou começando com `change-me`. | `docker compose logs runtime --tail 50` mostra o motivo. Corrija e `docker compose up -d`. |
| Bot não responde no WhatsApp | WhatsApp desconectado ou chave do Groq errada. | Integrações do bot → **Testar conexão** em WhatsApp e IA. Se o WhatsApp caiu, **Gerar código de conexão** de novo (isso também religa o webhook). |
| Bot não marca na agenda | Falta `configs/google_credentials.json` ou a agenda não foi compartilhada com a conta de serviço. | Integrações do bot → Google Agenda → **Testar conexão** diz qual. |
| Atendimentos/Financeiro vazios ou com erro | `RUNTIME_ADMIN_TOKEN` vazio. | Preencha e `docker compose up -d`. |
| Código do portal não chega | Número do bot não conectado e `PORTAL_SENDER_NUMBER` vazio. | Conecte o número ou preencha o reserva. |
| Login do portal volta para a tela de código | Cookie seguro em http sem HTTPS *(não verificado)*. | Use o endereço do ngrok, ou `PORTAL_SECURE_COOKIES=false` só para teste local. |
