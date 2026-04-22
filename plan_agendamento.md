# Specialized Scheduling Bot Flow

This plan introduces a parallel, specialized flow for creating **Scheduling bots** (Bots de Agendamento) with built-in guardrails and hybrid configurations (mixing AI with hardcoded flows).

> [!NOTE]
> Currently waiting for the user to provide the specific conversation flow design before beginning execution.

## Proposed Changes

### `Agiliz.Wizard\Components\Atoms\AgilizToggle.razor`
-   **[NEW]** Create a new `AgilizToggle` atom component to be used across the application.

### `Agiliz.Wizard\Services\WizardSessionStore.cs`
-   **Add a new enum:** `BotType` (Generic, Scheduling) to distinguish between the two flows.
-   **Create a new prompt template:** `SchedulingMetaSystemPrompt`.
    -   This prompt will instruct the meta-agent to specifically follow the scheduling bot creation flow (TBD by the user).
    -   It will ensure the generated `BotConfig` JSON contains a `systemPrompt` with strict guardrails focused on scheduling.
    -   It will force the creation of predefined `flows` to save LLM tokens for repetitive interactions.
-   **Update `Create()` method:** Accept the new `BotType` enum as an argument and use the corresponding meta system prompt.

### `Agiliz.Wizard\Components\Pages\WizardPage.razor`
-   **Step 1 UI Update:** Add the `AgilizToggle` atom to Step 1, allowing the user to toggle between "Bot Genérico" and "Bot de Agendamento" (which will be the default).
-   **Pass parameter:** Update `StartInterview()` to pass the chosen `BotType` to `SessionStore.Create()`.
-   **Contextual Greeting:** Adjust the initial greeting sent by the bot based on the selected bot type.

## Execution
Execution is currently **blocked** pending the user's detailed flow design.
