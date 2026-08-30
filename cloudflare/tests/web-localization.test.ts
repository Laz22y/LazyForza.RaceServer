// @ts-expect-error Vitest runs these source-integrity tests in Node.js.
import { readFileSync, readdirSync } from "node:fs";
import { describe, expect, it } from "vitest";

const publicUrl = (name: string) => new URL(`../public/${name}`, import.meta.url);
const nativeUrl = (name: string) =>
  new URL(`../../src/LazyForza.RaceServer.Web/wwwroot/${name}`, import.meta.url);

describe("Race Control localization", () => {
  it("keeps native and Cloudflare Web resources identical", () => {
    const nativeFiles = listFiles(new URL("../../src/LazyForza.RaceServer.Web/wwwroot/", import.meta.url));
    const cloudflareFiles = listFiles(new URL("../public/", import.meta.url));
    expect(cloudflareFiles).toEqual(nativeFiles);
    for (const name of nativeFiles)
      expect(readFileSync(publicUrl(name))).toEqual(readFileSync(nativeUrl(name)));
  });

  it("restores session schedule controls when project editing is canceled", () => {
    const app = readFileSync(publicUrl("app.js"), "utf8");
    expect(app).toContain("eventProjectScheduleBeforeEdit=readEventSchedule()");
    expect(app).toContain("resetEventProjectForm(true)");
    expect(app).toContain("applyEventSchedule(eventProjectScheduleBeforeEdit)");
  });

  it("lets role permissions control the account panel instead of hiding it permanently", () => {
    const index = readFileSync(publicUrl("index.html"), "utf8");
    expect(index).toContain('id="controlAccess" class="panel control-access-panel" data-permission="superAdmin"');
    expect(index).not.toContain('id="controlAccess" class="panel control-access-panel hidden"');
  });

  it("keeps native terminal initialization separate from Cloudflare remote setup", () => {
    const index = readFileSync(publicUrl("index.html"), "utf8");
    const app = readFileSync(publicUrl("app.js"), "utf8");
    const worker = readFileSync(new URL("../src/index.ts", import.meta.url), "utf8");
    expect(index).toContain("请在运行 RaceServer 的服务器终端完成首次设置");
    expect(index).not.toContain('id="setupForm"');
    expect(index).not.toContain('id="setupPlayerPassword"');
    expect(index).not.toContain('id="setupAdminPassword"');
    expect(app).toContain("status.setupMode==='terminal'");
    expect(app).toContain("renderRemoteSetup");
    expect(worker).toContain('setupMode: "remote"');
    expect(worker).toContain('url.pathname === "/api/setup" && request.method === "POST"');
  });

  it("loads localization before the application and packages every referenced asset", () => {
    const index = readFileSync(publicUrl("index.html"), "utf8");
    expect(index.indexOf('/i18n.js')).toBeGreaterThan(0);
    expect(index.indexOf('/i18n.js')).toBeLessThan(index.indexOf('/app.js'));
    expect(index).toContain('id="languageSelect"');
    const localization = readFileSync(publicUrl("i18n.js"), "utf8");
    expect(localization).not.toContain("'placeholder', 'title', 'value'");

    const packageScript = readFileSync(
      new URL("../../scripts/Publish-Development.ps1", import.meta.url),
      "utf8");
    expect(packageScript).toContain("$cloudflarePublicInputs = Get-ChildItem");
    expect(packageScript).toContain("GetRelativePath($cloudflareRoot, $_.FullName)");
    expect(packageScript).toContain("'src/protocol.generated.ts'");
    expect(packageScript).toContain("'scripts/generate-repository-assets.mjs'");
    expect(packageScript).toContain("'src/rule-templates.ts'");
    expect(packageScript).toContain("'tests/rule-templates.test.ts'");
    expect(packageScript).toContain("'src/event-projects.ts'");
    expect(packageScript).toContain("'tests/event-projects.test.ts'");
    expect(packageScript).toContain("'src/control-access.ts'");
    expect(packageScript).toContain("'tests/control-access.test.ts'");
    expect(packageScript).toContain("'src/public-timing.ts'");
    expect(packageScript).toContain("'tests/public-timing.test.ts'");
  });

  it("translates every fixed Chinese Web label and JavaScript literal", () => {
    const translate = loadEnglishTranslator();
    const containsHan = (value: string) => /\p{Script=Han}/u.test(value);
    const html = ["index.html", "timing.html"]
      .map(name => readFileSync(publicUrl(name), "utf8"))
      .join("\n");
    const htmlValues = new Set<string>();
    for (const match of html.matchAll(/>([^<>]*\p{Script=Han}[^<>]*)</gu)) {
      const value = match[1].replace(/\s+/g, " ").trim();
      if (value && value !== "简体中文") htmlValues.add(value);
    }
    for (const match of html.matchAll(
      /(?:placeholder|aria-label|title|alt)="([^"]*\p{Script=Han}[^"]*)"/gu))
      htmlValues.add(match[1]);

    const app = ["app.js", "timing.js"]
      .map(name => readFileSync(publicUrl(name), "utf8"))
      .join("\n");
    const appValues = new Set<string>();
    for (const match of app.matchAll(/(['"])((?:\\.|(?!\1)[^\\\r\n])*)\1/g)) {
      if (containsHan(match[2])) appValues.add(match[2]);
    }

    expect([...htmlValues].filter(value => containsHan(translate(value)))).toEqual([]);
    expect([...appValues].filter(value => containsHan(translate(value)))).toEqual([]);
    expect(app).toContain("defaultSessionName==='地产赛事'?tr(defaultSessionName):defaultSessionName");
  });

  it("translates every fixed server system message exposed to Web or clients", () => {
    const translate = loadEnglishTranslator();
    const values = new Set<string>();
    for (const file of sourceFiles([
      new URL("../../src/LazyForza.RaceServer.Core/", import.meta.url),
      new URL("../../src/LazyForza.RaceServer.Web/", import.meta.url),
      new URL("../src/", import.meta.url)
    ])) {
      if (!/\.(?:cs|ts)$/i.test(file.pathname) || /[\\/](?:bin|obj)[\\/]/.test(file.pathname)) continue;
      const source = readFileSync(file, "utf8");
      for (const match of source.matchAll(/"((?:\\.|[^"\r\n])*)"/g)) {
        const value = match[1];
        if (/\p{Script=Han}/u.test(value) && !/[{}]/.test(value) && value.trim() === value)
          values.add(value);
      }
    }

    expect([...values].filter(value => /\p{Script=Han}/u.test(translate(value)))).toEqual([]);
  });

  it("translates representative dynamic race messages without changing driver names", () => {
    const translate = loadEnglishTranslator();
    const cases = new Map([
      ["Driver One 进入房间。", "Driver One entered the room."],
      ["Driver One 加入赛事。", "Driver One joined the event."],
      ["Driver One 的本圈无效：客户端判定无效。", "Driver One lap invalid: Marked invalid by client."],
      ["Driver One 未正确执行 5 秒停车罚时，处罚已转为通过维修区。", "Driver One did not serve the 5-second time penalty correctly; converted to a drive-through."],
      ["Driver One 抢跑，记录 5 秒待执行罚时。", "Driver One made a false start; a pending 5-second penalty was recorded."],
      ["待执行 +5 秒 · 抢跑", "+5 seconds pending · False start"],
      ["Q2 计时结束，2 名车手仍可完成最后飞驰圈。", "Q2 time expired; 2 drivers may complete their final flying lap."],
      ["房间设置已更新，赛道边界处理为 WarningsOnly。", "Room settings updated; track-limit mode: Warnings only."],
      ["赛事总控取消了 Driver One 的处罚：警告 · 抢跑。", "Race Control revoked Driver One's penalty: Warning · False start."]
    ]);
    for (const [source, expected] of cases) expect(translate(source)).toBe(expected);
  });
});

function listFiles(root: URL, current = root, prefix = ""): string[] {
  const entries = readdirSync(current, { withFileTypes: true }) as Array<{
    name: string;
    isDirectory(): boolean;
    isFile(): boolean;
  }>;
  return entries
    .flatMap(entry => entry.isDirectory()
      ? listFiles(root, new URL(`${entry.name}/`, current), `${prefix}${entry.name}/`)
      : entry.isFile() ? [`${prefix}${entry.name}`] : [])
    .sort();
}

function sourceFiles(roots: URL[]): URL[] {
  const files: URL[] = [];
  const visit = (directory: URL) => {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const child = new URL(`${entry.name}${entry.isDirectory() ? "/" : ""}`, directory);
      if (entry.isDirectory()) visit(child);
      else if (entry.isFile()) files.push(child);
    }
  };
  for (const root of roots) visit(root);
  return files;
}

function loadEnglishTranslator(): (value: string) => string {
  class FakeElement {
    nodeType = 1;
    closest(): null { return null; }
    hasAttribute(): boolean { return false; }
    getAttribute(): null { return null; }
    setAttribute(): void {}
  }
  const documentElement = new FakeElement();
  const document = {
    documentElement,
    addEventListener() {},
    querySelector() { return null; },
    createTreeWalker() { return { nextNode() { return null; } }; }
  };
  const localWindow: Record<string, unknown> = {
    confirm() {},
    prompt() {},
    alert() {}
  };
  const factory = new Function(
    "window", "navigator", "localStorage", "location", "document", "Element",
    "Node", "NodeFilter", "MutationObserver", "CanvasRenderingContext2D",
    `${readFileSync(publicUrl("i18n.js"), "utf8")}; return window.RaceI18n;`);
  const api = factory(
    localWindow,
    { language: "en-US" },
    { getItem: () => "en", setItem() {} },
    { reload() {} },
    document,
    FakeElement,
    { TEXT_NODE: 3, ELEMENT_NODE: 1, DOCUMENT_NODE: 9 },
    { SHOW_ELEMENT: 1, SHOW_TEXT: 4 },
    class { observe() {} },
    undefined) as { t(value: string): string };
  return value => api.t(value);
}
