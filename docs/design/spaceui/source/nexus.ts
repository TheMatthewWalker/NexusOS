export const APP_IDS = [
  "files",
  "browser",
  "terminal",
  "studio",
  "models",
  "settings",
] as const;
export type AppId = (typeof APP_IDS)[number];
export type DemoReply = {
  text: string;
  apps: AppId[];
  actions: string[];
  context: string[];
};
const patterns: Record<AppId, RegExp> = {
  files:
    /\b(file|files|folder|folders|document|documents|pdf|organise|organize|download|downloads|notes|report)\b/i,
  browser:
    /\b(research|search|web|website|browse|browser|find|learn|sources|article|articles|read)\b/i,
  terminal:
    /\b(code|coding|develop|developer|debug|python|script|scripts|terminal|command|linux|build|install|git)\b/i,
  studio:
    /\b(design|create|creative|image|images|draw|art|video|edit|mockup|logo|canvas|studio|presentation)\b/i,
  models:
    /\b(ai|model|models|local|llm|inference|train|training|generate|generation|agent|agents)\b/i,
  settings:
    /\b(settings|wifi|wi-fi|network|bluetooth|brightness|volume|sound|display|battery|configure|preferences|theme)\b/i,
};
export function suggestApps(text: string): AppId[] {
  if (!text.trim()) return [];
  const explicit = APP_IDS.filter(id => patterns[id].test(text));
  if (/\bresearch\b/i.test(text) && !explicit.includes("files"))
    explicit.push("files");
  if (/\b(design|create)\b/i.test(text) && !explicit.includes("files"))
    explicit.push("files");
  if (/\b(code|python|debug)\b/i.test(text) && !explicit.includes("files"))
    explicit.push("files");
  return explicit;
}
export function demoReply(text: string): DemoReply {
  const apps = suggestApps(text);
  if (/\b(research|sources|article|articles|learn)\b/i.test(text))
    return {
      text: "A good starting point is Browser for discovery and Files to keep your findings together. Open either when you’re ready.",
      apps: ["browser", "files"],
      actions: ["Explore in Browser", "Collect in Files"],
      context: [
        "Input · Current prompt",
        "Inferred · Research intent",
        "Suggested apps · Browser + Files",
      ],
    };
  if (/\b(code|python|debug|script|build|develop)\b/i.test(text))
    return {
      text: "Let’s bring your development tools into orbit. Terminal is ready for commands; Files keeps your project within reach. Models can support local AI-assisted coding.",
      apps: ["terminal", "files", "models"],
      actions: ["Open Terminal", "View project files", "Explore local models"],
      context: [
        "Input · Current prompt",
        "Inferred · Development intent",
        "Suggested apps · Terminal + Files + Models",
      ],
    };
  if (/\b(design|create|creative|image|logo|video|mockup)\b/i.test(text))
    return {
      text: "Studio is the place to shape your idea. I’ve also suggested Models for generative tools and Files for your creative assets.",
      apps: ["studio", "models", "files"],
      actions: ["Create in Studio", "Choose a model", "Browse assets"],
      context: [
        "Input · Current prompt",
        "Inferred · Creative intent",
        "Suggested apps · Studio + Models + Files",
      ],
    };
  if (apps.includes("settings"))
    return {
      text: "Settings is the right place to adjust that. Nothing has been changed—choose the app to continue.",
      apps: ["settings"],
      actions: ["Open Settings"],
      context: [
        "Input · Current prompt",
        "Inferred · System preferences intent",
      ],
    };
  if (apps.includes("models"))
    return {
      text: "Models brings local AI tools within reach. Use Terminal alongside it if you want to work with commands or scripts.",
      apps: ["models", "terminal"],
      actions: ["Explore Models", "Open Terminal"],
      context: ["Input · Current prompt", "Inferred · Local AI intent"],
    };
  if (apps.length)
    return {
      text: "I’ve brought the relevant tools into orbit. Choose an app below, or refine your request to change the suggestions.",
      apps,
      actions: apps.map(a => `Open ${a[0].toUpperCase()}${a.slice(1)}`),
      context: [
        "Input · Current prompt",
        "Inferred · Keyword-based app matching",
      ],
    };
  return {
    text: "Tell me what you want to research, create, code, or organise. The right tools will emerge around me as your intent becomes clear.",
    apps: [],
    actions: [],
    context: [
      "Input · Current prompt",
      "Inferred · No matching app intent yet",
    ],
  };
}
