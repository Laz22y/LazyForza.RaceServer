import { createHash } from "node:crypto";
import { access, readFile, writeFile } from "node:fs/promises";
import { constants as fsConstants } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const schemaPath = path.join(repositoryRoot, "protocol", "race-protocol.schema.json");
const checkOnly = process.argv.includes("--check");
const skipClient = process.argv.includes("--skip-client");
const clientRootArgumentIndex = process.argv.indexOf("--client-root");
if (clientRootArgumentIndex >= 0 && !process.argv[clientRootArgumentIndex + 1]) {
  throw new Error("--client-root requires a path.");
}
const configuredClientRoot = clientRootArgumentIndex >= 0
  ? path.resolve(process.argv[clientRootArgumentIndex + 1])
  : path.resolve(repositoryRoot, "..", "LazyForza");

const schemaText = normalizeNewlines(await readFile(schemaPath, "utf8"));
const schema = JSON.parse(schemaText);
const schemaHash = createHash("sha256").update(schemaText).digest("hex");

validateSchema(schema);

const outputs = [
  {
    target: "server",
    path: path.join(repositoryRoot, "src", "LazyForza.RaceServer.Protocol", "RaceProtocolModels.g.cs"),
    content: generateCSharp("server", "LazyForza.RaceServer.Protocol")
  },
  {
    target: "typescript",
    path: path.join(repositoryRoot, "cloudflare", "src", "protocol.generated.ts"),
    content: generateTypeScript()
  }
];

if (!skipClient) {
  if (!await exists(configuredClientRoot)) {
    throw new Error(`LazyForza client repository was not found at ${configuredClientRoot}. Use --skip-client only for an isolated RaceServer build.`);
  }
  outputs.push({
    target: "client",
    path: path.join(configuredClientRoot, "src", "LazyForza.Modules.EstateRace", "EstateRaceProtocol.g.cs"),
    content: generateCSharp("client", "LazyForza.Modules.EstateRace")
  });
}

let stale = false;
for (const output of outputs) {
  const current = await readFile(output.path, "utf8").catch(() => null);
  if (current !== null && normalizeNewlines(current) === output.content) continue;
  stale = true;
  if (checkOnly) {
    console.error(`Generated protocol model is stale: ${output.path}`);
  } else {
    await writeFile(output.path, output.content, "utf8");
    console.log(`Generated ${output.target} protocol model: ${output.path}`);
  }
}

if (checkOnly && stale) process.exitCode = 1;

function validateSchema(value) {
  if (value.schemaVersion !== 1) throw new Error("Unsupported protocol schema format.");
  if (!Number.isInteger(value.protocolVersion)) throw new Error("protocolVersion must be an integer.");
  const enumNames = new Set(value.enums.map(item => item.name));
  const modelNames = new Set(value.models.map(item => item.name));
  if (enumNames.size !== value.enums.length || modelNames.size !== value.models.length) {
    throw new Error("Enum and model names must be unique.");
  }
  for (const model of value.models) {
    const fieldNames = new Set();
    for (const field of model.fields) {
      if (fieldNames.has(field.name)) throw new Error(`Duplicate field ${model.name}.${field.name}.`);
      fieldNames.add(field.name);
      validateType(field.type, enumNames, modelNames, `${model.name}.${field.name}`);
      for (const [target, targetType] of Object.entries(field.types ?? {})) {
        validateType(targetType, enumNames, modelNames, `${model.name}.${field.name} (${target})`);
      }
    }
    for (const target of ["server", "client"]) {
      if (!hasTarget(model, target)) continue;
      let optionalFieldSeen = false;
      for (const field of model.fields.filter(value => !isExcluded(value, target))) {
        const hasDefault = defaultFor(field, target).hasDefault;
        if (hasDefault) optionalFieldSeen = true;
        else if (optionalFieldSeen) throw new Error(`${model.name}.${field.name} is required after an optional ${target} field.`);
      }
    }
  }
}

function validateType(type, enumNames, modelNames, location) {
  if (type.startsWith("nullable<") || type.startsWith("array<")) {
    validateType(type.slice(type.indexOf("<") + 1, -1), enumNames, modelNames, location);
    return;
  }
  if (type.startsWith("map<")) {
    for (const part of splitGeneric(type.slice(4, -1))) validateType(part, enumNames, modelNames, location);
    return;
  }
  if (type.startsWith("enum:")) {
    if (!enumNames.has(type.slice(5))) throw new Error(`Unknown enum type at ${location}: ${type}`);
    return;
  }
  if (type.startsWith("model:")) {
    if (!modelNames.has(type.slice(6))) throw new Error(`Unknown model type at ${location}: ${type}`);
    return;
  }
  if (type.startsWith("literal:")) return;
  if (!["string", "boolean", "int32", "int64", "float64", "uuid", "timestamp", "json", "unknown"].includes(type)) {
    throw new Error(`Unknown primitive type at ${location}: ${type}`);
  }
}

function generateCSharp(target, namespaceName) {
  const schemaSource = target === "client"
    ? "../LazyForza.RaceServer/protocol/race-protocol.schema.json"
    : "protocol/race-protocol.schema.json";
  const lines = [
    "// <auto-generated />",
    `// Source: ${schemaSource} (SHA-256 ${schemaHash})`,
    "#nullable enable",
    "",
    `namespace ${namespaceName};`,
    ""
  ];

  const constantTarget = schema.targets[target];
  lines.push(`public static class ${constantTarget.protocolClass}`, "{");
  lines.push(`    public const int CurrentVersion = ${schema.protocolVersion};`);
  for (const [name, value] of Object.entries(schema.constants)) {
    lines.push(`    public const int ${pascal(name)} = ${formatCSharpNumber(value)};`);
  }
  lines.push("}", "");

  lines.push(`${target === "client" ? "internal" : "public"} static class ${constantTarget.messageTypesClass}`, "{");
  for (const [name, value] of Object.entries(schema.messages)) {
    lines.push(`    public const string ${pascal(name)} = ${JSON.stringify(value)};`);
  }
  lines.push("}", "");

  for (const enumDefinition of schema.enums) {
    const targetName = targetEntry(enumDefinition.targets?.[target])?.name;
    if (!targetName) continue;
    const visibility = targetEntry(enumDefinition.targets[target]).visibility ?? "public";
    lines.push(`${visibility} enum ${targetName}`, "{");
    enumDefinition.values.forEach((value, index) => {
      lines.push(`    ${pascal(value)}${index === enumDefinition.values.length - 1 ? "" : ","}`);
    });
    lines.push("}", "");
  }

  for (const model of schema.models) {
    const targetDefinition = model.targets?.[target];
    if (!targetDefinition) continue;
    for (const concreteTarget of targetEntries(targetDefinition)) {
      const fields = model.fields.filter(field => !isExcluded(field, target));
      const visibility = concreteTarget.visibility ?? (target === "client" ? "internal" : "public");
      const partial = concreteTarget.partial ? " partial" : "";
      if (fields.length === 0) {
        lines.push(`${visibility} sealed${partial} record ${concreteTarget.name};`, "");
        continue;
      }
      lines.push(`${visibility} sealed${partial} record ${concreteTarget.name}(`);
      fields.forEach((field, index) => {
        const type = csharpType(typeFor(field, target), target);
        const name = pascal(nameFor(field, target));
        const defaultValue = defaultFor(field, target);
        const suffix = defaultValue.hasDefault ? ` = ${csharpDefault(defaultValue.value, target)}` : "";
        lines.push(`    ${type} ${name}${suffix}${index === fields.length - 1 ? ");" : ","}`);
      });
      lines.push("");
    }
  }
  return `${lines.join("\n")}\n`;
}

function generateTypeScript() {
  const lines = [
    "// <auto-generated />",
    `// Source: protocol/race-protocol.schema.json (SHA-256 ${schemaHash})`,
    "",
    `export const protocolVersion = ${schema.protocolVersion};`
  ];
  for (const [name, value] of Object.entries(schema.constants)) {
    lines.push(`export const ${name} = ${formatTypeScriptNumber(value)};`);
  }
  lines.push("", "export const messageTypes = {");
  for (const [name, value] of Object.entries(schema.messages)) {
    lines.push(`  ${name}: ${JSON.stringify(value)},`);
  }
  lines.push("} as const;", "");

  for (const enumDefinition of schema.enums) {
    const targetName = targetEntry(enumDefinition.targets?.typescript)?.name;
    if (!targetName) continue;
    lines.push(`export type ${targetName} = ${enumDefinition.values.map(JSON.stringify).join(" | ")};`);
  }
  lines.push("");

  for (const model of schema.models) {
    const targetDefinition = model.targets?.typescript;
    if (!targetDefinition) continue;
    for (const concreteTarget of targetEntries(targetDefinition)) {
      lines.push(`export interface ${concreteTarget.name} {`);
      for (const field of model.fields.filter(value => !isExcluded(value, "typescript"))) {
        const fieldType = typeFor(field, "typescript");
        const optional = requiredFor(field, "typescript") === false ||
          (requiredFor(field, "typescript") === undefined && (isNullable(fieldType) || defaultFor(field, "typescript").hasDefault));
        lines.push(`  ${nameFor(field, "typescript")}${optional ? "?" : ""}: ${typeScriptType(fieldType)};`);
      }
      lines.push("}", "");
    }
  }
  return `${lines.join("\n")}\n`;
}

function csharpType(type, target) {
  if (type.startsWith("nullable<")) return `${csharpType(type.slice(9, -1), target)}?`;
  if (type.startsWith("array<")) return `IReadOnlyList<${csharpType(type.slice(6, -1), target)}>`;
  if (type.startsWith("map<")) {
    const [key, value] = splitGeneric(type.slice(4, -1));
    return `IReadOnlyDictionary<${csharpType(key, target)}, ${csharpType(value, target)}>`;
  }
  if (type.startsWith("enum:")) return targetNameFor(schema.enums, type.slice(5), target);
  if (type.startsWith("model:")) return targetNameFor(schema.models, type.slice(6), target);
  if (type.startsWith("literal:")) throw new Error(`Literal types are not supported by C#: ${type}`);
  return ({ string: "string", boolean: "bool", int32: "int", int64: "long", float64: "double", uuid: "Guid", timestamp: "DateTimeOffset", json: "System.Text.Json.JsonElement", unknown: "object" })[type];
}

function typeScriptType(type) {
  if (type.startsWith("nullable<")) return `${typeScriptType(type.slice(9, -1))} | null`;
  if (type.startsWith("array<")) {
    const itemType = typeScriptType(type.slice(6, -1));
    return `${itemType.includes(" | ") ? `(${itemType})` : itemType}[]`;
  }
  if (type.startsWith("map<")) {
    const [key, value] = splitGeneric(type.slice(4, -1));
    return `Record<${typeScriptType(key)}, ${typeScriptType(value)}>`;
  }
  if (type.startsWith("enum:")) return targetNameFor(schema.enums, type.slice(5), "typescript");
  if (type.startsWith("model:")) return targetNameFor(schema.models, type.slice(6), "typescript");
  if (type.startsWith("literal:")) return type.slice(8).split("|").map(JSON.stringify).join(" | ");
  return ({ string: "string", boolean: "boolean", int32: "number", int64: "number", float64: "number", uuid: "string", timestamp: "string", json: "unknown", unknown: "unknown" })[type];
}

function targetNameFor(collection, canonicalName, target) {
  const definition = collection.find(value => value.name === canonicalName);
  const entry = targetEntry(definition?.targets?.[target]);
  if (!entry) throw new Error(`Type ${canonicalName} is not available for target ${target}.`);
  return entry.name;
}

function targetEntries(value) {
  return (Array.isArray(value) ? value : [value]).map(targetEntry);
}

function targetEntry(value) {
  if (!value) return null;
  return typeof value === "string" ? { name: value } : value;
}

function hasTarget(model, target) {
  return Boolean(model.targets?.[target]);
}

function isExcluded(field, target) {
  return field.exclude?.includes(target) ?? false;
}

function nameFor(field, target) {
  return field.names?.[target] ?? field.name;
}

function typeFor(field, target) {
  return field.types?.[target] ?? field.type;
}

function requiredFor(field, target) {
  return field.required?.[target];
}

function defaultFor(field, target) {
  if (field.defaults && Object.hasOwn(field.defaults, target)) return { hasDefault: true, value: field.defaults[target] };
  if (Object.hasOwn(field, "default")) return { hasDefault: true, value: field.default };
  return { hasDefault: false, value: undefined };
}

function csharpDefault(value, target) {
  if (value === null) return "null";
  if (["string", "number", "boolean"].includes(typeof value)) return typeof value === "string" ? JSON.stringify(value) : String(value).toLowerCase();
  if (value.enum) {
    const [enumName, enumValue] = value.enum.split(".");
    return `${targetNameFor(schema.enums, enumName, target)}.${pascal(enumValue)}`;
  }
  if (value.constant) return `${schema.targets[target].protocolClass}.${pascal(value.constant)}`;
  throw new Error(`Unsupported default value: ${JSON.stringify(value)}`);
}

function isNullable(type) {
  return type.startsWith("nullable<");
}

function splitGeneric(value) {
  let depth = 0;
  for (let index = 0; index < value.length; index++) {
    if (value[index] === "<") depth++;
    else if (value[index] === ">") depth--;
    else if (value[index] === "," && depth === 0) return [value.slice(0, index), value.slice(index + 1)];
  }
  return [value];
}

function pascal(value) {
  return value.length === 0 ? value : value[0].toUpperCase() + value.slice(1);
}

function formatCSharpNumber(value) {
  return value === 65536 ? "64 * 1024" : String(value);
}

function formatTypeScriptNumber(value) {
  return value === 65536 ? "64 * 1024" : String(value);
}

async function exists(targetPath) {
  try {
    await access(targetPath, fsConstants.F_OK);
    return true;
  } catch {
    return false;
  }
}

function normalizeNewlines(value) {
  return value.replace(/\r\n?/g, "\n");
}
