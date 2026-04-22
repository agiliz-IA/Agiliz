# Agiliz.Runtime - Integração com WhatsApp (Evolution API)

Este documento detalha o passo a passo para configurar e testar o envio e recebimento de mensagens no WhatsApp real, conectando a engine do Agiliz ao [Evolution API](https://evolution-api.com/).

O **Evolution API** é um serviço open-source (já incluído no `docker-compose.yml` deste projeto) que gerencia instâncias do WhatsApp via Web, convertendo as mensagens em requisições REST/JSON e Webhooks.

---

## 1. Subindo a infraestrutura

Certifique-se de que todos os serviços estão rodando via Docker:
```bash
docker-compose up -d
```

Neste momento, os seguintes serviços relevantes estarão ativos:
- **Evolution API**: `http://localhost:8080`
- **Agiliz.Runtime**: `http://localhost:5000` (porta mapeada no host, internamente é 8080)
- **Agiliz.Wizard**: `http://localhost:5001`

---

## 2. Autenticação na API do Evolution (Global API Key)

Toda requisição feita para o Evolution API precisa de uma chave de autenticação (API Token). 
Essa chave é definida no arquivo `.env` da raiz do projeto através da variável `EVOLUTION_API_TOKEN` e é repassada tanto para o contêiner do Evolution quanto para o Agiliz.Runtime.

Nas requisições abaixo (via Postman, Insomnia ou cURL), lembre-se de enviar o header:
`apikey: <SEU_EVOLUTION_API_TOKEN>`

Se você não tiver alterado, o valor padrão no `docker-compose.yml` é `change-me-default-token`.

---

## 3. Criando uma Instância (Conectando o WhatsApp)

Uma "Instância" no Evolution API equivale a um número de WhatsApp. 
O nome da instância deve preferencialmente ser o próprio número do WhatsApp (ex: `5511999999999`) para facilitar a identificação.

Faça uma requisição `POST` para o Evolution API:
- **URL**: `http://localhost:8080/instance/create`
- **Headers**:
  - `apikey: change-me-default-token`
  - `Content-Type: application/json`
- **Body** (JSON):
```json
{
  "instanceName": "5511999999999",
  "qrcode": true,
  "integration": "WHATSAPP-BAILEYS"
}
```

O retorno incluirá uma string em `base64` contendo a imagem do QR Code. 
Copie essa string base64, cole no navegador ou em um conversor de base64 para imagem e **leia o QR Code com o WhatsApp** (Aparelhos Conectados).

---

## 4. Configurando o Webhook

Agora que o número está conectado, você precisa avisar ao Evolution API para enviar as mensagens recebidas para o nosso **Agiliz.Runtime**.

Faça uma requisição `POST`:
- **URL**: `http://localhost:8080/webhook/set/5511999999999`
- **Headers**: `apikey: change-me-default-token`
- **Body** (JSON):
```json
{
  "webhook": {
    "url": "http://runtime:8080/webhook",
    "byEvents": false,
    "events": [
      "MESSAGES_UPSERT"
    ]
  }
}
```

> **Atenção:** Note que a URL é `http://runtime:8080/webhook`. Usamos `runtime` porque este é o nome do serviço do Agiliz.Runtime dentro da rede interna do Docker (`docker-compose.yml`), e `8080` é a porta interna dele.

---

## 5. Criando o Bot no Agiliz Wizard

Para que o **Agiliz.Runtime** saiba como responder a esse número específico, ele precisa ter um bot cadastrado com esse exato número.

1. Acesse o Wizard: `http://localhost:5001/create`
2. No passo de Identificação, preencha:
   - **Tenant ID**: O nome do seu bot (ex: `bot-vendas`)
   - **Número WhatsApp**: O mesmo número conectado na instância do Evolution (ex: `5511999999999`). *É fundamental que o número seja idêntico para que o roteamento funcione*.
3. Finalize a entrevista com o Meta-Agente e salve o bot.

---

## 6. Testando na prática!

Envie uma mensagem de um **outro celular** para o número de WhatsApp que você acabou de conectar na instância do Evolution.

**O fluxo que ocorrerá será:**
1. O usuário envia "Olá" pelo WhatsApp.
2. O Evolution API recebe a mensagem e dispara um `POST` no Webhook do Runtime (`http://runtime:8080/webhook`).
3. O `WhatsAppWebhook.cs` captura a mensagem e extrai o número de destino.
4. O `TenantRegistry.cs` localiza qual bot foi salvo com esse número.
5. O `BotRunner.cs` avalia o contexto, consulta o LLM (Groq/Claude) ou responde via fluxo estático.
6. A resposta é enviada de volta para o Evolution API via `EvolutionClient.cs`, que despacha a mensagem real pro WhatsApp do usuário final.

---

## Resolução de Problemas (Troubleshooting)

- **O Webhook não está sendo ativado:** Verifique nos logs do Evolution se o evento `MESSAGES_UPSERT` está ativado e se a URL do webhook não possui erros de digitação.
- **O bot recebe a mensagem, mas não responde:** Pode ser um erro na requisição do Agiliz para o Evolution na hora da resposta. Verifique os logs do Runtime com o comando: `docker logs agiliz-runtime -f`
- **Token inválido:** Verifique se o `EVOLUTION_API_TOKEN` no `.env` do Runtime é exatamente o mesmo configurado e usado na API Global do Evolution.
