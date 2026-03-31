# 🧙‍♂️ Agiliz Wizard (Admin Control Panel)

O `Agiliz.Wizard` é o centro de comando visual do ecossistema Agiliz. Ele não é apenas um painel de gerenciamento; é a ferramenta estratégica onde você e sua equipe criam, orquestram e acompanham o desempenho financeiro e comportamental de toda a sua frota de agentes IA.

Construído em **Blazor Server** utilizando a filosofia de design **Atomic Design** (componentes estritos, limpos e isolados), o Wizard oferece uma interface rica, responsiva e focada na experiência corporativa.

---

## 🧭 Tour pelas Funcionalidades (O que tem no Dashboard?)

### 1. 📊 Dashboard (Visão Geral)
A porta de entrada do painel. Aqui a equipe tem uma visão clara dos bots já provisionados no sistema (lidos diretamente do diretório `/configs`).
- **Casos de Uso**: Verificação rápida de saúde. Saber instantaneamente quantos tenantes (clientes) estão com bots ativos no momento e gerenciar o estado da aplicação. 

### 2. ✨ Novo Bot (Entrevista com o Meta-Agente)
Em vez de programar JSONs e configurações complexas na mão, você *conversa* com a Inteligência Artificial para gerar outra I.A.! O Meta-Agente do Wizard conduz uma entrevista de negócios para extrair os detalhes do cliente, tom de voz, e fluxos determinísticos, montando o "cérebro" do bot automaticamente.
- **Casos de Uso**: Onboarding de novos clientes da sua agência em minutos. O gestor só precisa detalhar "O cliente é uma pizzaria aberta até as 22h, eles vendem pizza de calabresa..." e o sistema se encarrega de formalizar o Prompt ideal.

### 3. 🧪 Simulador de Contexto (Sandbox)
O simulador permite debugar a personalidade do bot e a invocação de ferramentas (Function Calling) em tempo real, **antes** de conectar o bot ao WhatsApp do cliente em produção.
- **Casos de Uso**: Garantia de Qualidade (QA). Testar como o bot se comporta contra "clientes difíceis", validar se os comandos estritos estão ativando e garantir que o Prompt gerado pelo Meta-Agente foi eficaz.

### 4. 📈 Telemetria
Os logs estruturados de tudo que acontece no motor do Agiliz. Registra falhas, sucessos na invocação de Tools (como envio de e-mails), e exceções do sistema.
- **Casos de Uso**: Auditoria e Suporte. Quando o cliente reclamar "O bot não enviou a confirmação para o meu lead", a equipe de suporte entra aqui e consegue ver a exata request e falha assíncrona que o robô enfrentou.

### 5. 📧 Disparar E-mail (Integração Híbrida)
Uma aba originalmente concebida para testar integrações SMTP e EmailJS do painel. Ela atua como uma ferramenta interna flexível para disparos de comunicação.

### 6. 💰 Financeiro (TCO e Billing)
O **Coração Financeiro** do Agiliz. Esta tela processa e exibe todos os dólares gastos (convertidos ao escopo do Custo Total de Propriedade - TCO) em frações de tokens para modelos complexos (Groq, Claude).
- **Métricas Visuais**: Separação total de Custo LLM e Custo de Ferramentas. Tabela analítica de despesas categorizadas por tenant e milissegundo de uso.
- **Casos de Uso**: Base para precificação e faturamento. Saber exatamente quanto o Cliente X consumiu no mês para cobrar a fatia de uso correta, avaliando o custo "invisível" que os robôs geram no backend.

---

## 🔮 O Futuro: Multitenancy Front-end (Dashboards de Clientes)
Como parte do Roadmap desenhado pela gestão da Agiliz, a modularidade do Blazor e do sistema `TenantId` permitirá lançarmos visões em **"Modo Cliente"**:
Ao vender o Bot para um estabelecimento, nós concederemos um link ou portal enxuto (white-label) onde o próprio dono do negócio poderá acessar para:
1. Ver **apenas** o Histórico e a Telemetria da base de contatos **dele**.
2. Acompanhar a fatura parcial do consumo de Tokens do **seu próprio** LLM.
3. Possibilidade de plugar novas ferramentas ou habilitar funções exclusivas sob demanda (Upsell).

Nosso *BillingStore* e a separação extrema no `Agiliz.Core` já deixaram o terreno técnico perfeitamente fértil para essa fase.
